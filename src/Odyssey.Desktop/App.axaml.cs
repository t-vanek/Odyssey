using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Odyssey.Agent;
using Odyssey.Core;
using Odyssey.Infrastructure;
using Odyssey.Search;

namespace Odyssey.Desktop;

public sealed class App : Application
{
    private static readonly TimeSpan MinimumSplashVisibility = TimeSpan.FromMilliseconds(1100);
    private ServiceProvider? _services;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = new ServiceCollection();
            services.AddLogging(builder => builder.AddSimpleConsole(options => options.SingleLine = true).SetMinimumLevel(LogLevel.Information));
            services.AddSingleton(SystemPerformanceProfile.Current);
            services.AddSingleton<ApplicationStorage>();
            services.AddSingleton<LocalizationService>();
            services.AddSingleton(_ =>
            {
                var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("Odyssey-Desktop/1.0");
                client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
                client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2026-03-10");
                return client;
            });
            services.AddSingleton<UpdateService>();
            services.AddSingleton<UpdateViewModel>();
            services.AddSingleton<AgentAccessPolicyService>();
            services.AddSingleton<AgentOperationStore>();
            services.AddSingleton<AgentOperationService>();
            services.AddSingleton<AgentApprovalViewModel>();
            services.AddSingleton<SqliteConnectionFactory>();
            services.AddSingleton<IOdysseyStore, SqliteOdysseyStore>();
            services.AddSingleton<IFileClassifier, ExtensionFileClassifier>();
            services.AddSingleton<IExclusionPolicy, ConfigurableExclusionPolicy>();
            services.AddSingleton<IFileSystemScanner, PortableFileSystemScanner>();
            services.AddSingleton(_ => new LocalContentExtractor());
            services.AddSingleton<IContentExtractor>(provider => provider.GetRequiredService<LocalContentExtractor>());
            services.AddSingleton<IOcrCapability>(provider => provider.GetRequiredService<LocalContentExtractor>());
            services.AddSingleton<IStorageVolumeDiscovery, PortableStorageVolumeDiscovery>();
            services.AddSingleton<IDirectoryBrowserService, CachedDirectoryBrowserService>();
            services.AddSingleton<IDirectoryComparisonService, DirectoryComparisonService>();
            services.AddSingleton<IDirectorySynchronizationPlanner, DirectorySynchronizationPlanner>();
            services.AddSingleton<IDiskManagementService, PortableDiskManagementService>();
            services.AddSingleton<IFileOperationService, SafeFileOperationService>();
            services.AddSingleton<IMultiRenameService, MultiRenameService>();
            services.AddSingleton(provider => new SafeArchiveService(
                storage: provider.GetRequiredService<ApplicationStorage>()));
            services.AddSingleton<IArchiveEntryPreviewReader>(provider => provider.GetRequiredService<SafeArchiveService>());
            services.AddSingleton<IQuickViewService>(provider =>
                new QuickViewService(
                    provider.GetRequiredService<IArchiveEntryPreviewReader>(),
                    (IRemoteFilePreviewReader)provider.GetRequiredService<ISftpConnectionService>()));
            services.AddSingleton<ITextEditorService>(_ => new SafeTextEditorService());
            services.AddSingleton<IArchiveService>(provider => provider.GetRequiredService<SafeArchiveService>());
            services.AddSingleton<IArchiveMutationService>(provider => provider.GetRequiredService<SafeArchiveService>());
            services.AddSingleton<IArchiveRecoveryService>(provider => provider.GetRequiredService<SafeArchiveService>());
            services.AddSingleton<ISftpConnectionService, SftpConnectionService>();
            services.AddSingleton<IFileLocationProvider, LocalFileLocationProvider>();
            services.AddSingleton<IFileLocationProvider, SftpFileLocationProvider>();
            services.AddSingleton<IFileLocationProvider, ArchiveFileLocationProvider>();
            services.AddSingleton<IFileLocationProviderRegistry, FileLocationProviderRegistry>();
            services.AddSingleton<IFileTransferQueueService, FileTransferQueueService>();
            services.AddSingleton<UserPreferencesService>();
            services.AddSingleton(provider => new ScanPipelineOptions(
                PerformanceProfile: provider.GetRequiredService<SystemPerformanceProfile>()));
            services.AddSingleton<IScanCoordinator, ScanCoordinator>();
            services.AddSingleton<ISearchService, SqliteSearchService>();
            services.AddSingleton<ISystemSearchHistoryService, SystemSearchHistoryService>();
            services.AddSingleton<IBackgroundAutomationService, BackgroundAutomationService>();
            services.AddSingleton<DesktopInteractionService>();
            services.AddSingleton<IDesktopInteractionService>(provider => provider.GetRequiredService<DesktopInteractionService>());
            services.AddSingleton<MainViewModel>();
            services.AddSingleton<MainWindow>();
            var provider = services.BuildServiceProvider();
            _services = provider;

            var splash = new SplashWindow();
            splash.Opened += (_, _) => _ = CompleteStartupAsync(desktop, splash, provider);
            desktop.MainWindow = splash;
            desktop.Exit += (_, _) => _services.Dispose();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task CompleteStartupAsync(
        IClassicDesktopStyleApplicationLifetime desktop,
        SplashWindow splash,
        ServiceProvider services)
    {
        // Warm starts can complete before the first splash frame is perceptible.
        // Run the minimum display interval alongside real initialization so slow
        // starts are never delayed beyond the work they already need to perform.
        var minimumVisibility = Task.Delay(MinimumSplashVisibility);
        // Opened is raised before the first frame is guaranteed to have reached
        // the compositor. Yield briefly before constructing the heavier main UI.
        await Task.Delay(40);
        var localization = services.GetRequiredService<LocalizationService>();
        splash.SetStatus(localization["SplashCheckingFormats"]);
        await Task.Run(() => services.GetRequiredService<LocalContentExtractor>());

        var viewModel = services.GetRequiredService<MainViewModel>();
        var approvals = services.GetRequiredService<AgentApprovalViewModel>();
        await approvals.InitializeAsync();
        var updater = services.GetRequiredService<UpdateViewModel>();
        updater.RestartRequested += (_, _) => desktop.Shutdown();
        var window = services.GetRequiredService<MainWindow>();
        services.GetRequiredService<DesktopInteractionService>().Attach(window);
        await viewModel.InitializeAsync(splash.SetStatus);
        await minimumVisibility;
        desktop.MainWindow = window;
        window.Show();
        splash.Close();
        window.Activate();
        approvals.BeginPolling();
        updater.BeginAutomaticCheck();
    }
}
