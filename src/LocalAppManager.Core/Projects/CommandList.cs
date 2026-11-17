using System.Text.RegularExpressions;

namespace LocalAppManager.Core.Projects;

/// <summary>A chip resolved against package.json: what it's called, what it runs, and whether it starts a server.</summary>
/// <param name="Key">Stable id of the chip ("script:dev", "cmd:serve").</param>
/// <param name="ShellCommand">What cmd.exe runs, before the port is applied.</param>
/// <param name="Body">What the command really does: the script's body from package.json, or the command itself.</param>
public sealed record ResolvedCommand(string Key, string Name, string ShellCommand, string? Script, string Body)
{
    /// <summary>Starts a dev server (gets the pinned port's arguments, readiness on its port, crash handling).</summary>
    /// <remarks>A custom command named like a server script ("start", "dev") counts too: migrated start commands are named "start".</remarks>
    public bool IsServer => CommandList.IsServerCommand(Body, Script ?? Name);
}

/// <summary>
/// The ordered chip list of a project. <see cref="ProjectDefinition.Commands"/> holds the order the user chose;
/// package.json scripts it doesn't mention (and that aren't hidden) follow in package.json order, so a new script
/// shows up without any action. "Start" runs the default chip (<see cref="DefaultOf"/>).
/// </summary>
public static partial class CommandList
{
    public static string ScriptKey(string script) => "script:" + script;

    public static string CommandKey(string name) => "cmd:" + name;

    /// <summary>The key a stored chip resolves to.</summary>
    public static string KeyOf(ProjectCommand c) => c.Script is { Length: > 0 } s ? ScriptKey(s) : CommandKey(c.Name.Trim());

    /// <summary>The default chip: the one <see cref="ProjectDefinition.DefaultCommand"/> names, else the first.</summary>
    public static ResolvedCommand? DefaultOf(ProjectDefinition def, IReadOnlyList<ResolvedCommand> commands) =>
        commands.FirstOrDefault(c => c.Key == def.DefaultCommand) ?? commands.FirstOrDefault();

    public static IReadOnlyList<ResolvedCommand> Resolve(ProjectDefinition def, IReadOnlyDictionary<string, string> scripts)
    {
        var list = new List<ResolvedCommand>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in def.Commands)
        {
            if (c.Script is { Length: > 0 } script)
            {
                // A script that was removed from package.json disappears with it.
                if (scripts.TryGetValue(script, out var body) && seen.Add(ScriptKey(script)))
                    list.Add(new ResolvedCommand(ScriptKey(script), script, CommandBuilder.ScriptCommand(def, script), script, body));
            }
            else if (!string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Command) && seen.Add(CommandKey(c.Name.Trim())))
            {
                string command = c.Command.Trim();
                list.Add(new ResolvedCommand(CommandKey(c.Name.Trim()), c.Name.Trim(), command, null, command));
            }
        }
        foreach (var (script, body) in scripts)
        {
            if (def.HiddenScripts.Contains(script) || !seen.Add(ScriptKey(script))) continue;
            list.Add(new ResolvedCommand(ScriptKey(script), script, CommandBuilder.ScriptCommand(def, script), script, body));
        }
        return list;
    }

    /// <summary>
    /// The package.json script a command line runs ("pnpm run dev" → "dev", "npm start" → "start"), or null when
    /// it's something else.
    /// </summary>
    public static string? ScriptOf(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var parts = command.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts[0] is not ("npm" or "pnpm" or "yarn" or "bun")) return null;
        if (parts.Length == 3 && parts[1] is "run" or "run-script") return parts[2].Trim('"');
        if (parts.Length == 2 && parts[1] is "start" or "dev") return parts[1];
        if (parts.Length == 2 && parts[0] is "yarn" or "pnpm" or "bun" && parts[1] != "install") return parts[1];
        return null;
    }

    /// <summary>The chip for a detected start command: its script if it runs one, else a custom "start" command.</summary>
    public static ProjectCommand FromCommandLine(string command) =>
        ScriptOf(command) is { } script
            ? new ProjectCommand { Name = script, Script = script }
            : new ProjectCommand { Name = "start", Command = command.Trim() };

    /// <summary>
    /// Moves a legacy <see cref="ProjectDefinition.StartCommand"/> into the ordered list: it becomes the first chip and
    /// the default, followed by the visible package.json scripts and then the old custom commands (the order the chip
    /// row used to have). Returns false when there was nothing to migrate.
    /// </summary>
    public static bool MigrateStartCommand(ProjectDefinition def, IReadOnlyDictionary<string, string> scripts)
    {
        if (def.StartCommand is null) return false;
        string start = def.StartCommand.Trim();
        def.StartCommand = null;
        var custom = def.Commands.Where(c => c.Script is null).ToList();
        var ordered = new List<ProjectCommand>();
        if (start.Length > 0)
        {
            var first = FromCommandLine(start);
            // A custom command already named "start" would clash with the migrated one.
            if (first.Script is null && custom.Any(c => c.Name == first.Name)) first.Name = "start (기존)";
            ordered.Add(first);
            def.DefaultCommand ??= KeyOf(first);
        }
        foreach (var name in scripts.Keys)
            if (!def.HiddenScripts.Contains(name) && !ordered.Any(c => c.Script == name))
                ordered.Add(new ProjectCommand { Name = name, Script = name });
        ordered.AddRange(custom);
        def.Commands = ordered;
        return true;
    }

    // ---- server detection

    // Commands that start a dev/preview server for the frameworks we know, plus common generic ones.
    // "vite" alone/dev/serve/preview is a server, "vite build"/"vite optimize" isn't; "vitest" doesn't match.
    [GeneratedRegex(
        @"(^|[\s&|;(/\\""'])(" +
        @"vite(?!\s+(build|optimize)\b)(\s|$)|vite\s+(dev|serve|preview)\b|" +
        @"next\s+(dev|start)\b|nuxi?\s+(dev|preview|start)\b|astro\s+(dev|preview)\b|ng\s+serve\b|" +
        @"react-scripts\s+start\b|remix(-serve|\s+(dev|vite:dev))\b|nest\s+start\b|svelte-kit\s+(dev|preview)\b|" +
        @"manage\.py\s+runserver\b|uvicorn\b|fastapi\s+(dev|run)\b|flask\s+run\b|streamlit\s+run\b|http\.server\b|" +
        @"nodemon\b|tsx\s+watch\b|node\s+--watch\b)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ServerRegex();

    /// <summary>
    /// Whether a command starts a server: its body matches a known dev-server invocation, or it's a script with
    /// a conventional server name (dev, start, serve…) whose body we can't classify.
    /// </summary>
    public static bool IsServerCommand(string body, string? script) =>
        ServerRegex().IsMatch(body) || script is "dev" or "start" or "serve" or "develop" or "preview" or "start:dev";
}
