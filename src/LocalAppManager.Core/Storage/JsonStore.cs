using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalAppManager.Core.Diagnostics;

namespace LocalAppManager.Core.Storage;

/// <summary>Reads and atomically writes JSON files (write to a temp file, then replace).</summary>
public static class JsonStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // keep Korean readable in the file
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly object WriteGate = new();

    public static T Load<T>(string path) where T : new()
    {
        try
        {
            if (!File.Exists(path)) return new T();
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
        }
        catch (Exception e)
        {
            // Keep the broken file around instead of silently overwriting the user's data.
            AppLog.Error($"{Path.GetFileName(path)}을(를) 읽지 못했습니다. 백업 후 새로 시작합니다.", e);
            try { File.Copy(path, path + $".broken-{DateTime.Now:yyyyMMdd-HHmmss}", overwrite: true); } catch { }
            return new T();
        }
    }

    public static void Save<T>(string path, T value)
    {
        var json = JsonSerializer.Serialize(value, Options);
        lock (WriteGate)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(path)) File.Replace(tmp, path, null, ignoreMetadataErrors: true);
            else File.Move(tmp, path);
        }
    }
}
