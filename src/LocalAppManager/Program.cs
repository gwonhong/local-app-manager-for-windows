using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace LocalAppManager;

/// <summary>
/// Custom entry point (DISABLE_XAML_GENERATED_MAIN) so only one instance runs:
/// a second launch (Start menu, autostart, toast click) is redirected to the running app,
/// which shows its window instead of starting a second set of servers.
/// </summary>
public static partial class Program
{
    private const string InstanceKey = "LocalAppManager.Main";

    /// <summary>Launched by the Run key at login: start hidden in the tray.</summary>
    public static bool StartInBackground { get; private set; }

    /// <summary>Folders passed with <c>--add &lt;folder&gt;</c> (or as bare arguments) to register on start.</summary>
    public static IReadOnlyList<string> FoldersToAdd { get; private set; } = [];

    /// <summary>
    /// Command line: <c>[--background] [--add &lt;folder&gt;]… [&lt;folder&gt;]…</c>.
    /// A second launch with folders hands them to the running instance.
    /// </summary>
    public static (bool Background, List<string> Folders) ParseArguments(IEnumerable<string> args)
    {
        bool background = false;
        var folders = new List<string>();
        var list = args.ToList();
        for (int i = 0; i < list.Count; i++)
        {
            string a = list[i];
            if (a.Equals("--background", StringComparison.OrdinalIgnoreCase)) background = true;
            else if (a.Equals("--add", StringComparison.OrdinalIgnoreCase) && i + 1 < list.Count) folders.Add(list[++i]);
            else if (!a.StartsWith("--", StringComparison.Ordinal) && Directory.Exists(a)) folders.Add(a);
        }
        return (background, folders);
    }

    /// <summary>Splits a raw command-line string the way the C runtime does (for redirected launches).</summary>
    public static IEnumerable<string> SplitCommandLine(string commandLine)
    {
        var current = new System.Text.StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in commandLine)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0 || any) yield return current.ToString();
                current.Clear();
                any = false;
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0 || any) yield return current.ToString();
    }

    [STAThread]
    private static int Main(string[] args)
    {
        // Helper mode for graceful stop: raise Ctrl+C on a child's console from a throwaway process
        // (see ChildProcess.CtrlCSender). Must come before the single-instance check.
        if (args.Length == 2 && args[0] == "--send-ctrl-c" && int.TryParse(args[1], out int targetPid))
            return LocalAppManager.Core.Processes.ChildProcess.SendCtrlCFromHelper(targetPid);

        WinRT.ComWrappersSupport.InitializeComWrappers();
        (StartInBackground, var folders) = ParseArguments(args);
        FoldersToAdd = folders;

        if (RedirectToRunningInstance()) return 0;

        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    /// <summary>Raised (on a background thread) when another launch was redirected here.</summary>
    public static event Action<AppActivationArguments>? Redirected;

    private static bool RedirectToRunningInstance()
    {
        var instance = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (instance.IsCurrent)
        {
            instance.Activated += (_, e) => Redirected?.Invoke(e);
            return false;
        }

        // Redirecting is async; wait for it without blocking the STA in a way that could deadlock COM.
        var args = AppInstance.GetCurrent().GetActivatedEventArgs();
        nint evt = CreateEventW(0, true, false, null);
        _ = Task.Run(() =>
        {
            try { instance.RedirectActivationToAsync(args).AsTask().Wait(); }
            finally { SetEvent(evt); }
        });
        CoWaitForMultipleObjects(0, 0xFFFFFFFF, 1, [evt], out _);
        CloseHandle(evt);
        return true;
    }

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateEventW(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetEvent(nint handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("ole32.dll")]
    private static partial int CoWaitForMultipleObjects(uint flags, uint timeout, uint count, [In] nint[] handles, out uint index);
}
