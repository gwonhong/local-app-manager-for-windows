using System.Text.Json;
using System.Text.RegularExpressions;

namespace LocalAppManager.Core.Projects;

public sealed record FrameworkInfo(string Id, string DisplayName, PortMode PortMode, string PortArgs = "--port {port}", IReadOnlyDictionary<string, string>? ExtraEnv = null);

public sealed record DetectionResult(
    string Name,
    FrameworkInfo Framework,
    string? PackageManager,
    string StartCommand,
    IReadOnlyDictionary<string, string> Scripts,
    bool UsesPort);

/// <summary>Guesses framework, package manager and start command from a project folder.</summary>
public static partial class FrameworkDetector
{
    public static readonly FrameworkInfo Unknown = new("unknown", "기타", PortMode.None);

    // Vite ignores PORT entirely and silently moves to the next port if one is taken;
    // --strictPort makes it fail instead, so the assigned port is the real one.
    public static readonly FrameworkInfo Vite = new("vite", "Vite", PortMode.Args, "--port {port} --strictPort");
    public static readonly FrameworkInfo SvelteKit = new("sveltekit", "SvelteKit", PortMode.Args, "--port {port} --strictPort");
    public static readonly FrameworkInfo Next = new("next", "Next.js", PortMode.Env);
    public static readonly FrameworkInfo Nuxt = new("nuxt", "Nuxt", PortMode.Env);
    public static readonly FrameworkInfo Astro = new("astro", "Astro", PortMode.Args, "--port {port}");
    public static readonly FrameworkInfo Angular = new("angular", "Angular", PortMode.Args, "--port {port}");
    public static readonly FrameworkInfo Cra = new("cra", "Create React App", PortMode.Env, ExtraEnv: new Dictionary<string, string> { ["BROWSER"] = "none" });
    public static readonly FrameworkInfo Remix = new("remix", "Remix", PortMode.Env);
    public static readonly FrameworkInfo Nest = new("nest", "NestJS", PortMode.Env);
    public static readonly FrameworkInfo NodeServer = new("node", "Node.js", PortMode.Env);
    public static readonly FrameworkInfo Django = new("django", "Django", PortMode.None);
    public static readonly FrameworkInfo FastApi = new("fastapi", "FastAPI", PortMode.None);
    public static readonly FrameworkInfo Flask = new("flask", "Flask", PortMode.None);
    public static readonly FrameworkInfo Streamlit = new("streamlit", "Streamlit", PortMode.None);
    public static readonly FrameworkInfo Python = new("python", "Python", PortMode.None);

    public static IReadOnlyList<FrameworkInfo> All { get; } =
        [Vite, SvelteKit, Next, Nuxt, Astro, Angular, Cra, Remix, Nest, NodeServer, Django, FastApi, Flask, Streamlit, Python, Unknown];

    public static FrameworkInfo ById(string? id) => All.FirstOrDefault(f => f.Id == id) ?? Unknown;

    public static DetectionResult Detect(string dir)
    {
        string folderName = System.IO.Path.GetFileName(dir.TrimEnd('\\', '/'));
        var pkgPath = System.IO.Path.Combine(dir, "package.json");
        if (File.Exists(pkgPath)) return DetectNode(dir, pkgPath, folderName);
        return DetectPython(dir, folderName) ?? new DetectionResult(folderName, Unknown, null, "", new Dictionary<string, string>(), false);
    }

    // ---- Node ----

