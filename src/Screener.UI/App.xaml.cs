using System.Windows;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Screener.Abstractions.Capture;
using Screener.Abstractions.Clipping;
using Screener.Abstractions.Encoding;
using Screener.Abstractions.Recording;
using Screener.Abstractions.Scheduling;
using Screener.Abstractions.Streaming;
using Screener.Abstractions.Timecode;
using Screener.Abstractions.Upload;
using Screener.Abstractions.Output;
using Screener.Capture.Blackmagic;
using Screener.Capture.Ndi;
using Screener.Capture.Srt;
using Screener.Clipping;
using Screener.Core.Capture;
using Screener.Core.Output;
using Screener.Encoding.Codecs;
using Screener.Encoding.Pipelines;
using Screener.Recording;
using Screener.Scheduling;
using Screener.Streaming;
using Screener.Timecode;
using Screener.Timecode.Providers;
using Screener.UI.ViewModels;
using Screener.UI.Views;
using Screener.Upload;
using Screener.Upload.Providers;
using Screener.Core.Persistence;
using Screener.Core.Settings;
using Serilog;

namespace Screener.UI;

public partial class App : Application
{
    private IHost? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Global exception handlers
        AppDomain.CurrentDomain.UnhandledException += (s, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            Log.Fatal(ex, "Unhandled domain exception");
            MessageBox.Show($"Fatal error: {ex?.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
        };

        DispatcherUnhandledException += (s, args) =>
        {
            Log.Fatal(args.Exception, "Unhandled dispatcher exception");
            MessageBox.Show($"UI error: {args.Exception.Message}\n\n{args.Exception.StackTrace}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        TaskScheduler.UnobservedTaskException += (s, args) =>
        {
            Log.Fatal(args.Exception, "Unobserved task exception");
            args.SetObserved();
        };

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .WriteTo.File("logs/screener-.log", rollingInterval: RollingInterval.Day)
            .CreateLogger();

        try
        {
            _host = Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration((context, config) =>
                {
                    config.AddJsonFile("appsettings.json", optional: true, reloadOnChange: true);
                })
                .UseSerilog()
                .ConfigureServices((context, services) =>
                {
                    ConfigureServices(services, context.Configuration);
                })
                .Build();

            await _host.StartAsync();

            // Initialize database
            var db = _host.Services.GetRequiredService<DatabaseContext>();
            await db.InitializeAsync();

            // Initialize hardware detection
            var hwAccel = _host.Services.GetRequiredService<HardwareAccelerator>();
            _ = hwAccel.ProbeEncodersAsync();

            // Refresh capture devices
            var deviceManager = _host.Services.GetRequiredService<IDeviceManager>();
            deviceManager.RefreshDevices();

            var mainWindow = _host.Services.GetRequiredService<MainWindow>();
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Application startup failed");
            MessageBox.Show($"Failed to start application: {ex.Message}", "Error",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        // Configuration
        services.AddOptions();

        // Database and Repositories
        services.AddSingleton<DatabaseContext>();
        services.AddSingleton<ScheduleRepository>();
        services.AddSingleton<UploadQueueRepository>();
        services.AddSingleton<RecordingsRepository>();
        services.AddSingleton<SettingsRepository>();
        services.AddSingleton<ISettingsService, SettingsService>();

        // Core Services
        services.AddSingleton<HardwareAccelerator>();

        // Preview
        services.AddSingleton<Screener.Preview.D3DPreviewRenderer>();
        services.AddSingleton<Screener.Preview.AudioPreviewService>();

        // Capture - individual device managers
        services.AddSingleton<DeckLinkDeviceManager>();
        services.AddSingleton<NdiRuntime>();
        services.AddSingleton<NdiDeviceManager>();
        services.AddSingleton<SrtDeviceManager>();
        // Composite device manager aggregates all sources
        services.AddSingleton<IDeviceManager>(sp => new CompositeDeviceManager(
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CompositeDeviceManager>>(),
            new IDeviceManager[]
            {
                sp.GetRequiredService<DeckLinkDeviceManager>(),
                sp.GetRequiredService<NdiDeviceManager>(),
                sp.GetRequiredService<SrtDeviceManager>()
            }));

        // Encoding
        services.AddTransient<IEncodingPipeline, EncodingPipeline>();
        services.AddSingleton<Func<IEncodingPipeline>>(sp => () => sp.GetRequiredService<IEncodingPipeline>());

        // Timecode Providers
        services.AddSingleton<ITimecodeProvider, NtpTimeProvider>();
        services.AddSingleton<ITimecodeProvider, SystemTimeProvider>();
        services.AddSingleton<ITimecodeProvider, ManualTimeProvider>();
        services.AddSingleton<ITimecodeService, TimecodeService>();

        // Recording
        services.AddSingleton<IRecordingService, RecordingService>();
        services.AddSingleton<FilenameGenerator>();
        services.AddSingleton<DriveManager>();

        // Clipping
        services.AddSingleton<IClippingService, ClippingService>();

        // Streaming
        services.AddSingleton<IStreamingService, WebRtcStreamingService>();

        // Output services (NDI + SRT)
        services.AddSingleton<NdiOutputService>();
        services.AddSingleton<SrtOutputService>();
        services.AddSingleton<IOutputService>(sp => sp.GetRequiredService<NdiOutputService>());
        services.AddSingleton<IOutputService>(sp => sp.GetRequiredService<SrtOutputService>());
        services.AddSingleton<OutputManager>();

        // Upload Providers
        services.AddSingleton<ICloudStorageProvider, S3StorageProvider>();
        services.AddSingleton<ICloudStorageProvider, AzureBlobProvider>();
        services.AddSingleton<ICloudStorageProvider, Screener.Upload.Providers.DropboxProvider>();
        services.AddSingleton<ICloudStorageProvider, Screener.Upload.Providers.GoogleDriveProvider>();
        services.AddSingleton<ICloudStorageProvider, Screener.Upload.Providers.GoogleCloudStorageProvider>();
        services.AddSingleton<ICloudStorageProvider, Screener.Upload.Providers.FrameIoProvider>();
        services.AddSingleton<ICloudStorageProvider, Screener.Upload.Providers.FtpSftpProvider>();
        services.AddSingleton<ICloudStorageProvider, Screener.Upload.Providers.S3CompatibleProvider>();
        services.AddSingleton<CredentialStore>();
        services.AddSingleton<IUploadService, UploadService>();
        services.AddHostedService(sp => (UploadService)sp.GetRequiredService<IUploadService>());

        // Scheduling
        services.AddSingleton<ISchedulingService, SchedulingService>();

        // ViewModels
        services.AddTransient<MainViewModel>();
        services.AddTransient<RecordingControlsViewModel>();
        services.AddTransient<TimecodeViewModel>();
        services.AddTransient<AudioMetersViewModel>();
        services.AddTransient<DriveStatusViewModel>();
        services.AddTransient<ClipBinViewModel>();
        services.AddTransient<UploadQueueViewModel>();
        services.AddTransient<VideoPreviewViewModel>();
        services.AddTransient<InputConfigurationViewModel>();
        services.AddTransient<SettingsViewModel>();
        services.AddTransient<SchedulerViewModel>();

        // Views
        services.AddSingleton<MainWindow>();
        services.AddTransient<SettingsWindow>();
        services.AddTransient<SchedulerWindow>();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_host != null)
        {
            await _host.StopAsync(TimeSpan.FromSeconds(5));
            _host.Dispose();
        }

        Log.CloseAndFlush();
        base.OnExit(e);
    }
}
