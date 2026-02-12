# Screener - Professional Video Capture Application for Windows

## Overview
A Windows application for capturing SDI video from Blackmagic capture cards with recording, live clipping, cloud upload, and WebRTC streaming capabilities.

## Technology Stack
- **.NET 8 / WPF with C#** - Native Windows performance, excellent Blackmagic SDK support
- **Blackmagic Desktop Video SDK** - SDI capture from all DeckLink/UltraStudio devices
- **FFMpegCore** - Hardware-accelerated H.264/H.265 encoding
- **SIPSorcery** - WebRTC streaming for local network preview
- **SQLite + Dapper** - Local persistence for schedules and upload queue

---

## Solution Structure

```
Screener/
├── src/
│   ├── Screener.Abstractions/       # Core interfaces and DTOs
│   ├── Screener.Core/               # Buffers, threading, shared utilities
│   ├── Screener.Capture.Blackmagic/ # DeckLink SDK integration
│   ├── Screener.Encoding/           # FFmpeg encoding pipeline
│   ├── Screener.Recording/          # Recording session management
│   ├── Screener.Preview/            # D3D11 video preview for WPF
│   ├── Screener.Clipping/           # Live clip marking and extraction
│   ├── Screener.Timecode/           # NTP sync, timezone, manual override
│   ├── Screener.Upload/             # Cloud storage providers and queue
│   ├── Screener.Streaming/          # WebRTC local streaming
│   ├── Screener.Scheduling/         # Calendar-based recording scheduler
│   └── Screener.UI/                 # WPF Application (MVVM)
├── tests/
└── tools/
    └── ffmpeg/                      # FFmpeg binaries
```

---

## Implementation Phases

### Phase 1: Project Setup & Core Infrastructure
1. Create .NET 8 solution with project structure
2. Set up dependency injection with `Microsoft.Extensions.Hosting`
3. Implement core interfaces in `Screener.Abstractions`:
   - `ICaptureDevice`, `IDeviceManager`
   - `IEncodingPipeline`, `EncodingPreset`
   - `IRecordingService`, `RecordingSession`
   - `ITimecodeProvider`, `Timecode` struct
4. Implement thread-safe frame ring buffer and memory pooling
5. Configure Serilog logging

### Phase 2: Blackmagic Capture Integration
1. Reference DeckLink SDK COM type library
2. Implement `DeckLinkDeviceManager` - device discovery and hot-plug
3. Implement `DeckLinkCaptureDevice` with `IDeckLinkInputCallback`
4. Implement `VideoFramePool` for zero-copy frame handling
5. Support all video modes (SD/HD/4K, various frame rates)

### Phase 3: Video Preview
1. Implement `D3DPreviewRenderer` with DirectX 11 surface sharing
2. Create WPF `VideoPreviewControl` using `D3DImage`
3. Add overlay rendering (timecode, recording status, safe areas)
4. Implement audio preview with WASAPI

### Phase 4: Recording & Encoding
1. Implement `EncodingPipeline` with FFMpegCore
2. Hardware encoder detection (NVENC/QSV/AMF) with fallback
3. Quality presets: Proxy (720p), Medium (1080p), High, Master
4. Fragmented MP4 output for live clipping support
5. Implement `RecordingService` with start/stop/pause
6. Implement `FilenameGenerator` with template variables
7. Implement `DriveManager` for disk space monitoring

### Phase 5: Timecode System
1. Implement `NtpTimeProvider` with GuerrillaNtp
2. Implement `ManualTimeProvider` for user override
3. Implement `TimezoneService` with NodaTime
4. SMPTE timecode generation with drop-frame support

### Phase 6: Live Clipping
1. Implement `ClipMarkerService` for in/out point tracking
2. Implement `ClipExtractor` using FFmpeg stream copy
3. Background clip extraction queue
4. Clip metadata storage

### Phase 7: Recording Scheduler
1. Implement schedule database with SQLite
2. Integrate Quartz.NET for job scheduling
3. Recurring schedule support (daily/weekly/custom)
4. Pre-start buffer and conflict detection

### Phase 8: Cloud Upload System
1. Implement `ICloudStorageProvider` interface with resumable uploads
2. Implement providers:
   - AWS S3 (`AWSSDK.S3`)
   - Azure Blob (`Azure.Storage.Blobs`)
   - Google Cloud Storage (`Google.Cloud.Storage.V1`)
   - Dropbox (`Dropbox.Api`)
   - Google Drive (`Google.Apis.Drive.v3`)
   - Frame.io (REST API)
   - Generic S3-compatible (MinIO, Backblaze B2)
   - FTP/SFTP (`SSH.NET`, `FluentFTP`)
3. Implement `UploadQueueManager` as background service
4. SQLite persistence for queue state
5. Credential storage with Windows DPAPI

