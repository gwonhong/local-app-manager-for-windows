using LocalAppManager.Core.Projects;
using LocalAppManager.Core.Runtime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace LocalAppManager.Views;

/// <summary>
/// Edits one project's definition (name, framework, how a pinned port is passed, environment, behavior).
/// The commands themselves are managed in <see cref="CommandsDialog"/>. Shows the exact command the default chip
/// will run with.
/// </summary>
public sealed partial class ProjectSettingsDialog : ContentDialog
{
    private readonly ProjectDefinition _draft;
    private readonly IReadOnlyDictionary<string, string> _scripts;

    private readonly TextBox _name = new() { Header = "이름" };
    private readonly TextBox _path = Mono(new TextBox { Header = "폴더", IsReadOnly = true });
    private readonly ComboBox _framework = new() { Header = "프레임워크", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel _portSection = new() { Spacing = 12 };
    private readonly ComboBox _portMode = new() { Header = "포트 전달 방식", HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _portArgs = Mono(new TextBox { Header = "붙일 인자", PlaceholderText = "--port {port}" });
    private readonly TextBox _portEnv = Mono(new TextBox { Header = "환경 변수 이름", PlaceholderText = "PORT" });
    private readonly TextBlock _preview = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private readonly TextBox _env = Mono(new TextBox { Header = "환경 변수 (한 줄에 하나, KEY=VALUE)", AcceptsReturn = true, MinHeight = 80, TextWrapping = TextWrapping.NoWrap });
    private readonly TextBox _browserPath = Mono(new TextBox { Header = "브라우저로 열 경로", PlaceholderText = "/" });
    private readonly ToggleSwitch _autoRestart = new() { Header = "서버가 크래시하면 자동 재시작 (1초부터 두 배씩, 최대 60초 간격)" };

    public ProjectDefinition? Result { get; private set; }

    /// <summary>The user pressed "remove project"; the caller asks for confirmation once this dialog is closed.</summary>
    public bool RemoveRequested { get; private set; }

    public ProjectSettingsDialog(ProjectManager manager, ProjectDefinition definition)
    {
        _draft = definition.Clone();
        _scripts = FrameworkDetector.ReadScripts(_draft.Path);
        Title = "프로젝트 설정";
        PrimaryButtonText = "저장";
        CloseButtonText = "취소";
        DefaultButton = ContentDialogButton.Primary;
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        // The default 548px cap is too narrow for these forms; it only takes effect when set on the dialog itself.
        Resources["ContentDialogMaxWidth"] = 820.0;
        Resources["ContentDialogMaxHeight"] = 1000.0;

        foreach (var fw in FrameworkDetector.All) _framework.Items.Add(new ComboBoxItem { Content = fw.DisplayName, Tag = fw.Id });
        _portMode.Items.Add(new ComboBoxItem { Content = "환경 변수로 전달 (Next, CRA, Express…)", Tag = PortMode.Env });
        _portMode.Items.Add(new ComboBoxItem { Content = "명령 뒤에 인자로 붙이기 (Vite, Astro, Angular…)", Tag = PortMode.Args });
        _portMode.Items.Add(new ComboBoxItem { Content = "전달하지 않음 (명령에 {port}를 직접 쓰거나 고정 포트)", Tag = PortMode.None });

        Load();
        Content = BuildLayout();

        foreach (var tb in new[] { _portArgs, _portEnv, _env }) tb.TextChanged += (_, _) => UpdatePreview();
        _portMode.SelectionChanged += (_, _) => UpdatePreview();
        _framework.SelectionChanged += (_, _) => OnFrameworkChanged();
        PrimaryButtonClick += OnSave;
        Opened += (_, _) => _name.Focus(FocusState.Programmatic);
        UpdatePreview();
    }

    private static TextBox Mono(TextBox tb)
    {
        tb.FontFamily = (FontFamily)Application.Current.Resources["LogFontFamily"];
        // The header would inherit the monospace font, which renders Korean poorly.
        if (tb.Header is string header)
            tb.Header = new TextBlock { Text = header, FontFamily = (FontFamily)Application.Current.Resources["ContentControlThemeFontFamily"] };
        return tb;
    }

    private UIElement BuildLayout()
    {
        var detect = new Button { Content = "다시 감지", VerticalAlignment = VerticalAlignment.Bottom };
        ToolTipService.SetToolTip(detect, "폴더를 다시 살펴서 프레임워크·패키지 매니저·포트 방식을 채웁니다.");
        detect.Click += (_, _) => Redetect();

        var previewCard = new Border
        {
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorSecondaryBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = "▶가 실행할 명령 (기본 명령 ★)", Style = (Style)Application.Current.Resources["CaptionSecondaryStyle"] },
                    _preview,
                },
            },
        };
        _preview.FontFamily = (FontFamily)Application.Current.Resources["LogFontFamily"];
        _preview.FontSize = 12;

