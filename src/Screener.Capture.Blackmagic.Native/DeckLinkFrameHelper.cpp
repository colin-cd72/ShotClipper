#define DECKLINK_NATIVE_EXPORTS
#include "DeckLinkFrameHelper.h"

#include <Windows.h>
#include <Unknwn.h>
#include <string.h>
#include <stdio.h>
#include <math.h>

// Debug logging helper
static FILE* g_logFile = nullptr;

static void DebugLog(const char* format, ...)
{
    char buffer[512];
    va_list args;
    va_start(args, format);
    vsnprintf(buffer, sizeof(buffer), format, args);
    va_end(args);
    OutputDebugStringA(buffer);

    // Open, write, close each time to allow reading the log while app runs
    FILE* logFile = nullptr;
    if (fopen_s(&logFile, "C:\\screener\\native_debug.log", "a") == 0 && logFile != nullptr)
    {
        fprintf(logFile, "%s", buffer);
        fclose(logFile);
    }
}

// IDeckLinkVideoBuffer interface GUID (SDK 15.3 - from DeckLinkAPI.idl)
// {CCB4B64A-5C86-4E02-B778-885D352709FE}
static const GUID IID_IDeckLinkVideoBuffer =
{ 0xCCB4B64A, 0x5C86, 0x4E02, { 0xB7, 0x78, 0x88, 0x5D, 0x35, 0x27, 0x09, 0xFE } };

// IDeckLinkVideoFrame interface GUID (SDK 15.3 - GetBytes was removed in 14.3)
// {6502091C-615F-4F51-BAF6-45C4256DD5B0}
static const GUID IID_IDeckLinkVideoFrame =
{ 0x6502091C, 0x615F, 0x4F51, { 0xBA, 0xF6, 0x45, 0xC4, 0x25, 0x6D, 0xD5, 0xB0 } };

// IDeckLinkVideoInputFrame interface GUID (SDK 15.3)
// {C9ADD3D2-BE52-488D-AB2D-7FDEF7AF0C95}
static const GUID IID_IDeckLinkVideoInputFrame =
{ 0xC9ADD3D2, 0xBE52, 0x488D, { 0xAB, 0x2D, 0x7F, 0xDE, 0xF7, 0xAF, 0x0C, 0x95 } };

// Legacy IDeckLinkVideoInputFrame_v14_2_1 (still has GetBytes at vtable[8])
// {05CFE374-537C-4094-9A57-680525118F44}
static const GUID IID_IDeckLinkVideoInputFrame_v14_2_1 =
{ 0x05CFE374, 0x537C, 0x4094, { 0x9A, 0x57, 0x68, 0x05, 0x25, 0x11, 0x8F, 0x44 } };

// Buffer access mode
enum BMDBufferAccessMode
{
    bmdBufferAccessRead = 1,
    bmdBufferAccessWrite = 2,
    bmdBufferAccessReadWrite = 3
};

// IDeckLinkVideoBuffer vtable layout (SDK 15.3):
// [0] QueryInterface
// [1] AddRef
// [2] Release
// [3] GetBytes
// [4] StartAccess
// [5] EndAccess

// Helper for Welford's online variance calculation
static void UpdateStats(unsigned char v, double* mean, double* m2, int i)
{
    double x = (double)v;
    double delta = x - *mean;
    *mean += delta / (i + 1);
    double delta2 = x - *mean;
    *m2 += delta * delta2;
}

