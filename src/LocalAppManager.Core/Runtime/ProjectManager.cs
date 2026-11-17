using LocalAppManager.Core.Diagnostics;
using LocalAppManager.Core.Logs;
using LocalAppManager.Core.Ports;
using LocalAppManager.Core.Processes;
using LocalAppManager.Core.Projects;
using LocalAppManager.Core.Storage;

namespace LocalAppManager.Core.Runtime;

/// <summary>
/// The single owner of all project state: definitions, groups, settings and runtimes.
/// The UI only reads from it and calls its methods; it never keeps its own copy of the truth.
/// Events can fire on any thread.
/// </summary>
public sealed class ProjectManager
{
    private readonly object _gate = new();
    private readonly List<ProjectRuntime> _projects = [];
    private readonly List<ProjectGroup> _groups = [];
    private readonly PortAllocator _allocator;

    public AppPaths Paths { get; }
    public AppSettings Settings { get; private set; } = new();

    public ProjectManager(AppPaths paths, PortAllocator? allocator = null)
    {
        Paths = paths;
        _allocator = allocator ?? new PortAllocator();
    }

    /// <summary>Projects or groups were added, removed, renamed or reordered.</summary>
    public event Action? ProjectsChanged;

    /// <summary>A project's runtime state changed.</summary>
    public event Action<ProjectRuntime>? ProjectChanged;

    /// <summary>A project's start command crashed (exit code passed along).</summary>
    public event Action<ProjectRuntime, int>? ProjectCrashed;

    public IReadOnlyList<ProjectRuntime> Projects
    {
        get { lock (_gate) return _projects.ToList(); }
    }

    public IReadOnlyList<ProjectGroup> Groups
    {
        get { lock (_gate) return _groups.ToList(); }
    }

    public ProjectRuntime? Get(string id)
    {
        lock (_gate) return _projects.FirstOrDefault(p => p.Id == id);
    }

    // ------------------------------------------------------------------ persistence

    public void Load()
    {
        Settings = JsonStore.Load<AppSettings>(Paths.SettingsFile);
        ApplyRuntimeSettings();
        var doc = JsonStore.Load<ProjectsDocument>(Paths.ProjectsFile);
        bool migrated = false;
        lock (_gate)
        {
            _projects.Clear();
            foreach (var def in doc.Projects)
            {
                Normalize(def);
                // Older files have a separate "start command"; it becomes the first chip.
                if (CommandList.MigrateStartCommand(def, FrameworkDetector.ReadScripts(def.Path)))
                {
                    migrated = true;
                    AppLog.Info($"migrated start command of '{def.Name}' into its chip list");
                }
                _projects.Add(CreateRuntime(def));
            }
            _groups.Clear();
            _groups.AddRange(doc.Groups);
        }
        AppLog.Info($"loaded {doc.Projects.Count} projects, {doc.Groups.Count} groups from {Paths.ProjectsFile}");
        if (migrated)
        {
            // Keep the file as it was before this format change, once, in case the migration needs undoing.
            string backup = Paths.ProjectsFile + ".before-commands.bak";
            try
            {
                if (File.Exists(Paths.ProjectsFile) && !File.Exists(backup)) File.Copy(Paths.ProjectsFile, backup);
            }
            catch (IOException e)
            {
                AppLog.Warn($"could not back up projects.json before migrating: {e.Message}");
            }
            SaveProjects();
        }
        ProjectsChanged?.Invoke();
    }

    public void SaveProjects()
    {
        ProjectsDocument doc;
        lock (_gate)
            doc = new ProjectsDocument
            {
                Projects = _projects.Select(p => p.Definition).ToList(),
                Groups = _groups.Select(g => new ProjectGroup { Id = g.Id, Name = g.Name, ProjectIds = [.. g.ProjectIds] }).ToList(),
            };
        try
        {
            JsonStore.Save(Paths.ProjectsFile, doc);
        }
        catch (Exception e)
        {
            AppLog.Error("projects.json 저장 실패", e);
        }
    }

    public void SaveSettings()
    {
        try
        {
            JsonStore.Save(Paths.SettingsFile, Settings);
        }
        catch (Exception e)
        {
            AppLog.Error("settings.json 저장 실패", e);
        }
    }

    public void UpdateSettings(Action<AppSettings> change)
    {
        change(Settings);
        if (Settings.PortRangeEnd < Settings.PortRangeStart)
            (Settings.PortRangeStart, Settings.PortRangeEnd) = (Settings.PortRangeEnd, Settings.PortRangeStart);
        Settings.LogCapacity = Math.Clamp(Settings.LogCapacity, 500, 100_000);
        foreach (var p in Projects) p.Log.Resize(Settings.LogCapacity);
        ApplyRuntimeSettings();
        SaveSettings();
    }

