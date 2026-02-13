# Screener — Video Capture & Golf Mode Application

## Project Structure
- `src/Screener.UI` — WPF desktop app (MVVM with CommunityToolkit.Mvvm, Microsoft.Extensions.Hosting DI)
- `src/Screener.Abstractions` — Interfaces and shared types (IClippingService, IRecordingService, etc.)
- `src/Screener.Capture.Blackmagic` — DeckLink SDK capture (COM interop, native DLL)
- `src/Screener.Capture.Ndi` — NDI input capture
- `src/Screener.Capture.Srt` — SRT input capture
- `src/Screener.Core` — Shared services (settings, persistence, output)
- `src/Screener.Clipping` — Clip marking and extraction (FFmpeg-based)
- `src/Screener.Encoding` — FFmpeg encoding pipelines and hardware acceleration
- `src/Screener.Recording` — Multi-input recording orchestration
- `src/Screener.Streaming` — MJPEG-over-WebSocket streaming
- `src/Screener.Timecode` — SMPTE 12M timecode (NTP, system, manual providers)
- `src/Screener.Scheduling` — Quartz.NET scheduled recordings
- `src/Screener.Upload` — Cloud upload (S3, Azure, GCS, Dropbox, Google Drive, Frame.io, FTP/SFTP)
- `src/Screener.Preview` — Audio preview service
- `src/Screener.Golf` — Golf mode: swing detection, auto-cut, sequence recording, overlays, clip export
- `tests/Screener.Golf.Tests` — xUnit + Moq test suite for Golf module

## Key Patterns
- **MVVM**: `ObservableObject` base, `[ObservableProperty]`, `[RelayCommand]` source generators (CommunityToolkit.Mvvm 8.2.2)
- **DI**: `Microsoft.Extensions.Hosting` with `ConfigureServices` in `App.xaml.cs`
- **Database**: Dapper + SQLite (`DatabaseContext`, repository pattern)
- **Central Package Management**: `Directory.Packages.props` for all NuGet versions
- **Target Framework**: `net8.0-windows10.0.17763`
- **Thread Safety**: `Application.Current.Dispatcher.Invoke()` for UI updates from background threads
- **Dark Theme**: Resource dictionaries (BackgroundLevel0-3, AccentBlueBrush, BorderSubtleBrush, etc.)

## Switcher UI Layout (vMix-style)
The center column uses a broadcast-style layout inspired by vMix:
- **Preview monitor** (left) — shows whichever source is NOT on program, green tally border. Binds to `MainViewModel.PreviewSourceImage`.
- **Transition controls** (center) — vertical `TransitionBar` control (90px wide): CUT/DISS/DIP buttons, duration, AUTO, vertical T-bar slider, KEY toggle. DataContext = `SwitcherViewModel`.
- **Program monitor** (right) — shows the live transition engine output via `ProgramMonitorControl`, red tally border. Binds to `SwitcherViewModel.ProgramImage`.
- **Input source strip** (bottom) — scrollable horizontal thumbnails of all `EnabledInputs`. Each card shows preview image, source name, PGM badge. Click selects input.
- **Status bar** — input status, auto-cut state, swing counter, session status, CUT buttons (golf mode).
- `UpdatePreviewSource()` in `MainViewModel` swaps preview based on `ActiveSourceIndex`: when source 0 is program, preview shows source 1 and vice versa.

## Golf Mode Architecture
- **SwitcherService** — Tracks active program source, fires `ProgramSourceChanged` on cuts
- **AutoCutService** — State machine: WaitingForSwing → SwingDetected → FollowingShot → ResetDetected → Cooldown
- **SwingDetector** — Frame-to-frame SAD (Sum of Absolute Differences) spike detection against EMA baseline
- **ResetDetector** — Idle reference comparison with consecutive idle frame counting
- **FrameAnalyzer** — Pure static utility: ExtractLumaDownsampled, ComputeSadInRoi, ComputeSimilarity
- **SequenceRecorder** — Listens to `ProgramSourceChanged` for swing in/out points, fires `SequenceCompleted`
- **ClipExportService** — Orchestrates SequenceRecorder → IClippingService → OverlayCompositor for auto-export
- **OverlayCompositor** — FFmpeg filter_complex for logo bugs + lower third text overlays
- **GolferRepository / SessionRepository / OverlayRepository** — Dapper/SQLite persistence

## Clipping Pipeline
- **IClippingService** — Interface for clip marking (SetInPoint, SetOutPoint, AddChapterMarker) and extraction
- **ClippingService** — FFmpeg-based implementation; `SetActiveRecording(path)` → `CreateClipAsync` → `ExtractClipAsync`
- **MainViewModel** wires clipping: sets active recording on recording start, clears markers on stop, handles AddMarker/SetInPoint/SetOutPoint commands
- **ClipBin** — `LiveMarkers` (ObservableCollection) displays markers during recording; `Clips` shows completed clips

## Testing
- **Framework**: xUnit 2.7, Moq 4.20, Microsoft.NET.Test.Sdk 17.9
- **Test project**: `tests/Screener.Golf.Tests/Screener.Golf.Tests.csproj`
- **Run tests**: `dotnet test tests/Screener.Golf.Tests`

