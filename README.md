# JacksonTechWorks Network Scanner

A portable, self-contained network scanner and diagnostics toolkit for Windows, by **JacksonTechWorks**.

There's nothing to install. Download one `.exe`, run it from anywhere (Desktop, USB stick, tools share), and it updates itself from this repository's releases.

[![Build & Release](https://github.com/jacksontechworks-uk/networkscannerapp/actions/workflows/build.yml/badge.svg)](https://github.com/jacksontechworks-uk/networkscannerapp/actions/workflows/build.yml)

---

## Download

Get the latest release from the **[Releases page](https://github.com/jacksontechworks-uk/networkscannerapp/releases/latest)**:

1. Download `JTW-NetworkScanner-win-x64.zip`
2. Extract it to a folder you own (e.g. `Documents\Tools\NetworkScanner`)
3. Run `JTW-NetworkScanner.exe`

**Requirements:** Windows 10 / 11, 64-bit. No .NET install is needed because the runtime is bundled. The world-map view uses the Microsoft Edge WebView2 Runtime, which comes with Windows 11 and most Windows 10 PCs.

> Windows SmartScreen may warn you the first time you run the app because it isn't code-signed yet. Click **More info → Run anyway**.

Settings and logs are saved in a `JTW-Data` folder next to the exe, so the app stays fully portable. If that folder can't be written to, the app uses `%LOCALAPPDATA%\JacksonTechWorks\NetworkScanner` instead.

---

## Current release: v1.0.0

### ✅ What's working in this release

**Scanning**
- Detects your local subnet automatically from the active network adapter. A picker lets you choose between adapters.
- Can also scan any other subnet or range: CIDR (`10.0.0.0/24`), a range (`192.168.1.10-192.168.1.50` or `192.168.1.10-50`) or a single IP. Ranges can be /16 to /32, up to 65,536 addresses.
- Finds hosts with ICMP ping, plus **ARP on the local segment**, so it also finds devices that block ping. Off-subnet hosts that block ICMP are checked with a TCP probe.
- Shows the **total of hosts alive and not alive**, with live counters while a scan runs.
- **TCP port scan** of alive hosts. The default list has about 50 common ports and can be edited, including ranges like `8000-8100`.
- Reverse-DNS hostnames.
- **MAC vendor detection** from the bundled IEEE OUI database (~40k manufacturers). It works offline and flags randomised/private MACs.
- Marks this PC and the default gateway.

**Filtering**
- Show **All / Online / Offline**.
- **Filter by port**, e.g. `22,443` shows only hosts with SSH or HTTPS open.
- Free-text search across IP, hostname, MAC, vendor and ports.

**Right-click actions per host** (built from the ports that were found)
- HTTP/HTTPS ports: open in your default browser.
- Port 22: **SSH session in Windows Terminal** (or a console window), with an optional username.
- RDP: launches Remote Desktop (`mstsc`).
- SMB/NetBIOS: opens the host's shares in Explorer.
- FTP, Telnet and VNC handlers.
- Any other open port: try it in the browser.
- Live ping, traceroute, pathping, speed test, Wake-on-LAN, rescan the host, copy IP/MAC/row.
- Double-clicking a host opens its best service (HTTPS → HTTP → RDP → SSH → SMB). If none is found, it starts a live ping.

**Diagnostic modules.** Each opens in its own window and the main window stays in view. If you close a module while it's running, it **keeps running in the background**, and you can bring it back from **Running modules** in the main window.
- **Live Ping**: continuous ping with a live latency graph, sent/received/loss, min/avg/max and jitter.
  - **End-to-end route mapping**: traces the path, then pings **every hop live** (like WinMTR), so you can see where latency or loss starts.
  - **World map** for external targets: hops are geolocated and drawn as a live route on a map.
- **Traceroute** to a found host or any custom IP/hostname. Public hops are geolocated.
- **PathPing**: a traceroute, then N probes to each hop to get per-hop loss and latency statistics.
- **LAN Speed Test**: a built-in iperf-style throughput test with no iperf needed. Run *Server* on one PC and *Client* on another to measure upload and download in Mbps, with a live graph and result history.
- **Wake-on-LAN**: send magic packets from the module or by right-clicking any host with a known MAC.

**Export**
- Scan results, Live Ping (samples or route table), Traceroute/PathPing, speed test history and the Wake-on-LAN log can all be exported to **PDF** (branded report) or **CSV**.
- Exports respect the current filter.

**Self-update**
- On startup, the app checks this repo's GitHub Releases. When a newer version exists, a banner offers **Update now**.
- The update downloads the release zip, **verifies its SHA-256**, replaces the exe and any other files, and restarts. You can also skip a version or check manually.

### 🚧 Up & coming
- Scan profiles (quick / full / custom) and saved custom port lists
- NetBIOS / mDNS / SSDP name discovery (better names for printers, IoT, Apple devices)
- Service banners and HTTP page titles in the results
- UDP checks (SNMP, DNS, NTP) and SNMP device info
- Scheduled and repeat scans, with alerts when new devices appear
- Device history: remember known devices, nicknames and notes
- Live Ping alerts (loss or latency thresholds) and multi-target ping
- Speed test: parallel streams, UDP jitter/loss mode, and latency under load
- Wake-on-LAN address book
- IPv6 support
- Settings window (timeouts, concurrency, theme)
- Code-signed releases

---

## Building from source

The app is C# / .NET 8 / WPF. You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) on Windows.

```powershell
git clone https://github.com/jacksontechworks-uk/networkscannerapp
cd networkscannerapp
dotnet run --project src/JTW.NetworkScanner

# Portable single-file build (same as the release):
dotnet publish src/JTW.NetworkScanner -c Release -r win-x64 --self-contained true -o publish
```

### Project layout

```
src/JTW.NetworkScanner/
  Core/        Scanning engine & services (no UI)
    NetworkScanner.cs   ping + ARP + port sweep
    NetUtil.cs          adapters, subnets, ARP, DNS
    OuiDatabase.cs      MAC vendor lookup
    Diagnostics.cs      traceroute, pathping, Wake-on-LAN, geo-IP
    SpeedTest.cs        built-in LAN throughput test (client + server)
    Updater.cs          GitHub Releases self-update
    Exporter.cs         CSV + dependency-free PDF writer
    LaunchActions.cs    browser / SSH / RDP / SMB launchers
  Modules/     One window per diagnostic module (ModuleWindowBase + ModuleManager)
  Themes/      JacksonTechWorks colours & control styles (Theme.xaml)
  MainWindow.* Scanner UI
```

### Releasing a new version

1. Bump `<Version>` in `src/JTW.NetworkScanner/JTW.NetworkScanner.csproj`.
2. Add `release-notes/vX.Y.Z.md` and update the **Current release** section of this README.
3. Tag the commit and push it: `git tag vX.Y.Z && git push origin vX.Y.Z`

GitHub Actions builds the self-contained exe on Windows and publishes the release with `JTW-NetworkScanner-win-x64.zip` and its `.sha256`. Running copies pick it up automatically.

---

## Release history

| Version | Date | Highlights |
|---|---|---|
| v1.0.0 | 2026-11-25 | First release: subnet scan, ARP, ports, vendors, filters, right-click launchers, Live Ping + route map, Traceroute, PathPing, LAN speed test, Wake-on-LAN, PDF/CSV export, self-update |

## Credits

- MAC vendor data: IEEE OUI registry (via the `oui-data` package)
- Maps: [Leaflet](https://leafletjs.com), map data © OpenStreetMap contributors, tiles © CARTO
- Geo-IP: [ipwho.is](https://ipwho.is)

Only use this tool on networks you own or are authorised to test.

© JacksonTechWorks
