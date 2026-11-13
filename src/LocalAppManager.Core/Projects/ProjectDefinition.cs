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

public sealed class ProjectCommand
{
    public string Name { get; set; } = "";
    public string Command { get; set; } = "";
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

    /// <summary>Shell command run through cmd.exe. May contain <c>{port}</c>.</summary>
    public string StartCommand { get; set; } = "";

    /// <summary>Fixed port assigned from the app's range. Null when the project doesn't use one.</summary>
    public int? Port { get; set; }

    public PortMode PortMode { get; set; } = PortMode.Env;

    /// <summary>Arguments appended in <see cref="PortMode.Args"/> mode.</summary>
    public string PortArgs { get; set; } = "--port {port}";

    /// <summary>Variable set in <see cref="PortMode.Env"/> mode.</summary>
    public string PortEnvVar { get; set; } = "PORT";

    /// <summary>Extra commands (build, lint…) besides package.json scripts.</summary>
    public List<ProjectCommand> Commands { get; set; } = [];

    /// <summary>package.json scripts hidden from the chip row.</summary>
    public List<string> HiddenScripts { get; set; } = [];

    public Dictionary<string, string> Environment { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Restart automatically (with backoff) when the start command crashes.</summary>
    public bool AutoRestart { get; set; }

    /// <summary>Start this project when the app itself starts.</summary>
    public bool StartWithApp { get; set; }

    /// <summary>Path opened by "Open in browser", e.g. "/admin". The host and port come from the run.</summary>
    public string BrowserPath { get; set; } = "/";

    public DateTime AddedAt { get; set; } = DateTime.Now;

    public ProjectDefinition Clone()
    {
        var c = (ProjectDefinition)MemberwiseClone();
        c.Commands = Commands.Select(x => new ProjectCommand { Name = x.Name, Command = x.Command }).ToList();
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