# Video Capture Issues - Debug Notes

## Current Status (Latest)
- **Root cause found**: `GetBytes` was **removed** from `IDeckLinkVideoFrame` in SDK 14.3
- Native DLL now tries 3 paths in order: IDeckLinkVideoBuffer (correct GUID), legacy v14.2.1, offset-280 fallback
- C# interop fixed: removed phantom `GetBytes` from `IDeckLinkVideoInputFrame`, vtable alignment corrected
- VANC rows auto-detected at **52 rows** (was hardcoded at 84, causing 32 rows of video to be clipped)
- HANC auto-detected at **0 bytes** (no horizontal ancillary data in the DMA buffer)
- Conversion takes ~1.1-1.5ms per frame at half resolution (960x540)

## Architecture Overview

### Frame Data Pipeline
1. DeckLink hardware delivers frames via `IDeckLinkInputCallback.VideoInputFrameArrived`
2. Native DLL (`Screener.Capture.Blackmagic.Native.dll`) copies DMA buffer using SEH-protected memcpy
3. Ring buffer (3 slots) stores raw frames immediately to avoid DMA recycling
4. Pooled event buffers (3 slots) pass frame data to UI via `VideoFrameReceived` event
5. `VideoPreviewViewModel` converts UYVY to BGRA at half-res on thread pool
6. `CompositionTarget.Rendering` handler writes converted frame to `WriteableBitmap` at vsync

### Native DLL Frame Access (DeckLinkFrameHelper.cpp)
Three paths attempted in order:
1. **IDeckLinkVideoBuffer** (QI with GUID `{CCB4B64A-...}`) → `GetBytes` at vtable[3] — **preferred path**
2. **Legacy IDeckLinkVideoInputFrame_v14_2_1** (QI with GUID `{05CFE374-...}`) → `GetBytes` at vtable[8] — **FFmpeg approach**
3. **Offset 280 fallback** (raw pointer dereference) — **last resort**, returns DMA buffer including VANC rows

#### SDK 15.3 IDeckLinkVideoFrame vtable (GetBytes REMOVED in 14.3)
| Index | Method | Notes |
|-------|--------|-------|
| 0-2 | IUnknown | Standard COM |
| 3 | GetWidth | Works |
| 4 | GetHeight | Works |
| 5 | GetRowBytes | Works |
| 6 | GetPixelFormat | Works |
| 7 | GetFlags | Works |
| 8 | **GetTimecode** | Was GetBytes in SDK ≤14.2.1! |
| 9 | GetAncillaryData | Shifted up by 1 |

#### Previous bugs (now fixed)
- `IDeckLinkVideoBuffer` GUID was wrong (`{D3917C07-...}` → correct: `{CCB4B64A-...}`) — caused E_NOINTERFACE
- `IDeckLinkVideoFrame` GUID was the old v14.2.1 GUID (`{3F716FE0-...}` → correct: `{6502091C-...}`)
- `IDeckLinkVideoBuffer` vtable order was wrong: had StartAccess/EndAccess/GetBytes, actual is GetBytes/StartAccess/EndAccess
- C# `IDeckLinkVideoInputFrame` had phantom `GetBytes` method, misaligning all subsequent vtable slots

### Streaming Pipeline
1. `InputPreviewRenderer.OnVideoFrameReceived` pushes raw UYVY frames every 4th callback (~15fps) to `IStreamingService.PushFrameAsync`
2. Only the **selected input** streams (single-source, matches audio routing)
3. `WebRtcStreamingService.PushFrameAsync` converts UYVY→RGB (BT.601) at configured resolution, encodes to JPEG (quality 60)
4. JPEG bytes sent as binary WebSocket messages to all connected viewers
5. Browser viewer page renders frames via `<img>` + `URL.createObjectURL(blob)`

#### Streaming Architecture
- **Transport**: MJPEG over WebSocket (not full WebRTC — SIPSorcery deps exist but peer connections are stubbed)
- **Server**: `HttpListener` with 3-tier binding: `http://+:port/` → `http://{LAN_IP}:port/` → `http://localhost:port/`
- **Endpoints**: `/stream` (viewer HTML), `/ws` (WebSocket for frames + signaling), `/qr` (QR code PNG)
- **Settings**: 7 keys persisted via `SettingsRepository` (`streaming.enabled`, `.port`, `.maxViewers`, `.requireToken`, `.accessToken`, `.resolution`, `.bitrate`)
- **UI wiring**: `MainViewModel.OnIsStreamingChanged` partial method handles both ToggleButton two-way binding and menu command; uses `_streamingChanging` guard to prevent re-entrancy

#### Key Streaming Files
- `src/Screener.Streaming/WebRtcStreamingService.cs` - HTTP/WebSocket server, UYVY→JPEG encoding, frame delivery
- `src/Screener.UI/ViewModels/MainViewModel.cs` - streaming commands (ToggleStream, CopyStreamUrl, OpenStreamSettings), lifecycle
- `src/Screener.UI/ViewModels/InputPreviewRenderer.cs` - `SetStreamingService()`, frame push in `OnVideoFrameReceived`
- `src/Screener.Core/Settings/SettingsService.cs` - streaming settings persistence (7 fields in `AppSettings`)
- `src/Screener.UI/ViewModels/SettingsViewModel.cs` - streaming settings UI load/save

