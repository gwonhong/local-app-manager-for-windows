using LocalAppManager.Core.Diagnostics;
using LocalAppManager.Core.Runtime;
using LocalAppManager.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LocalAppManager.Views;

public sealed partial class AppSettingsDialog : ContentDialog
{
    private readonly ProjectManager _manager;
    private readonly NumberBox _rangeStart = new() { Header = "포트 범위 시작", Minimum = 1024, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly NumberBox _rangeEnd = new() { Header = "포트 범위 끝", Minimum = 1024, Maximum = 65535, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly NumberBox _logLines = new() { Header = "프로젝트마다 보관할 로그 줄 수", Minimum = 500, Maximum = 100000, SmallChange = 1000, LargeChange = 5000, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact };
    private readonly ToggleSwitch _closeToTray = new() { Header = "닫기 버튼을 누르면 트레이로 숨기기" };
    private readonly ToggleSwitch _startAtLogin = new() { Header = "Windows에 로그인하면 자동 실행 (트레이로 시작)" };
    private readonly ToggleSwitch _notify = new() { Header = "프로젝트가 비정상 종료하면 알림" };
    private readonly ComboBox _terminal = new() { Header = "\"터미널로 열기\"에 쓸 터미널", HorizontalAlignment = HorizontalAlignment.Stretch };

    public AppSettingsDialog(ProjectManager manager)
    {
        _manager = manager;
        Title = "설정";
        PrimaryButtonText = "저장";
        CloseButtonText = "취소";
        DefaultButton = ContentDialogButton.Primary;
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];

        var s = manager.Settings;
        _rangeStart.Value = s.PortRangeStart;
        _rangeEnd.Value = s.PortRangeEnd;
        _logLines.Value = s.LogCapacity;
        _closeToTray.IsOn = s.CloseToTray;
        _startAtLogin.IsOn = StartupRegistration.IsEnabled;
        _notify.IsOn = s.NotifyOnCrash;
        foreach (var (tag, label) in new[] { ("wt", "Windows Terminal"), ("pwsh", "PowerShell 7"), ("powershell", "Windows PowerShell"), ("cmd", "명령 프롬프트") })
        {
            var item = new ComboBoxItem { Content = label, Tag = tag };
            _terminal.Items.Add(item);
            if (tag == s.Terminal) _terminal.SelectedItem = item;
        }
        if (_terminal.SelectedIndex < 0) _terminal.SelectedIndex = 0;

        var caption = (Style)Application.Current.Resources["CaptionSecondaryStyle"];
        var panel = new StackPanel { Spacing = 12, MinWidth = 460, Padding = new Thickness(0, 0, 16, 0) };
        var range = new Grid { ColumnSpacing = 12 };
        range.ColumnDefinitions.Add(new ColumnDefinition());
        range.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(_rangeEnd, 1);
        range.Children.Add(_rangeStart);
        range.Children.Add(_rangeEnd);
        panel.Children.Add(range);
        panel.Children.Add(new TextBlock { Text = "새 프로젝트에는 이 범위에서 비어 있는 포트를 배정합니다. 이미 배정된 포트는 바뀌지 않습니다.", Style = caption, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_logLines);
        panel.Children.Add(_terminal);
        panel.Children.Add(_closeToTray);
        panel.Children.Add(_startAtLogin);
        if (StartupRegistration.IsStale)
            panel.Children.Add(new TextBlock { Text = "자동 실행 항목이 다른 위치의 실행 파일을 가리킵니다. 저장하면 지금 실행 파일로 고칩니다.", Style = caption, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_notify);

        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 8, 0, 0) };
        var openData = new HyperlinkButton { Content = "데이터 폴더 열기" };
        openData.Click += (_, _) => ShellLauncher.OpenFolder(manager.Paths.DataDir);
        var openLog = new HyperlinkButton { Content = "앱 진단 로그 열기" };
        openLog.Click += (_, _) =>
        {
            if (AppLog.FilePath is { } p && File.Exists(p)) ShellLauncher.OpenFile(p);
            else ShellLauncher.OpenFolder(manager.Paths.LogsDir);
        };
        links.Children.Add(openData);
        links.Children.Add(openLog);
        panel.Children.Add(links);
        panel.Children.Add(new TextBlock { Text = manager.Paths.DataDir, Style = caption, IsTextSelectionEnabled = true });

        Content = new ScrollViewer { Content = panel, MaxHeight = 640 };
    }

    public void Apply()
    {
        _manager.UpdateSettings(s =>
        {
            if (!double.IsNaN(_rangeStart.Value)) s.PortRangeStart = (int)_rangeStart.Value;
            if (!double.IsNaN(_rangeEnd.Value)) s.PortRangeEnd = (int)_rangeEnd.Value;
            if (!double.IsNaN(_logLines.Value)) s.LogCapacity = (int)_logLines.Value;
            s.CloseToTray = _closeToTray.IsOn;
            s.NotifyOnCrash = _notify.IsOn;
            s.Terminal = (_terminal.SelectedItem as ComboBoxItem)?.Tag as string ?? "wt";
        });
        try
        {
            if (_startAtLogin.IsOn != StartupRegistration.IsEnabled || (_startAtLogin.IsOn && StartupRegistration.IsStale))
                StartupRegistration.SetEnabled(_startAtLogin.IsOn);
        }
        catch (Exception e)
        {
            AppLog.Error("start at login toggle failed", e);
        }
    }
}
