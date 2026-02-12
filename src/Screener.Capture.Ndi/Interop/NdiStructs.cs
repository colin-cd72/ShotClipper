using System.Runtime.InteropServices;

namespace Screener.Capture.Ndi.Interop;

/// <summary>
/// NDI source descriptor returned by the Find API.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NDIlib_source_t
{
    /// <summary>
    /// A UTF-8 string describing the source, e.g. "MACHINE_NAME (Source Name)".
    /// </summary>
    public IntPtr p_ndi_name;

    /// <summary>
    /// A UTF-8 URL string for the source (used internally by the SDK for connection).
    /// </summary>
    public IntPtr p_url_address;
}

/// <summary>
/// Configuration for creating an NDI finder instance.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NDIlib_find_create_t
{
    /// <summary>
    /// Whether to show sources running on the local machine.
    /// </summary>
    [MarshalAs(UnmanagedType.U1)]
    public bool show_local_sources;

    /// <summary>
    /// Comma-separated list of groups to search, or IntPtr.Zero for the default group.
    /// </summary>
    public IntPtr p_groups;

    /// <summary>
    /// Comma-separated list of extra IP addresses to query, or IntPtr.Zero for auto-discovery only.
    /// </summary>
    public IntPtr p_extra_ips;
}

/// <summary>
/// Configuration for creating an NDI receiver instance (v3).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NDIlib_recv_create_v3_t
{
    /// <summary>
    /// The source to connect to. Can be left as default and connected later.
    /// </summary>
    public NDIlib_source_t source_to_connect_to;

    /// <summary>
    /// The desired color format for received video frames.
    /// </summary>
    public int color_format;

    /// <summary>
    /// The bandwidth mode (e.g. highest quality, lowest, audio-only).
    /// </summary>
    public int bandwidth;

    /// <summary>
    /// Whether to allow video fields (interlaced) or force progressive.
    /// </summary>
    [MarshalAs(UnmanagedType.U1)]
    public bool allow_video_fields;

    /// <summary>
    /// The name to use for this receiver on the network, or IntPtr.Zero for a default name.
    /// </summary>
    public IntPtr p_ndi_recv_name;
}

/// <summary>
/// NDI video frame descriptor (v2).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NDIlib_video_frame_v2_t
{
    /// <summary>
    /// Horizontal resolution in pixels.
    /// </summary>
    public int xres;

    /// <summary>
    /// Vertical resolution in pixels.
    /// </summary>
    public int yres;

    /// <summary>
    /// FourCC pixel format code (e.g. NDIlib_FourCC_type_UYVY).
    /// </summary>
    public int FourCC;

    /// <summary>
    /// Frame rate numerator (e.g. 30000 for 29.97fps).
    /// </summary>
    public int frame_rate_N;

    /// <summary>
    /// Frame rate denominator (e.g. 1001 for 29.97fps).
    /// </summary>
    public int frame_rate_D;

    /// <summary>
    /// Picture aspect ratio. 0.0 means square pixels (width/height).
    /// </summary>
    public float picture_aspect_ratio;

    /// <summary>
    /// Frame format: progressive, interlaced field 0/1, etc.
    /// </summary>
    public int frame_format_type;

    /// <summary>
    /// Timecode in 100ns intervals (NDI timecode), or NDIlib_send_timecode_synthesize.
    /// </summary>
    public long timecode;

    /// <summary>
    /// Pointer to the video frame data buffer.
    /// </summary>
    public IntPtr p_data;

    /// <summary>
    /// Number of bytes per row (stride). 0 means tightly packed based on FourCC and xres.
    /// </summary>
    public int line_stride_in_bytes;

    /// <summary>
    /// Optional XML metadata string, or IntPtr.Zero.
    /// </summary>
    public IntPtr p_metadata;

    /// <summary>
    /// Timestamp in 100ns intervals, or 0 for auto.
    /// </summary>
    public long timestamp;
}

/// <summary>
/// NDI audio frame descriptor (v2).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NDIlib_audio_frame_v2_t
{
    /// <summary>
    /// Audio sample rate in Hz (e.g. 48000).
    /// </summary>
    public int sample_rate;

    /// <summary>
    /// Number of audio channels.
    /// </summary>
    public int no_channels;

    /// <summary>
    /// Number of audio samples per channel in this frame.
    /// </summary>
    public int no_samples;

    /// <summary>
    /// Timecode in 100ns intervals.
    /// </summary>
    public long timecode;

    /// <summary>
    /// Pointer to the audio data buffer (32-bit float, planar layout).
    /// </summary>
    public IntPtr p_data;

    /// <summary>
    /// Stride in bytes between the start of each channel's data.
    /// </summary>
    public int channel_stride_in_bytes;

    /// <summary>
    /// Optional XML metadata string, or IntPtr.Zero.
    /// </summary>
    public IntPtr p_metadata;

    /// <summary>
    /// Timestamp in 100ns intervals, or 0 for auto.
    /// </summary>
    public long timestamp;
}

/// <summary>
/// Configuration for creating an NDI sender instance.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct NDIlib_send_create_t
{
    /// <summary>
    /// The NDI source name to advertise on the network (UTF-8).
    /// </summary>
    public IntPtr p_ndi_name;

    /// <summary>
    /// Comma-separated group names, or IntPtr.Zero for the default group.
    /// </summary>
    public IntPtr p_groups;

    /// <summary>
    /// Whether the sender should clock video (block until it's time to send the next frame).
    /// </summary>
    [MarshalAs(UnmanagedType.U1)]
    public bool clock_video;

    /// <summary>
    /// Whether the sender should clock audio.
    /// </summary>
    [MarshalAs(UnmanagedType.U1)]
    public bool clock_audio;
}

/// <summary>
/// NDI library constants.
/// </summary>
public static class NdiConstants
{
    /// <summary>Request UYVY for video, BGRA as fallback.</summary>
    public const int NDIlib_recv_color_format_UYVY_BGRA = 3;

    /// <summary>Receive at the highest available bandwidth/quality.</summary>
    public const int NDIlib_recv_bandwidth_highest = 100;

    /// <summary>A video frame was captured.</summary>
    public const int NDIlib_frame_type_video = 1;

    /// <summary>An audio frame was captured.</summary>
    public const int NDIlib_frame_type_audio = 2;

    /// <summary>No frame was available within the timeout.</summary>
    public const int NDIlib_frame_type_none = 0;

    /// <summary>FourCC code for 8-bit UYVY pixel format.</summary>
    public const int NDIlib_FourCC_type_UYVY = 0x59565955;
}
