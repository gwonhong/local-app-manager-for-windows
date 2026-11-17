using System.Text.RegularExpressions;

namespace LocalAppManager.Core.Projects;

/// <param name="PortPassed">The pinned port reaches the server (placeholder, appended arguments, or env var on a server command).</param>
public sealed record BuiltCommand(string ShellCommand, string CommandLine, Dictionary<string, string> Environment, bool PortPassed = false);

/// <summary>Turns a project definition into the command line and environment of the child process.</summary>
public static partial class CommandBuilder
{
    /// <summary>Environment every child gets (users can override any of these per project).</summary>
    public static readonly IReadOnlyDictionary<string, string> DefaultEnvironment = new Dictionary<string, string>
    {
        // Keep colors although stdout is a pipe (chalk, picocolors, supports-color, Vite, Next…).
        ["FORCE_COLOR"] = "1",
        // Python: no block buffering on pipes, UTF-8 output.
        ["PYTHONUNBUFFERED"] = "1",
        ["PYTHONIOENCODING"] = "utf-8",
    };

    /// <summary>
    /// Any chip's command with the pinned port applied. <c>{port}</c> always expands. Otherwise the port is passed
    /// the project's way (<see cref="PortMode"/>), but appended arguments only go to a server command:
    /// <c>vite build --port 4100</c> or <c>eslint . --port 4100</c> would fail. The env var is harmless, so every
    /// command gets it.
    /// </summary>
    public static BuiltCommand Build(ProjectDefinition project, string command, bool isServer, IReadOnlyDictionary<string, string> baseEnvironment)
    {
        var env = MergeEnvironment(project, baseEnvironment);
        command = command.Trim();
        bool passed = false;

        if (project.Port is int port)
        {
            if (command.Contains("{port}", StringComparison.Ordinal))
            {
                command = command.Replace("{port}", port.ToString(), StringComparison.Ordinal);
                passed = true;
            }
            else if (project.PortMode == PortMode.Args && isServer && !string.IsNullOrWhiteSpace(project.PortArgs))
            {
                command = AppendArguments(command, project.PortArgs.Replace("{port}", port.ToString(), StringComparison.Ordinal));
                passed = true;
            }
            else if (project.PortMode == PortMode.Env)
            {
                env[string.IsNullOrWhiteSpace(project.PortEnvVar) ? "PORT" : project.PortEnvVar.Trim()] = port.ToString();
                passed = isServer;
            }
        }

        var fw = FrameworkDetector.ById(project.Framework);
        if (fw.ExtraEnv is not null)
            foreach (var (k, v) in fw.ExtraEnv)
                if (!project.Environment.ContainsKey(k)) env[k] = v;

        return new BuiltCommand(command, WrapInCmd(command), env, passed);
    }

    /// <summary>The command that runs a package.json script with the project's package manager.</summary>
    public static string ScriptCommand(ProjectDefinition project, string script) =>
        $"{project.PackageManager ?? "npm"} run {QuoteIfNeeded(script)}";

    /// <summary>
    /// Appends arguments so they reach the underlying tool. <c>npm run x</c> needs <c>--</c> before them
    /// (otherwise npm swallows the flags), pnpm/yarn/bun forward them as-is. pnpm also forwards a literal <c>--</c>,
    /// which makes some CLIs stop parsing options, so <c>--</c> is only added for npm.
    /// </summary>
    internal static string AppendArguments(string command, string args)
    {
        if (string.IsNullOrWhiteSpace(args)) return command;
        if (NpmRunRegex().IsMatch(command) && !Regex.IsMatch(command, @"\s--(\s|$)"))
            return $"{command} -- {args}";
        return $"{command} {args}";
    }

    [GeneratedRegex(@"^\s*npm(\.cmd)?\s+(run|run-script|start|test|rum|urn)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NpmRunRegex();

    /// <summary>cmd.exe /d (no AutoRun) /s /c "…" — /s keeps the inner quotes exactly as written.</summary>
    public static string WrapInCmd(string command)
    {
        string comspec = System.Environment.GetEnvironmentVariable("ComSpec") is { Length: > 0 } c ? c : @"C:\Windows\System32\cmd.exe";
        return $"\"{comspec}\" /d /s /c \"{command}\"";
    }

    private static Dictionary<string, string> MergeEnvironment(ProjectDefinition project, IReadOnlyDictionary<string, string> baseEnvironment)
    {
        var env = new Dictionary<string, string>(baseEnvironment, StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in DefaultEnvironment) env[k] = v;
        // An inherited NO_COLOR fights FORCE_COLOR (Node prints a warning on every start). Only an explicit per-project NO_COLOR stays.
        env.Remove("NO_COLOR");
        foreach (var (k, v) in project.Environment)
        {
            if (string.IsNullOrWhiteSpace(k)) continue;
            env[k.Trim()] = System.Environment.ExpandEnvironmentVariables(v);
        }
        return env;
    }

    private static string QuoteIfNeeded(string s) => s.Any(char.IsWhiteSpace) || s.Contains('&') ? $"\"{s}\"" : s;
}
