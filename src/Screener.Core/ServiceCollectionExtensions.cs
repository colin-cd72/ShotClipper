using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Screener.Abstractions.Capture;
using Screener.Abstractions.Clipping;
using Screener.Abstractions.Encoding;
using Screener.Abstractions.Recording;
using Screener.Abstractions.Scheduling;
using Screener.Abstractions.Streaming;
using Screener.Abstractions.Timecode;
using Screener.Abstractions.Upload;

namespace Screener.Core;

/// <summary>
/// Extension methods for registering Screener services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add all Screener services to the service collection.
    /// </summary>
    public static IServiceCollection AddScreenerServices(this IServiceCollection services)
    {
        // This is a placeholder that can be implemented when all assemblies are referenced
        // For now, services are registered in App.xaml.cs

        return services;
    }
}
