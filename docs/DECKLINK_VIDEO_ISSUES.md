# DeckLink Video Capture Issues

## Summary
Video capture from Blackmagic DeckLink Duo via SDI is not displaying correctly. The native frame extraction finds video data, but there are issues with the GetBytes() COM method and data validation.

## Hardware Configuration
- **Device**: DeckLink Duo (4 inputs detected)
- **Input**: SDI (confirmed by user)
- **Format**: 1080p59.94, YUV 8-bit (UYVY / `0x32767579`)
- **Frame Size**: 1920x1080, rowBytes=3840 (1920 * 2 bytes for UYVY)

## Core Issues

### 1. GetBytes() COM Method Crashes
The `IDeckLinkVideoInputFrame::GetBytes()` method consistently crashes when called through the vtable.

**Evidence from native log:**
```
[DeckLinkNative] vtable[3]=000000018005C2A0 [4]=0000000180022590 [5]=000000018005C2B0 [6]=000000018005C2C0 [7]=000000018005C2D0 [8]=000000018005B250
[DeckLinkNative] Calling GetBytes at vtable[8]=000000018005B250
[DeckLinkNative] GetBytes crashed, trying memory scanning
```

**What works:**
- `GetWidth()` (vtable[3]) - returns 1920 correctly
- `GetHeight()` (vtable[4]) - returns 1080 correctly
- `GetRowBytes()` (vtable[5]) - returns 3840 correctly
- `GetPixelFormat()` (vtable[6]) - returns 0x32767579 ('2vuy') correctly
- `GetFlags()` (vtable[7]) - returns 0x00000002 correctly

**What crashes:**
- `GetBytes()` (vtable[8]) - SEH exception every time

**QueryInterface behavior:**
- QI for `IDeckLinkVideoFrame` fails with `0x80004002` (E_NOINTERFACE)
- QI for `IDeckLinkVideoInputFrame` succeeds but returns the same pointer

### 2. Memory Scanning Fallback Partially Works
A fallback mechanism scans object memory at various offsets to find the frame buffer.

**Current behavior:**
- Finds page-aligned buffer at offset 280
- Successfully copies 4,081,664 / 4,147,200 bytes (missing last 64KB)
- Native log shows valid mid-frame YUV data: `mid[2075520]: 7F 56 80 58`

**But C# validation sees zeros:**
```
[14:02:00 WRN] Frame 4: Data appears invalid (all zeros in middle), using cached frame
[14:02:00 INF] Frame 4: Got 4147200 bytes via native DLL. First: 00020120, Mid: 00000000
```

### 3. Data Discrepancy
There's a mismatch between what the native DLL reports and what C# sees:

| Source | First Bytes | Middle Bytes |
|--------|-------------|--------------|
| Native DLL | `00 02 01 20` | `7F 56 80 58` (valid YUV) |
| C# Validation | `00 02 01 20` | `00 00 00 00` (zeros) |

This suggests either:
1. The buffer is being modified/released between native copy and C# validation
2. The C# validation is checking the wrong offset
3. Memory copy is incomplete or corrupted

## Key Files

### Native DLL
- `src/Screener.Capture.Blackmagic.Native/DeckLinkFrameHelper.cpp`
  - `CopyDeckLinkFrameBytes()` - Main frame extraction function
  - Uses SEH (`__try/__except`) for crash protection
  - Falls back to memory offset scanning when GetBytes fails

### C# Interop
- `src/Screener.Capture.Blackmagic/DeckLinkDeviceManager.cs`
  - `DeckLinkCaptureDevice` class implements `IDeckLinkInputCallback`
  - `ProcessVideoFrame()` method at line ~1035 handles frame extraction
  - Uses `Marshal.GetComInterfaceForObject()` to get COM pointer

- `src/Screener.Capture.Blackmagic/Interop/DeckLinkAPI.cs`
  - COM interface definitions for DeckLink SDK
  - `IDeckLinkVideoInputFrame` interface definition

### UI/Display
- `src/Screener.UI/ViewModels/VideoPreviewViewModel.cs`
  - `OnVideoFrameReceived()` handles frame display
  - `ConvertYuv422ToRgb()` does YUV to BGRA conversion

## Technical Details

### COM Interface Pointer Handling
```csharp
// Current approach in DeckLinkDeviceManager.cs:
IntPtr framePtr = Marshal.GetComInterfaceForObject(frame, typeof(IDeckLinkVideoInputFrame));
int result = CopyDeckLinkFrameBytes(framePtr, unmanagedBuffer, frameSize);
```

Alternative that was tried:
```csharp
// Also crashes:
IntPtr framePtr = Marshal.GetIUnknownForObject(frame);
```

### Frame Validation Logic
```csharp
// In ProcessVideoFrame() - checks middle of frame for non-zero content
int midOffset = (height / 2) * rowBytes + (width / 2) * 2;
bool hasValidData = false;
for (int checkIdx = midOffset; checkIdx < midOffset + 16 && checkIdx < frameSize; checkIdx++)
{
    if (frameBuffer[checkIdx] != 0)
    {
        hasValidData = true;
        break;
    }
}
```

### Native Memory Scanning
```cpp
// Offsets checked for frame buffer pointer:
int offsets[] = { 280, 288, 272, 296, 264, 304, 256, 312, 248, 320, ... };

// Buffer at offset 280 is consistently page-aligned and contains data
// But chunked copy only gets 4,081,664 of 4,147,200 bytes (missing 65KB)
```

## Previous State
According to earlier session notes, video WAS displaying at one point but with "green flashes". This suggests:
1. The frame extraction mechanism was working
2. The issue was with YUV to RGB color conversion
3. Something may have regressed or changed

## Potential Fixes to Investigate

