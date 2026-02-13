# Shot Clipper - Professional Video Capture & Golf Mode Application

## Overview
A Windows desktop application for capturing SDI/NDI/SRT video from Blackmagic capture cards and network sources, with recording, live clipping, cloud upload, MJPEG streaming, and a golf-specific auto-cut switcher with swing detection.

## Technology Stack
- **.NET 8 / WPF** — MVVM with CommunityToolkit.Mvvm, Microsoft.Extensions.Hosting DI
- **Blackmagic Desktop Video SDK 15.3** — SDI capture via DeckLink/UltraStudio (COM interop + native DLL)
- **NDI / SRT** — Network video input support
- **FFMpegCore** — Hardware-accelerated H.264/H.265 encoding, clip extraction, overlay compositing
- **SQLite + Dapper** — Local persistence for schedules, golfers, overlays, upload queue
- **MJPEG over WebSocket** — Low-latency streaming to browsers on local network

---

## Solution Structure

```
ShotClipper/
├── src/
│   ├── Screener.Abstractions/           # Core interfaces and DTOs
│   ├── Screener.Core/                   # Settings, persistence, output manager
│   ├── Screener.Capture.Blackmagic/     # DeckLink SDK capture (COM + native DLL)
│   ├── Screener.Capture.Ndi/            # NDI input capture
│   ├── Screener.Capture.Srt/            # SRT input capture
│   ├── Screener.Encoding/               # FFmpeg encoding pipelines, HW acceleration
│   ├── Screener.Recording/              # Multi-input recording orchestration
│   ├── Screener.Preview/                # Audio preview service (WASAPI)
│   ├── Screener.Clipping/               # Live clip marking and FFmpeg extraction
│   ├── Screener.Timecode/               # SMPTE 12M timecode (NTP, system, manual)
│   ├── Screener.Upload/                 # Cloud upload (S3, Azure, GCS, Dropbox, etc.)
│   ├── Screener.Streaming/              # MJPEG-over-WebSocket streaming
│   ├── Screener.Scheduling/             # Quartz.NET scheduled recordings
│   ├── Screener.Golf/                   # Golf mode: swing detection, auto-cut, overlays
│   └── Screener.UI/                     # WPF Application (MVVM)
├── tests/
│   └── Screener.Golf.Tests/             # xUnit + Moq test suite (112 tests)
├── tools/                               # Utility scripts
└── web/                                 # CloudPanel web management
```

---

## UI Layout (vMix-style Switcher)

The main window uses a broadcast-style layout:

```
+------------------------------------------------------------------------+
|  Menu Bar                                                              |
+------------------------------------------------------------------------+
|  Toolbar (presets, drives, stream, golf mode toggle)                   |
+------------------------------------------------------------------------+
| Left  |                  CENTER AREA                           | Right |
| Panel |                                                        | Panel |
|       | +--PREVIEW--+  Transition  +--PROGRAM--+               |       |
|       | |           |  Controls    |           |               |       |
|       | | (Next/    | [ CUT  ]    | (Live     |               |       |
|       | |  Preview) | [ DISS ]    |  Output)  |               |       |
|       | |           | [ DIP  ]    |           |               |       |
|       | | Green     | Duration    | Red       |               |       |
|       | | border    | [ AUTO ]    | border    |               |       |
|       | |           | T-bar       |           |               |       |
|       | |           | [ KEY  ]    |           |               |       |
|       | +-----------+             +-----------+               |       |
|       | +-INPUT SOURCES (scrollable thumbnail strip)--------+ |       |
|       | | [Src1] [Src2] [Src3] [Src4] ...                   | |       |
|       | +---------------------------------------------------+ |       |
|       | Status bar: signal info | golf state | swings         |       |
+------------------------------------------------------------------------+
| Status Bar                                                             |
+------------------------------------------------------------------------+
```

- **Left panel**: Clip bin, upload queue
- **Right panel**: Recording controls, schedule, inputs, golf mode, timecode, audio meters, drive status
- **Center**: Preview monitor (left, green tally) | Transition bar (vertical) | Program monitor (right, red tally), input source strip, status bar

---

## Golf Mode

Automated camera switching for golf simulator setups with two sources (golfer camera + simulator output):

- **Swing Detection** — Frame-to-frame SAD spike detection with EMA baseline
- **Reset Detection** — Idle reference comparison for landing detection
- **Auto-Cut** — State machine: Waiting → Swing → Following → Landing → Cooldown
- **Sequence Recording** — Captures swing in/out points for automatic clip export
- **Overlay Compositing** — Logo bugs + lower third text via FFmpeg filter_complex
- **Session Management** — Per-golfer profiles, swing counters, auto-upload

---

## Key Features

- **Multi-input capture** — SDI (Blackmagic), NDI, SRT simultaneously
- **Live recording** — Multi-input with quality presets (Proxy/Medium/High/Master)
- **Live clipping** — Mark in/out points during recording, extract clips with FFmpeg
- **Transition engine** — Cut, dissolve, dip-to-black with T-bar manual control
- **MJPEG streaming** — Low-latency browser preview with QR code access
- **Cloud upload** — S3, Azure, GCS, Dropbox, Google Drive, Frame.io, FTP/SFTP
- **Scheduled recording** — Quartz.NET with conflict detection
- **NDI/SRT output** — Send program output over network
- **Timecode** — NTP-synced SMPTE 12M with timezone support

---

## Keyboard Shortcuts

| Key | Action |
|-----|--------|
| F9 | Start/Stop Recording |
| Shift+F9 | Pause/Resume |
| M | Add Marker |
| I / O | Set In/Out Points |
| F11 | Fullscreen Preview |
| 1 / 2 | Cut to Source 1/2 (Golf Mode) |
| Ctrl+, | Settings |

---

## Build & Test

```bash
dotnet build
dotnet test tests/Screener.Golf.Tests
```

Requires .NET 8 SDK and Windows 10/11. Blackmagic Desktop Video drivers required for SDI capture.
