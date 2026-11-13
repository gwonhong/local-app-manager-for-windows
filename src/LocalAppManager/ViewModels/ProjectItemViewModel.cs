using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LocalAppManager.Core.Projects;
using LocalAppManager.Core.Runtime;
using LocalAppManager.Services;

namespace LocalAppManager.ViewModels;

/// <summary>
/// Read-only projection of a <see cref="ProjectRuntime"/> for binding. It never holds state of its own:
/// <see cref="Refresh"/> copies everything from the runtime, on the UI thread, whenever the runtime changes.
/// </summary>
public sealed partial class ProjectItemViewModel : ObservableObject
{
    private readonly ProjectManager _manager;
    private int _refreshQueued;

    public ProjectItemViewModel(ProjectManager manager, ProjectRuntime runtime)
    {
        _manager = manager;
        Runtime = runtime;
        Refresh();
        RefreshScripts();
    }

    public ProjectRuntime Runtime { get; }
    public string Id => Runtime.Id;

    [ObservableProperty] public partial string Name { get; private set; } = "";
    [ObservableProperty] public partial string Path { get; private set; } = "";
    [ObservableProperty] public partial string FrameworkName { get; private set; } = "";
    [ObservableProperty] public partial string Subtitle { get; private set; } = "";
    [ObservableProperty] public partial ProjectState State { get; private set; }
    [ObservableProperty] public partial string StateText { get; private set; } = "";
    [ObservableProperty] public partial bool IsActive { get; private set; }
    [ObservableProperty] public partial bool IsStarting { get; private set; }
    [ObservableProperty] public partial string PortText { get; private set; } = "";
    [ObservableProperty] public partial string? Url { get; private set; }
    [ObservableProperty] public partial string? ListeningText { get; private set; }
    [ObservableProperty] public partial string? ErrorMessage { get; private set; }
    [ObservableProperty] public partial bool IsPortConflict { get; private set; }
    [ObservableProperty] public partial bool RestartRequired { get; private set; }
    [ObservableProperty] public partial string? RestartNote { get; private set; }
    [ObservableProperty] public partial string UptimeText { get; private set; } = "";
    [ObservableProperty] public partial string StartCommandText { get; private set; } = "";
    [ObservableProperty] public partial bool AutoRestart { get; private set; }
    [ObservableProperty] public partial bool HasRunningCommands { get; private set; }

    // Plain bools for visibility: x:Bind doesn't call functions with a null argument,
    // so "VisibleIf(Url)" would never collapse again once Url went back to null.
    [ObservableProperty] public partial bool HasPort { get; private set; }
    [ObservableProperty] public partial bool HasUrl { get; private set; }
    [ObservableProperty] public partial bool HasListening { get; private set; }
    [ObservableProperty] public partial bool HasUptime { get; private set; }
    [ObservableProperty] public partial bool HasError { get; private set; }
    [ObservableProperty] public partial bool HasRestartNote { get; private set; }

    partial void OnPortTextChanged(string value) => HasPort = !string.IsNullOrEmpty(value);
    partial void OnUrlChanged(string? value) => HasUrl = !string.IsNullOrEmpty(value);
    partial void OnListeningTextChanged(string? value) => HasListening = !string.IsNullOrEmpty(value);
    partial void OnUptimeTextChanged(string value) => HasUptime = !string.IsNullOrEmpty(value);
    partial void OnErrorMessageChanged(string? value) => HasError = !string.IsNullOrEmpty(value);
    partial void OnRestartNoteChanged(string? value) => HasRestartNote = !string.IsNullOrEmpty(value);

    /// <summary>package.json scripts and custom commands, as chips.</summary>
    public ObservableCollection<CommandChipViewModel> Chips { get; } = [];

    /// <summary>Hidden package.json scripts (offered in the chip row's "…" menu).</summary>
    public ObservableCollection<string> HiddenScripts { get; } = [];

    /// <summary>Re-evaluates theme-dependent bindings (state brushes are resolved in code).</summary>
    public void NotifyThemeChanged() => OnPropertyChanged(nameof(State));

