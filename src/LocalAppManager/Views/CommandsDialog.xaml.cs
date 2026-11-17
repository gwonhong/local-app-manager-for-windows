using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LocalAppManager.Core.Projects;
using LocalAppManager.Core.Runtime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LocalAppManager.Views;

/// <summary>
/// The command panel: the project's chips in order. Drag to reorder (the first one is what ▶ runs), add or edit
/// custom commands, remove chips, and put package.json scripts back. Returns the new list in <see cref="Result"/>.
/// </summary>
public sealed partial class CommandsDialog : ContentDialog
{
    private readonly ProjectDefinition _definition;
    private readonly IReadOnlyDictionary<string, string> _scripts;

    public ObservableCollection<CommandRow> Rows { get; } = [];

    /// <summary>The chips in their new order, as <see cref="ProjectDefinition.Commands"/> stores them.</summary>
    public IReadOnlyList<ProjectCommand>? Result { get; private set; }

    /// <summary>Key of the chip marked ★ (null when the list is empty).</summary>
    public string? DefaultKey { get; private set; }

    /// <summary>The row the user marked ★; when it's removed, the first row is the default.</summary>
    private CommandRow? _default;

    public CommandsDialog(ProjectManager manager, string projectId)
    {
        _definition = manager.Get(projectId)?.Definition.Clone() ?? new ProjectDefinition();
        _scripts = FrameworkDetector.ReadScripts(_definition.Path);
        InitializeComponent();
        Style = (Style)Application.Current.Resources["DefaultContentDialogStyle"];
        // The default 548px cap is too narrow; it only takes effect when set on the dialog itself.
        Resources["ContentDialogMaxWidth"] = 860.0;

        var commands = manager.GetCommands(projectId);
        string? defaultKey = CommandList.DefaultOf(_definition, commands)?.Key;
        foreach (var c in commands)
        {
            var row = c.Script is { } s ? ScriptRow(s) : new CommandRow { Name = c.Name, Command = c.ShellCommand };
            if (c.Key == defaultKey) _default = row;
            Rows.Add(row);
        }
        Rows.CollectionChanged += (_, _) => UpdateRows();
        UpdateRows();
        PrimaryButtonClick += OnSave;
    }

    private CommandRow ScriptRow(string script) =>
        new(script, $"{CommandBuilder.ScriptCommand(_definition, script)}  →  {_scripts.GetValueOrDefault(script, "")}") { Name = script };

    /// <summary>The ★ row: the one marked, or the first when it was removed (as <see cref="CommandList.DefaultOf"/> does).</summary>
    private CommandRow? EffectiveDefault => _default is not null && Rows.Contains(_default) ? _default : Rows.FirstOrDefault();

    private void UpdateRows()
    {
        var star = EffectiveDefault;
        foreach (var row in Rows) row.IsDefault = ReferenceEquals(row, star);
        EmptyNote.Visibility = Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddScriptButton.IsEnabled = _scripts.Keys.Any(s => !Rows.Any(r => r.Script == s));
    }

    private void Default_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not CommandRow row) return;
        _default = row;
        UpdateRows();
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is CommandRow row) Rows.Remove(row);
    }

    private void AddCustom_Click(object sender, RoutedEventArgs e)
    {
        var row = new CommandRow { Name = "", Command = "" };
        Rows.Add(row);
        List.ScrollIntoView(row);
        // Focus the new row's name box once its container exists.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (List.ContainerFromItem(row) is ListViewItem item && FindTextBox(item) is { } box) box.Focus(FocusState.Programmatic);
        });
    }

    private static TextBox? FindTextBox(DependencyObject parent)
    {
        for (int i = 0; i < Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is TextBox { Visibility: Visibility.Visible } tb) return tb;
            if (FindTextBox(child) is { } found) return found;
        }
        return null;
    }

    /// <summary>Offers the package.json scripts that aren't in the list (removed earlier, or never added).</summary>
    private void ScriptMenu_Opening(object sender, object e)
    {
        ScriptMenu.Items.Clear();
        foreach (var (name, body) in _scripts)
        {
            if (Rows.Any(r => r.Script == name)) continue;
            var item = new MenuFlyoutItem { Text = $"{name}    {body}" };
            string script = name;
            item.Click += (_, _) => Rows.Add(ScriptRow(script));
            ScriptMenu.Items.Add(item);
        }
    }

    private void OnSave(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        var result = new List<ProjectCommand>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        var star = EffectiveDefault;
        string? defaultKey = null;
        foreach (var row in Rows)
        {
            if (row.Script is { } script)
            {
                result.Add(new ProjectCommand { Name = script, Script = script });
                if (ReferenceEquals(row, star)) defaultKey = CommandList.ScriptKey(script);
                continue;
            }
            string name = row.Name.Trim(), command = row.Command.Trim();
            if (name.Length == 0 && command.Length == 0) continue; // an untouched new row
            string? error =
                name.Length == 0 ? $"이름이 없는 명령이 있습니다: {command}"
                : command.Length == 0 ? $"'{name}'의 명령이 비어 있습니다."
                : !names.Add(name) ? $"'{name}' 이름이 두 번 쓰였습니다."
                : null;
            if (error is not null)
            {
                args.Cancel = true;
                ErrorText.Text = error;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }
            result.Add(new ProjectCommand { Name = name, Command = command });
            if (ReferenceEquals(row, star)) defaultKey = CommandList.CommandKey(name);
        }
        Result = result;
        DefaultKey = defaultKey;
    }
}

/// <summary>One row of the command panel.</summary>
public sealed partial class CommandRow : ObservableObject
{
    // Get-only (not init): XAML's generated type info can't assign init-only properties.
    public CommandRow(string? script = null, string detail = "")
    {
        Script = script;
        Detail = detail;
    }

    /// <summary>The package.json script, or null for a custom command.</summary>
    public string? Script { get; }
    public bool IsScript => Script is not null;
    public bool IsCustom => Script is null;

    /// <summary>For a script: what it runs ("pnpm run dev  →  vite").</summary>
    public string Detail { get; }

    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial string Command { get; set; } = "";
    /// <summary>This row is the default command (★).</summary>
    [ObservableProperty] public partial bool IsDefault { get; set; }
    public bool IsNotDefault => !IsDefault;
    public string DefaultLabel => IsDefault ? "기본 명령 (▶가 실행)" : "기본 명령으로";

    partial void OnIsDefaultChanged(bool value)
    {
        OnPropertyChanged(nameof(IsNotDefault));
        OnPropertyChanged(nameof(DefaultLabel));
    }
}
