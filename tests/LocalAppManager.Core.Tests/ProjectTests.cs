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
            Assert.Equal("python manage.py runserver", r.StartCommand);
            Assert.True(r.Framework.CanPinPort);
            var pinned = new ProjectDefinition { Port = 4300, PortMode = r.Framework.PortMode, PortArgs = r.Framework.PortArgs };
            Assert.Equal("python manage.py runserver 4300", CommandBuilder.Build(pinned, r.StartCommand, isServer: true, new Dictionary<string, string>()).ShellCommand);
        }
        using (var d = new TempDir().File("pyproject.toml", "[project]\ndependencies = [\"fastapi>=0.110\", \"uvicorn\"]\n").File("uv.lock").File("app/main.py", "api = FastAPI()\n"))
        {
            var r = FrameworkDetector.Detect(d.Path);
            Assert.Equal("fastapi", r.Framework.Id);
            Assert.Equal("uv run uvicorn app.main:api --reload", r.StartCommand);
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
        var def = new ProjectDefinition { Port = 4100, PortMode = PortMode.Args, PortArgs = "--port {port} --strictPort" };
        Assert.Equal("npm run dev -- --port 4100 --strictPort", CommandBuilder.Build(def, "npm run dev", true, NoEnv).ShellCommand);
    }

    [Theory]
    [InlineData("pnpm run dev")]
    [InlineData("yarn dev")]
    [InlineData("bun run dev")]
    [InlineData("npx vite")]
    public void Other_managers_get_args_directly(string cmd)
    {
        var def = new ProjectDefinition { Port = 4100, PortMode = PortMode.Args, PortArgs = "--port {port} --strictPort" };
        Assert.Equal($"{cmd} --port 4100 --strictPort", CommandBuilder.Build(def, cmd, true, NoEnv).ShellCommand);
    }

    [Fact]
    public void Existing_double_dash_is_reused()
    {
        var def = new ProjectDefinition { Port = 4100, PortMode = PortMode.Args, PortArgs = "--port {port}" };
        Assert.Equal("npm run dev -- --host --port 4100", CommandBuilder.Build(def, "npm run dev -- --host", true, NoEnv).ShellCommand);
    }

    [Fact]
    public void Args_go_only_to_server_commands_env_goes_to_all()
    {
        var args = new ProjectDefinition { Port = 4100, PortMode = PortMode.Args, PortArgs = "--port {port} --strictPort" };
        var build = CommandBuilder.Build(args, "pnpm run build", isServer: false, NoEnv);
        Assert.Equal("pnpm run build", build.ShellCommand); // "vite build --port" would fail
        Assert.False(build.PortPassed);

        var env = new ProjectDefinition { Port = 4200, PortMode = PortMode.Env };
        var lint = CommandBuilder.Build(env, "pnpm run lint", isServer: false, NoEnv);
        Assert.Equal("4200", lint.Environment["PORT"]); // harmless for a non-server
        Assert.False(lint.PortPassed); // …and no port check / readiness for it
        Assert.True(CommandBuilder.Build(env, "pnpm run dev", isServer: true, NoEnv).PortPassed);
    }

    [Fact]
    public void Env_mode_sets_port_and_defaults()
    {
        var def = new ProjectDefinition { Port = 4200, PortMode = PortMode.Env, Framework = "cra" };
        def.Environment["FORCE_COLOR"] = "0";
        var built = CommandBuilder.Build(def, "npm run dev", true, NoEnv);
        Assert.Equal("npm run dev", built.ShellCommand);
        Assert.Equal("4200", built.Environment["PORT"]);
        Assert.Equal("0", built.Environment["FORCE_COLOR"]); // user override wins
        Assert.Equal("none", built.Environment["BROWSER"]);
        Assert.Equal("1", built.Environment["PYTHONUNBUFFERED"]);
    }

    [Fact]
    public void Inherited_no_color_is_dropped()
    {
        var def = new ProjectDefinition();
        Assert.False(CommandBuilder.Build(def, "x", true, new Dictionary<string, string> { ["NO_COLOR"] = "1" }).Environment.ContainsKey("NO_COLOR"));
        def.Environment["NO_COLOR"] = "1";
        Assert.Equal("1", CommandBuilder.Build(def, "x", true, new Dictionary<string, string>()).Environment["NO_COLOR"]);
    }

    [Fact]
    public void Placeholder_overrides_mode()
    {
        var def = new ProjectDefinition { Port = 4300, PortMode = PortMode.Env };
        var built = CommandBuilder.Build(def, "python manage.py runserver {port}", false, NoEnv);
        Assert.Equal("python manage.py runserver 4300", built.ShellCommand);
        Assert.False(built.Environment.ContainsKey("PORT"));
    }

    [Fact]
    public void Wraps_in_cmd_with_s_switch() =>
        Assert.EndsWith(" /d /s /c \"pnpm run dev\"", CommandBuilder.WrapInCmd("pnpm run dev"));
}