    /// <summary>Called from any thread; coalesces bursts into one UI update.</summary>
    public void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) == 1) return;
        Ui.Post(() =>
        {
            Interlocked.Exchange(ref _refreshQueued, 0);
            Refresh();
        });
    }

    public void Refresh()
    {
        var r = Runtime;
        var def = r.Definition;
        var fw = FrameworkDetector.ById(def.Framework);
        Name = def.Name;
        Path = def.Path;
        FrameworkName = fw.Id == "unknown" ? "기타" : fw.DisplayName;
        State = r.State;
        IsActive = r.IsActive;
        IsStarting = r.State == ProjectState.Starting;
        StateText = r.State switch
        {
            ProjectState.Stopped when r.NextRestartAt is not null => "재시작 대기",
            ProjectState.Crashed when r.NextRestartAt is not null => "재시작 대기",
            ProjectState.Stopped => "정지됨",
            ProjectState.Starting => "시작 중",
            ProjectState.Running => "실행 중",
            ProjectState.Exited => "종료됨",
            ProjectState.Crashed => $"크래시 ({ProjectRuntimeFormat.ExitCode(r.LastExitCode)})",
            _ => r.State.ToString(),
        };

        int? port = r.IsActive ? r.RunPort : def.Port;
        bool usesPort = def.Port is not null && (def.PortMode != PortMode.None || def.StartCommand.Contains("{port}"));
        PortText = usesPort && port is int p ? $":{p}" : "";
        Subtitle = string.Join(" · ", new[] { FrameworkName, PortText.Length > 0 ? PortText : null }.Where(s => !string.IsNullOrEmpty(s)));
        Url = r.IsActive ? r.BrowserUrl : null;
        ListeningText = r.IsActive && r.ListeningPorts.Count > 0 ? string.Join(", ", r.ListeningPorts.Select(x => ":" + x)) : null;
        ErrorMessage = r.LastError;
        IsPortConflict = r.IsPortConflict;
        RestartRequired = r.RestartRequired;
        RestartNote = r.NextRestartAt is DateTime at ? $"{Math.Max(0, (int)Math.Ceiling((at - DateTime.Now).TotalSeconds))}초 후 자동 재시작" : null;
        StartCommandText = def.StartCommand;
        AutoRestart = def.AutoRestart;
        UpdateUptime();

        var runs = r.CommandRuns;
        HasRunningCommands = runs.Count > 0;
        foreach (var chip in Chips) chip.Update(runs.Any(x => x.Key == chip.Key), r.GetLastCommandExit(chip.Key));
    }

    public void UpdateUptime()
    {
        if (Runtime.IsActive && Runtime.StartedAt is DateTime started)
        {
            var t = DateTime.Now - started;
            UptimeText = t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
        }
        else
        {
            UptimeText = "";
        }
        if (Runtime.NextRestartAt is DateTime at)
            RestartNote = $"{Math.Max(0, (int)Math.Ceiling((at - DateTime.Now).TotalSeconds))}초 후 자동 재시작";
    }

    /// <summary>Re-reads package.json scripts and custom commands into <see cref="Chips"/>.</summary>
    public void RefreshScripts()
    {
        var def = Runtime.Definition;
        var chips = new List<CommandChipViewModel>();
        var scripts = FrameworkDetector.ReadScripts(def.Path);
        string? startScript = StartScriptName(def);
        HiddenScripts.Clear();
        foreach (var (name, body) in scripts)
        {
            if (def.HiddenScripts.Contains(name))
            {
                HiddenScripts.Add(name);
                continue;
            }
            chips.Add(new CommandChipViewModel(this, ProjectManager.ScriptKey(name), name, body, isScript: true, isStartScript: name == startScript));
        }
        foreach (var cmd in def.Commands.Where(c => !string.IsNullOrWhiteSpace(c.Name)))
            chips.Add(new CommandChipViewModel(this, ProjectManager.CommandKey(cmd.Name), cmd.Name, cmd.Command, isScript: false, isStartScript: false));

        Chips.Clear();
        foreach (var c in chips) Chips.Add(c);
        Refresh();
    }

    /// <summary>The script the start command runs ("pnpm run dev" → "dev"), so its chip can be marked.</summary>
    private static string? StartScriptName(ProjectDefinition def)
    {
        var parts = def.StartCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 3 && parts[1] == "run") return parts[2];
        if (parts.Length == 2 && parts[0] is "npm" or "pnpm" or "yarn" or "bun" && parts[1] is "start" or "dev") return parts[1];
        if (parts.Length == 2 && parts[0] is "yarn" or "pnpm" or "bun") return parts[1];
        return null;
    }

    // ---- requests the view handles (dialogs need a XamlRoot)

    public static event Action<ProjectItemViewModel>? EditRequested;
    public static event Action<ProjectItemViewModel>? RemoveRequested;
    public static event Action<ProjectItemViewModel>? AddCommandRequested;

    [RelayCommand]
    private void Edit() => EditRequested?.Invoke(this);

    [RelayCommand]
    private void Remove() => RemoveRequested?.Invoke(this);

    [RelayCommand]
    private void AddCustomCommand() => AddCommandRequested?.Invoke(this);

    // ---- commands (pure actions)

    [RelayCommand]
    private Task StartAsync() => _manager.StartAsync(Id);

    [RelayCommand]
    private Task StopAsync() => _manager.StopAsync(Id);

    [RelayCommand]
    private Task RestartAsync() => _manager.RestartAsync(Id);

    [RelayCommand]
    private Task ToggleAsync() => Runtime.IsActive || Runtime.NextRestartAt is not null ? _manager.StopAsync(Id) : _manager.StartAsync(Id);

    [RelayCommand]
    private async Task ReassignPortAndStartAsync()
    {
        if (_manager.ReassignPort(Id) is null) return;
        await _manager.StartAsync(Id);
    }

    [RelayCommand]
    private void OpenBrowser()
    {
        if (Runtime.BrowserUrl is { } url) ShellLauncher.OpenUrl(url);
    }

    [RelayCommand]
    private void OpenInCode() => ShellLauncher.OpenInVsCode(Runtime.Definition.Path);

    [RelayCommand]
    private void OpenFolder() => ShellLauncher.OpenFolder(Runtime.Definition.Path);

    [RelayCommand]
    private void OpenTerminal() => ShellLauncher.OpenTerminal(Runtime.Definition.Path, _manager.Settings.Terminal);

    [RelayCommand]
    private void RunChip(CommandChipViewModel chip)
    {
        if (chip.IsRunning)
        {
            _manager.StopCommand(Id, chip.Key);
            return;
        }
        if (chip.IsScript) _manager.RunScript(Id, chip.Label);
        else if (Runtime.Definition.Commands.FirstOrDefault(c => c.Name == chip.Label) is { } cmd) _manager.RunCustomCommand(Id, cmd);
    }

    [RelayCommand]
    private void HideScript(CommandChipViewModel chip)
    {
        if (!chip.IsScript) return;
        var def = Runtime.Definition.Clone();
        if (!def.HiddenScripts.Contains(chip.Label)) def.HiddenScripts.Add(chip.Label);
        _manager.UpdateProject(def);
        RefreshScripts();
    }

    [RelayCommand]
    private void ShowScript(string name)
    {
        var def = Runtime.Definition.Clone();
        def.HiddenScripts.Remove(name);
        _manager.UpdateProject(def);
        RefreshScripts();
    }

    [RelayCommand]
    private void UseScriptAsStart(CommandChipViewModel chip)
    {
        if (!chip.IsScript) return;
        var def = Runtime.Definition.Clone();
        def.StartCommand = CommandBuilder.ScriptCommand(def, chip.Label);
        _manager.UpdateProject(def);
        RefreshScripts();
    }

    [RelayCommand]
    private void ToggleAutoRestart()
    {
        var def = Runtime.Definition.Clone();
        def.AutoRestart = !def.AutoRestart;
        _manager.UpdateProject(def);
        Refresh();
    }
}

