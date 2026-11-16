using LocalAppManager.Core.Projects;
using LocalAppManager.Core.Runtime;
using LocalAppManager.Core.Storage;

namespace LocalAppManager.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lam-test-" + Guid.NewGuid().ToString("N")[..8]);

    public TempDir() => Directory.CreateDirectory(Path);

    public TempDir File(string name, string content = "")
    {
        var full = System.IO.Path.Combine(Path, name);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return this;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, true); } catch { }
    }
}

public class FrameworkDetectorTests
{
    [Fact]
    public void Vite_with_pnpm()
    {
        using var d = new TempDir()
            .File("package.json", """{"name":"maple","scripts":{"dev":"vite","build":"tsc -b && vite build"},"devDependencies":{"vite":"^8"}}""")
            .File("pnpm-lock.yaml");
        var r = FrameworkDetector.Detect(d.Path);
        Assert.Equal("vite", r.Framework.Id);
        Assert.Equal("pnpm", r.PackageManager);
        Assert.Equal("pnpm run dev", r.StartCommand);
        Assert.Equal(PortMode.Args, r.Framework.PortMode);
        Assert.Contains("--strictPort", r.Framework.PortArgs);
        Assert.Equal("maple", r.Name);
        Assert.Equal(2, r.Scripts.Count);
    }

    [Theory]
    [InlineData("""{"scripts":{"dev":"next dev"},"dependencies":{"next":"15","react":"19"}}""", "next", PortMode.Env)]
    [InlineData("""{"scripts":{"start":"react-scripts start"},"dependencies":{"react-scripts":"5"}}""", "cra", PortMode.Env)]
    [InlineData("""{"scripts":{"dev":"vite dev"},"devDependencies":{"@sveltejs/kit":"2","vite":"6"}}""", "sveltekit", PortMode.Args)]
    [InlineData("""{"scripts":{"dev":"nodemon server.js"},"dependencies":{"express":"5"}}""", "node", PortMode.Env)]
    [InlineData("""{"scripts":{"dev":"astro dev"},"dependencies":{"astro":"5"}}""", "astro", PortMode.Args)]
    public void Node_frameworks(string pkg, string id, PortMode mode)
    {
        using var d = new TempDir().File("package.json", pkg).File("package-lock.json", "{}");
        var r = FrameworkDetector.Detect(d.Path);
        Assert.Equal(id, r.Framework.Id);
        Assert.Equal(mode, r.Framework.PortMode);
        Assert.Equal("npm", r.PackageManager);
    }

    [Fact]
    public void Package_manager_field_wins()
    {
        using var d = new TempDir().File("package.json", """{"packageManager":"yarn@4.1.0","scripts":{"dev":"vite"}}""").File("package-lock.json", "{}");
        Assert.Equal("yarn", FrameworkDetector.Detect(d.Path).PackageManager);
    }

    [Fact]
    public void Django_and_fastapi()
    {
        using (var d = new TempDir().File("manage.py").File("requirements.txt", "Django>=5\n"))
        {
            var r = FrameworkDetector.Detect(d.Path);
            Assert.Equal("django", r.Framework.Id);
            Assert.Equal("python manage.py runserver {port}", r.StartCommand);
        }
        using (var d = new TempDir().File("pyproject.toml", "[project]\ndependencies = [\"fastapi>=0.110\", \"uvicorn\"]\n").File("uv.lock").File("app/main.py", "api = FastAPI()\n"))
        {
            var r = FrameworkDetector.Detect(d.Path);
            Assert.Equal("fastapi", r.Framework.Id);
            Assert.Equal("uv run uvicorn app.main:api --reload --port {port}", r.StartCommand);
        }
    }

    [Fact]
    public void Unknown_folder() =>
        Assert.Equal("unknown", FrameworkDetector.Detect(new TempDir().Path).Framework.Id);
}

public class CommandBuilderTests
{
    private static readonly Dictionary<string, string> NoEnv = new();

    [Fact]
    public void Npm_gets_double_dash_for_args()
    {
        var def = new ProjectDefinition { StartCommand = "npm run dev", Port = 4100, PortMode = PortMode.Args, PortArgs = "--port {port} --strictPort" };
        Assert.Equal("npm run dev -- --port 4100 --strictPort", CommandBuilder.BuildStart(def, NoEnv).ShellCommand);
    }

    [Theory]
    [InlineData("pnpm run dev")]
    [InlineData("yarn dev")]
    [InlineData("bun run dev")]
    [InlineData("npx vite")]
    public void Other_managers_get_args_directly(string cmd)
    {
        var def = new ProjectDefinition { StartCommand = cmd, Port = 4100, PortMode = PortMode.Args, PortArgs = "--port {port} --strictPort" };
        Assert.Equal($"{cmd} --port 4100 --strictPort", CommandBuilder.BuildStart(def, NoEnv).ShellCommand);
    }

