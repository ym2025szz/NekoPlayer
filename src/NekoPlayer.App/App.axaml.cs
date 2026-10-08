using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NekoPlayer.App.ViewModels;
using NekoPlayer.App.Views;
using NekoPlayer.Audio.Playback;
using NekoPlayer.Audio.Spectrum;
using NekoPlayer.Core.Interfaces;
using NekoPlayer.Core.Services;
using NekoPlayer.Infrastructure.Configuration;
using NekoPlayer.Infrastructure.Data;
using NekoPlayer.Infrastructure.Metadata;
using NekoPlayer.Infrastructure.Repositories;
using NekoPlayer.Infrastructure.Online;
using NekoPlayer.App.Services;
using Serilog;

namespace NekoPlayer.App;

public partial class App : Application
{
    private IHost? _host;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            DisableAvaloniaDataAnnotationValidation();
            var paths = new UserDataPaths();
            paths.EnsureCreated();
            Log.Logger = new LoggerConfiguration().MinimumLevel.Information()
                .WriteTo.File(Path.Combine(paths.LogsDirectory, "nekoplayer-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
                .CreateLogger();
            Log.Information("猫娘播放器启动，版本 {Version}", AppVersionInfo.Version);

            _host = Host.CreateDefaultBuilder().UseSerilog().ConfigureServices(services =>
            {
                services.AddSingleton<IUserDataPaths>(paths);
                services.AddDbContextFactory<NekoPlayerDbContext>(options => options.UseSqlite($"Data Source={paths.DatabasePath}"));
                services.AddSingleton<ISettingsService, JsonSettingsService>();
                services.AddSingleton<IFfmpegLocator, FfmpegLocator>();
                services.AddSingleton<TrackMetadataReader>();
                services.AddSingleton<ITrackStateStore, TrackStateStore>();
                services.AddSingleton<IClock, SystemClock>();
                services.AddSingleton<IMusicLibraryService, MusicLibraryService>();
                services.AddSingleton<ITrackCatalog, OnlineTrackCatalog>();
                services.AddSingleton<IGatewayRuntime, GatewayRuntime>();
                services.AddSingleton<WindowsAccountVault>();
                services.AddSingleton<IProviderAccountService, ProviderAccountService>();
                services.AddSingleton<IOnlineMusicService, OnlineMusicService>();
                services.AddSingleton<IPlaylistService, PlaylistService>();
                services.AddSingleton<ILyricsService, LrcParser>();
                services.AddSingleton<IPlaybackQueueService, PlaybackQueueService>();
                services.AddSingleton<ISpectrumService, SpectrumService>();
                services.AddSingleton<IAudioPlayerService, FfmpegAudioPlayerService>();
                services.AddSingleton<PlaybackCoordinator>();
                services.AddSingleton<LyricsPresentationService>();
                services.AddSingleton<IDesktopLyricsService, DesktopLyricsService>();
                services.AddSingleton<WindowsMediaControlsService>();
                services.AddSingleton<SleepTimerService>(sp => new SleepTimerService(sp.GetRequiredService<PlaybackCoordinator>().PauseAsync));
                services.AddSingleton<PlayerLifetimeService>();
                services.AddSingleton<OnlineSearchViewModel>();
                services.AddSingleton<ProviderAccountsViewModel>();
                services.AddSingleton<MainWindowViewModel>();
            }).Build();

            var locator = _host.Services.GetRequiredService<IFfmpegLocator>();
            locator.Configure();
            var viewModel = _host.Services.GetRequiredService<MainWindowViewModel>();
            var lifetime = _host.Services.GetRequiredService<PlayerLifetimeService>();
            var media = _host.Services.GetRequiredService<WindowsMediaControlsService>();
            var window = new MainWindow { DataContext = viewModel, Lifetime = lifetime, MediaControls = media };
            desktop.MainWindow = window;
            lifetime.Configure(this, desktop, window, () => viewModel.CloseToTray,
                viewModel.TogglePlayCommand, viewModel.PreviousCommand, viewModel.NextCommand,
                () => viewModel.ToggleDesktopLyricsCommand.Execute(null), () => viewModel.UnlockDesktopLyricsCommand.Execute(null),
                () => viewModel.DesktopLyricsEnabled);
            desktop.Exit += (_, _) =>
            {
                Log.Information("猫娘播放器退出");
                try
                {
                    if (_host is IAsyncDisposable asyncHost) asyncHost.DisposeAsync().AsTask().GetAwaiter().GetResult();
                    else _host.Dispose();
                }
                catch (Exception ex) { Log.Warning(ex, "应用退出时宿主清理失败"); }
                finally { Log.CloseAndFlush(); }
            };
            Dispatcher.UIThread.UnhandledException += (_, e) => { Log.Fatal(e.Exception, "UI 线程未处理异常"); e.Handled = true; };
            TaskScheduler.UnobservedTaskException += (_, e) => { Log.Error(e.Exception, "后台任务未观察异常"); e.SetObserved(); };
        }
        base.OnFrameworkInitializationCompleted();
    }

    private static void DisableAvaloniaDataAnnotationValidation()
    {
        foreach (var plugin in BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray()) BindingPlugins.DataValidators.Remove(plugin);
    }
}
