using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using InTheHand.Bluetooth;
using Microsoft.Extensions.Logging;

using XossHrmServer;

var desiredNameToken = (Environment.GetEnvironmentVariable("HRM_DEVICE_NAME") ?? "XOSS").Trim();
var httpPort = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 5279;

var sockets = new ConcurrentDictionary<Guid, WebSocket>();
LatestHr? latest = null;

BluetoothDevice? _activeDev = null;
int _lastLoggedBpm = -1;
DateTimeOffset _lastLoggedTime = DateTimeOffset.MinValue;
string? _activeId = null;
bool _subscribed = false;
EventHandler<GattCharacteristicValueChangedEventArgs>? _hrmHandler = null;
bool AllowZeroBpm = (Environment.GetEnvironmentVariable("ALLOW_ZERO_BPM") ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase);

void ConfigureApp(WebApplication app)
{
    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

    app.Map("/ws", async ctx =>
    {
        if (!ctx.WebSockets.IsWebSocketRequest)
        {
            ctx.Response.StatusCode = 400;
            return;
        }

        using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
        var id = Guid.NewGuid();
        sockets[id] = ws;
        try
        {
            while (ws.State == WebSocketState.Open)
                await Task.Delay(1000, ctx.RequestAborted);
        }
        finally
        {
            sockets.TryRemove(id, out _);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", default); } catch { }
        }
    });

    app.MapGet("/", () =>
        Results.Json(new
        {
            ok = true,
            ws = "/ws",
            port = httpPort,
            mode = "scan+auto-connect",
            match = $"name contains '{desiredNameToken}'"
        })
    );

    app.MapGet("/latest", () => latest is null ? Results.NoContent() : Results.Json(latest));

    HrMetrics.MapEndpoints(app, defaultWindowSecs: 60);
    app.MapDashboardAndLogs("logs");

    app.MapGet("/bpm", () =>
    {
        if (latest == null)
            return Results.NoContent();
        string bpmPadded = $"{latest.bpm,3}";
        string batteryPadded = $"{(latest.battery ?? 0),3}";
        return Results.Json(new { bpm = bpmPadded, battery = batteryPadded });
    });
}

// Get version from assembly
var assemblyVersion = Assembly.GetExecutingAssembly().GetName().Version;
var version = assemblyVersion != null 
    ? (assemblyVersion.Revision >= 0 
        ? $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}.{assemblyVersion.Revision}"
        : $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}")
    : "1.0.0";

// Welcome message
Console.WriteLine($"═══════════════════════════════════════════════════════════");
Console.WriteLine($"  Welcome to XossHrmServer v{version}");
Console.WriteLine($"═══════════════════════════════════════════════════════════");
Console.WriteLine();

var cts = new CancellationTokenSource();
var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
var isLinux = RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
var isMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);

// Start BLE worker on all platforms - InTheHand.BluetoothLE provides platform-specific providers
// Check for environment variable to disable BLE if needed
var disableBle = (Environment.GetEnvironmentVariable("DOTNET_DISABLE_BLE") ?? "false").Equals("true", StringComparison.OrdinalIgnoreCase);

Task? bleTask = null;
if (!disableBle)
{
    Console.WriteLine($"[BLE] Starting BLE worker on {RuntimeInformation.OSDescription}...");
    bleTask = Task.Run(() => BleWorkerAsync(cts.Token));
}
else
{
    Console.WriteLine("[BLE] Bluetooth LE disabled via DOTNET_DISABLE_BLE environment variable. Running in HTTP-only mode.");
}

// Helper to build and configure the app with proper logging filters
WebApplication BuildApp(int port)
{
    var builder = WebApplication.CreateBuilder(args);
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

    // Suppress verbose Microsoft logging (especially on Linux where we don't want ASP.NET noise)
    builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.AspNetCore.Routing.EndpointMiddleware", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.AspNetCore.Http.Result", LogLevel.Warning);
    builder.Logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.None);
    builder.Logging.AddFilter("Microsoft.AspNetCore.Server.Kestrel", LogLevel.Error);
    builder.Logging.AddFilter("Microsoft.Hosting.Lifetime", LogLevel.None);

    var app = builder.Build();
    ConfigureApp(app);
    app.Lifetime.ApplicationStopping.Register(() => cts.Cancel());
    return app;
}