        var panel = new StackPanel { Spacing = 12, Padding = new Thickness(0, 0, 16, 0), MinWidth = 600 };
        panel.Children.Add(Row(_name, _framework, detect));
        panel.Children.Add(_path);
        // Pinning itself lives in the project header (pin button). This only says how a pinned port reaches the
        // server, and is shown only for dev servers we know how to hand a port to (or when one is already pinned).
        _portSection.Children.Add(Section("포트 전달 방식"));
        _portSection.Children.Add(new TextBlock
        {
            Text = "위쪽 핀 버튼으로 포트를 고정하면 서버를 띄우는 명령에 이 방식으로 넘깁니다(환경 변수는 모든 명령에). 고정하지 않으면 아무것도 넘기지 않고, 실제로 열린 포트만 보여 줍니다.",
            Style = (Style)Application.Current.Resources["CaptionSecondaryStyle"],
            TextWrapping = TextWrapping.Wrap,
        });
        _portSection.Children.Add(_portMode);
        _portSection.Children.Add(Row(_portArgs, _portEnv));
        panel.Children.Add(_portSection);
        panel.Children.Add(previewCard);
        panel.Children.Add(Section("추가 설정"));
        panel.Children.Add(_env);
        panel.Children.Add(_browserPath);
        panel.Children.Add(_autoRestart);