// Detect if buffer contains corrupt BGRA data instead of UYVY
// Returns true if the data looks like BGRA (corrupt), false if it looks like valid UYVY
static bool LooksLikeCorruptBGRA(unsigned char* p, int width, int height, int rowBytes)
{
    // Sample ~2048 pixels worth of 4-byte groups
    const int samples = 2048;
    int widthBytes = width * 2; // UYVY: 2 bytes per pixel

    int c0FF = 0, c1FF = 0, c2FF = 0, c3FF = 0;
    double m0 = 0, m1 = 0, m2 = 0, m3 = 0;
    double s0 = 0, s1 = 0, s2 = 0, s3 = 0;

    // Pick lines spread out; avoid only one row
    for (int i = 0; i < samples; i++)
    {
        int y = (i * 131) % height;                 // pseudo-spread
        int x = ((i * 337) % (widthBytes - 4));     // byte offset
        x &= ~3;                                    // align to 4 bytes

        int offset = y * rowBytes + x;
        unsigned char b0 = p[offset + 0];
        unsigned char b1 = p[offset + 1];
        unsigned char b2 = p[offset + 2];
        unsigned char b3 = p[offset + 3];

        if (b0 == 0xFF) c0FF++;
        if (b1 == 0xFF) c1FF++;
        if (b2 == 0xFF) c2FF++;
        if (b3 == 0xFF) c3FF++;

        // online mean/variance per lane (Welford)
        UpdateStats(b0, &m0, &s0, i);
        UpdateStats(b1, &m1, &s1, i);
        UpdateStats(b2, &m2, &s2, i);
        UpdateStats(b3, &m3, &s3, i);
    }

    double p0 = (double)c0FF / samples;
    double p1 = (double)c1FF / samples;
    double p2 = (double)c2FF / samples;
    double p3 = (double)c3FF / samples;

    double maxOther = (p0 > p1) ? p0 : p1;
    if (p2 > maxOther) maxOther = p2;
    double alphaBias = p3 - maxOther;

    double var0 = s0 / (samples - 1);
    double var1 = s1 / (samples - 1);
    double var2 = s2 / (samples - 1);
    double var3 = s3 / (samples - 1);

    double varY = var1 + var3;
    double varUV = var0 + var2;

    // In BGRA, byte 3 (alpha) is typically 0xFF with low variance
    // In UYVY, all bytes have similar variance patterns
    bool suspiciousAlpha = (p3 > 0.20 && alphaBias > 0.10) || (p3 > 0.35);
    bool suspiciousVariance = (var3 < 50.0 && p3 > 0.10) || (varY < varUV * 0.8);

    return suspiciousAlpha || suspiciousVariance;
}