// Enable CSV logging once (before retry loop so it's not duplicated)
HrMetrics.EnableLogging("logs", flushIntervalSeconds: 5);

WebApplication? app = null;
int maxRetries = 10;

for (int attempt = 0; attempt < maxRetries; attempt++)
{
    try
    {
        app = BuildApp(httpPort);
        await app.StartAsync();
        Console.WriteLine($"[HTTP] Server running on http://0.0.0.0:{httpPort}");
        break; // Successfully started
    }
    catch (IOException ex) when (ex.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException ||
                                  ex.Message.Contains("address already in use", StringComparison.OrdinalIgnoreCase))
    {
        httpPort++;
        Console.WriteLine($"[HTTP] Port in use; retrying on {httpPort} …");
        if (app != null)
        {
            try { await app.DisposeAsync(); } catch { }
            app = null;
        }
    }
}

if (app == null)
{
    Console.WriteLine($"[HTTP] Failed to bind after {maxRetries} attempts. Exiting.");
}
else
{
    try
    {
        await app.WaitForShutdownAsync();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[HTTP] Server error: {ex.Message}");
    }
}

// Always cleanup: cancel BLE and await its completion
cts.Cancel();
if (bleTask != null)
{
    try { await bleTask; } catch (OperationCanceledException) { }
}