    private void ApplyRuntimeSettings() =>
        ProjectRuntime.GracefulStopTimeout = Settings.GracefulStop ? TimeSpan.FromSeconds(3) : TimeSpan.Zero;

    private static void Normalize(ProjectDefinition def)
    {
        def.Commands ??= [];
        def.HiddenScripts ??= [];
        def.Environment = new Dictionary<string, string>(def.Environment ?? [], StringComparer.OrdinalIgnoreCase);
        def.PortArgs ??= "--port {port}";
        def.PortEnvVar ??= "PORT";
        def.BrowserPath ??= "/";
        foreach (var c in def.Commands)
        {
            c.Name ??= "";
            c.Command ??= "";
            if (c.Script is { Length: 0 }) c.Script = null;
        }
        def.Name = string.IsNullOrWhiteSpace(def.Name) ? Path.GetFileName(def.Path.TrimEnd('\\')) : def.Name;
    }

    private ProjectRuntime CreateRuntime(ProjectDefinition def)
    {
        var runtime = new ProjectRuntime(def, new LogBuffer(Settings.LogCapacity), () => EnvironmentBuilder.CreateBase());
        runtime.Changed += r => ProjectChanged?.Invoke(r);
        runtime.LastPortChanged += _ => SaveProjects();
        runtime.Crashed += (r, code) =>
        {
            AppLog.Warn($"project '{r.Definition.Name}' crashed with code {code}");
            ProjectCrashed?.Invoke(r, code);
        };
        return runtime;
    }

    // ------------------------------------------------------------------ projects

    /// <summary>Detects the framework in <paramref name="path"/> and registers it. No port is pinned; see <see cref="ProjectDefinition.Port"/>.</summary>
    public ProjectRuntime AddProject(string path)
    {
        path = Path.GetFullPath(path).TrimEnd('\\');
        lock (_gate)
        {
            var existing = _projects.FirstOrDefault(p => string.Equals(Path.GetFullPath(p.Definition.Path).TrimEnd('\\'), path, StringComparison.OrdinalIgnoreCase));
            if (existing is not null) return existing;
        }

        var detection = FrameworkDetector.Detect(path);
        var def = new ProjectDefinition
        {
            Name = UniqueName(detection.Name),
            Path = path,
            Framework = detection.Framework.Id,
            PackageManager = detection.PackageManager,
            PortMode = detection.Framework.PortMode,
            PortArgs = detection.Framework.PortArgs,
        };
        // The detected dev command goes first and is the default (the list's ▶ runs it); other scripts follow on their own.
        if (!string.IsNullOrWhiteSpace(detection.StartCommand))
        {
            var start = CommandList.FromCommandLine(detection.StartCommand);
            def.Commands.Add(start);
            def.DefaultCommand = CommandList.KeyOf(start);
        }

        var runtime = CreateRuntime(def);
        lock (_gate) _projects.Add(runtime);
        AppLog.Info($"added project '{def.Name}' ({def.Framework}, {def.PackageManager ?? "-"}) cmd='{detection.StartCommand}'");
        runtime.Log.System($"등록했습니다: {detection.Framework.DisplayName}" + (def.PackageManager is { } pm ? $" · {pm}" : "") + (def.Port is int port ? $" · 포트 {port}" : ""));
        SaveProjects();
        ProjectsChanged?.Invoke();
        return runtime;
    }

