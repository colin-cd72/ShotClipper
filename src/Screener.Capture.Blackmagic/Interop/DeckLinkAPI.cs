using System.Runtime.InteropServices;

namespace Screener.Capture.Blackmagic.Interop;

// DeckLink API GUIDs (SDK 15.3)
public static class DeckLinkGuid
{
    // Class IDs (for CoCreateInstance)
    public const string CDeckLinkIterator = "BA6C6F44-6DA5-4DCE-94AA-EE2D1372A676";
    public const string CDeckLinkDiscovery = "22FBFC33-8D07-495C-A5BF-DAB5EA9B82DB";

    // Interface IDs
    public const string IDeckLinkIterator = "50FB36CD-3063-4B73-BDBB-958087F2D8BA";
    public const string IDeckLinkDiscovery = "CDBF631C-BC76-45FA-B44D-C55059BC6101";
    public const string IDeckLinkDeviceNotificationCallback = "4997053B-0ADF-4CC8-AC70-7A50C4BE728F";
    public const string IDeckLink = "C418FBDD-0587-48ED-8FE5-640F0A14AF91";
    public const string IDeckLinkInput = "4095DB82-E294-4B8C-AAA8-3B9E80C49336";
    public const string IDeckLinkOutput = "A3EF0963-0862-44ED-92A9-EE89ABCF431A";
    public const string IDeckLinkConfiguration = "912F634B-2D4E-40A4-8AAB-8D80B73F1289";
    public const string IDeckLinkDisplayModeIterator = "9C88499F-F601-4021-B80B-032E4EB41C35";
    public const string IDeckLinkDisplayMode = "3EB2C1AB-0A3D-4523-A3AD-F40D7FB14E78";
    public const string IDeckLinkVideoInputFrame = "C9ADD3D2-BE52-488D-AB2D-7FDEF7AF0C95";
    public const string IDeckLinkAudioInputPacket = "E43D5870-2894-11DE-8C30-0800200C9A66";
    public const string IDeckLinkInputCallback = "3A94F075-C37D-4BA8-BCC0-1D778C8F881B";
    public const string IDeckLinkNotificationCallback = "B002A1EC-070D-4288-8289-BD5D36E5FF0D";
}

// Display mode flags
[Flags]
public enum BMDDisplayModeFlags : uint
{
    bmdDisplayModeSupports3D = 1 << 0,
    bmdDisplayModeColorspaceRec601 = 1 << 1,
    bmdDisplayModeColorspaceRec709 = 1 << 2,
    bmdDisplayModeColorspaceRec2020 = 1 << 3
}

// Field dominance
public enum BMDFieldDominance : uint
{
    bmdUnknownFieldDominance = 0,
    bmdLowerFieldFirst = 0x6C6F7772, // 'lowr'
    bmdUpperFieldFirst = 0x75707072, // 'uppr'
    bmdProgressiveFrame = 0x70726F67, // 'prog'
    bmdProgressiveSegmentedFrame = 0x70736620 // 'psf '
}

// Pixel formats
public enum BMDPixelFormat : uint
{
    bmdFormatUnspecified = 0,
    bmdFormat8BitYUV = 0x32767579, // '2vuy' - UYVY
    bmdFormat10BitYUV = 0x76323130, // 'v210'
    bmdFormat8BitARGB = 32,
    bmdFormat8BitBGRA = 0x42475241, // 'BGRA'
    bmdFormat10BitRGB = 0x72323130, // 'r210'
    bmdFormat12BitRGB = 0x52313242, // 'R12B'
    bmdFormat12BitRGBLE = 0x5231324C, // 'R12L'
    bmdFormat10BitRGBXLE = 0x5231306C, // 'R10l'
    bmdFormat10BitRGBX = 0x52313062, // 'R10b'
    bmdFormatH265 = 0x68657631, // 'hev1'
    bmdFormatDNxHR = 0x41566468  // 'AVdh'
}

// Video input flags
[Flags]
public enum BMDVideoInputFlags : uint
{
    bmdVideoInputFlagDefault = 0,
    bmdVideoInputEnableFormatDetection = 1 << 0,
    bmdVideoInputDualStream3D = 1 << 1,
    bmdVideoInputSynchronizeToCaptureGroup = 1 << 2
}

