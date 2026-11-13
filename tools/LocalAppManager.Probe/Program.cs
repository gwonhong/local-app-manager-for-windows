// Headless driver for LocalAppManager.Core, used to verify process handling against real projects.
//
//   probe run <folder> [--seconds N] [--crash] [--port N]
//
// Registers the folder in a throwaway data dir, starts it, streams its log, prints the job's
// process ids once it is running, then either stops it after N seconds (and checks that every
// process is gone) or, with --crash, kills this process abruptly to prove the job object
// takes the tree down with it.

using System.Diagnostics;
using LocalAppManager.Core.Runtime;
using LocalAppManager.Core.Storage;

if (args.Length < 2 || args[0] != "run")
{
    Console.WriteLine("usage: probe run <folder> [--seconds N] [--crash] [--port N]");
    return 2;
}

string folder = args[1];
int seconds = ArgInt("--seconds") ?? 8;
bool crash = args.Contains("--crash");
int? port = ArgInt("--port");

var data = Path.Combine(Path.GetTempPath(), "lam-probe-" + Environment.ProcessId);
var manager = new ProjectManager(new AppPaths(data));
manager.Load();
var project = manager.AddProject(folder);
if (port is int p)
{
    var def = project.Definition.Clone();
    def.Port = p;
    manager.UpdateProject(def);
}

long printed = 0;
void PrintLog()
{
    foreach (var line in project.Log.GetSince(printed))
    {
        Console.WriteLine($"  | {(line.Kind == LocalAppManager.Core.Logs.LogLineKind.Output ? "" : "[" + line.Kind + "] ")}{line.Text}");
        printed = line.Seq + 1;
    }
}

Console.WriteLine($"framework={project.Definition.Framework} pm={project.Definition.PackageManager} port={project.Definition.Port} cmd='{project.Definition.StartCommand}'");
await manager.StartAsync(project.Id);
var sw = Stopwatch.StartNew();
while (project.State == ProjectState.Starting && sw.Elapsed < TimeSpan.FromSeconds(30))
{
    PrintLog();
    await Task.Delay(100);
}
await Task.Delay(1500);
PrintLog();
Console.WriteLine($"state={project.State} after {sw.ElapsedMilliseconds}ms url={project.DetectedUrl} listening=[{string.Join(",", project.ListeningPorts)}] error={project.LastError}");
if (project.State != ProjectState.Running) return 1;

var pids = project.ProcessIds;
Console.WriteLine("PIDS " + string.Join(" ", pids.Select(x => $"{x}:{Name(x)}")));

if (crash)
{
    Console.WriteLine("CRASHING");
    Console.Out.Flush();
    Environment.FailFast("probe: simulated crash");
}

await Task.Delay(TimeSpan.FromSeconds(seconds));
PrintLog();
await manager.StopAsync(project.Id);
await Task.Delay(300);
PrintLog();
var survivors = pids.Where(IsAlive).ToList();
Console.WriteLine($"state={project.State} survivors=[{string.Join(",", survivors.Select(x => $"{x}:{Name(x)}"))}]");
try { Directory.Delete(data, true); } catch { }
return survivors.Count == 0 ? 0 : 1;

int? ArgInt(string name)
{
    int i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : null;
}

static bool IsAlive(int pid)
{
    try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
    catch { return false; }
}

static string Name(int pid)
{
    try { using var p = Process.GetProcessById(pid); return p.ProcessName; }
    catch { return "?"; }
}
