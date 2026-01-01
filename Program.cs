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

// Throttling for repeated log messages to prevent spam
DateTimeOffset _lastUnavailableLog = DateTimeOffset.MinValue;
DateTimeOffset _lastNoDeviceLog = DateTimeOffset.MinValue;
DateTimeOffset _lastConnectionLostLog = DateTimeOffset.MinValue;
DateTimeOffset _lastHealthWarningLog = DateTimeOffset.MinValue;
DateTimeOffset _lastScanTimeoutLog = DateTimeOffset.MinValue;
const int ThrottleIntervalSeconds = 30; // Only log repeated messages every 30 seconds
const int ConnectionLostLogIntervalSeconds = 10; // Log connection lost every 10 seconds

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
    ? $"{assemblyVersion.Major}.{assemblyVersion.Minor}.{assemblyVersion.Build}" 
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

// Handle Ctrl+C to ensure graceful shutdown
Console.CancelKeyPress += (sender, e) =>
{
    e.Cancel = true; // Prevent immediate termination
    Console.WriteLine("\n[SHUTDOWN] Shutdown requested (Ctrl+C). Shutting down gracefully...");
    cts.Cancel();
};

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

// Timeout helper to prevent operations from hanging indefinitely
static async Task<T?> WithTimeoutRef<T>(Task<T> task, TimeSpan timeout, string operation, CancellationToken cancel = default) where T : class
{
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
    timeoutCts.CancelAfter(timeout);
    try
    {
        return await task.WaitAsync(timeoutCts.Token);
    }
    catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !cancel.IsCancellationRequested)
    {
        TimeoutLogger.LogTimeout(operation, timeout);
        return null;
    }
}

static async Task<bool> WithTimeoutTask(Task task, TimeSpan timeout, string operation, CancellationToken cancel = default)
{
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
    timeoutCts.CancelAfter(timeout);
    try
    {
        await task.WaitAsync(timeoutCts.Token);
        return true;
    }
    catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !cancel.IsCancellationRequested)
    {
        TimeoutLogger.LogTimeout(operation, timeout);
        return false;
    }
}

static async Task<bool?> WithTimeoutBool(Task<bool> task, TimeSpan timeout, string operation, CancellationToken cancel = default)
{
    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancel);
    timeoutCts.CancelAfter(timeout);
    try
    {
        return await task.WaitAsync(timeoutCts.Token);
    }
    catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested && !cancel.IsCancellationRequested)
    {
        TimeoutLogger.LogTimeout(operation, timeout);
        return null;
    }
}

// Always cleanup: cancel BLE and await its completion
cts.Cancel();
if (bleTask != null)
{
    Console.WriteLine("[SHUTDOWN] Waiting for BLE worker to stop...");
    try 
    { 
        // Give it a timeout to prevent hanging forever
        await Task.WhenAny(bleTask, Task.Delay(TimeSpan.FromSeconds(5)));
        if (!bleTask.IsCompleted)
        {
            Console.WriteLine("[SHUTDOWN] ⚠️ BLE worker did not stop within 5s, forcing shutdown...");
        }
        else
        {
            await bleTask; // Get any exceptions
        }
    } 
    catch (OperationCanceledException) 
    {
        Console.WriteLine("[SHUTDOWN] BLE worker stopped.");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[SHUTDOWN] BLE worker error during shutdown: {ex.Message}");
    }
}