extern "C" {

DECKLINK_API int CopyDeckLinkFrameBytes(void* framePtr, void* buffer, int bufferSize)
{
    if (framePtr == nullptr || buffer == nullptr || bufferSize <= 0)
        return 0;

    IUnknown* unknown = reinterpret_cast<IUnknown*>(framePtr);

    // Validate the COM pointer is valid by querying IUnknown
    IUnknown* testUnk = nullptr;
    HRESULT hrTest = unknown->QueryInterface(IID_IUnknown, (void**)&testUnk);

    static bool loggedValidation = false;
    if (!loggedValidation)
    {
        DebugLog("[DeckLinkNative] COM validation: unknown=%p, QI(IUnknown) hr=0x%08X, ptr=%p\n", unknown, hrTest, testUnk);
        loggedValidation = true;
    }

    if (FAILED(hrTest) || testUnk == nullptr)
    {
        DebugLog("[DeckLinkNative] Invalid COM pointer: QueryInterface(IID_IUnknown) failed hr=0x%08X\n", hrTest);
        return 0;
    }
    testUnk->Release();

    // Also try QI for IDeckLinkVideoFrame to verify we have the right interface
    IUnknown* testVF = nullptr;
    HRESULT hrVF = unknown->QueryInterface(IID_IDeckLinkVideoFrame, (void**)&testVF);
    static bool loggedVF = false;
    if (!loggedVF)
    {
        DebugLog("[DeckLinkNative] QI(IDeckLinkVideoFrame) hr=0x%08X, ptr=%p\n", hrVF, testVF);
        loggedVF = true;
    }
    if (testVF) testVF->Release();

    // Try QI for IDeckLinkVideoInputFrame (the actual type from callbacks)
    IUnknown* testVIF = nullptr;
    HRESULT hrVIF = unknown->QueryInterface(IID_IDeckLinkVideoInputFrame, (void**)&testVIF);
    static bool loggedVIF = false;
    if (!loggedVIF)
    {
        DebugLog("[DeckLinkNative] QI(IDeckLinkVideoInputFrame) hr=0x%08X, ptr=%p\n", hrVIF, testVIF);
        loggedVIF = true;
    }
    if (testVIF) testVIF->Release();

    // Get frame dimensions from vtable (IDeckLinkVideoFrame interface)
    void** vtable = *(void***)unknown;

    typedef long (STDMETHODCALLTYPE *GetLongFunc)(void* pThis);
    typedef unsigned int (STDMETHODCALLTYPE *GetPixelFormatFunc)(void* pThis);
    GetLongFunc getWidth = (GetLongFunc)vtable[3];
    GetLongFunc getHeight = (GetLongFunc)vtable[4];
    GetLongFunc getRowBytes = (GetLongFunc)vtable[5];
    GetPixelFormatFunc getPixelFormat = (GetPixelFormatFunc)vtable[6];

    long width = 0, height = 0, rowBytes = 0;
    unsigned int pixelFormat = 0;
    __try
    {
        width = getWidth(unknown);
        height = getHeight(unknown);
        rowBytes = getRowBytes(unknown);
        pixelFormat = getPixelFormat(unknown);
    }
    __except(EXCEPTION_EXECUTE_HANDLER)
    {
        DebugLog("[DeckLinkNative] Failed to get frame dimensions\n");
        return 0;
    }

    // SDK 15.3 IDeckLinkVideoFrame vtable (GetBytes was REMOVED in SDK 14.3):
    //   [3]=GetWidth, [4]=GetHeight, [5]=GetRowBytes, [6]=GetPixelFormat,
    //   [7]=GetFlags, [8]=GetTimecode, [9]=GetAncillaryData
    // GetBytes is now only available via:
    //   1. IDeckLinkVideoBuffer interface (QI) - GetBytes at vtable[3]
    //   2. Legacy IDeckLinkVideoInputFrame_v14_2_1 (QI) - GetBytes at vtable[8]
    //   3. Offset-280 fallback (raw DMA pointer, fragile)

    // Try to get IDeckLinkVideoBuffer interface (SDK 12.0+)
    IUnknown* videoBuffer = nullptr;
    HRESULT hr = unknown->QueryInterface(IID_IDeckLinkVideoBuffer, (void**)&videoBuffer);

    static bool loggedQI = false;
    if (!loggedQI)
    {
        DebugLog("[DeckLinkNative] QueryInterface(IDeckLinkVideoBuffer): hr=0x%08X, ptr=%p, unknown=%p\n", hr, videoBuffer, unknown);
        loggedQI = true;
    }

    if (SUCCEEDED(hr) && videoBuffer != nullptr)
    {
        DebugLog("[DeckLinkNative] Got IDeckLinkVideoBuffer interface\n");

        void** vbVtable = *(void***)videoBuffer;

        // SDK 15.3 vtable layout: [3]=GetBytes, [4]=StartAccess, [5]=EndAccess
        typedef HRESULT (STDMETHODCALLTYPE *GetBytesFunc)(void* pThis, void** buffer);
        typedef HRESULT (STDMETHODCALLTYPE *StartAccessFunc)(void* pThis, unsigned int accessMode);
        typedef HRESULT (STDMETHODCALLTYPE *EndAccessFunc)(void* pThis, unsigned int accessMode);

        GetBytesFunc getBytes = (GetBytesFunc)vbVtable[3];
        StartAccessFunc startAccess = (StartAccessFunc)vbVtable[4];
        EndAccessFunc endAccess = (EndAccessFunc)vbVtable[5];

        void* srcPtr = nullptr;
        int result = 0;

        __try
        {
            hr = startAccess(videoBuffer, bmdBufferAccessRead);
            if (SUCCEEDED(hr))
            {
                hr = getBytes(videoBuffer, &srcPtr);
                if (SUCCEEDED(hr) && srcPtr != nullptr)
                {
                    int copySize = (bufferSize < rowBytes * height) ? bufferSize : (rowBytes * height);
                    memcpy(buffer, srcPtr, copySize);
                    result = 1;

                    static int frameCount = 0;
                    frameCount++;
                    if (frameCount <= 5 || frameCount % 100 == 0)
                    {
                        unsigned char* b = (unsigned char*)buffer;
                        int midOffset = (height / 2) * rowBytes + (width / 2) * 2;
                        DebugLog("[DeckLinkNative] Frame %d (VideoBuffer): %dx%d, copied %d, first: %02X %02X %02X %02X, mid: %02X %02X %02X %02X\n",
                            frameCount, width, height, copySize,
                            b[0], b[1], b[2], b[3],
                            b[midOffset], b[midOffset+1], b[midOffset+2], b[midOffset+3]);
                    }
                }
                else
                {
                    DebugLog("[DeckLinkNative] VideoBuffer GetBytes failed: hr=0x%08X, ptr=%p\n", hr, srcPtr);
                }

                endAccess(videoBuffer, bmdBufferAccessRead);
            }
            else
            {
                DebugLog("[DeckLinkNative] VideoBuffer StartAccess failed: hr=0x%08X\n", hr);
            }
        }
        __except(EXCEPTION_EXECUTE_HANDLER)
        {
            DebugLog("[DeckLinkNative] Exception in VideoBuffer access\n");
        }

        videoBuffer->Release();
        if (result == 1) return result;
        // If VideoBuffer path failed, fall through to legacy/offset paths
    }

    // Try legacy IDeckLinkVideoInputFrame_v14_2_1 which still has GetBytes at vtable[8]
    // This is the approach FFmpeg uses for SDK 14.3+ compatibility
    IUnknown* legacyFrame = nullptr;
    HRESULT hrLegacy = unknown->QueryInterface(IID_IDeckLinkVideoInputFrame_v14_2_1, (void**)&legacyFrame);

    static bool loggedLegacy = false;
    if (!loggedLegacy)
    {
        DebugLog("[DeckLinkNative] QI(IDeckLinkVideoInputFrame_v14_2_1): hr=0x%08X, ptr=%p\n", hrLegacy, legacyFrame);
        loggedLegacy = true;
    }

    if (SUCCEEDED(hrLegacy) && legacyFrame != nullptr)
    {
        void** legacyVtable = *(void***)legacyFrame;

        // v14_2_1 vtable: [0-2]=IUnknown, [3-7]=IDeckLinkVideoFrame_v14_2_1
        // (GetWidth/GetHeight/GetRowBytes/GetPixelFormat/GetFlags), [8]=GetBytes
        typedef HRESULT (STDMETHODCALLTYPE *GetBytesFunc)(void* pThis, void** buffer);
        GetBytesFunc getBytes = (GetBytesFunc)legacyVtable[8];

        void* srcPtr = nullptr;
        int result = 0;

        __try
        {
            HRESULT hrGB = getBytes(legacyFrame, &srcPtr);
            if (SUCCEEDED(hrGB) && srcPtr != nullptr)
            {
                int copySize = (bufferSize < rowBytes * height) ? bufferSize : (rowBytes * height);
                memcpy(buffer, srcPtr, copySize);
                result = 1;

                static int legacyOkCount = 0;
                legacyOkCount++;
                if (legacyOkCount <= 5 || legacyOkCount % 100 == 0)
                {
                    unsigned char* b = (unsigned char*)buffer;
                    int midOffset = (height / 2) * rowBytes + (width / 2) * 2;
                    DebugLog("[DeckLinkNative] Frame %d (Legacy v14.2.1 GetBytes): %dx%d, copied %d, first: %02X %02X %02X %02X, mid: %02X %02X %02X %02X\n",
                        legacyOkCount, width, height, copySize,
                        b[0], b[1], b[2], b[3],
                        b[midOffset], b[midOffset+1], b[midOffset+2], b[midOffset+3]);
                }
            }
            else
            {
                DebugLog("[DeckLinkNative] Legacy GetBytes failed: hr=0x%08X, ptr=%p\n", hrGB, srcPtr);
            }
        }
        __except(EXCEPTION_EXECUTE_HANDLER)
        {
            DebugLog("[DeckLinkNative] Exception in legacy GetBytes\n");
        }

        legacyFrame->Release();
        if (result == 1) return result;
    }

    // Last resort: Direct buffer access via offset 280 in the DeckLink frame object.
    // This offset consistently returns valid frame structure (ANC at top, video below).
    // When there's no signal, the video area shows BLACK (80 10 80 10).

    unknown->AddRef();

    unsigned char* objBytes = (unsigned char*)unknown;
    void* ptr = *(void**)(objBytes + 280);
    int usedOffset = 280;

    static bool loggedOnce = false;
    if (!loggedOnce) {
        DebugLog("[DeckLinkNative] Using offset %d for buffer access. Dimensions: %dx%d, rowBytes=%d\n",
            usedOffset, width, height, rowBytes);
        loggedOnce = true;
    }

    if (ptr == nullptr)
    {
        unknown->Release();
        return 0;
    }

    uintptr_t ptrVal = (uintptr_t)ptr;
    if (ptrVal < 0x10000 || ptrVal >= 0x7FFF00000000ULL)
    {
        unknown->Release();
        return 0;
    }

    unsigned char* src = (unsigned char*)ptr;
    unsigned char* dst = (unsigned char*)buffer;
    int bytesCopied = 0;

    // Skip VANC row detection - it causes delays and exceptions
    // Copy as fast as possible, C# will handle VANC row skipping
    const int chunkSize = 65536;

    __try
    {
        memcpy(dst, src, bufferSize);
        bytesCopied = bufferSize;
    }
    __except(EXCEPTION_EXECUTE_HANDLER)
    {
        // Fallback: copy in smaller chunks
        for (int pos = 0; pos < bufferSize; pos += chunkSize)
        {
            int thisChunk = (pos + chunkSize <= bufferSize) ? chunkSize : (bufferSize - pos);
            __try
            {
                memcpy(dst + pos, src + pos, thisChunk);
                bytesCopied += thisChunk;
            }
            __except(EXCEPTION_EXECUTE_HANDLER)
            {
                // Fill with black in UYVY format: U=128, Y=16, V=128, Y=16
                // Pattern: 0x80 0x10 0x80 0x10 (repeating)
                unsigned char* fillPtr = dst + pos;
                for (int i = 0; i < thisChunk; i += 4)
                {
                    fillPtr[i] = 0x80;     // U (neutral)
                    fillPtr[i + 1] = 0x10; // Y (black)
                    fillPtr[i + 2] = 0x80; // V (neutral)
                    fillPtr[i + 3] = 0x10; // Y (black)
                }
            }
        }
    }

    static int legacyFrameCount = 0;
    legacyFrameCount++;

    // Check if the copied data looks like corrupt BGRA instead of valid UYVY
    bool isCorrupt = false;
    if (bytesCopied > bufferSize / 2)
    {
        isCorrupt = LooksLikeCorruptBGRA(dst, width, height, rowBytes);
    }

    // Log every frame for first 10, then every 100th, with full geometry info
    if (legacyFrameCount <= 10 || legacyFrameCount % 100 == 0)
    {
        unsigned char* b = (unsigned char*)buffer;

        // Convert pixelFormat to 4CC string
        char fcc[5] = {0};
        fcc[0] = (char)(pixelFormat & 0xFF);
        fcc[1] = (char)((pixelFormat >> 8) & 0xFF);
        fcc[2] = (char)((pixelFormat >> 16) & 0xFF);
        fcc[3] = (char)((pixelFormat >> 24) & 0xFF);

        DebugLog("[DeckLinkNative] Frame %d: w=%d h=%d rowBytes=%d pixFmt=0x%08X('%s') ptr=%p corrupt=%d\n",
            legacyFrameCount, width, height, rowBytes, pixelFormat, fcc, ptr, isCorrupt ? 1 : 0);

        // Dump bytes at different offsets to help diagnose pointer start position
        // User requested: bytes at p+0, p+rowBytes, p+2*rowBytes, p-16, p+16
        DebugLog("  ptr+0:       %02X %02X %02X %02X %02X %02X %02X %02X  %02X %02X %02X %02X %02X %02X %02X %02X\n",
            b[0], b[1], b[2], b[3], b[4], b[5], b[6], b[7],
            b[8], b[9], b[10], b[11], b[12], b[13], b[14], b[15]);
        DebugLog("  ptr+rowBytes: %02X %02X %02X %02X %02X %02X %02X %02X  %02X %02X %02X %02X %02X %02X %02X %02X\n",
            b[rowBytes+0], b[rowBytes+1], b[rowBytes+2], b[rowBytes+3],
            b[rowBytes+4], b[rowBytes+5], b[rowBytes+6], b[rowBytes+7],
            b[rowBytes+8], b[rowBytes+9], b[rowBytes+10], b[rowBytes+11],
            b[rowBytes+12], b[rowBytes+13], b[rowBytes+14], b[rowBytes+15]);
        DebugLog("  ptr+2*rowBytes: %02X %02X %02X %02X %02X %02X %02X %02X  %02X %02X %02X %02X %02X %02X %02X %02X\n",
            b[2*rowBytes+0], b[2*rowBytes+1], b[2*rowBytes+2], b[2*rowBytes+3],
            b[2*rowBytes+4], b[2*rowBytes+5], b[2*rowBytes+6], b[2*rowBytes+7],
            b[2*rowBytes+8], b[2*rowBytes+9], b[2*rowBytes+10], b[2*rowBytes+11],
            b[2*rowBytes+12], b[2*rowBytes+13], b[2*rowBytes+14], b[2*rowBytes+15]);

        // Check UYVY phase: try decoding with offsets 0,1,2,3 and show first pixel RGB values
        DebugLog("  UYVY phase check (first pixel at different offsets):\n");
        for (int phase = 0; phase < 4; phase++)
        {
            unsigned char u = b[phase + 0];
            unsigned char y0 = b[phase + 1];
            unsigned char v = b[phase + 2];
            unsigned char y1 = b[phase + 3];
            int r = (int)(y0 + 1.402 * (v - 128));
            int g = (int)(y0 - 0.344 * (u - 128) - 0.714 * (v - 128));
            int bl = (int)(y0 + 1.772 * (u - 128));
            if (r < 0) r = 0; if (r > 255) r = 255;
            if (g < 0) g = 0; if (g > 255) g = 255;
            if (bl < 0) bl = 0; if (bl > 255) bl = 255;
            DebugLog("    phase %d: U=%3d Y0=%3d V=%3d Y1=%3d -> RGB(%3d,%3d,%3d)\n",
                phase, u, y0, v, y1, r, g, bl);
        }

        // Detailed scan to understand buffer structure
        // The 1/4 screen shift (480 pixels = 960 bytes) suggests HANC at row start
        DebugLog("  Detailed row scan (looking for video content and HANC boundary):\n");

        // Calculate expected HANC offset for HD-SDI
        // 1920 active pixels * 2 bytes/pixel = 3840 bytes of active video per row
        // If rowBytes > 3840, there's horizontal blanking
        int expectedActiveVideo = width * 2;  // 3840 for 1920 width
        int potentialHancOffset = rowBytes - expectedActiveVideo;  // Should be 0 if no HANC
        DebugLog("    rowBytes=%d, expected active=%d, HANC offset=%d\n",
            rowBytes, expectedActiveVideo, potentialHancOffset);

        // Scan multiple rows, checking the entire row horizontally
        int rowsToScan[] = {0, 42, 84, 200, 400, 540, 700, 900, 1000, 1070};
        for (int i = 0; i < sizeof(rowsToScan)/sizeof(rowsToScan[0]); i++)
        {
            int rowNum = rowsToScan[i];
            int rowOffset = rowNum * rowBytes;
            if (rowOffset + rowBytes >= bufferSize) break;

            // Scan the row in 64-byte steps to find transition points
            int lastType = -1;  // -1=unknown, 0=ANC, 1=BLK, 2=VIDEO
            int transitionOffset = -1;

            for (int byteOff = 0; byteOff < rowBytes - 8; byteOff += 64)
            {
                int off = rowOffset + byteOff;
                unsigned char b0 = b[off], b1 = b[off+1], b2 = b[off+2], b3 = b[off+3];

                int type = 2;  // default: video
                if (b0 == 0x00 && b1 == 0x02 && b2 == 0x01 && b3 == 0x20) type = 0;  // ANC
                else if (b0 == 0x80 && b1 == 0x10 && b2 == 0x80 && b3 == 0x10) type = 1;  // BLACK
                else if (b0 == 0x00 && b1 == 0x00 && b2 == 0x00 && b3 == 0x00) type = 0;  // zero=ANC-like

                if (lastType != -1 && type != lastType && transitionOffset < 0)
                {
                    transitionOffset = byteOff;
                }
                lastType = type;
            }

            // Also check specific offsets: 0, 960, end
            int off = rowOffset;
            unsigned char* at0 = b + rowOffset;
            unsigned char* at960 = b + rowOffset + 960;
            unsigned char* atEnd = b + rowOffset + rowBytes - 8;

            DebugLog("    row%4d: @0=%02X%02X%02X%02X @960=%02X%02X%02X%02X @end=%02X%02X%02X%02X trans@%d\n",
                rowNum,
                at0[0], at0[1], at0[2], at0[3],
                at960[0], at960[1], at960[2], at960[3],
                atEnd[0], atEnd[1], atEnd[2], atEnd[3],
                transitionOffset);
        }

        // Special: detailed byte-by-byte scan of row 540 to find exact video start
        if (height >= 540)
        {
            int midRowOffset = 540 * rowBytes;
            DebugLog("  Row 540 byte-level scan (first 1024 bytes):\n");
            for (int byteOff = 0; byteOff < 1024 && midRowOffset + byteOff + 4 < bufferSize; byteOff += 32)
            {
                int off = midRowOffset + byteOff;
                unsigned char b0 = b[off], b1 = b[off+1], b2 = b[off+2], b3 = b[off+3];
                bool isAnc = (b0 == 0x00 && b1 == 0x02 && b2 == 0x01 && b3 == 0x20);
                bool isBlk = (b0 == 0x80 && b1 == 0x10 && b2 == 0x80 && b3 == 0x10);
                bool isZero = (b0 == 0x00 && b1 == 0x00 && b2 == 0x00 && b3 == 0x00);
                const char* type = isAnc ? "ANC" : (isBlk ? "BLK" : (isZero ? "ZER" : "???"));
                // Only log if not black (to reduce output)
                if (!isBlk || byteOff < 128 || byteOff > 896)
                {
                    DebugLog("      +%4d: %02X %02X %02X %02X [%s]\n", byteOff, b0, b1, b2, b3, type);
                }
            }
        }
    }

    unknown->Release();

    // Return 1 only if we got most of the data AND it's not corrupt BGRA
    if (bytesCopied <= bufferSize / 2)
        return 0;  // Not enough data copied
    if (isCorrupt)
        return -1; // Data looks corrupt (BGRA-like), C# should use cached frame
    return 1;      // Good frame
}

DECKLINK_API int GetDeckLinkFrameInfo(void* framePtr, int* width, int* height, int* rowBytes, unsigned int* flags)
{
    if (framePtr == nullptr)
        return 0;

    IUnknown* unknown = reinterpret_cast<IUnknown*>(framePtr);
    void** vtable = *(void***)unknown;

    typedef long (STDMETHODCALLTYPE *GetLongFunc)(void* pThis);
    typedef unsigned int (STDMETHODCALLTYPE *GetUIntFunc)(void* pThis);

    __try
    {
        if (width) *width = ((GetLongFunc)vtable[3])(unknown);
        if (height) *height = ((GetLongFunc)vtable[4])(unknown);
        if (rowBytes) *rowBytes = ((GetLongFunc)vtable[5])(unknown);
        if (flags) *flags = ((GetUIntFunc)vtable[7])(unknown);
        return 1;
    }
    __except(EXCEPTION_EXECUTE_HANDLER)
    {
        return 0;
    }
}

}
