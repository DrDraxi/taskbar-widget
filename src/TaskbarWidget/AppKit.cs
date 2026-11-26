using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace TaskbarWidget;

/// <summary>Small helpers every widget app needs.</summary>
public static class AppKit
{
    /// <summary>%LOCALAPPDATA%\&lt;appName&gt;, created if missing.</summary>
    public static string DataDir(string appName)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), appName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Adds/removes the app from HKCU\...\Run, always refreshing the path (the exe may have moved).</summary>
    public static void SetStartWithWindows(string appName, bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
            if (key == null) return;
            if (enabled && Environment.ProcessPath is { } exe)
            {
                var value = $"\"{exe}\"";
                if (key.GetValue(appName) as string != value) key.SetValue(appName, value);
            }
            else if (!enabled && key.GetValue(appName) != null)
                key.DeleteValue(appName, throwOnMissingValue: false);
        }
        catch (Exception ex) { Log.Write($"Startup registration failed: {ex.Message}"); }
    }

    /// <summary>Opens a file or URL with its default handler.</summary>
    public static void Open(string pathOrUrl) => Native.ShellExecuteW(IntPtr.Zero, "open", pathOrUrl, null, null, 1);

    /// <summary>Looks up a variable from a .env file next to the exe (or the working directory), then the environment.</summary>
    public static string? Env(string key)
    {
        foreach (var dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var path = Path.Combine(dir, ".env");
            if (!File.Exists(path)) continue;
            try
            {
                foreach (var raw in File.ReadLines(path))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0 || line[..eq].Trim() != key) continue;
                    var v = line[(eq + 1)..].Trim();
                    if (v.Length >= 2 && (v[0] == '"' || v[0] == '\'') && v[^1] == v[0]) v = v[1..^1];
                    return v;
                }
            }
            catch { }
        }
        return Environment.GetEnvironmentVariable(key);
    }

    /// <summary>Writes a file atomically (temp file + rename) so a crash never leaves it half-written.</summary>
    public static void WriteAllTextAtomic(string path, string contents)
    {
        var tmp = $"{path}.{Environment.ProcessId}.tmp";
        File.WriteAllText(tmp, contents, new UTF8Encoding(false));
        File.Move(tmp, path, overwrite: true);
    }
}

/// <summary>
/// A JSON config file read with case-insensitive keys. Unknown keys are preserved when saving,
/// so hand-edited configs never lose settings.
/// </summary>
public sealed class JsonConfig
{
    readonly string _path;
    JsonObject _root = new();

    public JsonConfig(string path) { _path = path; Reload(); }

    public string Path => _path;
    public bool Exists => File.Exists(_path);
    public string? LoadError { get; private set; }

    public void Reload()
    {
        LoadError = null;
        try
        {
            if (!File.Exists(_path)) { _root = new JsonObject(); return; }
            var opts = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            _root = JsonNode.Parse(File.ReadAllText(_path), documentOptions: opts) as JsonObject ?? new JsonObject();
        }
        catch (Exception ex)
        {
            LoadError = $"config.json is invalid: {ex.Message}";
            Log.Write(LoadError);
        }
    }

    public JsonObject Root => _root;

    public JsonNode? this[string key] => Find(_root, key);

    public static JsonNode? Find(JsonObject? obj, string key)
    {
        if (obj == null) return null;
        if (obj.TryGetPropertyValue(key, out var exact)) return exact;
        foreach (var (k, v) in obj)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return v;
        return null;
    }

    public static string? Str(JsonNode? n) =>
        n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

    public static int? Int(JsonNode? n) =>
        n is JsonValue v ? (v.TryGetValue<int>(out var i) ? i : v.TryGetValue<string>(out var s) && int.TryParse(s, out i) ? i : null) : null;

    public static bool? Bool(JsonNode? n) => n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;

    public string? GetString(string key) => Str(this[key]);
    public int GetInt(string key, int fallback) => Int(this[key]) ?? fallback;
    public bool GetBool(string key, bool fallback) => Bool(this[key]) ?? fallback;

    /// <summary>Sets a value, reusing the existing key's casing if present.</summary>
    public void Set(string key, JsonNode? value)
    {
        foreach (var (k, _) in _root)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) { key = k; break; }
        _root[key] = value;
    }

    public void Save()
    {
        if (LoadError != null) return; // never overwrite a file we couldn't parse
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                _root.WriteTo(w);
            AppKit.WriteAllTextAtomic(_path, Encoding.UTF8.GetString(ms.ToArray()));
        }
        catch (Exception ex) { Log.Write($"Saving {_path} failed: {ex.Message}"); }
    }
}