// Audio sample rate
public enum BMDAudioSampleRate : uint
{
    bmdAudioSampleRate48kHz = 48000
}

// Audio sample type
public enum BMDAudioSampleType : uint
{
    bmdAudioSampleType16bitInteger = 16,
    bmdAudioSampleType32bitInteger = 32
}

// Video input format changed events
[Flags]
public enum BMDVideoInputFormatChangedEvents : uint
{
    bmdVideoInputDisplayModeChanged = 1 << 0,
    bmdVideoInputFieldDominanceChanged = 1 << 1,
    bmdVideoInputColorspaceChanged = 1 << 2
}

// Frame flags
[Flags]
public enum BMDFrameFlags : uint
{
    bmdFrameFlagDefault = 0,
    bmdFrameFlagFlipVertical = 1 << 0,
    bmdFrameContainsHDRMetadata = 1 << 1,
    bmdFrameCapturedAsPsF = 1 << 30,
    bmdFrameHasNoInputSource = 1u << 31
}

// Display mode IDs (common ones)
public enum BMDDisplayMode : uint
{
    bmdModeNTSC = 0x6E747363, // 'ntsc'
    bmdModeNTSC2398 = 0x6E743233, // 'nt23'
    bmdModePAL = 0x70616C20, // 'pal '
    bmdModeNTSCp = 0x6E747370, // 'ntsp'
    bmdModePALp = 0x70616C70, // 'palp'

    bmdModeHD1080p2398 = 0x32337073, // '23ps'
    bmdModeHD1080p24 = 0x32347073, // '24ps'
    bmdModeHD1080p25 = 0x48703235, // 'Hp25'
    bmdModeHD1080p2997 = 0x48703239, // 'Hp29'
    bmdModeHD1080p30 = 0x48703330, // 'Hp30'
    bmdModeHD1080p4795 = 0x48703437, // 'Hp47'
    bmdModeHD1080p48 = 0x48703438, // 'Hp48'
    bmdModeHD1080p50 = 0x48703530, // 'Hp50'
    bmdModeHD1080p5994 = 0x48703539, // 'Hp59'
    bmdModeHD1080p6000 = 0x48703630, // 'Hp60'
    bmdModeHD1080i50 = 0x48693530, // 'Hi50'
    bmdModeHD1080i5994 = 0x48693539, // 'Hi59'
    bmdModeHD1080i6000 = 0x48693630, // 'Hi60'

    bmdModeHD720p50 = 0x68703530, // 'hp50'
    bmdModeHD720p5994 = 0x68703539, // 'hp59'
    bmdModeHD720p60 = 0x68703630, // 'hp60'

    bmdMode4K2160p2398 = 0x346B3233, // '4k23'
    bmdMode4K2160p24 = 0x346B3234, // '4k24'
    bmdMode4K2160p25 = 0x346B3235, // '4k25'
    bmdMode4K2160p2997 = 0x346B3239, // '4k29'
    bmdMode4K2160p30 = 0x346B3330, // '4k30'
    bmdMode4K2160p50 = 0x346B3530, // '4k50'
    bmdMode4K2160p5994 = 0x346B3539, // '4k59'
    bmdMode4K2160p60 = 0x346B3630, // '4k60'

    bmdModeUnknown = 0x69756E6B // 'iunk'
}

// HRESULT constants
public static class DeckLinkHResult
{
    public const int S_OK = 0;
    public const int S_FALSE = 1;
    public const int E_FAIL = unchecked((int)0x80004005);
    public const int E_INVALIDARG = unchecked((int)0x80070057);
    public const int E_ACCESSDENIED = unchecked((int)0x80070005);
    public const int E_OUTOFMEMORY = unchecked((int)0x8007000E);
}

// COM Interfaces

