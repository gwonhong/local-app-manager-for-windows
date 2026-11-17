using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using LocalAppManager.Core.Logs;
using LocalAppManager.Core.Processes;
using LocalAppManager.Core.Projects;

namespace LocalAppManager.Core.Tests;

/// <summary>Real processes: cmd → node → node grandchild, killed through the job object.</summary>
public class ProcessTreeTests
{
    private static bool HasNode => FindOnPath("node.exe") is not null;

    private static string? FindOnPath(string exe) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d.Trim(), exe)).FirstOrDefault(File.Exists);

    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    /// <summary>
    /// Non-ASCII text that cmd can write in this machine's OEM code page. cmd turns characters the
    /// code page lacks into '?' before we ever see them, so Korean only works on a CP949 machine
    /// (CI runners are en-US, CP437).
    /// </summary>
    private static string OemSample(string preferred)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var oem = Encoding.GetEncoding((int)GetOEMCP(), EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        foreach (var candidate in new[] { preferred, "café", "naïve" })
        {
            try { if (oem.GetString(oem.GetBytes(candidate)) == candidate) return candidate; }
            catch (EncoderFallbackException) { }
        }
        throw new InvalidOperationException($"No sample text fits OEM code page {oem.CodePage}");
    }

    // Parent prints its pid, spawns a child (which prints its pid) and both idle forever.
    private const string TreeScript =
        "const {spawn}=require('child_process');console.log('P'+process.pid);" +
        "const c=spawn(process.execPath,['-e','console.log(`C`+process.pid);setInterval(()=>{},1000)'],{stdio:'inherit'});" +
        "setInterval(()=>{},1000)";

    // No double quotes inside the script, so cmd's quote parsing can't split it.
    private static string NodeEval(string script) => $"node -e \"{script}\"";

    private static async Task<(ChildProcess Proc, List<int> Pids, StringBuilder Output)> StartTreeAsync(string shellCommand)
    {
        var proc = ChildProcess.Start(new ChildProcessOptions
        {
            CommandLine = CommandBuilder.WrapInCmd(shellCommand),
            WorkingDirectory = Path.GetTempPath(),
            Environment = EnvironmentBuilder.CreateBase(),
        });
        var output = new StringBuilder();
        var pids = new List<int>();
        var reader = Task.Run(() =>
        {
            var decoder = new LineDecoder();
            var lines = new List<string>();
            var buf = new byte[4096];
            int n;
            while ((n = proc.Output.Read(buf, 0, buf.Length)) > 0)
            {
                decoder.Feed(buf.AsSpan(0, n), lines);
                lock (output)
                {
                    foreach (var l in lines)
                    {
                        output.AppendLine(l);
                        if (l.Length > 1 && (l[0] == 'P' || l[0] == 'C') && int.TryParse(l[1..], out var pid)) pids.Add(pid);
                    }
                }
                lines.Clear();
            }
        });
        var deadline = DateTime.Now.AddSeconds(15);
        while (DateTime.Now < deadline)
        {
            lock (output) if (pids.Count >= 2) break;
            await Task.Delay(50);
        }
        lock (output) Assert.True(pids.Count >= 2, "node tree didn't start: " + output);
        return (proc, pids, output);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public async Task Kill_terminates_cmd_node_and_grandchild()
    {
        if (!HasNode) return;
        var (proc, pids, _) = await StartTreeAsync(NodeEval(TreeScript));
        using (proc)
        {
            Assert.All(pids, pid => Assert.True(IsAlive(pid)));
            Assert.True(proc.Job.ActiveProcessCount >= 3); // cmd + node + node
            var jobPids = proc.Job.GetProcessIds();
            Assert.Contains(proc.ProcessId, jobPids);
            Assert.All(pids, pid => Assert.Contains(pid, jobPids));

            proc.Kill();
            var finished = await Task.WhenAny(proc.Exited, Task.Delay(5000));
            Assert.Same(proc.Exited, finished);
            await Task.Delay(200);
            Assert.All(pids, pid => Assert.False(IsAlive(pid), $"pid {pid} survived"));
            Assert.False(IsAlive(proc.ProcessId));
        }
    }

    [Fact]
    public async Task Root_exit_kills_leftover_descendants()
    {
        if (!HasNode) return;
        // cmd starts the tree in the background with `start /b` and returns right away;
        // the leftover node processes must not outlive the root.
        var (proc, pids, _) = await StartTreeAsync($"start \"\" /b {NodeEval(TreeScript)} & ping -n 2 127.0.0.1 >nul & exit /b 3");
        using (proc)
        {
            var finished = await Task.WhenAny(proc.Exited, Task.Delay(10000));
            Assert.Same(proc.Exited, finished);
            Assert.Equal(3, await proc.Exited);
            await Task.Delay(200);
            Assert.All(pids, pid => Assert.False(IsAlive(pid), $"pid {pid} survived the root"));
        }
    }

    [Fact]
    public async Task Disposing_the_job_kills_the_tree()
    {
        if (!HasNode) return;
        var (proc, pids, _) = await StartTreeAsync(NodeEval(TreeScript));
        // Simulates the app dying: the job handle closes and KILL_ON_JOB_CLOSE does the rest.
        proc.Job.Dispose();
        await Task.Delay(500);
        Assert.All(pids, pid => Assert.False(IsAlive(pid), $"pid {pid} survived job close"));
        proc.Dispose();
    }

    [Fact]
    public async Task Exit_code_and_output_are_captured()
    {
        var sample = OemSample("오류");
        var proc = ChildProcess.Start(new ChildProcessOptions
        {
            CommandLine = CommandBuilder.WrapInCmd($"echo hello& echo {sample}>&2& exit /b 7"),
            WorkingDirectory = Path.GetTempPath(),
            Environment = EnvironmentBuilder.CreateBase(),
        });
        using (proc)
        {
            var ms = new MemoryStream();
            await Task.Run(() => proc.Output.CopyTo(ms));
            Assert.Equal(7, await proc.Exited);
            var decoder = new LineDecoder();
            var lines = new List<string>();
            decoder.Feed(ms.ToArray(), lines);
            decoder.Flush(lines);
            Assert.Contains("hello", lines);
            Assert.Contains(sample, lines); // cmd writes the OEM code page here; the decoder falls back to it
        }
    }

    [Fact]
    public void Environment_block_is_passed()
    {
        var env = EnvironmentBuilder.CreateBase();
        var sample = OemSample("값") + " 123";
        env["LAM_TEST_VAR"] = sample;
        var proc = ChildProcess.Start(new ChildProcessOptions
        {
            CommandLine = CommandBuilder.WrapInCmd("echo %LAM_TEST_VAR%"),
            WorkingDirectory = Path.GetTempPath(),
            Environment = env,
        });
        using (proc)
        {
            var ms = new MemoryStream();
            proc.Output.CopyTo(ms);
            var decoder = new LineDecoder();
            var lines = new List<string>();
            decoder.Feed(ms.ToArray(), lines);
            decoder.Flush(lines);
            Assert.Equal(sample, lines.FirstOrDefault()?.Trim());
        }
    }
}
