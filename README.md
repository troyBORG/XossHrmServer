# <img src="app_icon_64.png" alt="XossHrmServer Icon" width="48" align="center" /> XossHrmServer  

[![.NET 9](https://img.shields.io/badge/.NET-9.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux%20%7C%20macOS-green)](#)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Cross-platform **.NET 9** Bluetooth LE heart-rate monitor server for the [**XOSS X1 Heart Rate Monitor Armband**](https://www.amazon.com/dp/B07H3QN6JC) and compatible BLE HRM devices.  
Streams real-time BPM data via WebSocket and JSON.

---

## 🧩 Features
- Connects automatically to your **XOSS X1** or other Bluetooth LE heart-rate monitors.  
- Outputs live BPM readings to:
  - **WebSocket:** `ws://localhost:5279/ws`
  - **HTTP JSON endpoint:** `http://localhost:5279/latest`
  - **Rolling Stats:** `http://localhost:5279/stats`
  - **Session History:** `http://localhost:5279/history`
  - **HTML Dashboard:** `http://localhost:5279/dashboard`
  - **Log Browser / CSV:** `http://localhost:5279/logs`
- Cross-platform — Windows, Linux, macOS (.NET 9 auto-selects the right target).  
- Shows live console output for debugging and verification.

---

## ⚙️ Requirements
- [.NET 9 SDK](https://dotnet.microsoft.com/download)
- Bluetooth LE adapter supported by your OS  
  (Windows Bluetooth stack or BlueZ on Linux)

---

## 🚀 Run
Clone and run directly:
```bash
git clone https://github.com/troyBORG/XossHrmServer.git
cd XossHrmServer
dotnet run
```

On Windows it automatically targets `net9.0-windows10.0.19041.0`;  
on Linux/macOS it uses `net9.0`.

---

## 🖥️ Example Console Output
When running successfully, you should see something like this:
```bash
dotnet run
Using launch settings from T:\git\XossHrmServer\Properties\launchSettings.json...
Building...
[BLE] Worker starting… token: XOSS
info: Microsoft.Hosting.Lifetime[14]
      Now listening on: http://0.0.0.0:5279
info: Microsoft.Hosting.Lifetime[0]
      Application started. Press Ctrl+C to shut down.
info: Microsoft.Hosting.Lifetime[0]
      Hosting environment: Development
info: Microsoft.Hosting.Lifetime[0]
      Content root path: T:\git\XossHrmServer
[BLE] Scanning for devices…
[BLE] Connecting to XOSS_HRM_0376102 (D6804BE1E7A1) …
[BLE] Subscribing to HR notifications…
[BLE] XOSS_HRM_0376102: 73 bpm
[BLE] XOSS_HRM_0376102: 69 bpm
[BLE] XOSS_HRM_0376102: 78 bpm
[BLE] XOSS_HRM_0376102: 80 bpm
[BLE] XOSS_HRM_0376102: 79 bpm
[BLE] XOSS_HRM_0376102: 78 bpm
```

---

## 🌐 API Examples

### 🔹 Latest Reading
```
http://localhost:5279/latest
```
```json
{
  "device": "XOSS_HRM_0376102",
  "bpm": 79,
  "ts": "2025-10-06T18:19:26.3176012+00:00",
  "battery": 100,
  "rr": [],
  "energy": null
}
```

### 🔹 Rolling Stats
```
http://localhost:5279/stats
```
```json
{
  "from":"2025-10-06T18:20:01.120Z",
  "to":"2025-10-06T18:21:01.987Z",
  "count":58,
  "bpmAvg":77.41,
  "bpmMin":69,
  "bpmMax":83,
  "stdDev":3.92,
  "ratePerSec":0.012,
  "ratePer5Sec":-0.066,
  "zScore":-0.51,
  "rmssd":null,
  "sdnn":null
}
```

### 🔹 Session History
```
http://localhost:5279/history
```
Returns the full set of per-minute aggregated readings in JSON.

### 🔹 Dashboard View
```
http://localhost:5279/dashboard
```
Interactive HTML chart displaying live and historical BPM data.

### 🔹 Logs Directory
```
http://localhost:5279/logs
```
Lists all saved CSV sessions (when logging is enabled).

---

## 🧠 Environment Variables
| Variable | Default | Description |
|-----------|----------|-------------|
| `HRM_DEVICE_NAME` | `XOSS` | Name token to match your HRM device |
| `PORT` | `5279` | HTTP / WebSocket server port |

---

## 🔧 Notes
- Uses [InTheHand.BluetoothLE](https://www.nuget.org/packages/InTheHand.BluetoothLE) for cross-platform BLE support.  
- `/latest` returns **204 No Content** until the first BPM packet is received.  
- `/ws` provides a live WebSocket telemetry stream.  
- `/stats`, `/history`, and `/dashboard` provide rolling analytics and CSV logging.

---

## 📄 License
MIT License © 2025 troyBORG