    private string UniqueName(string name)
    {
        lock (_gate)
        {
            string candidate = name;
            for (int i = 2; _projects.Any(p => p.Definition.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)); i++)
                candidate = $"{name} ({i})";
            return candidate;
        }
    }

    /// <summary>Replaces a project's definition (from the settings dialog).</summary>
    public void UpdateProject(ProjectDefinition updated)
    {
        var runtime = Get(updated.Id) ?? throw new InvalidOperationException("unknown project");
        Normalize(updated);
        var old = runtime.Definition;
        updated.LastPort = old.LastPort; // observed by the runtime, not edited in the dialog
        runtime.OnDefinitionChanged(old, updated.Clone());
        SaveProjects();
        if (old.Name != updated.Name) ProjectsChanged?.Invoke();
    }

    public async Task RemoveProjectAsync(string id)
    {
        var runtime = Get(id);
        if (runtime is null) return;
        await runtime.StopEverythingAsync().ConfigureAwait(false);
        runtime.KillAll();
        lock (_gate)
        {
            _projects.Remove(runtime);
            foreach (var g in _groups) g.ProjectIds.Remove(id);
        }
        SaveProjects();
        ProjectsChanged?.Invoke();
    }

    public void MoveProject(string id, int newIndex)
    {
        lock (_gate)
        {
            int index = _projects.FindIndex(p => p.Id == id);
            if (index < 0) return;
            var item = _projects[index];
            _projects.RemoveAt(index);
            _projects.Insert(Math.Clamp(newIndex, 0, _projects.Count), item);
        }
        SaveProjects();
        ProjectsChanged?.Invoke();
    }

    /// <summary>Lowest free port in the app's range not assigned to another project.</summary>
    public int? AllocatePort(string? forProjectId, int? preferAfter = null)
    {
        List<int> assigned;
        lock (_gate)
            assigned = _projects.Where(p => p.Id != forProjectId && p.Definition.Port is not null).Select(p => p.Definition.Port!.Value).ToList();
        return _allocator.Allocate(Settings.PortRangeStart, Settings.PortRangeEnd, assigned, preferAfter);
    }

    /// <summary>The project, other than <paramref name="exceptId"/>, that already has <paramref name="port"/> pinned.</summary>
    public ProjectRuntime? PinnedBy(int port, string? exceptId)
    {
        lock (_gate) return _projects.FirstOrDefault(p => p.Id != exceptId && p.Definition.Port == port);
    }

    /// <summary>
    /// Pins the project to <paramref name="port"/> (null unpins it) and saves. Returns the other project that already
    /// pins that port, in which case nothing changes. A running project picks the change up on its next start.
    /// </summary>
    public ProjectRuntime? PinPort(string id, int? port)
    {
        var runtime = Get(id);
        if (runtime is null) return null;
        if (port is int p && PinnedBy(p, id) is { } clash) return clash;
        if (runtime.Definition.Port == port) return null;
        var def = runtime.Definition.Clone();
        def.Port = port;
        runtime.OnDefinitionChanged(runtime.Definition, def);
        runtime.ClearError();
        runtime.Log.System(port is int n ? $"포트를 {n}(으)로 고정했습니다." : "포트 고정을 풀었습니다.");
        SaveProjects();
        return null;
    }

    /// <summary>
    /// The port to suggest when pinning: the one already pinned, else the last-seen one if no other project pins it
    /// and it's free (or this project is the one using it), else a free one from the app's range.
    /// </summary>
    public int? SuggestPinPort(string id)
    {
        var runtime = Get(id);
        if (runtime is null) return null;
        if (runtime.Definition.Port is int pinned) return pinned;
        if (runtime.Definition.LastPort is int last && PinnedBy(last, id) is null
            && (runtime.ListeningPorts.Contains(last) || PortProbe.IsFree(last)))
            return last;
        return AllocatePort(id);
    }

    /// <summary>Gives the project a new port (after a conflict) and saves it.</summary>
    public int? ReassignPort(string id)
    {
        var runtime = Get(id);
        if (runtime is null) return null;
        int? port = AllocatePort(id, runtime.Definition.Port);
        if (port is null) return null;
        var def = runtime.Definition.Clone();
        int? old = def.Port;
        def.Port = port;
        runtime.OnDefinitionChanged(runtime.Definition, def);
        runtime.ClearError();
        runtime.Log.System($"포트를 {old?.ToString() ?? "-"} → {port}(으)로 바꿨습니다.");
        SaveProjects();
        return port;
    }

    // ------------------------------------------------------------------ lifecycle

    /// <summary>Runs the project's default chip, unless something already runs.</summary>
    public Task StartAsync(string id) => Get(id) is { } r ? StartDefaultAsync(r) : Task.CompletedTask;

    private Task StartDefaultAsync(ProjectRuntime runtime)
    {
        if (runtime.IsActive) return Task.CompletedTask;
        var command = CommandList.DefaultOf(runtime.Definition, GetCommands(runtime));
        if (command is null)
        {
            runtime.Log.Error("실행할 명령이 없습니다. 명령 칩을 추가하세요.");
            return Task.CompletedTask;
        }
        return runtime.StartIfIdleAsync(command);
    }

    /// <summary>Stops whatever the project runs.</summary>
    public Task StopAsync(string id) => Get(id)?.StopEverythingAsync() ?? Task.CompletedTask;

    /// <summary>Runs the current (or last) command again; the default chip if nothing has run yet.</summary>
    public Task RestartAsync(string id)
    {
        var runtime = Get(id);
        if (runtime is null) return Task.CompletedTask;
        // Re-resolve by key so an edited command or a changed package manager applies.
        var key = runtime.LastCommand?.Key;
        var commands = GetCommands(runtime);
        var command = commands.FirstOrDefault(c => c.Key == key) ?? runtime.LastCommand ?? CommandList.DefaultOf(runtime.Definition, commands);
        return command is null ? Task.CompletedTask : runtime.RunAsync(command);
    }

    public Task StopAllAsync() => Task.WhenAll(Projects.Select(p => p.StopEverythingAsync()));

    /// <summary>Kills every process tree right away (app exit). Job objects would do it anyway when the process dies.</summary>
    public void KillAll()
    {
        foreach (var p in Projects) p.KillAll();
    }

    // ------------------------------------------------------------------ commands (chips)

    /// <summary>The project's chips in order (custom order, then package.json scripts it doesn't mention).</summary>
    public IReadOnlyList<ResolvedCommand> GetCommands(string id) =>
        Get(id) is { } r ? GetCommands(r) : [];

    private static IReadOnlyList<ResolvedCommand> GetCommands(ProjectRuntime runtime) =>
        CommandList.Resolve(runtime.Definition, FrameworkDetector.ReadScripts(runtime.Definition.Path));

    /// <summary>
    /// Runs one chip, stopping whatever the project was running (one run at a time). Running the chip that already
    /// runs restarts it.
    /// </summary>
    public Task RunAsync(string id, string key)
    {
        var runtime = Get(id);
        if (runtime is null) return Task.CompletedTask;
        var command = GetCommands(runtime).FirstOrDefault(c => c.Key == key);
        return command is null ? Task.CompletedTask : runtime.RunAsync(command);
    }

    /// <summary>Makes the chip <paramref name="key"/> the default (what ▶, the tray and groups run) and saves.</summary>
    public void SetDefaultCommand(string id, string key)
    {
        var runtime = Get(id);
        if (runtime is null || runtime.Definition.DefaultCommand == key) return;
        var def = runtime.Definition.Clone();
        def.DefaultCommand = key;
        UpdateProject(def);
    }

    /// <summary>
    /// Replaces the chip list with <paramref name="ordered"/> (from the command panel) and saves, with
    /// <paramref name="defaultKey"/> as the default chip. Scripts left out are remembered as hidden, so they aren't
    /// appended again; scripts put back are un-hidden.
    /// </summary>
    public void SetCommands(string id, IReadOnlyList<ProjectCommand> ordered, string? defaultKey)
    {
        var runtime = Get(id);
        if (runtime is null) return;
        var def = runtime.Definition.Clone();
        var scripts = FrameworkDetector.ReadScripts(def.Path);
        def.Commands = ordered.Select(c => c.Clone()).ToList();
        def.DefaultCommand = defaultKey;
        var listed = def.Commands.Where(c => c.Script is not null).Select(c => c.Script!).ToHashSet();
        def.HiddenScripts = scripts.Keys.Where(s => !listed.Contains(s)).ToList();
        UpdateProject(def);
    }

    // ------------------------------------------------------------------ groups

    public ProjectGroup AddGroup(string name, IEnumerable<string> projectIds)
    {
        var group = new ProjectGroup { Name = name.Trim(), ProjectIds = projectIds.Distinct().ToList() };
        lock (_gate) _groups.Add(group);
        SaveProjects();
        ProjectsChanged?.Invoke();
        return group;
    }

    public void UpdateGroup(string groupId, string name, IEnumerable<string> projectIds)
    {
        lock (_gate)
        {
            var g = _groups.FirstOrDefault(x => x.Id == groupId);
            if (g is null) return;
            g.Name = name.Trim();
            g.ProjectIds = projectIds.Distinct().ToList();
        }
        SaveProjects();
        ProjectsChanged?.Invoke();
    }

    public void RemoveGroup(string groupId)
    {
        lock (_gate) _groups.RemoveAll(g => g.Id == groupId);
        SaveProjects();
        ProjectsChanged?.Invoke();
    }

    public IReadOnlyList<ProjectRuntime> GroupMembers(string groupId)
    {
        lock (_gate)
        {
            var g = _groups.FirstOrDefault(x => x.Id == groupId);
            if (g is null) return [];
            return g.ProjectIds.Select(id => _projects.FirstOrDefault(p => p.Id == id)).OfType<ProjectRuntime>().ToList();
        }
    }

    public Task StartGroupAsync(string groupId) => Task.WhenAll(GroupMembers(groupId).Select(StartDefaultAsync));

    public Task StopGroupAsync(string groupId) => Task.WhenAll(GroupMembers(groupId).Select(p => p.StopEverythingAsync()));
}