public class CommandListTests
{
    private static readonly Dictionary<string, string> Scripts = new() { ["dev"] = "vite", ["build"] = "vite build", ["lint"] = "eslint ." };

    [Fact]
    public void Custom_order_first_then_unlisted_scripts()
    {
        var def = new ProjectDefinition { PackageManager = "pnpm" };
        def.Commands.Add(new ProjectCommand { Name = "lint", Script = "lint" });
        def.Commands.Add(new ProjectCommand { Name = "tunnel", Command = "cloudflared tunnel run" });
        def.Commands.Add(new ProjectCommand { Name = "gone", Script = "gone" }); // not in package.json anymore
        def.HiddenScripts.Add("build");
        var list = CommandList.Resolve(def, Scripts);
        Assert.Equal(["lint", "tunnel", "dev"], list.Select(c => c.Name));
        Assert.Equal("pnpm run lint", list[0].ShellCommand);
        Assert.Equal("cmd:tunnel", list[1].Key);
        Assert.Equal("script:dev", list[2].Key);
    }

    [Theory]
    [InlineData("pnpm run dev", "dev")]
    [InlineData("npm start", "start")]
    [InlineData("yarn dev", "dev")]
    [InlineData("npm run build:prod", "build:prod")]
    [InlineData("python manage.py runserver", null)]
    [InlineData("pnpm install", null)]
    public void Script_of_a_command_line(string command, string? script) =>
        Assert.Equal(script, CommandList.ScriptOf(command));

    [Theory]
    [InlineData("vite", null, true)]
    [InlineData("vite --host", null, true)]
    [InlineData("vite preview", null, true)]
    [InlineData("vite build", null, false)]
    [InlineData("vitest run", null, false)]
    [InlineData("tsc -b && vite build", null, false)]
    [InlineData("next dev --turbo", null, true)]
    [InlineData("next build", null, false)]
    [InlineData("eslint .", "lint", false)]
    [InlineData("node server.js", "start", true)] // unknown body, server-like script name
    [InlineData("uv run uvicorn app:app --reload", null, true)]
    [InlineData("python manage.py runserver", null, true)]
    [InlineData("python manage.py migrate", null, false)]
    public void Server_detection(string body, string? script, bool server) =>
        Assert.Equal(server, CommandList.IsServerCommand(body, script));

    [Fact]
    public void Legacy_start_command_becomes_the_first_chip()
    {
        var def = new ProjectDefinition { StartCommand = "pnpm run dev" };
        def.Commands.Add(new ProjectCommand { Name = "tunnel", Command = "cloudflared tunnel run" });
        def.HiddenScripts.Add("lint");
        Assert.True(CommandList.MigrateStartCommand(def, Scripts));
        Assert.Null(def.StartCommand);
        Assert.Equal("script:dev", def.DefaultCommand); // the old start command stays the default
        // start first, then the visible scripts as the chip row showed them, then the custom commands.
        Assert.Equal(["dev", "build", "tunnel"], CommandList.Resolve(def, Scripts).Select(c => c.Name));
        Assert.False(CommandList.MigrateStartCommand(def, Scripts)); // only once

        var py = new ProjectDefinition { StartCommand = "python app.py" };
        CommandList.MigrateStartCommand(py, new Dictionary<string, string>());
        var only = Assert.Single(CommandList.Resolve(py, new Dictionary<string, string>()));
        Assert.Equal("start", only.Name);
        Assert.Equal("cmd:start", py.DefaultCommand);
        Assert.Equal("python app.py", only.ShellCommand);
        Assert.True(only.IsServer); // the old start command keeps crash handling
    }

    [Fact]
    public void Default_is_the_chosen_chip_else_the_first()
    {
        var def = new ProjectDefinition { PackageManager = "pnpm" };
        var list = CommandList.Resolve(def, Scripts);
        Assert.Equal("dev", CommandList.DefaultOf(def, list)!.Name); // none chosen: the first
        def.DefaultCommand = "script:lint";
        Assert.Equal("lint", CommandList.DefaultOf(def, list)!.Name); // anywhere in the order
        def.DefaultCommand = "cmd:gone";
        Assert.Equal("dev", CommandList.DefaultOf(def, list)!.Name); // removed: back to the first
        Assert.Null(CommandList.DefaultOf(def, []));
    }

