namespace Screener.Golf.Switching;

/// <summary>
/// Pure-logic engine that blends two BGRA frames for transitions.
/// No WPF dependencies — operates entirely on byte arrays.
///
/// Callers feed frames via SetSource(index, data, w, h) using fixed slot indices
/// (e.g., enabled[0] always feeds slot 0, enabled[1] always feeds slot 1).
/// The engine internally tracks which slot is "program" (A) vs "preview" (B)
/// via an index swap on cut/transition completion.
/// </summary>
public class TransitionEngine
{
    // Frame data indexed by source slot (0 or 1)
    private readonly byte[]?[] _sources = new byte[]?[2];
    private int _width;
    private int _height;
    private double _autoStartTime;
    private bool _autoRunning;

    // When false: slot 0 = A (program), slot 1 = B (preview)
    // When true:  slot 1 = A (program), slot 0 = B (preview)
    private bool _swapped;

    /// <summary>Active transition type.</summary>
    public TransitionType ActiveTransition { get; private set; } = TransitionType.Cut;

    /// <summary>Whether a transition is currently in progress.</summary>
    public bool IsTransitioning { get; private set; }

    /// <summary>Current transition position (0.0 = source A, 1.0 = source B).</summary>
    public double TransitionPosition { get; private set; }

    /// <summary>Duration of auto-transitions in milliseconds.</summary>
    public int DurationMs { get; set; } = 1000;

    /// <summary>Fired when a transition reaches position 1.0.</summary>
    public event EventHandler? TransitionCompleted;

    // Internal A/B accessors that respect the swap state
    private byte[]? SourceA => _swapped ? _sources[1] : _sources[0];
    private byte[]? SourceB => _swapped ? _sources[0] : _sources[1];

    /// <summary>
    /// Feed a frame into a source slot. Slot indices are fixed per physical input
    /// (e.g., enabled[0] always uses slot 0, enabled[1] always uses slot 1).
    /// The engine handles which slot is program vs preview internally.
    /// </summary>
    public void SetSource(int index, byte[]? data, int width, int height)
    {
        if (index is 0 or 1)
            _sources[index] = data;
        _width = width;
        _height = height;
    }

    /// <summary>
    /// Set the two source frames for blending (legacy convenience).
    /// </summary>
    public void SetSources(byte[]? sourceA, byte[]? sourceB, int width, int height)
    {
        _sources[0] = sourceA;
        _sources[1] = sourceB;
        _width = width;
        _height = height;
    }