### Phase 9: WebRTC Streaming
1. Implement `WebRtcStreamServer` with SIPSorcery
2. WebSocket signaling server
3. VP8 video encoding for streaming
4. Embedded HTML viewer page
5. QR code generation for easy mobile access
6. Multi-viewer support (configurable limit)
7. Optional access token authentication

### Phase 10: WPF User Interface
1. **Main Window Layout:**
   - Left panel: Clip bin, upload queue (collapsible)
   - Center: Video preview with timecode overlay
   - Right panel: Recording controls, audio meters, drive status
   - Toolbar: Quick actions, preset selector, drive dropdown
   - Status bar: Input status, frame rate, disk space

2. **Dialogs:**
   - Settings (tabbed: General, Video, Audio, Recording, Timecode, Streaming, Cloud, Shortcuts)
   - Filename Template Editor
   - Recording Scheduler (calendar view)
   - Manual Timecode Entry
   - Cloud Storage Configuration
   - WebRTC Stream Settings

3. **Custom Controls:**
   - `TimecodeDisplay` - Large 7-segment style
   - `AudioMeterControl` - VU/PPM meters with peak hold
   - `DriveSpaceIndicator` - Ring/bar chart
   - `RecordButton` - Animated states

4. **MVVM Architecture:**
   - CommunityToolkit.Mvvm for source generators
   - ViewModels in separate assembly
   - Event aggregator for cross-component communication

5. **Styling:**
   - Custom dark theme (broadcast industry standard)
   - Accent colors: Red (recording), Green (ready), Blue (selection)

6. **Keyboard Shortcuts:**
   - F9: Start/Stop Recording
   - M: Add Marker
   - I/O: Set In/Out Points
   - F11: Fullscreen Preview
   - Ctrl+,: Settings

---

## Key NuGet Packages

| Category | Package | Purpose |
|----------|---------|---------|
| Framework | Microsoft.Extensions.Hosting | DI, hosted services |
| MVVM | CommunityToolkit.Mvvm | Source-generated MVVM |
| Video | FFMpegCore | FFmpeg wrapper |
| Video | SharpDX.Direct3D11 | DirectX preview |
| WebRTC | SIPSorcery | WebRTC streaming |
| Time | NodaTime | Timezone handling |
| Time | GuerrillaNtp | NTP synchronization |
| Scheduler | Quartz | Job scheduling |
| Cloud | AWSSDK.S3 | AWS S3 uploads |
| Cloud | Azure.Storage.Blobs | Azure uploads |
| Cloud | Google.Cloud.Storage.V1 | GCS uploads |
| Cloud | Dropbox.Api | Dropbox uploads |
| Database | Microsoft.Data.Sqlite | Local persistence |
| Database | Dapper | Data access |
| Resilience | Polly | Retry policies |
| QR Code | QRCoder | Stream URL QR codes |

---

## Critical Technical Considerations

1. **Zero-Copy Frame Pipeline**: Use pooled memory with `Memory<T>` to avoid GC pressure at 4K60
2. **Thread Safety**: Lock-free ring buffer with reference counting for frame distribution
3. **Live Clipping**: Fragmented MP4 (fMP4) format enables reading while recording
4. **Hardware Encoding**: Probe for NVENC/QSV/AMF at startup with software fallback
5. **WPF D3DImage**: DirectX 11 surface sharing for smooth preview
6. **Resumable Uploads**: Multipart uploads with persistent session state for reliability

---

## Verification Plan

1. **Capture Test**: Connect Blackmagic card, verify device detection and video display
2. **Recording Test**: Record 1-minute clip, verify MP4 playback and quality
3. **Clipping Test**: Mark in/out during recording, extract clip, verify output
4. **Timecode Test**: Verify NTP sync, timezone changes, manual override
5. **Schedule Test**: Create scheduled recording, verify automatic start/stop
6. **Upload Test**: Configure S3 bucket, upload file, verify resumable on disconnect
7. **Stream Test**: Start WebRTC stream, connect from browser on same network
8. **Full Workflow**: End-to-end test of capture → record → clip → upload

---

## Files to Create (Initial Implementation)

1. `/src/Screener.Abstractions/Capture/ICaptureDevice.cs`
2. `/src/Screener.Abstractions/Encoding/IEncodingPipeline.cs`
3. `/src/Screener.Abstractions/Recording/IRecordingService.cs`
4. `/src/Screener.Capture.Blackmagic/DeckLinkCaptureDevice.cs`
5. `/src/Screener.Encoding/Pipelines/EncodingPipeline.cs`
6. `/src/Screener.Preview/D3DPreviewRenderer.cs`
7. `/src/Screener.Clipping/ClipExtractor.cs`
8. `/src/Screener.Upload/UploadQueueManager.cs`
9. `/src/Screener.Streaming/WebRtcStreamServer.cs`
10. `/src/Screener.UI/Views/MainWindow.xaml`
11. `/src/Screener.UI/ViewModels/MainViewModel.cs`
