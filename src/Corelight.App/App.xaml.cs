using System.IO;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Corelight.App.Services;
using Corelight.App.ViewModels;
using Corelight.Core;
using Corelight.Core.Backup;
using Corelight.Core.Catalog;
using Corelight.Core.Platform;
using Corelight.Core.Scanning;
using Corelight.Core.Startup;
using Corelight.Core.SystemInfo;
using Corelight.Core.Tweaks;

namespace Corelight.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private Mutex? _singleInstance;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(true, @"Global\Corelight.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            System.Windows.MessageBox.Show("Corelight уже запущен.", "Corelight", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        string? tourDir = null;
#if DEBUG
        // Тур только читает систему, поэтому при разработке его можно запускать без прав администратора.
        tourDir = e.Args.FirstOrDefault(a => a.StartsWith("--tour=", StringComparison.Ordinal))?["--tour=".Length..];
#endif
        if (tourDir is null && !new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        {
            System.Windows.MessageBox.Show(
                "Для изменения системных настроек нужны права администратора. Запустите программу от имени администратора.",
                "Corelight", MessageBoxButton.OK, MessageBoxImage.Warning);
            Shutdown();
            return;
        }

        Directory.CreateDirectory(AppPaths.LogDirectory);
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.File(Path.Combine(AppPaths.LogDirectory, "log-.txt"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 14)
            .CreateLogger();

        DispatcherUnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => Log.Error(args.Exception, "Необработанное исключение в фоновой задаче");

        _services = BuildServices();
        LogStartupProblems(_services);

        var window = _services.GetRequiredService<MainWindow>();
        MainWindow = window;
#if DEBUG
        if (tourDir is not null)
            window.Loaded += async (_, _) => await DebugTour.RunAsync(_services, window, tourDir);
#endif
        window.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _services?.Dispose();
        Log.CloseAndFlush();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    private static ServiceProvider BuildServices()
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSerilog());

        // Система
        services.AddSingleton<IRegistryAccess, WindowsRegistryAccess>();
        services.AddSingleton<IProcessRunner, ProcessRunner>();
        services.AddSingleton<IServiceControl, WindowsServiceControl>();
        services.AddSingleton<IScheduledTaskControl, WindowsScheduledTaskControl>();
        services.AddSingleton<IAppxControl, WindowsAppxControl>();
        services.AddSingleton<IPowerControl, WindowsPowerControl>();
        services.AddSingleton<IOneDriveControl, WindowsOneDriveControl>();
        services.AddSingleton<PlatformServices>();

        // Ядро
        services.AddSingleton(_ => new JsonBackupStore(AppPaths.BackupFile));
        services.AddSingleton<IBackupStore>(sp => sp.GetRequiredService<JsonBackupStore>());
        services.AddSingleton(_ => TweakCatalog.LoadEmbedded());
        services.AddSingleton<TweakEngine>();
        services.AddSingleton<HardwareProbe>();
        services.AddSingleton<Scanner>();
        services.AddSingleton<RestorePointService>();
        services.AddSingleton<StartupManager>();

        // Приложение
        services.AddSingleton<AppState>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<ApplyCoordinator>();
        services.AddSingleton<HomeViewModel>();
        services.AddSingleton<ScanResultsViewModel>();
        services.AddSingleton<AdvancedViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<MainWindow>();

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }

    private static void LogStartupProblems(IServiceProvider services)
    {
        var log = services.GetRequiredService<ILogger<App>>();
        foreach (var rejected in services.GetRequiredService<TweakCatalog>().Rejected)
            log.LogWarning("Твик отклонён валидатором: {Reason}", rejected);

        var corrupt = services.GetRequiredService<JsonBackupStore>().CorruptCopyPath;
        if (corrupt is not null)
        {
            log.LogError("Файл бэкапа повреждён, копия сохранена в {Path}", corrupt);
            System.Windows.MessageBox.Show(
                $"Файл с историей изменений повреждён и не может быть прочитан. Его копия сохранена:\n{corrupt}\n\n" +
                "Откатить прошлые изменения через раздел «История» не получится, но точки восстановления Windows продолжают работать.",
                "Corelight", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Необработанное исключение");
        System.Windows.MessageBox.Show(
            $"Произошла непредвиденная ошибка:\n{e.Exception.Message}\n\nПодробности записаны в журнал: {AppPaths.LogDirectory}",
            "Corelight", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