public sealed partial class CommandChipViewModel : ObservableObject
{
    public CommandChipViewModel(ProjectItemViewModel owner, string key, string label, string command, bool isScript, bool isStartScript)
    {
        Owner = owner;
        Key = key;
        Label = label;
        CommandText = command;
        IsScript = isScript;
        IsStartScript = isStartScript;
    }

    public ProjectItemViewModel Owner { get; }
    public string Key { get; }
    public string Label { get; }
    public string CommandText { get; }
    public bool IsScript { get; }
    public bool IsStartScript { get; }
    public string Tooltip => IsStartScript ? $"{CommandText}\n(시작 명령이 실행하는 스크립트)" : CommandText;

    [ObservableProperty] public partial bool IsRunning { get; private set; }

    /// <summary>null: never run, 0: succeeded, -1: stopped, other: failed.</summary>
    [ObservableProperty] public partial int? LastExit { get; private set; }

    public bool Succeeded => !IsRunning && LastExit == 0;
    public bool Failed => !IsRunning && LastExit is int c && c != 0 && c != -1;

    public void Update(bool running, int? lastExit)
    {
        IsRunning = running;
        LastExit = lastExit;
        OnPropertyChanged(nameof(Succeeded));
        OnPropertyChanged(nameof(Failed));
    }

    public string Glyph => Converters.ChipGlyph(IsRunning, Succeeded, Failed);

    partial void OnIsRunningChanged(bool value) => OnPropertyChanged(nameof(Glyph));

    partial void OnLastExitChanged(int? value) => OnPropertyChanged(nameof(Glyph));

    [RelayCommand]
    private void Run() => Owner.RunChipCommand.Execute(this);

    [RelayCommand]
    private void Hide() => Owner.HideScriptCommand.Execute(this);

    [RelayCommand]
    private void UseAsStart() => Owner.UseScriptAsStartCommand.Execute(this);
}

internal static class ProjectRuntimeFormat
{
    public static string ExitCode(int? code) =>
        code is not int c ? "?" : c is < 0 or > 0xFFFF ? $"0x{unchecked((uint)c):X8}" : $"코드 {c}";
}