    private static DetectionResult DetectNode(string dir, string pkgPath, string folderName)
    {
        var scripts = new Dictionary<string, string>();
        var deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string name = folderName;
        string? pmField = null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(pkgPath), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()))
                name = n.GetString()!;
            if (root.TryGetProperty("packageManager", out var pm) && pm.ValueKind == JsonValueKind.String)
                pmField = pm.GetString();
            if (root.TryGetProperty("scripts", out var s) && s.ValueKind == JsonValueKind.Object)
                foreach (var p in s.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) scripts[p.Name] = p.Value.GetString()!;
            foreach (var section in new[] { "dependencies", "devDependencies", "peerDependencies" })
                if (root.TryGetProperty(section, out var d) && d.ValueKind == JsonValueKind.Object)
                    foreach (var p in d.EnumerateObject()) deps.Add(p.Name);
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            // Unreadable package.json: still a Node project, just without scripts.
        }

        string packageManager = DetectPackageManager(dir, pmField);
        string? devScript = PickStartScript(scripts);
        FrameworkInfo fw = DetectNodeFramework(dir, deps, devScript is null ? null : scripts[devScript]);

        string start = devScript is null ? "" : $"{packageManager} run {devScript}";
        bool usesPort = fw.PortMode != PortMode.None;
        return new DetectionResult(name, fw, packageManager, start, scripts, usesPort);
    }

    public static IReadOnlyDictionary<string, string> ReadScripts(string dir)
    {
        var pkgPath = System.IO.Path.Combine(dir, "package.json");
        if (!File.Exists(pkgPath)) return new Dictionary<string, string>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(pkgPath), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var result = new Dictionary<string, string>();
            if (doc.RootElement.TryGetProperty("scripts", out var s) && s.ValueKind == JsonValueKind.Object)
                foreach (var p in s.EnumerateObject())
                    if (p.Value.ValueKind == JsonValueKind.String) result[p.Name] = p.Value.GetString()!;
            return result;
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    internal static string DetectPackageManager(string dir, string? packageManagerField)
    {
        if (!string.IsNullOrEmpty(packageManagerField))
        {
            var id = packageManagerField.Split('@')[0].Trim().ToLowerInvariant();
            if (id is "npm" or "pnpm" or "yarn" or "bun") return id;
        }
        // Walk up a few levels so packages inside a monorepo find the workspace lockfile.
        var current = new DirectoryInfo(dir);
        for (int depth = 0; current is not null && depth < 4; depth++, current = current.Parent)
        {
            string P(string f) => System.IO.Path.Combine(current.FullName, f);
            if (File.Exists(P("pnpm-lock.yaml")) || File.Exists(P("pnpm-workspace.yaml"))) return "pnpm";
            if (File.Exists(P("bun.lockb")) || File.Exists(P("bun.lock"))) return "bun";
            if (File.Exists(P("yarn.lock"))) return "yarn";
            if (File.Exists(P("package-lock.json"))) return "npm";
        }
        return "npm";
    }

    internal static string? PickStartScript(IReadOnlyDictionary<string, string> scripts)
    {
        foreach (var candidate in new[] { "dev", "start:dev", "develop", "serve", "start" })
            if (scripts.ContainsKey(candidate)) return candidate;
        return null;
    }

    private static FrameworkInfo DetectNodeFramework(string dir, HashSet<string> deps, string? devScriptBody)
    {
        string script = devScriptBody ?? "";
        bool ScriptRuns(string tool) => Regex.IsMatch(script, $@"(^|[\s&|;(]){Regex.Escape(tool)}(\s|$)");

        // The dev script is the strongest signal: "vite" wins even if "next" is also a dependency, etc.
        if (ScriptRuns("next")) return Next;
        if (ScriptRuns("nuxt") || ScriptRuns("nuxi")) return Nuxt;
        if (ScriptRuns("astro")) return Astro;
        if (ScriptRuns("ng")) return Angular;
        if (ScriptRuns("react-scripts")) return Cra;
        if (ScriptRuns("remix") && !deps.Contains("vite")) return Remix;
        if (ScriptRuns("vite"))
            return deps.Contains("@sveltejs/kit") ? SvelteKit : Vite;
        if (ScriptRuns("nest")) return Nest;

        if (deps.Contains("next")) return Next;
        if (deps.Contains("nuxt")) return Nuxt;
        if (deps.Contains("astro")) return Astro;
        if (deps.Contains("@angular/core")) return Angular;
        if (deps.Contains("react-scripts")) return Cra;
        if (deps.Contains("@sveltejs/kit")) return SvelteKit;
        if (deps.Contains("vite") || HasViteConfig(dir)) return Vite;
        if (deps.Contains("@remix-run/dev")) return Remix;
        if (deps.Contains("@nestjs/core")) return Nest;
        if (deps.Overlaps(["express", "fastify", "koa", "hono", "@hono/node-server", "nodemon", "restify", "@hapi/hapi", "polka"]))
            return NodeServer;
        return NodeServer;
    }

    private static bool HasViteConfig(string dir) =>
        new[] { "vite.config.ts", "vite.config.js", "vite.config.mjs", "vite.config.mts", "vite.config.cjs", "vite.config.cts" }
            .Any(f => File.Exists(System.IO.Path.Combine(dir, f)));

    // ---- Python ----

    private static DetectionResult? DetectPython(string dir, string folderName)
    {
        string P(string f) => System.IO.Path.Combine(dir, f);
        bool hasManage = File.Exists(P("manage.py"));
        bool hasPyproject = File.Exists(P("pyproject.toml"));
        bool hasRequirements = File.Exists(P("requirements.txt"));
        bool hasPy = hasManage || hasPyproject || hasRequirements || File.Exists(P("setup.py")) || File.Exists(P("Pipfile"));
        if (!hasPy) return null;

        string deps = "";
        foreach (var f in new[] { "pyproject.toml", "requirements.txt", "Pipfile", "setup.py" })
        {
            try { if (File.Exists(P(f))) deps += File.ReadAllText(P(f)).ToLowerInvariant() + "\n"; }
            catch { }
        }
        bool Has(string pkg) => Regex.IsMatch(deps, $@"(^|[\s""'\[,=]){Regex.Escape(pkg)}([\s""'\],<>=~!;\[]|$)", RegexOptions.Multiline);

        // How to run Python tools in this folder: uv > poetry > local venv > PATH.
        string run;
        if (File.Exists(P("uv.lock"))) run = "uv run ";
        else if (File.Exists(P("poetry.lock"))) run = "poetry run ";
        else if (File.Exists(P(@".venv\Scripts\python.exe"))) run = @".venv\Scripts\";
        else if (File.Exists(P(@"venv\Scripts\python.exe"))) run = @"venv\Scripts\";
        else run = "";

        string py = run + "python";

        if (hasManage || Has("django"))
            return new(folderName, Django, null, $"{py} manage.py runserver {{port}}", new Dictionary<string, string>(), true);

        if (Has("fastapi"))
        {
            string module = FindAsgiModule(dir) ?? "main:app";
            string uvicorn = run + "uvicorn";
            return new(folderName, FastApi, null, $"{uvicorn} {module} --reload --port {{port}}", new Dictionary<string, string>(), true);
        }

        if (Has("flask"))
        {
            string flask = run + "flask";
            return new(folderName, Flask, null, $"{flask} run --debug --port {{port}}", new Dictionary<string, string>(), true);
        }

        if (Has("streamlit"))
        {
            string entry = new[] { "app.py", "streamlit_app.py", "main.py", "Home.py" }.FirstOrDefault(f => File.Exists(P(f))) ?? "app.py";
            string st = run + "streamlit";
            return new(folderName, Streamlit, null, $"{st} run {entry} --server.port {{port}} --server.headless true", new Dictionary<string, string>(), true);
        }

        string mainFile = new[] { "main.py", "app.py", "run.py", "server.py" }.FirstOrDefault(f => File.Exists(P(f))) ?? "main.py";
        return new(folderName, Python, null, $"{py} {mainFile}", new Dictionary<string, string>(), false);
    }

    private static string? FindAsgiModule(string dir)
    {
        foreach (var (file, module) in new[] { ("main.py", "main"), ("app.py", "app"), (@"app\main.py", "app.main"), (@"src\main.py", "src.main"), ("server.py", "server") })
        {
            var path = System.IO.Path.Combine(dir, file);
            if (!File.Exists(path)) continue;
            try
            {
                var m = AsgiAppRegex().Match(File.ReadAllText(path));
                return $"{module}:{(m.Success ? m.Groups[1].Value : "app")}";
            }
            catch
            {
                return $"{module}:app";
            }
        }
        return null;
    }

    [GeneratedRegex(@"^(\w+)\s*=\s*FastAPI\(", RegexOptions.Multiline)]
    private static partial Regex AsgiAppRegex();
}
