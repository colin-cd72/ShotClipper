using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;
using Screener.Abstractions.Output;
using Screener.Abstractions.Streaming;

namespace Screener.Core.Output;

/// <summary>
/// Coordinates frame push to WebSocket streaming + all IOutputService instances.
/// Replaces direct IStreamingService reference in InputPreviewRenderer.
/// </summary>
public sealed class OutputManager
{
    private readonly ILogger<OutputManager> _logger;
    private readonly IStreamingService _streamingService;
    private readonly IOutputService[] _outputServices;

    public IStreamingService StreamingService => _streamingService;
    public IReadOnlyList<IOutputService> OutputServices => _outputServices;

    public OutputManager(
        ILogger<OutputManager> logger,
        IStreamingService streamingService,
        IEnumerable<IOutputService> outputServices)
    {
        _logger = logger;
        _streamingService = streamingService;
        _outputServices = outputServices.ToArray();
    }

    /// <summary>
    /// Push a video frame to the WebSocket streaming service and all active output services.
    /// </summary>
    public async Task PushFrameToAllAsync(ReadOnlyMemory<byte> frameData, VideoMode mode, TimeSpan timestamp, CancellationToken ct = default)
    {
        // Push to WebSocket streaming
        if (_streamingService.State == StreamingState.Running)
        {
            try
            {
                await _streamingService.PushFrameAsync(frameData, timestamp, ct);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error pushing frame to streaming service");
            }
        }

        // Push to all active output services
        foreach (var output in _outputServices)
        {
            if (output.State == OutputState.Running)
            {
                try
                {
                    await output.PushFrameAsync(frameData, mode, timestamp, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error pushing frame to output {OutputId}", output.OutputId);
                }
            }
        }
    }

    /// <summary>
    /// Push audio samples to all active output services.
    /// </summary>
    public async Task PushAudioToAllAsync(ReadOnlyMemory<byte> audioData, int sampleRate, int channels, int bitsPerSample, CancellationToken ct = default)
    {
        foreach (var output in _outputServices)
        {
            if (output.State == OutputState.Running)
            {
                try
                {
                    await output.PushAudioAsync(audioData, sampleRate, channels, bitsPerSample, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Error pushing audio to output {OutputId}", output.OutputId);
                }
            }
        }
    }

    /// <summary>
    /// Get an output service by its ID.
    /// </summary>
    public IOutputService? GetOutput(string outputId)
    {
        return _outputServices.FirstOrDefault(o => o.OutputId == outputId);
    }
}
