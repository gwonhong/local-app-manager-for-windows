using LocalAppManager.Core.Diagnostics;
using LocalAppManager.Core.Runtime;
using LocalAppManager.Core.Storage;
using LocalAppManager.Services;
using LocalAppManager.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

namespace LocalAppManager;

public partial class App : Application
{
    private MainWindow? _window;
    private TrayService? _tray;
    private bool _exiting;

    public static new App Current => (App)Application.Current;

    public ProjectManager Manager { get; }
    public NotificationService Notifications { get; } = new();
    public MainViewModel ViewModel { get; private set; } = null!;
    public MainWindow? Window => _window;

    public App()
    {
        var paths = new AppPaths();
        AppLog.Initialize(paths.LogsDir);
        HookExceptionHandlers();
        InitializeComponent();

        Manager = new ProjectManager(paths);
        Manager.Load();
    }

    private void HookExceptionHandlers()
    {
        // XAML failures often arrive as a COM exception without a managed stack; log everything.
        UnhandledException += (_, e) =>
        {
            AppLog.Error($"XAML unhandled exception: {e.Message}", e.Exception);
            // Keep running: dying would kill every project's process tree (job objects) for a UI glitch.
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppLog.Error($"AppDomain unhandled exception (terminating={e.IsTerminating})", e.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Error("Unobserved task exception", e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        Ui.Initialize(DispatcherQueue.GetForCurrentThread());
        ViewModel = new MainViewModel(Manager);

        Notifications.Invoked += (action, projectId) => Ui.Run(() => OnNotificationInvoked(action, projectId));
        Notifications.Register();
        Manager.ProjectCrashed += OnProjectCrashed;
        Program.Redirected += e => Ui.Run(() => OnRedirected(e));

        _window = new MainWindow(ViewModel);
        _tray = new TrayService(this, ViewModel);

        // Launched by clicking a toast while the app wasn't running?
        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        if (activation.Kind == ExtendedActivationKind.AppNotification && activation.Data is AppNotificationActivatedEventArgs toast)
        {
            ShowWindow();
            Notifications.HandleArguments(toast.Arguments);
        }
        else if (!Program.StartInBackground)
        {
            ShowWindow();
        }
        AppLog.Info($"launched (background={Program.StartInBackground})");
        foreach (var folder in Program.FoldersToAdd) _window.Page.AddFolder(folder);

        Ui.Forget(Manager.StartAutoStartProjectsAsync(), "auto-start projects");
    }

    private void OnRedirected(AppActivationArguments e)
    {
        if (e.Kind == ExtendedActivationKind.AppNotification && e.Data is AppNotificationActivatedEventArgs toast)
        {
            Notifications.HandleArguments(toast.Arguments);
            return;
        }
        var folders = new List<string>();
        if (e.Kind == ExtendedActivationKind.Launch && e.Data is Windows.ApplicationModel.Activation.ILaunchActivatedEventArgs launch)
        {
            // Arguments holds the full command line including the exe; drop it.
            var parts = Program.SplitCommandLine(launch.Arguments ?? "").ToList();
            if (parts.Count > 0 && parts[0].EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) parts.RemoveAt(0);
            var parsed = Program.ParseArguments(parts);
            folders = parsed.Folders;
            // A redirected autostart launch (--background) shouldn't pop the window up.
            if (parsed.Background && folders.Count == 0) return;
        }
        AppLog.Info($"activation redirected here ({e.Kind}, {folders.Count} folders)");
        ShowWindow();
        foreach (var folder in folders) _window?.Page.AddFolder(folder);
    }

    private void OnNotificationInvoked(string action, string? projectId)
    {
        if (action == "restart" && projectId is not null)
        {
            Ui.Forget(Manager.StartAsync(projectId), "restart from toast");
            return;
        }
        ShowWindow();
        if (projectId is not null && ViewModel.Find(projectId) is { } vm) ViewModel.SelectedProject = vm;
    }

    private void OnProjectCrashed(ProjectRuntime runtime, int code)
    {
        if (!Manager.Settings.NotifyOnCrash) return;
        string detail = $"종료 코드 {ProjectRuntimeFormat.ExitCode(code).Replace("코드 ", "")}"
            + (runtime.Definition.AutoRestart ? " · 자동으로 다시 시작합니다." : "");
        Notifications.ShowCrash(runtime.Id, runtime.Definition.Name, detail, canRestart: !runtime.Definition.AutoRestart);
    }

    public void ShowWindow() => _window?.ShowAndActivate();

    /// <summary>
    /// Quits for real: stops every project (Ctrl+C, then kill after a few seconds), saves window
    /// placement, removes the tray icon. Even if this is skipped, the job objects kill all trees.
    /// </summary>
    public async void Quit()
    {
        if (_exiting) return;
        _exiting = true;
        AppLog.Info("quitting");
        try
        {
            _window?.SavePlacement();
            Manager.SaveSettings();
            _window?.AppWindow.Hide();
            _tray?.Dispose();
            await Task.WhenAny(Manager.StopAllAsync(), Task.Delay(TimeSpan.FromSeconds(4)));
            Manager.KillAll();
        }
        catch (Exception e)
        {
            AppLog.Error("error while quitting", e);
        }
        _window?.CloseForReal();
        Exit();
    }

    public bool IsExiting => _exiting;
}