### Applied Fix: AddRef Frame Buffer (Jan 31, 2026)
Added `AddRef()` call at the start of `CopyDeckLinkFrameBytes()` and corresponding `Release()` calls before each return.

```cpp
// At start of function:
unknown->AddRef();

// Before each return:
unknown->Release();
```

**Result**: Did NOT fully solve the issue. The underlying DMA buffer appears to be recycled independently of the COM object lifetime:
- Good frame: `Chunked copy 4081664/4147200... mid[2075520]: 7F 56 80 58`
- Bad frame: `Chunked copy 3885056/4147200... mid[2075520]: 00 00 00 00`

The COM AddRef keeps the frame object alive, but the actual video buffer memory (at offset 280) is managed separately by the DeckLink driver's DMA system.

### Applied Fix: Stride-Aware YUV Conversion (Jan 31, 2026)
Updated `VideoPreviewViewModel.ConvertYuv422ToRgb` to handle stride properly:

**Problem**: The conversion assumed `stride = width * bytesPerPixel` for both source and destination.
This caused horizontal shift/wrap artifacts when padding existed.

**Solution**:
1. Calculate `srcRowBytes` from frame data: `frameData.Length / height`
2. Use `WriteableBitmap.BackBufferStride` for destination stride
3. Process row-by-row instead of flat array iteration
4. Write directly to `BackBuffer` (eliminates intermediate copy)

```csharp
// Before: flat array, assumed stride = width * bpp
int pixelPairs = (width * height) / 2;
for (int i = 0; i < pixelPairs; i++) { ... }

// After: row-by-row with explicit strides
for (int row = 0; row < height; row++)
{
    int yuvRowStart = row * srcRowBytes;
    int rgbRowStart = row * destStride;
    // Process each row independently
}
```

**Files changed**:
- `src/Screener.UI/ViewModels/VideoPreviewViewModel.cs` - stride-aware conversion
- `src/Screener.UI/Screener.UI.csproj` - added `AllowUnsafeBlocks` for direct BackBuffer access

### Applied Fix: Ring Buffer Immediate Copy (Jan 31, 2026)
Implemented ring buffer approach to avoid DMA buffer recycling issues.

**Problem**: The chroma validation was rejecting frames because the DMA buffer at offset 280
gets recycled by the driver during the copy operation. Native DLL would see valid data at
the start, but by the time C# validated the middle of the frame, it had zeros.

**Solution**:
1. Added 3-slot ring buffer for frame data
2. Copy frame data immediately to ring buffer slot (no validation in callback)
3. Removed the blocking chroma validation that was rejecting frames
4. Cache updated after successful copy for fallback on future failures
5. Let consumer (UI thread) handle any validation if needed

```csharp
// Before: Validate chroma in callback, reject if zeros
if (!hasValidChroma) { /* use cached frame */ }

// After: Copy immediately, no validation
int result = CopyDeckLinkFrameBytes(framePtr, _unmanagedBuffer, frameSize);
Marshal.Copy(_unmanagedBuffer, currentSlot, 0, frameSize);  // Immediate copy to ring buffer
```

**Files changed**:
- `src/Screener.Capture.Blackmagic/DeckLinkDeviceManager.cs` - ring buffer and removed validation

### Other Fixes to Consider

1. **Fix the GetBytes crash**: Determine why vtable[8] crashes while vtable[3-7] work fine
   - Check DeckLink SDK version compatibility
   - Verify calling convention matches SDK expectations
   - Try different COM interface retrieval methods

3. **Try BGRA format**: Request BGRA instead of YUV from the SDK
   - Previously attempted but GetBytes still crashed
   - Might avoid conversion issues

4. **Verify stride handling**: Ensure row-by-row copy uses correct source/dest pitches

## Recommended Next Steps

1. **Use actual DeckLink SDK**: The current approach scans memory at offset 280 to find the buffer since GetBytes crashes. This is fragile. Consider:
   - Using the official DeckLink SDK C++ library directly
   - Creating a proper C++/CLI wrapper that can call GetBytes correctly
   - The vtable[8] crash suggests a calling convention or SDK version mismatch

2. **Custom Frame Allocator**: DeckLink supports `IDeckLinkMemoryAllocator` interface:
   - Implement custom allocator that uses your own buffer pool
   - This gives you control over buffer lifetime
   - See DeckLink SDK sample: "CapturePreview"

3. **Different capture approach**: Consider:
   - Using the Windows Media Foundation DeckLink source
   - Using FFmpeg with DeckLink input device (`-f decklink`)
   - These handle buffer management internally

4. **Debug GetBytes crash**: The vtable looks correct (entries 3-7 work), but entry 8 crashes:
   ```
   vtable[3]=GetWidth     - WORKS (returns 1920)
   vtable[4]=GetHeight    - WORKS (returns 1080)
   vtable[5]=GetRowBytes  - WORKS (returns 3840)
   vtable[6]=GetPixelFormat - WORKS (returns 0x32767579)
   vtable[7]=GetFlags     - WORKS (returns 0x00000002)
   vtable[8]=GetBytes     - CRASHES
   ```
   Possible causes:
   - SDK version mismatch (currently using SDK 15.3 GUIDs)
   - GetBytes requires specific thread context
   - Buffer not yet allocated when called

## Debug Logs Location
- Native debug log: `C:\Screener\native_debug.log`
- Application logs: Console output when running `dotnet run`

## Build Instructions
```bash
# Build native DLL
& 'C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe' `
  'src\Screener.Capture.Blackmagic.Native\Screener.Capture.Blackmagic.Native.vcxproj' `
  /p:Configuration=Debug /p:Platform=x64

# Build C# project
dotnet build src/Screener.UI/Screener.UI.csproj

# Run
cd src/Screener.UI && dotnet run
```
