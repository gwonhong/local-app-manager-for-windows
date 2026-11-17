namespace LocalAppManager.Core.Projects;

/// <summary>How the assigned port reaches the dev server.</summary>
public enum PortMode
{
    /// <summary>The app doesn't pass a port (the command is used as-is).</summary>
    None,
    /// <summary>Set an environment variable (PORT by default). Next, CRA, Express…</summary>
    Env,
    /// <summary>Append arguments such as <c>--port {port} --strictPort</c>. Vite ignores PORT, so it uses this.</summary>
    Args,
}

/// <summary>One chip: a package.json script (<see cref="Script"/>) or a custom shell command.</summary>
public sealed class ProjectCommand
{
    public string Name { get; set; } = "";

    /// <summary>Shell command run through cmd.exe (may contain <c>{port}</c>). Empty for a package.json script.</summary>
    public string Command { get; set; } = "";

    /// <summary>The package.json script this chip runs with the package manager, or null for a custom command.</summary>
    public string? Script { get; set; }

    public ProjectCommand Clone() => new() { Name = Name, Command = Command, Script = Script };
}

/// <summary>Persisted definition of a registered project. Runtime state lives in <see cref="Runtime.ProjectRuntime"/>.</summary>
public sealed class ProjectDefinition
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";

    /// <summary>Framework id from <see cref="FrameworkDetector"/> (vite, next, django, …).</summary>
    public string Framework { get; set; } = "unknown";

    /// <summary>npm / pnpm / yarn / bun, or null for non-Node projects.</summary>
    public string? PackageManager { get; set; }

    /// <summary>
    /// Legacy: the single "start command" from before chips were ordered. Moved into <see cref="Commands"/>
    /// (as the first chip) when projects.json is loaded, then cleared. Not written anymore.
    /// </summary>
    public string? StartCommand { get; set; }

    /// <summary>
    /// Pinned port, passed to the server on every start (see <see cref="PortMode"/>). Null (the default) means the
    /// app passes nothing and only observes which ports the process tree listens on.
    /// </summary>
    public int? Port { get; set; }

    /// <summary>The port this project was last seen serving on, kept so a stopped project can still show it.</summary>
    public int? LastPort { get; set; }

    public PortMode PortMode { get; set; } = PortMode.Env;

    /// <summary>Arguments appended in <see cref="PortMode.Args"/> mode.</summary>
    public string PortArgs { get; set; } = "--port {port}";

    /// <summary>Variable set in <see cref="PortMode.Env"/> mode.</summary>
    public string PortEnvVar { get; set; } = "PORT";

    /// <summary>
    /// The chips, in order. package.json scripts not listed here (and not hidden) are appended after these;
    /// see <see cref="CommandList"/>.
    /// </summary>
    public List<ProjectCommand> Commands { get; set; } = [];

    /// <summary>
    /// Key of the default chip ("script:dev", "cmd:start"): what "start" runs (the list's ▶, tray, groups).
    /// Null or a chip that no longer exists means the first chip.
    /// </summary>
    public string? DefaultCommand { get; set; }

    /// <summary>package.json scripts removed from the chip row (so they aren't appended again).</summary>
    public List<string> HiddenScripts { get; set; } = [];

    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Restart automatically (with backoff) when a server command crashes.</summary>
    public bool AutoRestart { get; set; }

    /// <summary>Path opened by "Open in browser", e.g. "/admin". The host and port come from the run.</summary>
    public string BrowserPath { get; set; } = "/";

    public DateTime AddedAt { get; set; } = DateTime.Now;

    public ProjectDefinition Clone()
    {
        var c = (ProjectDefinition)MemberwiseClone();
        c.Commands = Commands.Select(x => x.Clone()).ToList();
        c.HiddenScripts = [.. HiddenScripts];
        c.Environment = new Dictionary<string, string>(Environment, StringComparer.OrdinalIgnoreCase);
        return c;
    }
}

public sealed class ProjectGroup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public List<string> ProjectIds { get; set; } = [];
}
