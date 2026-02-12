using System.Runtime.InteropServices;

namespace Screener.Capture.Ndi.Interop;

/// <summary>
/// P/Invoke declarations for the NDI runtime library (Processing.NDI.Lib.x64.dll).
/// The NDI runtime must be installed separately from https://ndi.video/tools/.
/// </summary>
internal static class NdiInterop
{
    private const string NdiLib = "Processing.NDI.Lib.x64.dll";

    // --- Library lifecycle ---

    /// <summary>
    /// Initialize the NDI library. Must be called before any other NDI function.
    /// </summary>
    /// <returns>True if initialization succeeded.</returns>
    [DllImport(NdiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "NDIlib_initialize")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool NDIlib_initialize();

    /// <summary>
    /// Shut down the NDI library. Call once when finished with all NDI operations.
    /// </summary>
    [DllImport(NdiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "NDIlib_destroy")]
    public static extern void NDIlib_destroy();

    // --- Find (source discovery) ---

    /// <summary>
    /// Create an NDI finder instance for discovering sources on the network.
    /// </summary>
    /// <param name="p_create_settings">Finder configuration.</param>
    /// <returns>Finder handle, or IntPtr.Zero on failure.</returns>
    [DllImport(NdiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "NDIlib_find_create_v2")]
    public static extern IntPtr NDIlib_find_create_v2(ref NDIlib_find_create_t p_create_settings);

    /// <summary>
    /// Wait for the source list to change. Blocks until sources change or timeout expires.
    /// </summary>
    /// <param name="p_instance">Finder handle.</param>
    /// <param name="timeout_in_ms">Maximum time to wait in milliseconds.</param>
    /// <returns>True if the source list changed.</returns>
    [DllImport(NdiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "NDIlib_find_wait_for_sources")]
    [return: MarshalAs(UnmanagedType.U1)]
    public static extern bool NDIlib_find_wait_for_sources(IntPtr p_instance, uint timeout_in_ms);

    /// <summary>
    /// Get the current list of discovered NDI sources.
    /// </summary>
    /// <param name="p_instance">Finder handle.</param>
    /// <param name="p_no_sources">Receives the number of sources.</param>
    /// <returns>Pointer to an array of NDIlib_source_t structs. Valid until the next call to this function or destroy.</returns>
    [DllImport(NdiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "NDIlib_find_get_current_sources")]
    public static extern IntPtr NDIlib_find_get_current_sources(IntPtr p_instance, out uint p_no_sources);

    /// <summary>
    /// Destroy an NDI finder instance.
    /// </summary>
    /// <param name="p_instance">Finder handle.</param>
    [DllImport(NdiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "NDIlib_find_destroy")]
    public static extern void NDIlib_find_destroy(IntPtr p_instance);

    // --- Receive (source capture) ---

    /// <summary>
    /// Create an NDI receiver instance.
    /// </summary>
    /// <param name="p_create_settings">Receiver configuration.</param>
    /// <returns>Receiver handle, or IntPtr.Zero on failure.</returns>
    [DllImport(NdiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "NDIlib_recv_create_v3")]
    public static extern IntPtr NDIlib_recv_create_v3(ref NDIlib_recv_create_v3_t p_create_settings);

    /// <summary>
    /// Capture a frame from an NDI receiver. Blocks until a frame is available or timeout expires.
    /// </summary>
    /// <param name="p_instance">Receiver handle.</param>
    /// <param name="p_video_data">Receives video frame data if a video frame was captured.</param>
    /// <param name="p_audio_data">Receives audio frame data if an audio frame was captured.</param>
    /// <param name="p_metadata">Pointer to metadata struct, or IntPtr.Zero to ignore metadata.</param>
    /// <param name="timeout_in_ms">Maximum time to wait in milliseconds.</param>
    /// <returns>Frame type constant (NDIlib_frame_type_video, _audio, _none, etc.).</returns>
    [DllImport(NdiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "NDIlib_recv_capture_v3")]
    public static extern int NDIlib_recv_capture_v3(
        IntPtr p_instance,
        out NDIlib_video_frame_v2_t p_video_data,
        out NDIlib_audio_frame_v2_t p_audio_data,
        IntPtr p_metadata,
        uint timeout_in_ms);

    /// <summary>
    /// Free a video frame previously returned by NDIlib_recv_capture_v3.
    /// </summary>
    /// <param name="p_instance">Receiver handle.</param>
    /// <param name="p_video_data">The video frame to release.</param>
    [DllImport(NdiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "NDIlib_recv_free_video_v2")]
    public static extern void NDIlib_recv_free_video_v2(IntPtr p_instance, ref NDIlib_video_frame_v2_t p_video_data);

    /// <summary>
    /// Free an audio frame previously returned by NDIlib_recv_capture_v3.
    /// </summary>
    /// <param name="p_instance">Receiver handle.</param>
    /// <param name="p_audio_data">The audio frame to release.</param>
    [DllImport(NdiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "NDIlib_recv_free_audio_v2")]
    public static extern void NDIlib_recv_free_audio_v2(IntPtr p_instance, ref NDIlib_audio_frame_v2_t p_audio_data);

    /// <summary>
    /// Destroy an NDI receiver instance.
    /// </summary>
    /// <param name="p_instance">Receiver handle.</param>
    [DllImport(NdiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "NDIlib_recv_destroy")]
    public static extern void NDIlib_recv_destroy(IntPtr p_instance);

    // --- Send (output) ---

    /// <summary>
    /// Create an NDI sender instance.
    /// </summary>
    /// <param name="p_create_settings">Sender configuration.</param>
    /// <returns>Sender handle, or IntPtr.Zero on failure.</returns>
    [DllImport(NdiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "NDIlib_send_create")]
    public static extern IntPtr NDIlib_send_create(ref NDIlib_send_create_t p_create_settings);

    /// <summary>
    /// Send a video frame via NDI.
    /// </summary>
    /// <param name="p_instance">Sender handle.</param>
    /// <param name="p_video_data">The video frame to send.</param>
    [DllImport(NdiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "NDIlib_send_send_video_v2")]
    public static extern void NDIlib_send_send_video_v2(IntPtr p_instance, ref NDIlib_video_frame_v2_t p_video_data);

    /// <summary>
    /// Send an audio frame via NDI.
    /// </summary>
    /// <param name="p_instance">Sender handle.</param>
    /// <param name="p_audio_data">The audio frame to send.</param>
    [DllImport(NdiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "NDIlib_send_send_audio_v2")]
    public static extern void NDIlib_send_send_audio_v2(IntPtr p_instance, ref NDIlib_audio_frame_v2_t p_audio_data);

    /// <summary>
    /// Destroy an NDI sender instance.
    /// </summary>
    /// <param name="p_instance">Sender handle.</param>
    [DllImport(NdiLib, CallingConvention = CallingConvention.Cdecl, EntryPoint = "NDIlib_send_destroy")]
    public static extern void NDIlib_send_destroy(IntPtr p_instance);
}
