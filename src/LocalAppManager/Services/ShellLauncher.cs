using System.Diagnostics;
using LocalAppManager.Core.Diagnostics;
using LocalAppManager.Core.Processes;

namespace LocalAppManager.Services;

/// <summary>
/// Opens things outside the app: browser, VS Code, Explorer, a terminal.
/// These are started normally (not in a project's job), so they keep running when a project
/// stops or the app quits.
/// </summary>
public static class ShellLauncher
{
    public static bool OpenUrl(string url) => Shell(url);

    public static bool OpenFolder(string path) => Try(() => Process.Start("explorer.exe", $"\"{path}\""), "explorer");

    public static bool OpenFile(string path) => Shell(path);

    public static bool OpenInVsCode(string path)
    {
        // Prefer the `code` launcher on the (fresh) PATH, so Insiders/portable/system installs all work.
        var env = EnvironmentBuilder.CreateBase();
        string? codeCmd = FindOnPath(env, "code.cmd");
        if (codeCmd is not null)
        {
            return Try(() =>
            {
                var psi = new ProcessStartInfo("cmd.exe", $"/d /c \"\"{codeCmd}\" \"{path}\"\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WorkingDirectory = path,
                };
                CopyEnv(env, psi);
                return Process.Start(psi);
            }, "code");
        }

        foreach (var exe in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Microsoft VS Code\Code.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft VS Code\Code.exe"),
                 })
        {
            if (File.Exists(exe)) return Try(() => Process.Start(exe, $"\"{path}\""), "Code.exe");
        }
        return false;
    }

    /// <summary>Opens a terminal in <paramref name="path"/>. <paramref name="terminal"/>: wt, pwsh, powershell, cmd.</summary>
    public static bool OpenTerminal(string path, string terminal)
    {
        var env = EnvironmentBuilder.CreateBase();
        string? wt = FindOnPath(env, "wt.exe");
        if (terminal == "wt" && wt is not null)
            return Try(() => Process.Start(new ProcessStartInfo(wt, $"-d \"{path.TrimEnd('\\')}\"") { UseShellExecute = true, WorkingDirectory = path }), "wt");

        string exe = terminal switch
        {
            "pwsh" when FindOnPath(env, "pwsh.exe") is { } p => p,
            "powershell" or "pwsh" => "powershell.exe",
            _ => "cmd.exe",
        };
        return Try(() => Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = path }), exe);
    }

    public static bool HasWindowsTerminal => FindOnPath(EnvironmentBuilder.CreateBase(), "wt.exe") is not null;

    private static string? FindOnPath(IReadOnlyDictionary<string, string> env, string file)
    {
        if (!env.TryGetValue("PATH", out var path)) return null;
        foreach (var dir in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(dir, file);
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
            }
        }
        return null;
    }

    private static void CopyEnv(IReadOnlyDictionary<string, string> env, ProcessStartInfo psi)
    {
        psi.Environment.Clear();
        foreach (var (k, v) in env) psi.Environment[k] = v;
    }

    private static bool Shell(string target) =>
        Try(() => Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }), target);

    private static bool Try(Func<Process?> start, string what)
    {
        try
        {
            using var p = start();
            return true;
        }
        catch (Exception e)
        {
            AppLog.Warn($"failed to open {what}", e);
            return false;
        }
    }
}
