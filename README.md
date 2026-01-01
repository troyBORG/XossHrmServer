# <img src="app_icon_64.png" alt="XossHrmServer Icon" width="48" align="center" /> XossHrmServer  

[![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/platform-Windows%20%7C%20Linux-green)](#)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

Cross-platform **.NET 10** Bluetooth LE heart-rate monitor server for the [**XOSS X1 Heart Rate Monitor Armband**](https://www.amazon.com/dp/B07H3QN6JC) and compatible BLE HRM devices.  
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
- Cross-platform — Windows and Linux (.NET 10 auto-selects the right target).
- Automatic port retry if the default port is in use.  
- Shows live console output for debugging and verification.

---

## ⚙️ Requirements
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
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

On Windows it automatically targets `net10.0-windows10.0.19041.0`;  
on Linux it uses `net10.0`.

---

## 🖥️ Example Console Output
When running successfully, you should see something like this:
```bash
$ dotnet run
[BLE] Starting BLE worker on Microsoft Windows 11...
[BLE] Worker starting on Microsoft Windows 11… token: XOSS
[LOG] Writing HRM data to logs/session_2025-12-03_17-54-30.csv
[BLE] Scanning for devices…
[HTTP] Server running on http://0.0.0.0:5279
[BLE] Connecting to XOSS_HRM_0376102 (D6:80:4B:E1:E7:A1) …
[BLE] Battery initial: 57%
[BLE] Subscribing to HR notifications…
[BLE] XOSS_HRM_0376102: 73 bpm
[BLE] XOSS_HRM_0376102: 69 bpm
[BLE] XOSS_HRM_0376102: 78 bpm
```

On Linux:
```bash
$ dotnet run -f net10.0
[BLE] Starting BLE worker on CachyOS...
[BLE] Worker starting on CachyOS… token: XOSS
[LOG] Writing HRM data to logs/session_2025-12-03_17-54-30.csv
[BLE] Scanning for devices…
[HTTP] Server running on http://0.0.0.0:5279
[BLE] Connecting to XOSS_HRM_0376102 (D6:80:4B:E1:E7:A1) …
[BLE] Battery initial: 57%
[BLE] Subscribing to HR notifications…
[BLE] XOSS_HRM_0376102: 81 bpm
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
| `PORT` | `5279` | HTTP / WebSocket server port (auto-retries if in use) |
| `DOTNET_DISABLE_BLE` | `false` | Set to `true` to run in HTTP-only mode |
| `ALLOW_ZERO_BPM` | `false` | Set to `true` to allow 0 BPM readings |

---

## 🔧 Notes
- Uses [InTheHand.BluetoothLE](https://www.nuget.org/packages/InTheHand.BluetoothLE) for cross-platform BLE support.
  - **Windows:** Uses WinRT Bluetooth APIs
  - **Linux:** Uses BlueZ via D-Bus (ensure BlueZ is installed: `sudo pacman -S bluez bluez-utils` or `sudo apt install bluez`)
- `/latest` returns **204 No Content** until the first BPM packet is received.  
- `/ws` provides a live WebSocket telemetry stream.  
- `/stats`, `/history`, and `/dashboard` provide rolling analytics and CSV logging.
- Session data is automatically logged to CSV files in the `logs/` directory.

---

## 🐛 Troubleshooting

### Device Not Found / Connection Issues

**BLE devices can only connect to one client at a time.** If your device is connected to system Bluetooth (KDE, Windows Settings, etc.), disconnect it first:

- **Linux (KDE/GNOME):** Disconnect the device in Bluetooth settings, or use `bluetoothctl`:
  ```bash
  bluetoothctl
  disconnect <device_mac>
  trust <device_mac>  # Optional: mark device as trusted
  ```

- **Windows:** Disconnect the device in Settings → Bluetooth & devices

- **If scanning still times out:**
  - Restart Bluetooth service: `sudo systemctl restart bluetooth` (Linux)
  - Turn Bluetooth off and on again in system settings
  - Turn the HRM device OFF and ON to make it re-announce/advertise
  - Check Bluetooth logs: `journalctl -u bluetooth | tail -20` (Linux)
  - If `bluetoothctl` can see the device but the app can't, this may be a D-Bus permissions issue with the InTheHand.BluetoothLE library
  - Reboot if issues persist

**Known Issue:** On some Linux systems, the InTheHand.BluetoothLE library's scan may timeout even when `bluetoothctl` can see the device. This appears to be a library/D-Bus compatibility issue. The app will continue retrying, but you may need to restart the Bluetooth service or reboot.

The app includes detailed diagnostics and will show what devices are found during scanning to help troubleshoot connection issues.

---

## 📄 License
MIT License © 2025 troyBORG