    /// <summary>
    /// Instant cut — swaps which slot is program, fires completion.
    /// </summary>
    public void TriggerCut()
    {
        _swapped = !_swapped;
        TransitionPosition = 0;
        IsTransitioning = false;
        _autoRunning = false;
        TransitionCompleted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Start a timed auto-transition of the given type.
    /// </summary>
    public void TriggerAutoTransition(TransitionType type)
    {
        if (type == TransitionType.Cut)
        {
            TriggerCut();
            return;
        }

        ActiveTransition = type;
        TransitionPosition = 0;
        IsTransitioning = true;
        _autoRunning = true;
        _autoStartTime = 0;
    }

    /// <summary>
    /// Set the transition position manually (T-bar control, 0.0 to 1.0).
    /// </summary>
    public void SetManualPosition(double position)
    {
        _autoRunning = false;
        TransitionPosition = Math.Clamp(position, 0.0, 1.0);
        IsTransitioning = TransitionPosition > 0;

        if (TransitionPosition >= 1.0)
        {
            CompleteTransition();
        }
    }

    /// <summary>
    /// Advance the auto-transition by the given elapsed time.
    /// Call this from the render loop.
    /// </summary>
    public void Tick(double elapsedMs)
    {
        if (!_autoRunning || !IsTransitioning) return;

        _autoStartTime += elapsedMs;
        double duration = Math.Max(1, DurationMs);
        TransitionPosition = Math.Clamp(_autoStartTime / duration, 0.0, 1.0);

        if (TransitionPosition >= 1.0)
        {
            CompleteTransition();
        }
    }

    /// <summary>
    /// Get the blended program output frame as BGRA byte array.
    /// </summary>
    public byte[]? GetProgramFrame()
    {
        var a = SourceA;
        var b = SourceB;

        if (a == null) return b;
        if (b == null) return a;

        // No transition active — return source A (current program)
        if (!IsTransitioning || TransitionPosition <= 0)
            return a;

        int length = _width * _height * 4;
        if (a.Length < length || b.Length < length)
            return a;

        var output = new byte[length];
        double t = TransitionPosition;

        int rowBytes = _width * 4;

        switch (ActiveTransition)
        {
            case TransitionType.Dissolve:
                BlendDissolve(a, b, output, length, t, rowBytes);
                break;

            case TransitionType.DipToBlack:
                BlendDipToBlack(a, b, output, length, t, rowBytes);
                break;

            default:
                return a;
        }

        return output;
    }

    private void CompleteTransition()
    {
        // Swap which slot is program — the callbacks keep feeding the same slots,
        // so after the swap, what was preview is now program.
        _swapped = !_swapped;
        TransitionPosition = 0;
        IsTransitioning = false;
        _autoRunning = false;
        TransitionCompleted?.Invoke(this, EventArgs.Empty);
    }

    private static void BlendDissolve(byte[] a, byte[] b, byte[] output, int length, double t, int rowBytes)
    {
        int invT = (int)((1.0 - t) * 256);
        int fwdT = (int)(t * 256);
        int numRows = length / rowBytes;

        Parallel.For(0, numRows, row =>
        {
            int start = row * rowBytes;
            int end = Math.Min(start + rowBytes, length);
            for (int i = start; i < end; i++)
            {
                output[i] = (byte)((a[i] * invT + b[i] * fwdT) >> 8);
            }
        });

        // Handle any remaining bytes
        int remainder = numRows * rowBytes;
        for (int i = remainder; i < length; i++)
        {
            output[i] = (byte)((a[i] * invT + b[i] * fwdT) >> 8);
        }
    }

    private static void BlendDipToBlack(byte[] a, byte[] b, byte[] output, int length, double t, int rowBytes)
    {
        int numRows = length / rowBytes;

        if (t <= 0.5)
        {
            // First half: fade A to black
            double fade = 1.0 - (t * 2.0); // 1.0 -> 0.0
            int f = (int)(fade * 256);

            Parallel.For(0, numRows, row =>
            {
                int start = row * rowBytes;
                int end = Math.Min(start + rowBytes, length);
                for (int i = start; i < end; i += 4)
                {
                    output[i] = (byte)((a[i] * f) >> 8);         // B
                    output[i + 1] = (byte)((a[i + 1] * f) >> 8); // G
                    output[i + 2] = (byte)((a[i + 2] * f) >> 8); // R
                    output[i + 3] = 255;                          // A
                }
            });
        }
        else
        {
            // Second half: fade black to B
            double fade = (t - 0.5) * 2.0; // 0.0 -> 1.0
            int f = (int)(fade * 256);

            Parallel.For(0, numRows, row =>
            {
                int start = row * rowBytes;
                int end = Math.Min(start + rowBytes, length);
                for (int i = start; i < end; i += 4)
                {
                    output[i] = (byte)((b[i] * f) >> 8);         // B
                    output[i + 1] = (byte)((b[i + 1] * f) >> 8); // G
                    output[i + 2] = (byte)((b[i + 2] * f) >> 8); // R
                    output[i + 3] = 255;                          // A
                }
            });
        }
    }
}

/// <summary>
/// Types of transitions supported by the TransitionEngine.
/// </summary>
public enum TransitionType
{
    Cut,
    Dissolve,
    DipToBlack
}
