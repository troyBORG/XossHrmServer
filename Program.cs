using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using InTheHand.Bluetooth;

using XossHrmServer; // for HrMetrics

var desiredNameToken = (Environment.GetEnvironmentVariable("HRM_DEVICE_NAME") ?? "XOSS").Trim();
var httpPort = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var p) ? p : 5279;

// UUIDs
var HrsService = BluetoothUuid.FromShortId(0x180D);
var HrmChar = BluetoothUuid.FromShortId(0x2A37);
var BattSrv = BluetoothUuid.FromShortId(0x180F);
var BattChr = BluetoothUuid.FromShortId(0x2A19);

// -------------- Web host / WS --------------
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls($"http://0.0.0.0:{httpPort}");
var app = builder.Build();

var sockets = new ConcurrentDictionary<Guid, WebSocket>();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

LatestHr? latest = null;

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

HrMetrics.MapEndpoints(app, defaultWindowSecs: 60); // enables /stats and /history
app.MapDashboardAndLogs("logs");                    // enables /dashboard and /logs
HrMetrics.EnableLogging("logs", flushIntervalSeconds: 5);


// new /bpm endpoint with padded values
app.MapGet("/bpm", () =>
{
    if (latest == null)
        return Results.NoContent();

    string bpmPadded = $"{latest.bpm,3}";
    string batteryPadded = $"{(latest.battery ?? 0),3}";


    return Results.Json(new
    {
        bpm = bpmPadded,
        battery = batteryPadded
    });
});

var cts = new CancellationTokenSource();
var bleTask = Task.Run(() => BleWorkerAsync(cts.Token));
app.Lifetime.ApplicationStopping.Register(() => cts.Cancel());

await app.RunAsync();
cts.Cancel();
try { await bleTask; } catch (TaskCanceledException) { }


// safer startup with port retry
try
{
    await app.RunAsync();
}
catch (IOException)
{
    httpPort++;
    Console.WriteLine($"[HTTP] Port in use; retrying on {httpPort} …");
    builder.WebHost.UseUrls($"http://0.0.0.0:{httpPort}");
    var appRetry = builder.Build();
    await appRetry.RunAsync();
}

// -------------- BLE worker --------------
async Task BleWorkerAsync(CancellationToken cancel)
{
    Console.WriteLine("[BLE] Worker starting… token: " + desiredNameToken);

    while (!cancel.IsCancellationRequested)
    {
        try
        {
            if (!await Bluetooth.GetAvailabilityAsync())
            {
                Console.WriteLine("[BLE] Bluetooth unavailable. Retrying in 5s…");
                await Task.Delay(5000, cancel);
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
                        // Initial read
                        var bv = await bc.ReadValueAsync();
                        if (bv is { Length: > 0 })
                        {
                            batteryPct = bv[0];
                            if (latest is not null) latest = latest with { battery = batteryPct };
                            Console.WriteLine($"[BLE] Battery initial: {batteryPct}%");
                        }

                        // Try notifications (best-effort; some devices never fire)
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
                        catch
                        {
                            // ignore (stack may throw if notify unsupported)
                        }

                        // ALWAYS run a gentle poll as a safety net (covers “silent notify” devices)
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
                                catch { /* transient read failures are fine */ }

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

            Console.WriteLine("[BLE] Subscribing to HR notifications…");
            hrm.CharacteristicValueChanged += async (_, e) =>
            {
                if (e.Value is null || e.Value.Length == 0) return;
                var reading = HeartRateParser.Parse(e.Value);
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

                Console.WriteLine($"[BLE] {target.Name}: {reading.Bpm} bpm");
            };

            await hrm.StartNotificationsAsync();

            while (!cancel.IsCancellationRequested && gatt.IsConnected)
                await Task.Delay(500, cancel);

            Console.WriteLine("[BLE] Disconnected.");
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