### Key Files
- `src/Screener.UI/ViewModels/VideoPreviewViewModel.cs` - preview rendering, YUV conversion, VANC detection
- `src/Screener.Capture.Blackmagic/DeckLinkDeviceManager.cs` - device management, frame capture, validation
- `src/Screener.Capture.Blackmagic.Native/DeckLinkFrameHelper.cpp` - native SEH-protected frame copy
- `src/Screener.Capture.Blackmagic/Interop/DeckLinkAPI.cs` - COM interop definitions

## Frame Format
- Resolution: 1920x1080
- Pixel Format: 8-bit UYVY (bmdFormat8BitYUV = 0x32767579)
- Row Bytes: 3840 (1920 * 2, no HANC padding)
- Frame Size: 4,147,200 bytes (3840 * 1080)
- DMA buffer includes ~52 VANC rows at top (ANC pattern: `00 02 01 20`)

## Performance Optimizations Applied

### 1. Frame Rate (15fps -> 28fps)
- Replaced timestamp-based rate limiter with frame counting (`_diagCallbackCount % 2`) - immune to GC jitter
- Pooled event buffers in DeckLinkDeviceManager (3 pre-allocated 4MB buffers) - eliminated 240MB/s heap allocations
- Eliminated redundant 4MB copy using `MemoryMarshal.TryGetArray` to get backing array directly
- Half-resolution preview (960x540) with `Parallel.For` row processing
- Pre-computed YUV->RGB lookup tables (YtoC, UtoG, UtoB, VtoR, VtoG, ClampTable)
- Double-buffered RGB conversion buffers
- `CompositionTarget.Rendering` for vsync-aligned UI updates (replaced `Dispatcher.BeginInvoke`)

### 2. Frame Geometry Auto-Detection (Fixed Image Shift)
- **Problem**: Hardcoded `VancRowsToSkip=84` was wrong - skipping 32 rows of actual video
- **Root cause**: Native debug log only sampled rows 0, 42, 84 with gaps; actual transition is at row 52
- **Fix**: `DetectFrameGeometry()` combines VANC and HANC detection on the first valid frame:
  - **VANC**: Scans first 120 rows, checks first 4 bytes per row for ANC pattern (`00 02 01 20`). Detected: row 52.
  - **HANC**: Scans 4 sample video rows horizontally for non-video→video transition. Detected: 0 bytes (no HANC).
- Re-detects on format change (signal switch)
- Bottom rows of RGB buffer cleared when effectiveHeight < dstHeight

### 3. Frame Validation (Fixed Glitching)
- **Problem 1**: Over-aggressive validation rejected legitimate frames, causing stale cached frames to display
  - Removed `y1 == 0xFF` check - Y=255 is valid super-white in UYVY
  - Removed `isInvalidUyvy` check - could false-positive on dark scenes
  - Changed from "reject on any single bad point" to "reject only if majority (5/9) of sample points are bad"
  - Black fill pattern `80 10 80 10` is valid UYVY black; single occurrence is not corruption
- **Problem 2**: vtable[9] (GetTimecode) was being called with GetBytes signature, returning garbage data that passed validation and contaminated the frame cache ("max headroom" glitch effect)
  - Fix: Removed all vtable GetBytes probing from native DLL; go straight to offset-280 fallback

### 4. Thread Safety
- `_readyFrame` is `volatile` for memory ordering
- `Interlocked.Exchange` for atomic grab-and-clear in rendering handler
- Dimensions written before volatile frame reference (release semantics)

## COM Interop - Root Cause Explained
- **GetBytes was removed from IDeckLinkVideoFrame in SDK 14.3** (confirmed via SDK 15.3 IDL)
- vtable[8] is now GetTimecode, not GetBytes — this is why calling it with GetBytes signature caused SEH/garbage
- QI for `IDeckLinkVideoFrame` failed because native code used the old v14.2.1 GUID
- QI for `IDeckLinkVideoBuffer` failed because native code had the wrong GUID entirely
- C# interface had `GetBytes` at vtable slot 8, misaligning all methods after it
- **All fixed**: correct GUIDs, correct vtable order, phantom GetBytes removed from C#

### Frame data access (SDK 15.3+)
- `IDeckLinkVideoBuffer` (separate interface, QI from frame): GetBytes/StartAccess/EndAccess
- `IDeckLinkVideoInputFrame_v14_2_1` (legacy compatibility): still has GetBytes at vtable[8]
- Both are proper SDK-supported paths; offset-280 is retained as last resort only

### SDK Reference
- SDK location: `C:\Users\Screener\Downloads\Blackmagic_DeckLink_SDK_15.3\`
- Key file: `Win\include\DeckLinkAPI.idl` — definitive interface/GUID source
- Legacy compat: `Win\include\DeckLinkAPI_v14_2_1.idl` — has GetBytes on video frame