    [Fact]
    public void Existing_double_dash_is_reused()
    {
        var def = new ProjectDefinition { StartCommand = "npm run dev -- --host", Port = 4100, PortMode = PortMode.Args, PortArgs = "--port {port}" };
        Assert.Equal("npm run dev -- --host --port 4100", CommandBuilder.BuildStart(def, NoEnv).ShellCommand);
    }

    [Fact]
    public void Env_mode_sets_port_and_defaults()
    {
        var def = new ProjectDefinition { StartCommand = "npm run dev", Port = 4200, PortMode = PortMode.Env, Framework = "cra" };
        def.Environment["FORCE_COLOR"] = "0";
        var built = CommandBuilder.BuildStart(def, NoEnv);
        Assert.Equal("npm run dev", built.ShellCommand);
        Assert.Equal("4200", built.Environment["PORT"]);
        Assert.Equal("0", built.Environment["FORCE_COLOR"]); // user override wins
        Assert.Equal("none", built.Environment["BROWSER"]);
        Assert.Equal("1", built.Environment["PYTHONUNBUFFERED"]);
    }

    [Fact]
    public void Inherited_no_color_is_dropped()
    {
        var def = new ProjectDefinition { StartCommand = "x" };
        Assert.False(CommandBuilder.BuildStart(def, new Dictionary<string, string> { ["NO_COLOR"] = "1" }).Environment.ContainsKey("NO_COLOR"));
        def.Environment["NO_COLOR"] = "1";
        Assert.Equal("1", CommandBuilder.BuildStart(def, new Dictionary<string, string>()).Environment["NO_COLOR"]);
    }

    [Fact]
    public void Placeholder_overrides_mode()
    {
        var def = new ProjectDefinition { StartCommand = "python manage.py runserver {port}", Port = 4300, PortMode = PortMode.Env };
        var built = CommandBuilder.BuildStart(def, NoEnv);
        Assert.Equal("python manage.py runserver 4300", built.ShellCommand);
        Assert.False(built.Environment.ContainsKey("PORT"));
    }

    [Fact]
    public void Wraps_in_cmd_with_s_switch() =>
        Assert.EndsWith(" /d /s /c \"pnpm run dev\"", CommandBuilder.WrapInCmd("pnpm run dev"));
}

public class ProjectManagerTests
{
    [Fact]
    public void Add_detects_assigns_port_and_persists()
    {
        using var data = new TempDir();
        using var proj = new TempDir().File("package.json", """{"name":"a","scripts":{"dev":"vite"}}""");
        var m = new ProjectManager(new AppPaths(data.Path), new Ports.PortAllocator(_ => true));
        m.Load();
        var r = m.AddProject(proj.Path);
        Assert.Equal(4100, r.Definition.Port);
        Assert.Same(r, m.AddProject(proj.Path + "\\")); // same folder isn't added twice

        using var proj2 = new TempDir().File("package.json", """{"name":"a","scripts":{"dev":"next dev"},"dependencies":{"next":"1"}}""");
        var r2 = m.AddProject(proj2.Path);
        Assert.Equal(4101, r2.Definition.Port);
        Assert.Equal("a (2)", r2.Definition.Name);

        var m2 = new ProjectManager(new AppPaths(data.Path));
        m2.Load();
        Assert.Equal(2, m2.Projects.Count);
        Assert.Equal(4101, m2.Projects[1].Definition.Port);
        Assert.Equal(PortMode.Env, m2.Projects[1].Definition.PortMode);
    }

    [Fact]
    public async Task Start_and_stop_a_command()
    {
        using var data = new TempDir();
        using var proj = new TempDir();
        var m = new ProjectManager(new AppPaths(data.Path));
        m.Load();
        var r = m.AddProject(proj.Path);
        var def = r.Definition.Clone();
        def.StartCommand = "echo started& ping -n 30 127.0.0.1 >nul";
        m.UpdateProject(def);

        await m.StartAsync(r.Id);
        Assert.True(r.IsActive);
        for (int i = 0; i < 50 && r.State != ProjectState.Running; i++) await Task.Delay(100);
        Assert.Equal(ProjectState.Running, r.State); // no port: running after 2s alive
        await m.StopAsync(r.Id);
        Assert.Equal(ProjectState.Stopped, r.State);
        Assert.Contains(r.Log.Snapshot(), l => l.Text == "started");
    }