        // Removing the project (the confirmation follows once this dialog is closed).
        var remove = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 8,
                Children =
                {
                    new FontIcon { Glyph = "", FontSize = 14 },
                    new TextBlock { Text = "목록에서 제거…" },
                },
            },
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
        };
        AutomationProperties.SetAutomationId(remove, "RemoveProjectButton");
        remove.Click += (_, _) =>
        {
            RemoveRequested = true;
            Hide();
        };
        panel.Children.Add(Section("프로젝트 제거"));
        panel.Children.Add(new TextBlock
        {
            Text = "목록에서만 지웁니다. 프로젝트 폴더와 파일은 그대로 둡니다.",
            Style = (Style)Application.Current.Resources["CaptionSecondaryStyle"],
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(remove);

        return new ScrollViewer { Content = panel, MaxHeight = 640, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static TextBlock Section(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) };

    private static Grid Row(params FrameworkElement[] items)
    {
        var g = new Grid { ColumnSpacing = 12 };
        for (int i = 0; i < items.Length; i++)
        {
            bool fixedWidth = items[i] is Button || items[i] is NumberBox || items[i] is TextBlock;
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = fixedWidth ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(items[i], i);
            g.Children.Add(items[i]);
        }
        return g;
    }

    private void Load()
    {
        _name.Text = _draft.Name;
        _path.Text = _draft.Path;
        SelectByTag(_framework, _draft.Framework);
        UpdatePortSectionVisibility(FrameworkDetector.ById(_draft.Framework));
        SelectByTag(_portMode, _draft.PortMode);
        _portArgs.Text = _draft.PortArgs;
        _portEnv.Text = _draft.PortEnvVar;
        _env.Text = string.Join("\r", _draft.Environment.Select(kv => $"{kv.Key}={kv.Value}"));
        _browserPath.Text = _draft.BrowserPath;
        _autoRestart.IsOn = _draft.AutoRestart;
    }

    private static void SelectByTag(ComboBox combo, object tag)
    {
        foreach (var item in combo.Items.OfType<ComboBoxItem>())
            if (Equals(item.Tag, tag)) combo.SelectedItem = item;
        combo.SelectedIndex = combo.SelectedIndex < 0 ? combo.Items.Count - 1 : combo.SelectedIndex;
    }

    private PortMode SelectedPortMode => (_portMode.SelectedItem as ComboBoxItem)?.Tag is PortMode m ? m : PortMode.None;

    private void OnFrameworkChanged()
    {
        if ((_framework.SelectedItem as ComboBoxItem)?.Tag is not string id || id == _draft.Framework) return;
        var fw = FrameworkDetector.ById(id);
        // A different framework means a different way to pass the port.
        SelectByTag(_portMode, fw.PortMode);
        if (fw.PortMode == PortMode.Args) _portArgs.Text = fw.PortArgs;
        _draft.Framework = id;
        UpdatePortSectionVisibility(fw);
        UpdatePreview();
    }

    private void Redetect()
    {
        var d = FrameworkDetector.Detect(_draft.Path);
        _draft.Framework = d.Framework.Id;
        _draft.PackageManager = d.PackageManager;
        SelectByTag(_framework, d.Framework.Id);
        SelectByTag(_portMode, d.Framework.PortMode);
        _portArgs.Text = d.Framework.PortArgs;
        UpdatePortSectionVisibility(d.Framework);
        UpdatePreview();
    }

    private void UpdatePortSectionVisibility(FrameworkInfo fw) =>
        _portSection.Visibility = fw.CanPinPort || _draft.Port is not null || _draft.Commands.Any(c => c.Command.Contains("{port}"))
            ? Visibility.Visible : Visibility.Collapsed;

    private ProjectDefinition Collect()
    {
        var d = _draft.Clone();
        d.Name = _name.Text.Trim();
        d.PortMode = SelectedPortMode;
        d.PortArgs = _portArgs.Text.Trim();
        d.PortEnvVar = string.IsNullOrWhiteSpace(_portEnv.Text) ? "PORT" : _portEnv.Text.Trim();
        d.BrowserPath = string.IsNullOrWhiteSpace(_browserPath.Text) ? "/" : _browserPath.Text.Trim();
        d.AutoRestart = _autoRestart.IsOn;

        d.Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in Lines(_env.Text))
        {
            int eq = line.IndexOf('=');
            if (eq > 0) d.Environment[line[..eq].Trim()] = line[(eq + 1)..];
        }
        return d;
    }

    private static IEnumerable<string> Lines(string text) =>
        text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'));

    private void UpdatePreview()
    {
        _portArgs.IsEnabled = SelectedPortMode == PortMode.Args;
        _portEnv.IsEnabled = SelectedPortMode == PortMode.Env;

        var d = Collect();
        var first = CommandList.DefaultOf(d, CommandList.Resolve(d, _scripts));
        if (first is null)
        {
            _preview.Text = "(명령 없음 — 명령 관리에서 추가하세요)";
            return;
        }
        var built = CommandBuilder.Build(d, first.ShellCommand, first.IsServer, new Dictionary<string, string>());
        var vars = new List<string>();
        if (d.Port is int p && d.PortMode == PortMode.Env && !first.ShellCommand.Contains("{port}")) vars.Add($"{d.PortEnvVar}={p}");
        foreach (var (k, v) in d.Environment) vars.Add($"{k}={v}");
        var fw = FrameworkDetector.ById(d.Framework);
        if (fw.ExtraEnv is not null) vars.AddRange(fw.ExtraEnv.Where(kv => !d.Environment.ContainsKey(kv.Key)).Select(kv => $"{kv.Key}={kv.Value}"));
        _preview.Text = $"{first.Name}: {built.ShellCommand}" + (vars.Count > 0 ? "\n환경 변수: " + string.Join("  ", vars) : "")
            + (d.Port is null && first.ShellCommand.Contains("{port}") ? "\n⚠ 포트를 고정하지 않으면 {port}가 그대로 남습니다." : "");
    }

    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var d = Collect();
        if (string.IsNullOrWhiteSpace(d.Name))
        {
            args.Cancel = true;
            _name.Focus(FocusState.Programmatic);
            return;
        }
        Result = d;
    }
}