async Task BleWorkerAsync(CancellationToken cancel)
{
    // Define UUIDs - InTheHand.BluetoothLE provides platform-specific providers
    var HrsService = BluetoothUuid.FromShortId(0x180D);
    var HrmChar = BluetoothUuid.FromShortId(0x2A37);
    var BattSrv = BluetoothUuid.FromShortId(0x180F);
    var BattChr = BluetoothUuid.FromShortId(0x2A19);
    
    Console.WriteLine($"[BLE] Worker starting on {RuntimeInformation.OSDescription}… token: " + desiredNameToken);

    while (!cancel.IsCancellationRequested)
    {
        try
        {
            bool available;
            try
            {
                available = await Bluetooth.GetAvailabilityAsync();
            }
            catch (DllNotFoundException ex)
            {
                Console.WriteLine($"[BLE] Bluetooth library not available on this platform: {ex.Message}");
                Console.WriteLine("[BLE] Running in HTTP-only mode. BLE functionality disabled.");
                return;
            }
            catch (Exception ex) when (ex.Message.Contains("No path specified for UNIX transport"))
            {
                Console.WriteLine($"[BLE] Error: Wrong framework detected - Linux Bluetooth libraries on Windows!");
                Console.WriteLine("[BLE] On Windows, you must use the Windows-specific framework.");
                Console.WriteLine("[BLE] Try: dotnet run -f net9.0-windows10.0.19041.0");
                Console.WriteLine("[BLE] Or: dotnet run -f net10.0-windows10.0.19041.0");
                Console.WriteLine("[BLE] Running in HTTP-only mode. BLE functionality disabled.");
                return;
            }
            catch (Exception ex) when (ex.Message.Contains("api-ms-win-core") || 
                                       ex.Message.Contains("Unable to load shared library") ||
                                       ex.Message.Contains("BlueZ") ||
                                       ex.Message.Contains("CoreBluetooth"))
            {
                Console.WriteLine($"[BLE] Bluetooth provider error: {ex.Message}");
                Console.WriteLine("[BLE] This may indicate missing system dependencies:");
                if (isLinux)
                {
                    Console.WriteLine("  - Linux: Ensure BlueZ is installed (sudo apt-get install bluez)");
                    Console.WriteLine("  - Linux: Ensure you have Bluetooth permissions");
                }
                else if (isMacOS)
                {
                    Console.WriteLine("  - macOS: Ensure Bluetooth permissions are granted");
                }
                Console.WriteLine("[BLE] Running in HTTP-only mode. BLE functionality disabled.");
                return;
            }

            if (!available)
            {
                Console.WriteLine("[BLE] Bluetooth unavailable. Retrying in 5s…");
                await Task.Delay(5000, cancel);
                continue;
            }

            if (_activeDev?.Gatt?.IsConnected == true && _subscribed)
            {
                await Task.Delay(500, cancel);
                continue;
            }

            Console.WriteLine("[BLE] Scanning for devices…");
            BluetoothDevice? target = null;

            var devices = await Bluetooth.ScanForDevicesAsync(new RequestDeviceOptions { AcceptAllDevices = true });

            if (!string.IsNullOrWhiteSpace(desiredNameToken))
                target = devices.FirstOrDefault(d => (d?.Name ?? "").Contains(desiredNameToken, StringComparison.OrdinalIgnoreCase));

            target ??= devices.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d?.Name));

            if (target is null)
            {
                Console.WriteLine("[BLE] No matching device found. Retrying in 2s…");
                await Task.Delay(2000, cancel);
                continue;
            }

            if (_activeId == target.Id && _activeDev?.Gatt?.IsConnected == true && _subscribed)
            {
                await Task.Delay(1000, cancel);
                continue;
            }

            Console.WriteLine($"[BLE] Connecting to {target.Name} ({target.Id}) …");
            await target.Gatt.ConnectAsync();
            var gatt = target.Gatt;
            if (gatt is null || !gatt.IsConnected)
            {
                Console.WriteLine("[BLE] GATT connection failed.");
                await Task.Delay(2000, cancel);
                continue;
            }

            var hrs = await gatt.GetPrimaryServiceAsync(HrsService);
            if (hrs is null)
            {
                Console.WriteLine("[BLE] Heart Rate Service missing; rescanning.");
                SafeDisconnect(target);
                await Task.Delay(1000, cancel);
                continue;
            }

            int? batteryPct = null;
            try
            {
                var bs = await gatt.GetPrimaryServiceAsync(BattSrv);
                if (bs is not null)
                {
                    var bc = await bs.GetCharacteristicAsync(BattChr);
                    if (bc is not null)
                    {
                        var bv = await bc.ReadValueAsync();
                        if (bv is { Length: > 0 })
                        {
                            batteryPct = bv[0];
                            if (latest is not null) latest = latest with { battery = batteryPct };
                            Console.WriteLine($"[BLE] Battery initial: {batteryPct}%");
                        }
                        try
                        {
                            bc.CharacteristicValueChanged += async (_, be) =>
                            {
                                if (be.Value is { Length: > 0 })
                                {
                                    var newPct = (int)be.Value[0];
                                    if (newPct != batteryPct)
                                    {
                                        batteryPct = newPct;
                                        if (latest is not null) latest = latest with { battery = batteryPct };
                                        Console.WriteLine($"[BLE] Battery notify: {batteryPct}%");
                                        await BroadcastAsync(new
                                        {
                                            type = "battery",
                                            battery = $"{(batteryPct ?? 0),3}",
                                            device = target.Name
                                        });
                                    }
                                }
                            };
                            await bc.StartNotificationsAsync();
                        }
                        catch { }

                        _ = Task.Run(async () =>
                        {
                            while (!cancel.IsCancellationRequested && gatt.IsConnected)
                            {
                                try
                                {
                                    var vb2 = await bc.ReadValueAsync();
                                    if (vb2 is { Length: > 0 })
                                    {
                                        var newPct = (int)vb2[0];
                                        if (newPct != batteryPct)
                                        {
                                            batteryPct = newPct;
                                            if (latest is not null) latest = latest with { battery = batteryPct };
                                            Console.WriteLine($"[BLE] Battery poll: {batteryPct}%");
                                            await BroadcastAsync(new
                                            {
                                                type = "battery",
                                                battery = $"{(batteryPct ?? 0),3}",
                                                device = target.Name
                                            });
                                        }
                                    }
                                }
                                catch { }
                                await Task.Delay(TimeSpan.FromSeconds(60), cancel);
                            }
                        }, cancel);
                    }
                }
            }
            catch { }

            var hrm = await hrs.GetCharacteristicAsync(HrmChar);
            if (hrm is null)
            {
                Console.WriteLine("[BLE] HR Measurement characteristic not found; disconnecting.");
                SafeDisconnect(target);
                await Task.Delay(1000, cancel);
                continue;
            }

            _activeDev = target;
            _activeId = target.Id;

            // Only subscribe if not already subscribed
            if (_subscribed)
            {
                await Task.Delay(500, cancel);
                continue;
            }

            // Remove old handler if it exists (prevents duplicate handlers on reconnect)
            if (_hrmHandler != null)
            {
                try { hrm.CharacteristicValueChanged -= _hrmHandler; } catch { }
                _hrmHandler = null;
            }

            Console.WriteLine("[BLE] Subscribing to HR notifications…");
            
            // Create handler and store reference
            _hrmHandler = async (_, e) =>
            {
                if (e.Value is null || e.Value.Length == 0) return;
                var reading = HeartRateParser.Parse(e.Value);
                if (!AllowZeroBpm && reading.Bpm == 0) return;

                var now = DateTimeOffset.UtcNow;

                latest = new LatestHr(
                    device: target.Name ?? "unknown",
                    bpm: reading.Bpm,
                    ts: now,
                    battery: batteryPct,
                    rr: reading.RRIntervals,
                    energy: reading.EnergyExpended
                );

                HrMetrics.PushSample(now, reading.Bpm, reading.RRIntervals, batteryPct, reading.EnergyExpended);

                var payload = new
                {
                    type = "hr",
                    bpm = $"{reading.Bpm,3}",
                    battery = $"{(batteryPct ?? 0),3}",
                    device = target.Name
                };
                await BroadcastAsync(payload);

                // Only log if BPM changed or it's been more than 0.5 seconds since last log (prevents duplicate logs)
                if (reading.Bpm != _lastLoggedBpm || (now - _lastLoggedTime).TotalSeconds > 0.5)
                {
                    Console.WriteLine($"[BLE] {target.Name}: {reading.Bpm} bpm");
                    _lastLoggedBpm = reading.Bpm;
                    _lastLoggedTime = now;
                }
            };

            hrm.CharacteristicValueChanged += _hrmHandler;
            await hrm.StartNotificationsAsync();
            _subscribed = true;

            while (!cancel.IsCancellationRequested && gatt.IsConnected)
                await Task.Delay(500, cancel);

            Console.WriteLine("[BLE] Disconnected.");
            _subscribed = false;
            _activeDev = null;
            _activeId = null;
            _hrmHandler = null; // Clear handler reference on disconnect
            await Task.Delay(1000, cancel);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Console.WriteLine("[BLE] Error: " + ex.Message);
            await Task.Delay(1500, cancel);
        }
    }
}

