using LocalAppManager.Core.Diagnostics;
using LocalAppManager.Services;
using LocalAppManager.ViewModels;
using LocalAppManager.Views;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace LocalAppManager;

public sealed partial class MainPage : Page
{
    private MainWindow? _window;
    private bool _programmaticScroll;
    private bool _dialogOpen;
    private DispatcherQueueTimer? _toastTimer;

    public MainPage()
    {
        InitializeComponent();
    }

    public MainViewModel ViewModel { get; private set; } = null!;

    public void Initialize(MainViewModel vm, MainWindow window)
    {
        ViewModel = vm;
        _window = window;
        Bindings.Update();

        ListColumn.Width = new GridLength(Math.Clamp(vm.Manager.Settings.ProjectListWidth, 220, 520));
        ApplyWrap();
        Splitter.Resized += w => vm.Manager.UpdateSettings(s => s.ProjectListWidth = w);

        vm.Log.Appended += OnLogAppended;
        vm.Log.Rebuilt += () => Ui.Post(ScrollToEnd, DispatcherQueuePriority.Low);
        vm.Log.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LogViewModel.AutoScroll) && vm.Log.AutoScroll) ScrollToEnd();
        };

        ProjectItemViewModel.EditRequested += p => Ui.Forget(EditProjectAsync(p), "edit project");
        ProjectItemViewModel.ManageCommandsRequested += p => Ui.Forget(ManageCommandsAsync(p), "manage commands");
        ProjectItemViewModel.RemoveRequested += p => Ui.Forget(RemoveProjectAsync(p), "remove project");
        ProjectItemViewModel.ConfirmInterrupt = ConfirmInterruptAsync;
        GroupViewModel.EditRequested += g => Ui.Forget(EditGroupAsync(g), "edit group");
        GroupViewModel.RemoveRequested += g => vm.Manager.RemoveGroup(g.Id);

        Converters.Theme = ActualTheme;
        ActualThemeChanged += (_, _) =>
        {
            // ANSI colors and state brushes are resolved per theme in code; refresh them.
            Converters.Theme = ActualTheme;
            vm.Log.Rebuild();
            Bindings.Update();
            foreach (var p in vm.Projects) p.NotifyThemeChanged();
        };

        AddAccelerator(VirtualKey.N, VirtualKeyModifiers.Control, () => Ui.Forget(AddProjectAsync(), "add project"));
        AddAccelerator(VirtualKey.F, VirtualKeyModifiers.Control, () => FilterBox.Focus(FocusState.Keyboard));
        AddAccelerator(VirtualKey.Enter, VirtualKeyModifiers.Control, () => ViewModel.SelectedProject?.ToggleCommand.Execute(null));
        AddAccelerator(VirtualKey.R, VirtualKeyModifiers.Control, () =>
        {
            if (ViewModel.SelectedProject is { IsRunningAny: true } p) p.RestartCommand.Execute(null);
        });
        AddAccelerator(VirtualKey.L, VirtualKeyModifiers.Control, () => ViewModel.Log.Clear());
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var acc = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        acc.Invoked += (_, e) =>
        {
            if (_dialogOpen) return;
            e.Handled = true;
            action();
        };
        KeyboardAccelerators.Add(acc);
    }

    // ------------------------------------------------------------------ projects

    private void AddProject_Click(object sender, RoutedEventArgs e) => Ui.Forget(AddProjectAsync(), "add project");

    private bool _picking;

    private async Task AddProjectAsync()
    {
        if (_window is null || _picking || _dialogOpen) return;
        _picking = true;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(_window.AppWindow.Id)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                CommitButtonText = "이 폴더 추가",
            };
            var result = await picker.PickSingleFolderAsync();
            if (result is null || string.IsNullOrEmpty(result.Path)) return;
            AddFolder(result.Path);
        }
        finally
        {
            _picking = false;
        }
    }

    /// <summary>Adds a folder (also used by drag-and-drop onto the window).</summary>
    public void AddFolder(string path)
    {
        try
        {
            bool existed = ViewModel.Manager.Projects.Any(p => string.Equals(Path.GetFullPath(p.Definition.Path).TrimEnd('\\'), Path.GetFullPath(path).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            var vm = ViewModel.AddProject(path);
            if (vm is null) return;
            ShowToast(existed ? $"이미 등록된 프로젝트입니다: {vm.Name}" : $"추가했습니다: {vm.Name} ({vm.Subtitle})", existed ? InfoBarSeverity.Informational : InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            AppLog.Error("add project failed", ex);
            ShowToast("추가하지 못했습니다: " + ex.Message, InfoBarSeverity.Error);
        }
    }

    // Dropping folders from Explorer registers them.
    private void LayoutRoot_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Link;
        e.DragUIOverride.Caption = "프로젝트로 추가";
        e.Handled = true;
    }

    private async void LayoutRoot_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.Handled = true;
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            foreach (var item in items.OfType<Windows.Storage.StorageFolder>()) AddFolder(item.Path);
            if (!items.OfType<Windows.Storage.StorageFolder>().Any()) ShowToast("폴더를 끌어다 놓으세요.", InfoBarSeverity.Informational);
        }
        catch (Exception ex)
        {
            AppLog.Error("drop failed", ex);
        }
    }

    private async Task ManageCommandsAsync(ProjectItemViewModel project)
    {
        if (_dialogOpen) return;
        var dialog = new CommandsDialog(ViewModel.Manager, project.Id) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        _dialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || dialog.Result is null) return;
            ViewModel.Manager.SetCommands(project.Id, dialog.Result, dialog.DefaultKey);
            project.RefreshScripts();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private async Task EditProjectAsync(ProjectItemViewModel project)
    {
        if (_dialogOpen) return;
        var dialog = new ProjectSettingsDialog(ViewModel.Manager, project.Runtime.Definition) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        _dialogOpen = true;
        try
        {
            await dialog.ShowAsync();
        }
        finally
        {
            _dialogOpen = false;
        }
        // Only one ContentDialog can be open at a time, so the removal confirmation comes after this one closed.
        if (dialog.RemoveRequested)
        {
            await RemoveProjectAsync(project);
            return;
        }
        if (dialog.Result is null) return;
        ViewModel.Manager.UpdateProject(dialog.Result);
        project.RefreshScripts();
    }

    private async Task RemoveProjectAsync(ProjectItemViewModel project)
    {
        if (_dialogOpen) return;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = $"{project.Name}을(를) 목록에서 제거할까요?",
            Content = new TextBlock
            {
                Text = (project.IsActive ? "실행 중인 프로세스는 정지합니다. " : "") + "프로젝트 폴더와 파일은 그대로 둡니다. 설정(명령, 포트, 환경 변수)만 지워집니다.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "제거",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Close,
        };
        _dialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }
        finally
        {
            _dialogOpen = false;
        }
        await ViewModel.RemoveProjectAsync(project);
    }

    /// <summary>Warns before a restart or another command stops the one that runs.</summary>
    private async Task<bool> ConfirmInterruptAsync(string title, string message)
    {
        if (_dialogOpen) return false;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "계속",
            CloseButtonText = "취소",
            DefaultButton = ContentDialogButton.Primary,
        };
        _dialogOpen = true;
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    // ------------------------------------------------------------------ port pinning (header pin button)

    private void PinPortFlyout_Opening(object sender, object e)
    {
        if (ViewModel.SelectedProject is not { } p) return;
        bool pinned = p.HasPinnedPort;
        PinPortBox.Value = ViewModel.Manager.SuggestPinPort(p.Id) ?? double.NaN;
        UnpinButton.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
        PinApplyButton.Content = pinned ? "변경" : "고정";
        Grid.SetColumn(PinApplyButton, pinned ? 1 : 0);
        Grid.SetColumnSpan(PinApplyButton, pinned ? 1 : 2);
        UpdatePinStatus();
    }

    private void PinPortBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdatePinStatus();

    private int? PinPortValue => double.IsNaN(PinPortBox.Value) ? null : (int)PinPortBox.Value;

    /// <summary>Warns about the chosen port before pinning: another project pins it, or something else is using it.</summary>
    private void UpdatePinStatus()
    {
        if (ViewModel.SelectedProject is not { } p || PinPortValue is not int port)
        {
            PinPortStatus.Text = "";
            PinApplyButton.IsEnabled = false;
            return;
        }
        var clash = ViewModel.Manager.PinnedBy(port, p.Id);
        PinApplyButton.IsEnabled = clash is null;
        bool ours = p.Runtime.ListeningPorts.Contains(port);
        PinPortStatus.Text =
            clash is not null ? $"⚠ {clash.Definition.Name}이(가) 이미 고정한 포트입니다."
            : !ours && !Core.Ports.PortProbe.IsFree(port) ? "⚠ 지금 다른 프로그램이 쓰고 있습니다. 시작할 때 비어 있어야 합니다."
            : p.IsActive && p.Runtime.RunPort != port ? "실행 중입니다. 재시작하면 적용됩니다."
            : "";
    }

    private void PinFindFree_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProject is not { } p) return;
        if (ViewModel.Manager.AllocatePort(p.Id, PinPortValue) is int free) PinPortBox.Value = free;
        else PinPortStatus.Text = "앱의 포트 범위에 빈 포트가 없습니다.";
    }

    private void PinApply_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProject is not { } p || PinPortValue is not int port) return;
        if (ViewModel.Manager.PinPort(p.Id, port) is { } clash)
        {
            PinPortStatus.Text = $"⚠ {clash.Definition.Name}이(가) 이미 고정한 포트입니다.";
            return;
        }
        p.Refresh();
        PinPortFlyout.Hide();
        ShowToast($"포트를 {port}(으)로 고정했습니다." + (p.IsActive && p.Runtime.RunPort != port ? " 재시작하면 적용됩니다." : ""));
    }

    private void Unpin_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedProject is not { } p) return;
        ViewModel.Manager.PinPort(p.Id, null);
        p.Refresh();
        PinPortFlyout.Hide();
        ShowToast("포트 고정을 풀었습니다." + (p.IsActive ? " 재시작하면 적용됩니다." : ""), InfoBarSeverity.Informational);
    }

    private void ProjectList_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        // The ListView already reordered the ObservableCollection; persist that order.
        for (int i = 0; i < ViewModel.Projects.Count; i++)
        {
            var id = ViewModel.Projects[i].Id;
            if (ViewModel.Manager.Projects.ToList().FindIndex(p => p.Id == id) != i) ViewModel.Manager.MoveProject(id, i);
        }
    }

    // ------------------------------------------------------------------ groups & settings

    private void AddGroup_Click(object sender, RoutedEventArgs e) => Ui.Forget(EditGroupAsync(null), "add group");

    private async Task EditGroupAsync(GroupViewModel? group)
    {
        if (_dialogOpen) return;
        var dialog = new GroupDialog(ViewModel.Manager, group?.Id) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        _dialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (group is null) ViewModel.Manager.AddGroup(dialog.GroupName, dialog.SelectedIds);
            else ViewModel.Manager.UpdateGroup(group.Id, dialog.GroupName, dialog.SelectedIds);
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => Ui.Forget(OpenSettingsAsync(), "settings");

    private async Task OpenSettingsAsync()
    {
        if (_dialogOpen) return;
        var dialog = new AppSettingsDialog(ViewModel.Manager) { XamlRoot = XamlRoot, RequestedTheme = ActualTheme };
        _dialogOpen = true;
        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary) dialog.Apply();
        }
        finally
        {
            _dialogOpen = false;
        }
    }

    private void Quit_Click(object sender, RoutedEventArgs e) => App.Current.Quit();

    // ------------------------------------------------------------------ log

    private void OnLogAppended()
    {
        if (ViewModel.Log.AutoScroll) Ui.Post(ScrollToEnd, DispatcherQueuePriority.Low);
    }

    private void ScrollToEnd()
    {
        if (!ViewModel.Log.AutoScroll) return;
        LogScroller.UpdateLayout();
        _programmaticScroll = true;
        LogScroller.ChangeView(null, LogScroller.ScrollableHeight, null, disableAnimation: true);
    }

    private void LogScroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (e.IsIntermediate) return;
        if (_programmaticScroll)
        {
            _programmaticScroll = false;
            return;
        }
        // The user scrolled: follow the end only while they're at the bottom.
        bool atBottom = LogScroller.VerticalOffset >= LogScroller.ScrollableHeight - 8;
        if (ViewModel.Log.AutoScroll != atBottom) ViewModel.Log.AutoScroll = atBottom;
    }

    private void WrapToggle_Click(object sender, RoutedEventArgs e) => ApplyWrap();

    private void ApplyWrap()
    {
        bool wrap = ViewModel.Log.WrapLines;
        LogRepeater.ItemTemplate = (DataTemplate)Resources[wrap ? "LogLineWrapTemplate" : "LogLineNoWrapTemplate"];
        // Text only wraps when the viewport doesn't offer unlimited width.
        LogScroller.HorizontalScrollMode = wrap ? ScrollMode.Disabled : ScrollMode.Auto;
        LogScroller.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    private void TimestampToggle_Click(object sender, RoutedEventArgs e) =>
        ViewModel.SetLogTimestamps(!ViewModel.Log.ShowTimestamps);

    private void FilterBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            ViewModel.Log.Filter = "";
            e.Handled = true;
        }
    }

    private void CopyLog_Click(object sender, RoutedEventArgs e)
    {
        var text = ViewModel.Log.VisibleText();
        if (text.Length == 0) return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        ShowToast($"{ViewModel.Log.Lines.Count:N0}줄을 복사했습니다.", InfoBarSeverity.Success);
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => ViewModel.Log.Clear();

    private void StdinBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter || ViewModel.SelectedProject is not { } p) return;
        p.Runtime.SendInput(StdinBox.Text + "\r\n");
        p.Runtime.Log.System($"⏎ {StdinBox.Text}");
        StdinBox.Text = "";
        e.Handled = true;
    }

    // ------------------------------------------------------------------ toast

    public void ShowToast(string message, InfoBarSeverity severity = InfoBarSeverity.Success)
    {
        Toast.Message = message;
        Toast.Severity = severity;
        Toast.IsOpen = true;
        _toastTimer ??= CreateToastTimer();
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private DispatcherQueueTimer CreateToastTimer()
    {
        var t = DispatcherQueue.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(3.5);
        t.IsRepeating = false;
        t.Tick += (_, _) => Toast.IsOpen = false;
        return t;
    }
}