    [Fact]
    public void Old_projects_json_is_migrated_on_load()
    {
        using var data = new TempDir();
        using var proj = new TempDir().File("package.json", """{"scripts":{"build":"vite build","dev":"vite"}}""");
        File.WriteAllText(System.IO.Path.Combine(data.Path, "projects.json"), $$"""
            {"projects":[{"id":"p","name":"p","path":{{System.Text.Json.JsonSerializer.Serialize(proj.Path)}},"startCommand":"npm run dev",
              "commands":[{"name":"x","command":"echo x"}]}],"groups":[]}
            """);
        var m = new ProjectManager(new AppPaths(data.Path));
        m.Load();
        Assert.Equal(["dev", "build", "x"], m.GetCommands("p").Select(c => c.Name));
        Assert.DoesNotContain("startCommand", File.ReadAllText(System.IO.Path.Combine(data.Path, "projects.json")));
        Assert.Contains("startCommand", File.ReadAllText(System.IO.Path.Combine(data.Path, "projects.json.before-commands.bak"))); // original kept
    }
}

public class ProjectManagerTests
{
    /// <summary>Gives the project a single custom chip.</summary>
    private static void SetCommand(ProjectManager m, ProjectRuntime r, string name, string command, bool autoRestart = false)
    {
        var def = r.Definition.Clone();
        def.Commands = [new ProjectCommand { Name = name, Command = command }];
        def.AutoRestart = autoRestart;
        m.UpdateProject(def);
    }

    [Fact]
    public void Add_detects_and_persists_without_pinning_a_port()
    {
        using var data = new TempDir();
        using var proj = new TempDir().File("package.json", """{"name":"a","scripts":{"dev":"vite"}}""");
        var m = new ProjectManager(new AppPaths(data.Path), new Ports.PortAllocator(_ => true));
        m.Load();
        var r = m.AddProject(proj.Path);
        Assert.Null(r.Definition.Port); // observed, not pinned
        Assert.Equal(PortMode.Args, r.Definition.PortMode); // ready if the user pins one
        Assert.Same(r, m.AddProject(proj.Path + "\\")); // same folder isn't added twice

        using var proj2 = new TempDir().File("package.json", """{"name":"a","scripts":{"dev":"next dev"},"dependencies":{"next":"1"}}""");
        var r2 = m.AddProject(proj2.Path);
        Assert.Null(r2.Definition.Port);
        Assert.Equal("a (2)", r2.Definition.Name);

        var m2 = new ProjectManager(new AppPaths(data.Path));
        m2.Load();
        Assert.Equal(2, m2.Projects.Count);
        Assert.Null(m2.Projects[1].Definition.Port);
        Assert.Equal(PortMode.Env, m2.Projects[1].Definition.PortMode);
    }

    [Fact]
    public void Pin_and_unpin_a_port()
    {
        using var data = new TempDir();
        using var a = new TempDir().File("package.json", """{"name":"a","scripts":{"dev":"vite"}}""");
        using var b = new TempDir().File("package.json", """{"name":"b","scripts":{"dev":"vite"}}""");
        var m = new ProjectManager(new AppPaths(data.Path), new Ports.PortAllocator(_ => true));
        m.Load();
        var ra = m.AddProject(a.Path);
        var rb = m.AddProject(b.Path);

        Assert.Equal(4100, m.SuggestPinPort(ra.Id)); // nothing seen yet: a free one from the range
        Assert.Null(m.PinPort(ra.Id, 4100));
        Assert.Equal(4100, ra.Definition.Port);
        Assert.Equal(4100, m.SuggestPinPort(ra.Id)); // pinned: suggest the pinned one

        Assert.Same(ra, m.PinPort(rb.Id, 4100)); // taken by a: refused
        Assert.Null(rb.Definition.Port);
        Assert.Equal(4101, m.SuggestPinPort(rb.Id));

        Assert.Null(m.PinPort(ra.Id, null));
        Assert.Null(ra.Definition.Port);
        var reloaded = new ProjectManager(new AppPaths(data.Path));
        reloaded.Load();
        Assert.Null(reloaded.Projects[0].Definition.Port);
    }

