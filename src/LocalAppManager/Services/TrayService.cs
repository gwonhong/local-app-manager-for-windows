using CommunityToolkit.Mvvm.Input;
using H.NotifyIcon;
using LocalAppManager.Core.Diagnostics;
using LocalAppManager.Core.Storage;
using LocalAppManager.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LocalAppManager.Services;

/// <summary>
/// Tray icon (H.NotifyIcon — WinUI has none built in). Left click shows the window; the context menu
/// lists every project with a check mark when it's running, and clicking one starts/stops it.
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly App _app;
    private readonly MainViewModel _vm;
    private readonly TaskbarIcon _icon;
    private readonly MenuFlyout _menu = new();
    private bool _rebuildQueued;

    public TrayService(App app, MainViewModel vm)
    {
        _app = app;
        _vm = vm;
        _icon = new TaskbarIcon
        {
            ToolTipText = AppPaths.AppName,
            ContextMenuMode = ContextMenuMode.PopupMenu,
            NoLeftClickDelay = true,
            LeftClickCommand = new RelayCommand(app.ShowWindow),
            ContextFlyout = _menu,
        };
        try
        {
            _icon.Icon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        }
        catch (Exception e)
        {
            AppLog.Warn("tray icon image failed to load", e);
        }
        Rebuild();
        _icon.ForceCreate(enablesEfficiencyMode: false);
        vm.TrayStateChanged += QueueRebuild;
    }

    private void QueueRebuild()
    {
        if (_rebuildQueued) return;
        _rebuildQueued = true;
        Ui.Post(() =>
        {
            _rebuildQueued = false;
            Rebuild();
        });
    }

    private void Rebuild()
    {
        int active = _vm.Projects.Count(p => p.Runtime.IsActive);
        _icon.ToolTipText = active == 0 ? $"{AppPaths.AppName} — 실행 중인 프로젝트 없음" : $"{AppPaths.AppName} — {active}개 실행 중";

        var items = _menu.Items;
        items.Clear();
        items.Add(new MenuFlyoutItem { Text = "창 열기", Command = new RelayCommand(_app.ShowWindow) });
        items.Add(new MenuFlyoutSeparator());

        if (_vm.Projects.Count == 0)
        {
            items.Add(new MenuFlyoutItem { Text = "등록된 프로젝트 없음", IsEnabled = false });
        }
        foreach (var p in _vm.Projects)
        {
            var project = p;
            var runtime = project.Runtime;
            string port = runtime.Definition.Port is int pt && project.PortText.Length > 0 ? $"  :{pt}" : "";
            items.Add(new ToggleMenuFlyoutItem
            {
                Text = project.Name + port,
                IsChecked = runtime.IsActive,
                Command = new AsyncRelayCommand(() => runtime.IsActive ? _app.Manager.StopAsync(project.Id) : _app.Manager.StartAsync(project.Id)),
            });
        }

        if (_vm.Groups.Count > 0)
        {
            items.Add(new MenuFlyoutSeparator());
            foreach (var g in _vm.Groups)
            {
                var group = g;
                items.Add(new MenuFlyoutItem
                {
                    Text = group.AnyActive ? $"그룹 정지: {group.Name}" : $"그룹 시작: {group.Name}",
                    Command = new AsyncRelayCommand(() => group.AnyActive ? _app.Manager.StopGroupAsync(group.Id) : _app.Manager.StartGroupAsync(group.Id)),
                });
            }
        }

        items.Add(new MenuFlyoutSeparator());
        items.Add(new MenuFlyoutItem { Text = "모두 정지", IsEnabled = active > 0, Command = new AsyncRelayCommand(_app.Manager.StopAllAsync) });
        items.Add(new MenuFlyoutItem { Text = "종료", Command = new RelayCommand(_app.Quit) });
    }

    public void Dispose()
    {
        _vm.TrayStateChanged -= QueueRebuild;
        _icon.Dispose();
    }
}
