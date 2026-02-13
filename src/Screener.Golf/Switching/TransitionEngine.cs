namespace Screener.Golf.Switching;

/// <summary>
/// Pure-logic engine that blends two BGRA frames for transitions.
/// No WPF dependencies — operates entirely on byte arrays.
/// </summary>
public class TransitionEngine
{
    private byte[]? _sourceA;
    private byte[]? _sourceB;
    private int _width;
    private int _height;
    private double _autoStartTime;
    private bool _autoRunning;

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

    /// <summary>
    /// Set the two source frames for blending.
    /// </summary>
    public void SetSources(byte[]? sourceA, byte[]? sourceB, int width, int height)
    {
        _sourceA = sourceA;
        _sourceB = sourceB;
        _width = width;
        _height = height;
    }

    /// <summary>
    /// Set only source A (program) without changing source B.
    /// </summary>
    public void SetSourceA(byte[]? sourceA, int width, int height)
    {
        _sourceA = sourceA;
        _width = width;
        _height = height;
    }

    /// <summary>
    /// Set only source B (preview/next) without changing source A.
    /// </summary>
    public void SetSourceB(byte[]? sourceB, int width, int height)
    {
        _sourceB = sourceB;
        _width = width;
        _height = height;
    }

    /// <summary>
    /// Instant cut — swaps A and B, no blend.
    /// </summary>
    public void TriggerCut()
    {
        // Swap sources: B becomes the new A (program)
        (_sourceA, _sourceB) = (_sourceB, _sourceA);
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
        IsTransitioning = TransitionPosition > 0 && TransitionPosition < 1.0;

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
        if (_sourceA == null) return _sourceB;
        if (_sourceB == null) return _sourceA;

        // No transition active — return source A (current program)
        if (!IsTransitioning || TransitionPosition <= 0)
            return _sourceA;

        int length = _width * _height * 4;
        if (_sourceA.Length < length || _sourceB.Length < length)
            return _sourceA;

        var output = new byte[length];
        double t = TransitionPosition;

        int rowBytes = _width * 4;

        switch (ActiveTransition)
        {
            case TransitionType.Dissolve:
                BlendDissolve(_sourceA, _sourceB, output, length, t, rowBytes);
                break;

            case TransitionType.DipToBlack:
                BlendDipToBlack(_sourceA, _sourceB, output, length, t, rowBytes);
                break;

            default:
                return _sourceA;
        }

        return output;
    }

    private void CompleteTransition()
    {
        // Swap: B becomes the new program (A)
        (_sourceA, _sourceB) = (_sourceB, _sourceA);
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