// Clean up active connection
if (_activeDev != null)
{
    Console.WriteLine("[SHUTDOWN] Disconnecting Bluetooth device...");
    SafeDisconnect(_activeDev);
}
Console.WriteLine("[SHUTDOWN] Shutdown complete.");

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
                var availabilityTask = Bluetooth.GetAvailabilityAsync();
                var availabilityResult = await WithTimeoutBool(availabilityTask, TimeSpan.FromSeconds(5), "Bluetooth.GetAvailabilityAsync()", cancel);
                if (availabilityResult == null && !cancel.IsCancellationRequested)
                {
                    Console.WriteLine("[BLE] ⚠️ Bluetooth availability check timed out. Retrying in 5s...");
                    await Task.Delay(5000, cancel);
                    continue;
                }
                available = availabilityResult ?? false;
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
                var now = DateTimeOffset.UtcNow;
                if ((now - _lastUnavailableLog).TotalSeconds >= ThrottleIntervalSeconds)
                {
                    Console.WriteLine("[BLE] ⚠️ Bluetooth unavailable. Make sure Bluetooth is enabled and try turning it off and on again. Retrying in 5s…");
                    _lastUnavailableLog = now;
                }
                await Task.Delay(5000, cancel);
                continue;
            }

            if (_activeDev?.Gatt?.IsConnected == true && _subscribed)
            {
                await Task.Delay(500, cancel);
                continue;
            }

            // If we were connected but now we're not, log it periodically
            if (_activeDev != null && _lastConnectionLostLog != DateTimeOffset.MinValue)
            {
                var now = DateTimeOffset.UtcNow;
                if ((now - _lastConnectionLostLog).TotalSeconds >= ConnectionLostLogIntervalSeconds)
                {
                    Console.WriteLine("[BLE] ❌ Connection still lost. Attempting to reconnect...");
                    _lastConnectionLostLog = now;
                }
            }

            Console.WriteLine("[BLE] Scanning for devices…");
            BluetoothDevice? target = null;

            IReadOnlyCollection<BluetoothDevice>? devices = null;
            try
            {
                var scanTask = Bluetooth.ScanForDevicesAsync(new RequestDeviceOptions { AcceptAllDevices = true });
                devices = await WithTimeoutRef(scanTask, TimeSpan.FromSeconds(15), "Bluetooth.ScanForDevicesAsync()", cancel);
                if (devices == null && !cancel.IsCancellationRequested)
                {
                    var now = DateTimeOffset.UtcNow;
                    if ((now - _lastScanTimeoutLog).TotalSeconds >= ThrottleIntervalSeconds)
                    {
                        Console.WriteLine("[BLE] ⚠️ Device scan timed out after 15s. This may indicate Bluetooth issues. Retrying in 3s...");
                        _lastScanTimeoutLog = now;
                    }
                    await Task.Delay(3000, cancel);
                    continue;
                }
                if (devices == null) continue;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BLE] ❌ ERROR: Device scan failed: {ex.Message}");
                Console.WriteLine("[BLE] 💡 TIP: Try turning Bluetooth off and on again.");
                Console.WriteLine("[BLE] 💡 TIP: If your device is connected in system Bluetooth settings, disconnect it first - BLE devices can only connect to one client at a time.");
                await Task.Delay(3000, cancel);
                continue;
            }

            // Log what devices were found for debugging
            if (devices != null && devices.Count > 0)
            {
                Console.WriteLine($"[BLE] Found {devices.Count} device(s):");
                foreach (var dev in devices)
                {
                    Console.WriteLine($"  - {dev?.Name ?? "Unknown"} (ID: {dev?.Id ?? "N/A"})");
                }
            }

            if (devices != null)
            {
                if (!string.IsNullOrWhiteSpace(desiredNameToken))
                    target = devices.FirstOrDefault(d => (d?.Name ?? "").Contains(desiredNameToken, StringComparison.OrdinalIgnoreCase));

                target ??= devices.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d?.Name));
            }

            if (target is null)
            {
                var now = DateTimeOffset.UtcNow;
                if ((now - _lastNoDeviceLog).TotalSeconds >= ThrottleIntervalSeconds)
                {
                    Console.WriteLine($"[BLE] ⚠️ No matching device found (looking for name containing '{desiredNameToken}').");
                    if (devices != null && devices.Count == 0)
                    {
                        Console.WriteLine("[BLE] 💡 No devices found in scan. This might indicate:");
                        Console.WriteLine("[BLE]    - Device is connected to system Bluetooth (disconnect it first)");
                        Console.WriteLine("[BLE]    - Device is not advertising/discoverable");
                        Console.WriteLine("[BLE]    - Bluetooth permissions issue (check with: journalctl -u bluetooth)");
                    }
                    Console.WriteLine("[BLE] 💡 TIP: Make sure your device is on and nearby.");
                    Console.WriteLine("[BLE] 💡 TIP: If the device is connected in system Bluetooth settings, disconnect it first - BLE devices can only connect to one client at a time.");
                    _lastNoDeviceLog = now;
                }
                await Task.Delay(3000, cancel);
                continue;
            }

            if (_activeId == target.Id && _activeDev?.Gatt?.IsConnected == true && _subscribed)
            {
                await Task.Delay(1000, cancel);
                continue;
            }

            Console.WriteLine($"[BLE] Connecting to {target.Name} ({target.Id}) …");
            try
            {
                var connectTask = target.Gatt.ConnectAsync();
                bool connected = await WithTimeoutTask(connectTask, TimeSpan.FromSeconds(10), $"GATT.ConnectAsync() to {target.Name}", cancel);
                if (!connected && !cancel.IsCancellationRequested)
                {
                    Console.WriteLine($"[BLE] ❌ CONNECTION FAILED: Could not connect to {target.Name} within 10s");
                    Console.WriteLine("[BLE] 💡 TIP: Try turning Bluetooth off and on again, or ensure the device is not connected to another application. Retrying in 3s...");
                    SafeDisconnect(target);
                    await Task.Delay(3000, cancel);
                    continue;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BLE] ❌ CONNECTION ERROR: Failed to connect to {target.Name}: {ex.Message}");
                Console.WriteLine("[BLE] 💡 TIP: Try turning Bluetooth off and on again. Retrying in 3s...");
                SafeDisconnect(target);
                await Task.Delay(3000, cancel);
                continue;
            }
            
            var gatt = target.Gatt;
            if (gatt is null || !gatt.IsConnected)
            {
                Console.WriteLine($"[BLE] ❌ GATT connection failed: GATT is null or not connected after ConnectAsync");
                Console.WriteLine("[BLE] 💡 TIP: Try turning Bluetooth off and on again. Retrying in 3s...");
                SafeDisconnect(target);
                await Task.Delay(3000, cancel);
                continue;
            }
            Console.WriteLine($"[BLE] ✓ Connected to {target.Name}");
            // Reset connection lost log timer on successful connection
            _lastConnectionLostLog = DateTimeOffset.MinValue;

            GattService? hrs = null;
            try
            {
                var hrsTask = gatt.GetPrimaryServiceAsync(HrsService);
                hrs = await WithTimeoutRef(hrsTask, TimeSpan.FromSeconds(5), "GetPrimaryServiceAsync(HeartRateService)", cancel);
                if (hrs == null && !cancel.IsCancellationRequested)
                {
                    Console.WriteLine("[BLE] ❌ Heart Rate Service not found or request timed out");
                    Console.WriteLine("[BLE] 💡 This device may not support Heart Rate Service (UUID 0x180D)");
                    SafeDisconnect(target);
                    await Task.Delay(2000, cancel);
                    continue;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BLE] ❌ ERROR: Failed to get Heart Rate Service: {ex.Message}");
                SafeDisconnect(target);
                await Task.Delay(2000, cancel);
                continue;
            }
            
            if (hrs is null)
            {
                Console.WriteLine("[BLE] ❌ Heart Rate Service missing; disconnecting and rescanning.");
                SafeDisconnect(target);
                await Task.Delay(2000, cancel);
                continue;
            }
            Console.WriteLine("[BLE] ✓ Heart Rate Service found");

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

            GattCharacteristic? hrm = null;
            try
            {
                var hrmTask = hrs.GetCharacteristicAsync(HrmChar);
                hrm = await WithTimeoutRef(hrmTask, TimeSpan.FromSeconds(5), "GetCharacteristicAsync(HRMeasurement)", cancel);
                if (hrm == null && !cancel.IsCancellationRequested)
                {
                    Console.WriteLine("[BLE] ❌ HR Measurement characteristic not found or request timed out");
                    Console.WriteLine("[BLE] 💡 This device may not support HR Measurement characteristic (UUID 0x2A37)");
                    SafeDisconnect(target);
                    await Task.Delay(2000, cancel);
                    continue;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BLE] ❌ ERROR: Failed to get HR Measurement characteristic: {ex.Message}");
                SafeDisconnect(target);
                await Task.Delay(2000, cancel);
                continue;
            }
            
            if (hrm is null)
            {
                Console.WriteLine("[BLE] ❌ HR Measurement characteristic not found; disconnecting.");
                SafeDisconnect(target);
                await Task.Delay(2000, cancel);
                continue;
            }
            Console.WriteLine("[BLE] ✓ HR Measurement characteristic found");

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
            try
            {
                var subscribeTask = hrm.StartNotificationsAsync();
                bool subscribed = await WithTimeoutTask(subscribeTask, TimeSpan.FromSeconds(5), "StartNotificationsAsync()", cancel);
                if (!subscribed && !cancel.IsCancellationRequested)
                {
                    Console.WriteLine("[BLE] ❌ SUBSCRIPTION FAILED: Could not start HR notifications within 5s");
                    Console.WriteLine("[BLE] 💡 TIP: The device may not support notifications, or there may be a connection issue");
                    hrm.CharacteristicValueChanged -= _hrmHandler;
                    _hrmHandler = null;
                    SafeDisconnect(target);
                    await Task.Delay(2000, cancel);
                    continue;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[BLE] ❌ SUBSCRIPTION ERROR: Failed to start HR notifications: {ex.Message}");
                Console.WriteLine("[BLE] 💡 TIP: Try disconnecting and reconnecting the device");
                hrm.CharacteristicValueChanged -= _hrmHandler;
                _hrmHandler = null;
                SafeDisconnect(target);
                await Task.Delay(2000, cancel);
                continue;
            }
            
            _subscribed = true;
            Console.WriteLine("[BLE] ✓ Successfully subscribed to HR notifications");
            
            // Monitor connection health - check if we're actually receiving data
            var healthCheckTask = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(10), cancel); // Wait 10s for first reading
                while (!cancel.IsCancellationRequested && gatt.IsConnected && _subscribed)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15), cancel);
                    if (cancel.IsCancellationRequested || !gatt.IsConnected || !_subscribed) break;
                    
                    var currentLatest = latest; // Capture current value
                    var now = DateTimeOffset.UtcNow;
                    var timeSinceLastHr = currentLatest != null 
                        ? (now - currentLatest.ts).TotalSeconds 
                        : double.MaxValue;
                    
                    if (timeSinceLastHr > 20)
                    {
                        // Throttle health warnings to avoid spam
                        if ((now - _lastHealthWarningLog).TotalSeconds >= ThrottleIntervalSeconds)
                        {
                            Console.WriteLine($"[BLE] ⚠️ WARNING: No heart rate data received in {timeSinceLastHr:F1}s");
                            Console.WriteLine("[BLE] 💡 The subscription may not be working. Checking connection status...");
                            _lastHealthWarningLog = now;
                        }
                        if (!gatt.IsConnected)
                        {
                            Console.WriteLine("[BLE] ❌ Connection lost. Will attempt to reconnect...");
                            break;
                        }
                    }
                }
            }, cancel);

            // Monitor connection status - wait while connected
            while (!cancel.IsCancellationRequested && gatt.IsConnected)
            {
                await Task.Delay(500, cancel);
            }

            // Connection was lost
            if (!cancel.IsCancellationRequested && !gatt.IsConnected)
            {
                var now = DateTimeOffset.UtcNow;
                Console.WriteLine("[BLE] ❌ Connection lost. Will attempt to reconnect...");
                _lastConnectionLostLog = now;
            }
            else if (cancel.IsCancellationRequested)
            {
                Console.WriteLine("[BLE] Disconnected (shutdown requested).");
            }

            _subscribed = false;
            _activeDev = null;
            _activeId = null;
            if (_hrmHandler != null && hrm != null)
            {
                try { hrm.CharacteristicValueChanged -= _hrmHandler; } catch { }
            }
            _hrmHandler = null; // Clear handler reference on disconnect
            await Task.Delay(1000, cancel);
        }
        catch (OperationCanceledException) 
        {
            Console.WriteLine("[BLE] Operation cancelled. Shutting down...");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[BLE] ❌ ERROR in BLE worker: {ex.Message}");
            Console.WriteLine($"[BLE] Stack trace: {ex.StackTrace}");
            Console.WriteLine("[BLE] Retrying in 3s...");
            await Task.Delay(3000, cancel);
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

// Track last timeout log time per operation to throttle messages
static class TimeoutLogger
{
    private static readonly Dictionary<string, DateTimeOffset> _logTimes = new();
    private static readonly object _lock = new();

    public static void LogTimeout(string operation, TimeSpan timeout)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_lock)
        {
            if (!_logTimes.TryGetValue(operation, out var lastLog) || (now - lastLog).TotalSeconds >= 30)
            {
                Console.WriteLine($"[BLE] ⚠️ TIMEOUT: {operation} took longer than {timeout.TotalSeconds}s and was cancelled");
                _logTimes[operation] = now;
            }
        }
    }
}

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