void SafeDisconnect(BluetoothDevice dev)
{
    try { dev.Gatt?.Disconnect(); } catch { }
}

async Task BroadcastAsync(object obj)
{
    var json = JsonSerializer.Serialize(obj);
    var bytes = Encoding.UTF8.GetBytes(json);
    var remove = new List<Guid>();
    foreach (var kv in sockets)
    {
        var ws = kv.Value;
        if (ws.State != WebSocketState.Open) { remove.Add(kv.Key); continue; }
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, default); }
        catch { remove.Add(kv.Key); }
    }
    foreach (var id in remove) sockets.TryRemove(id, out _);
}

public sealed record LatestHr(
    string device,
    int bpm,
    DateTimeOffset ts,
    int? battery,
    IReadOnlyList<int> rr,
    int? energy
);

static class HeartRateParser
{
    public sealed record Reading(int Bpm, List<int> RRIntervals, int? EnergyExpended);

    public static Reading Parse(byte[] data)
    {
        var span = data.AsSpan();
        if (span.Length == 0) return new Reading(0, new(), null);

        var flags = span[0];
        var idx = 1;

        int bpm;
        if ((flags & 0x01) == 0) { bpm = span[idx]; idx += 1; }
        else { bpm = span[idx] | (span[idx + 1] << 8); idx += 2; }

        int? energy = null;
        if ((flags & 0x08) != 0 && idx + 1 < span.Length)
        {
            energy = span[idx] | (span[idx + 1] << 8);
            idx += 2;
        }

        var rrs = new List<int>();
        if ((flags & 0x10) != 0)
        {
            while (idx + 1 < span.Length)
            {
                var rr = span[idx] | (span[idx + 1] << 8);
                idx += 2;
                var ms = (int)Math.Round(rr * 1000.0 / 1024.0);
                rrs.Add(ms);
            }
        }

        return new Reading(bpm, rrs, energy);
    }
}