    [Fact]
    public async Task Only_one_thing_runs_at_a_time()
    {
        using var data = new TempDir();
        using var proj = new TempDir();
        var m = new ProjectManager(new AppPaths(data.Path));
        m.Load();
        var r = m.AddProject(proj.Path);
        var def = r.Definition.Clone();
        def.StartCommand = "ping -n 30 127.0.0.1 >nul";
        def.Commands.Add(new ProjectCommand { Name = "a", Command = "echo a-ran& ping -n 30 127.0.0.1 >nul" });
        def.Commands.Add(new ProjectCommand { Name = "b", Command = "ping -n 30 127.0.0.1 >nul" });
        m.UpdateProject(def);
        var a = r.Definition.Commands[0];
        var b = r.Definition.Commands[1];
        string[] Running() => r.CommandRuns.Select(x => x.DisplayName).ToArray();
        int Ran() => r.Log.Snapshot().Count(l => l.Text == "a-ran");
        async Task WaitRan(int n) { for (int i = 0; i < 100 && Ran() < n; i++) await Task.Delay(20); }

        await m.StartAsync(r.Id);
        Assert.True(r.IsActive);

        // Another command replaces the start command…
        await m.RunCustomCommandAsync(r.Id, a);
        Assert.False(r.IsActive);
        Assert.Equal(ProjectState.Stopped, r.State);
        Assert.Equal(["a"], Running());
        await WaitRan(1);

        // …a third replaces that one…
        await m.RunCustomCommandAsync(r.Id, b);
        Assert.Equal(["b"], Running());

        // …running the same one again restarts it…
        var first = r.CommandRuns[0].StartedAt;
        await m.RunCustomCommandAsync(r.Id, b);
        Assert.Equal(["b"], Running());
        Assert.True(r.CommandRuns[0].StartedAt > first);

        // …and the start command replaces it again.
        await m.StartAsync(r.Id);
        Assert.True(r.IsActive);
        Assert.Empty(Running());

        // Stop stops whichever it is.
        await m.RunCustomCommandAsync(r.Id, a);
        await WaitRan(2);
        await m.StopAsync(r.Id);
        Assert.False(r.IsActive);
        Assert.Empty(Running());
        Assert.Equal(2, Ran());
    }

    [Fact]
    public async Task Crash_is_reported_and_auto_restart_schedules()
    {
        using var data = new TempDir();
        using var proj = new TempDir();
        var m = new ProjectManager(new AppPaths(data.Path));
        m.Load();
        var r = m.AddProject(proj.Path);
        var def = r.Definition.Clone();
        def.StartCommand = "exit /b 5";
        def.AutoRestart = true;
        m.UpdateProject(def);
        int crashes = 0;
        m.ProjectCrashed += (_, code) => { Assert.Equal(5, code); Interlocked.Increment(ref crashes); };

        await m.StartAsync(r.Id);
        for (int i = 0; i < 50 && crashes == 0; i++) await Task.Delay(50);
        for (int i = 0; i < 20 && r.NextRestartAt is null; i++) await Task.Delay(10);
        Assert.Equal(1, crashes);
        Assert.Equal(ProjectState.Crashed, r.State);
        Assert.Equal(5, r.LastExitCode);
        Assert.NotNull(r.NextRestartAt);
        await m.StopAsync(r.Id); // cancels the pending restart
        Assert.Null(r.NextRestartAt);
    }

    [Theory]
    [InlineData(900)]
    [InlineData(1000)]
    [InlineData(1100)]
    public async Task Stop_racing_an_auto_restart_wins(int stopAfterMs)
    {
        using var data = new TempDir();
        using var proj = new TempDir();
        var m = new ProjectManager(new AppPaths(data.Path));
        m.Load();
        var r = m.AddProject(proj.Path);
        var def = r.Definition.Clone();
        // Crashes after ~0.3 s every time, so restarts keep coming.
        def.StartCommand = "ping -n 1 -w 300 10.255.255.1 >nul & exit /b 5";
        def.AutoRestart = true;
        m.UpdateProject(def);

        await m.StartAsync(r.Id);
        for (int i = 0; i < 100 && r.NextRestartAt is null; i++) await Task.Delay(20);
        Assert.NotNull(r.NextRestartAt);
        await Task.Delay(stopAfterMs); // the first restart is due after 1 s
        await m.StopAsync(r.Id);
        await Task.Delay(2500);
        Assert.False(r.IsActive, $"still {r.State} after stop");
        Assert.Null(r.NextRestartAt);
    }

    [Fact]
    public void Backoff_doubles_and_caps() =>
        Assert.Equal([1, 2, 4, 8, 16, 32, 60, 60], Enumerable.Range(0, 8).Select(i => (int)ProjectRuntime.BackoffDelay(i).TotalSeconds));

    [Fact]
    public async Task Port_conflict_blocks_start()
    {
        using var data = new TempDir();
        using var proj = new TempDir().File("package.json", """{"scripts":{"dev":"vite"}}""");
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        int port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        try
        {
            var m = new ProjectManager(new AppPaths(data.Path));
            m.Load();
            var r = m.AddProject(proj.Path);
            var def = r.Definition.Clone();
            def.Port = port;
            m.UpdateProject(def);
            await m.StartAsync(r.Id);
            Assert.Equal(ProjectState.Stopped, r.State);
            Assert.True(r.IsPortConflict);
            Assert.Contains($"PID {Environment.ProcessId}", r.LastError);
            Assert.NotEqual(port, m.ReassignPort(r.Id));
            Assert.False(r.IsPortConflict);
        }
        finally
        {
            l.Stop();
        }
    }
}