    [Fact]
    public async Task Start_and_stop_a_command()
    {
        using var data = new TempDir();
        using var proj = new TempDir();
        var m = new ProjectManager(new AppPaths(data.Path));
        m.Load();
        var r = m.AddProject(proj.Path);
        SetCommand(m, r, "work", "echo started& ping -n 30 127.0.0.1 >nul");

        await m.StartAsync(r.Id);
        Assert.True(r.IsActive);
        for (int i = 0; i < 50 && r.State != ProjectState.Running; i++) await Task.Delay(100);
        Assert.Equal(ProjectState.Running, r.State); // not a server: running after 2s alive
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
        def.Commands =
        [
            new ProjectCommand { Name = "main", Command = "ping -n 30 127.0.0.1 >nul" },
            new ProjectCommand { Name = "a", Command = "echo a-ran& ping -n 30 127.0.0.1 >nul" },
            new ProjectCommand { Name = "b", Command = "ping -n 30 127.0.0.1 >nul" },
        ];
        m.UpdateProject(def);
        int Ran() => r.Log.Snapshot().Count(l => l.Text == "a-ran");
        async Task WaitRan(int n) { for (int i = 0; i < 100 && Ran() < n; i++) await Task.Delay(20); }

        // Start runs the first chip when none is chosen as the default…
        await m.StartAsync(r.Id);
        Assert.True(r.IsActive);
        Assert.Equal("cmd:main", r.RunKey);

        // …and the chosen default otherwise, wherever it is in the order.
        await m.StopAsync(r.Id);
        m.SetDefaultCommand(r.Id, "cmd:b");
        await m.StartAsync(r.Id);
        Assert.Equal("cmd:b", r.RunKey);
        await m.StopAsync(r.Id);
        m.SetCommands(r.Id, r.Definition.Commands, defaultKey: null);
        await m.StartAsync(r.Id);
        Assert.Equal("cmd:main", r.RunKey);

        // Another chip replaces it…
        await m.RunAsync(r.Id, "cmd:a");
        Assert.True(r.IsActive);
        Assert.Equal("a", r.RunName);
        Assert.Equal(-1, r.GetLastExit("cmd:main")); // stopped by the switch
        await WaitRan(1);

        // …running the same one again restarts it…
        var first = r.StartedAt;
        await m.RunAsync(r.Id, "cmd:a");
        await WaitRan(2);
        Assert.Equal("a", r.RunName);
        Assert.True(r.StartedAt > first);

        // …start does nothing while something runs, restart runs the current one again.
        await m.StartAsync(r.Id);
        Assert.Equal("a", r.RunName);
        await m.RestartAsync(r.Id);
        await WaitRan(3);
        Assert.Equal("a", r.RunName);

        // Stop stops whichever it is.
        await m.RunAsync(r.Id, "cmd:b");
        await m.StopAsync(r.Id);
        Assert.False(r.IsActive);
        Assert.Equal(3, Ran());
    }

    [Fact]
    public async Task A_failed_build_is_not_a_crash()
    {
        using var data = new TempDir();
        using var proj = new TempDir();
        var m = new ProjectManager(new AppPaths(data.Path));
        m.Load();
        var r = m.AddProject(proj.Path);
        SetCommand(m, r, "lint", "exit /b 3", autoRestart: true);
        int crashes = 0;
        m.ProjectCrashed += (_, _) => Interlocked.Increment(ref crashes);

        await m.StartAsync(r.Id);
        for (int i = 0; i < 50 && r.IsActive; i++) await Task.Delay(50);
        await Task.Delay(200);
        Assert.Equal(ProjectState.Crashed, r.State);
        Assert.Equal(3, r.GetLastExit("cmd:lint"));
        Assert.False(r.IsServerRun);
        Assert.Equal(0, crashes); // no toast…
        Assert.Null(r.NextRestartAt); // …and no auto-restart
    }

    [Fact]
    public async Task Crash_is_reported_and_auto_restart_schedules()
    {
        using var data = new TempDir();
        using var proj = new TempDir();
        var m = new ProjectManager(new AppPaths(data.Path));
        m.Load();
        var r = m.AddProject(proj.Path);
        SetCommand(m, r, "start", "exit /b 5", autoRestart: true); // "start": a server by convention
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
        // Crashes after ~0.3 s every time, so restarts keep coming.
        SetCommand(m, r, "start", "ping -n 1 -w 300 10.255.255.1 >nul & exit /b 5", autoRestart: true);

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
