using System.Text.RegularExpressions;

namespace LocalAppManager.Core.Projects;

public sealed record BuiltCommand(string ShellCommand, string CommandLine, Dictionary<string, string> Environment);

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

    /// <summary>The start command with the port applied.</summary>
    public static BuiltCommand BuildStart(ProjectDefinition project, IReadOnlyDictionary<string, string> baseEnvironment)
    {
        var env = MergeEnvironment(project, baseEnvironment);
        string command = project.StartCommand.Trim();

        if (project.Port is int port)
        {
            if (command.Contains("{port}", StringComparison.Ordinal))
            {
                command = command.Replace("{port}", port.ToString(), StringComparison.Ordinal);
            }
            else if (project.PortMode == PortMode.Args && !string.IsNullOrWhiteSpace(project.PortArgs))
            {
                command = AppendArguments(command, project.PortArgs.Replace("{port}", port.ToString(), StringComparison.Ordinal));
            }
            else if (project.PortMode == PortMode.Env)
            {
                env[string.IsNullOrWhiteSpace(project.PortEnvVar) ? "PORT" : project.PortEnvVar.Trim()] = port.ToString();
            }
        }

        var fw = FrameworkDetector.ById(project.Framework);
        if (fw.ExtraEnv is not null)
            foreach (var (k, v) in fw.ExtraEnv)
                if (!project.Environment.ContainsKey(k)) env[k] = v;

        return new BuiltCommand(command, WrapInCmd(command), env);
    }

    /// <summary>Any other command (scripts, build, lint). <c>{port}</c> still expands to the project's port.</summary>
    public static BuiltCommand BuildOther(ProjectDefinition project, string command, IReadOnlyDictionary<string, string> baseEnvironment)
    {
        var env = MergeEnvironment(project, baseEnvironment);
        if (project.Port is int port)
            command = command.Replace("{port}", port.ToString(), StringComparison.Ordinal);
        return new BuiltCommand(command, WrapInCmd(command), env);
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