[ComImport]
[Guid(DeckLinkGuid.IDeckLinkIterator)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDeckLinkIterator
{
    [PreserveSig]
    int Next([MarshalAs(UnmanagedType.Interface)] out IDeckLink? deckLink);
}

// SDK 10.x+ Discovery interface (replaces iterator for async device discovery)
[ComImport]
[Guid(DeckLinkGuid.IDeckLinkDiscovery)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDeckLinkDiscovery
{
    [PreserveSig]
    int InstallDeviceNotifications([MarshalAs(UnmanagedType.Interface)] IDeckLinkDeviceNotificationCallback deviceNotificationCallback);

    [PreserveSig]
    int UninstallDeviceNotifications();
}

[ComImport]
[Guid(DeckLinkGuid.IDeckLinkDeviceNotificationCallback)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDeckLinkDeviceNotificationCallback
{
    [PreserveSig]
    int DeckLinkDeviceArrived([MarshalAs(UnmanagedType.Interface)] IDeckLink deckLinkDevice);

    [PreserveSig]
    int DeckLinkDeviceRemoved([MarshalAs(UnmanagedType.Interface)] IDeckLink deckLinkDevice);
}

[ComImport]
[Guid(DeckLinkGuid.IDeckLink)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDeckLink
{
    [PreserveSig]
    int GetModelName([MarshalAs(UnmanagedType.BStr)] out string modelName);

    [PreserveSig]
    int GetDisplayName([MarshalAs(UnmanagedType.BStr)] out string displayName);
}

[ComImport]
[Guid(DeckLinkGuid.IDeckLinkInput)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDeckLinkInput
{
    // Method order must match SDK 15.3 vtable exactly!
    [PreserveSig]
    int DoesSupportVideoMode(
        BMDVideoConnection connection,
        BMDDisplayMode requestedMode,
        BMDPixelFormat requestedPixelFormat,
        BMDVideoInputConversionMode conversionMode,
        BMDSupportedVideoModeFlags flags,
        out BMDDisplayMode actualMode,
        [MarshalAs(UnmanagedType.Bool)] out bool supported);

    [PreserveSig]
    int GetDisplayMode(BMDDisplayMode displayMode, [MarshalAs(UnmanagedType.Interface)] out IDeckLinkDisplayMode? resultDisplayMode);

    [PreserveSig]
    int GetDisplayModeIterator([MarshalAs(UnmanagedType.Interface)] out IDeckLinkDisplayModeIterator? iterator);

    [PreserveSig]
    int SetScreenPreviewCallback([MarshalAs(UnmanagedType.Interface)] object? previewCallback);

    [PreserveSig]
    int EnableVideoInput(BMDDisplayMode displayMode, BMDPixelFormat pixelFormat, BMDVideoInputFlags flags);

    [PreserveSig]
    int DisableVideoInput();

    [PreserveSig]
    int GetAvailableVideoFrameCount(out uint availableFrameCount);

    // SetVideoInputFrameMemoryAllocator - Slot reserved for vtable compatibility
    // Note: This method exists in the SDK but requires custom allocator implementation
    // which has COM marshaling issues with C#. Left as placeholder for vtable order.
    [PreserveSig]
    int SetVideoInputFrameMemoryAllocator([MarshalAs(UnmanagedType.Interface)] object? allocator);

    [PreserveSig]
    int EnableAudioInput(BMDAudioSampleRate sampleRate, BMDAudioSampleType sampleType, uint channels);

    [PreserveSig]
    int DisableAudioInput();

    [PreserveSig]
    int GetAvailableAudioSampleFrameCount(out uint availableSampleFrameCount);

    [PreserveSig]
    int StartStreams();

    [PreserveSig]
    int StopStreams();

    [PreserveSig]
    int PauseStreams();

    [PreserveSig]
    int FlushStreams();

    [PreserveSig]
    int SetCallback([MarshalAs(UnmanagedType.Interface)] IDeckLinkInputCallback? callback);

    [PreserveSig]
    int GetHardwareReferenceClock(long desiredTimeScale, out long hardwareTime, out long timeInFrame, out long ticksPerFrame);
}

[ComImport]
[Guid(DeckLinkGuid.IDeckLinkDisplayModeIterator)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDeckLinkDisplayModeIterator
{
    [PreserveSig]
    int Next([MarshalAs(UnmanagedType.Interface)] out IDeckLinkDisplayMode? displayMode);
}

[ComImport]
[Guid(DeckLinkGuid.IDeckLinkDisplayMode)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDeckLinkDisplayMode
{
    [PreserveSig]
    int GetName([MarshalAs(UnmanagedType.BStr)] out string name);

    [PreserveSig]
    BMDDisplayMode GetDisplayMode();

    [PreserveSig]
    int GetWidth();

    [PreserveSig]
    int GetHeight();

    [PreserveSig]
    int GetFrameRate(out long frameDuration, out long timeScale);

    [PreserveSig]
    BMDFieldDominance GetFieldDominance();

    [PreserveSig]
    BMDDisplayModeFlags GetFlags();
}

[ComImport]
[Guid(DeckLinkGuid.IDeckLinkVideoInputFrame)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDeckLinkVideoInputFrame
{
    // IUnknown methods handled by COM interop

    // IDeckLinkVideoFrame methods (SDK 15.3 - GetBytes was REMOVED in SDK 14.3)
    // GetBytes is now on IDeckLinkVideoBuffer, obtained via QueryInterface.
    [PreserveSig]
    int GetWidth();

    [PreserveSig]
    int GetHeight();

    [PreserveSig]
    int GetRowBytes();

    [PreserveSig]
    BMDPixelFormat GetPixelFormat();

    [PreserveSig]
    BMDFrameFlags GetFlags();

    // vtable[8] = GetTimecode (was GetBytes in SDK <= 14.2.1)
    [PreserveSig]
    int GetTimecode(BMDTimecodeFormat format, [MarshalAs(UnmanagedType.Interface)] out object? timecode);

    // vtable[9] = GetAncillaryData
    [PreserveSig]
    int GetAncillaryData([MarshalAs(UnmanagedType.Interface)] out object? ancillary);

    // IDeckLinkVideoInputFrame methods
    [PreserveSig]
    int GetStreamTime(out long frameTime, out long frameDuration, long timeScale);

    [PreserveSig]
    int GetHardwareReferenceTimestamp(long timeScale, out long frameTime, out long frameDuration);
}

[ComImport]
[Guid(DeckLinkGuid.IDeckLinkAudioInputPacket)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDeckLinkAudioInputPacket
{
    [PreserveSig]
    int GetSampleFrameCount();

    [PreserveSig]
    int GetBytes(out IntPtr buffer);

    [PreserveSig]
    int GetPacketTime(out long packetTime, long timeScale);
}

[ComImport]
[Guid(DeckLinkGuid.IDeckLinkInputCallback)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDeckLinkInputCallback
{
    [PreserveSig]
    int VideoInputFormatChanged(
        BMDVideoInputFormatChangedEvents notificationEvents,
        [MarshalAs(UnmanagedType.Interface)] IDeckLinkDisplayMode newDisplayMode,
        BMDDetectedVideoInputFormatFlags detectedSignalFlags);

    [PreserveSig]
    int VideoInputFrameArrived(
        [MarshalAs(UnmanagedType.Interface)] IDeckLinkVideoInputFrame? videoFrame,
        [MarshalAs(UnmanagedType.Interface)] IDeckLinkAudioInputPacket? audioPacket);
}

// Additional enums needed
public enum BMDVideoConnection : uint
{
    bmdVideoConnectionUnspecified = 0,
    bmdVideoConnectionSDI = 1 << 0,
    bmdVideoConnectionHDMI = 1 << 1,
    bmdVideoConnectionOpticalSDI = 1 << 2,
    bmdVideoConnectionComponent = 1 << 3,
    bmdVideoConnectionComposite = 1 << 4,
    bmdVideoConnectionSVideo = 1 << 5
}

public enum BMDVideoInputConversionMode : uint
{
    bmdNoVideoInputConversion = 0,
    bmdVideoInputLetterboxDownconversionFromHD1080 = 1 << 0,
    bmdVideoInputAnamorphicDownconversionFromHD1080 = 1 << 1,
    bmdVideoInputLetterboxDownconversionFromHD720 = 1 << 2,
    bmdVideoInputAnamorphicDownconversionFromHD720 = 1 << 3,
    bmdVideoInputLetterboxUpconversion = 1 << 4,
    bmdVideoInputAnamorphicUpconversion = 1 << 5
}

[Flags]
public enum BMDSupportedVideoModeFlags : uint
{
    bmdSupportedVideoModeDefault = 0,
    bmdSupportedVideoModeKeying = 1 << 0,
    bmdSupportedVideoModeDualStream3D = 1 << 1,
    bmdSupportedVideoModeSDISingleLink = 1 << 2,
    bmdSupportedVideoModeSDIDualLink = 1 << 3,
    bmdSupportedVideoModeSDIQuadLink = 1 << 4,
    bmdSupportedVideoModeInAnyProfile = 1 << 5
}

[Flags]
public enum BMDDetectedVideoInputFormatFlags : uint
{
    bmdDetectedVideoInputYCbCr422 = 1 << 0,
    bmdDetectedVideoInputRGB444 = 1 << 1,
    bmdDetectedVideoInputDualStream3D = 1 << 2
}

public enum BMDTimecodeFormat : uint
{
    bmdTimecodeRP188VITC1 = 0x72703138, // 'rp18'
    bmdTimecodeRP188VITC2 = 0x72703238, // 'rp28'
    bmdTimecodeRP188LTC = 0x72706C74, // 'rplt'
    bmdTimecodeRP188HighFrameRate = 0x72706872, // 'rphr'
    bmdTimecodeRP188Any = 0x7270616E, // 'rpan'
    bmdTimecodeVITC = 0x76697463, // 'vitc'
    bmdTimecodeVITCField2 = 0x76697432, // 'vit2'
    bmdTimecodeSerial = 0x73657269 // 'seri'
}

// Configuration IDs for IDeckLinkConfiguration
public enum BMDDeckLinkConfigurationID : uint
{
    bmdDeckLinkConfigVideoInputConnection = 0x7669636E  // 'vicn' - current video input connector
}

// Attribute IDs for IDeckLinkProfileAttributes
public enum BMDDeckLinkAttributeID : uint
{
    BMDDeckLinkVideoInputConnections = 0x76696373  // 'vics' - available video input connectors (bitmask)
}

// IDeckLinkProfileAttributes interface for querying device attributes
[ComImport]
[Guid("17D4BF8E-4911-473A-80A0-731CF6FF345B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDeckLinkProfileAttributes
{
    [PreserveSig]
    int GetFlag(BMDDeckLinkAttributeID cfgID, [MarshalAs(UnmanagedType.Bool)] out bool value);

    [PreserveSig]
    int GetInt(BMDDeckLinkAttributeID cfgID, out long value);

    [PreserveSig]
    int GetFloat(BMDDeckLinkAttributeID cfgID, out double value);

    [PreserveSig]
    int GetString(BMDDeckLinkAttributeID cfgID, [MarshalAs(UnmanagedType.BStr)] out string value);
}

// IDeckLinkConfiguration interface for configuring device settings
[ComImport]
[Guid(DeckLinkGuid.IDeckLinkConfiguration)]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDeckLinkConfiguration
{
    [PreserveSig]
    int SetFlag(BMDDeckLinkConfigurationID cfgID, int value);

    [PreserveSig]
    int GetFlag(BMDDeckLinkConfigurationID cfgID, out int value);

    [PreserveSig]
    int SetInt(BMDDeckLinkConfigurationID cfgID, long value);

    [PreserveSig]
    int GetInt(BMDDeckLinkConfigurationID cfgID, out long value);

    [PreserveSig]
    int SetFloat(BMDDeckLinkConfigurationID cfgID, double value);

    [PreserveSig]
    int GetFloat(BMDDeckLinkConfigurationID cfgID, out double value);

    [PreserveSig]
    int SetString(BMDDeckLinkConfigurationID cfgID, [MarshalAs(UnmanagedType.BStr)] string value);

    [PreserveSig]
    int GetString(BMDDeckLinkConfigurationID cfgID, [MarshalAs(UnmanagedType.BStr)] out string value);
}

// COM Class factory
[ComImport]
[Guid(DeckLinkGuid.CDeckLinkIterator)]
public class CDeckLinkIterator
{
}
