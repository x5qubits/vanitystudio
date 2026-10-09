using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VanityStudio.Llm;

namespace VanityStudio.Infra;

/// <summary>Where the agent keeps its own state: <c>%USERPROFILE%\.vanity-studio</c> (or <c>$HOME/.vanity-studio</c>),
/// overridable with the <c>VANITY_STUDIO_HOME</c> environment variable. <c>config.json</c> holds the AI profiles
/// (keys, OAuth tokens, models); <c>projects/&lt;slug&gt;</c> holds per-workspace memory and usage; <c>logs</c> the
/// diagnostics and per-call prompt log; <c>scratch</c> oversized tool outputs.</summary>
public static class AgentConfig
{
    public static string Dir
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("VANITY_STUDIO_HOME");
            if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".vanity-studio");
        }
    }

    public static string ConfigFile  => Path.Combine(Dir, "config.json");
    public static string LogsDir     => Path.Combine(Dir, "logs");
    public static string ScratchRoot => Path.Combine(Dir, VanityPathHelper.ScratchDir);
    public static string ProjectsDir => Path.Combine(Dir, "projects");

    /// <summary>The per-workspace state folder: a readable name plus a short hash of the full path.</summary>
    public static string ProjectDir(string workspace)
    {
        var full = Path.GetFullPath(workspace).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(full);
        if (string.IsNullOrEmpty(name)) name = "root";
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray());
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..8].ToLowerInvariant();
        var dir = Path.Combine(ProjectsDir, safe + "-" + hash);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void EnsureDirs()
    {
        Directory.CreateDirectory(Dir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(ScratchRoot);
        Directory.CreateDirectory(ProjectsDir);
        Directory.CreateDirectory(Path.Combine(Dir, "personas"));
        Directory.CreateDirectory(Path.Combine(Dir, "skills"));
        PruneOld(Path.Combine(LogsDir, "_shared", "prompts"), TimeSpan.FromDays(3), "*.jsonl");
        PruneOld(ScratchRoot, TimeSpan.FromDays(2), "*.txt", recursive: true);
    }

    private static void PruneOld(string dir, TimeSpan age, string pattern, bool recursive = false)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var f in Directory.EnumerateFiles(dir, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly))
                try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow - age) File.Delete(f); } catch { }
        }
        catch { }
    }

    // ── profiles ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The profiles on disk; an empty catalog when there is no config yet.</summary>
    public static AiOptions Load()
    {
        lock (Gate)
        {
            if (!File.Exists(ConfigFile)) return new AiOptions();
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(ConfigFile));
                var root = doc.RootElement;
                var opts = new AiOptions { DecisionMode = root.TryGetProperty("DecisionMode", out var dm) ? dm.GetString() ?? "llm" : "llm" };
                if (root.TryGetProperty("Profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
                    foreach (var p in profiles.EnumerateArray())
                        opts.Profiles.Add(AiOptions.ParseProfile(p));
                var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                if (root.TryGetProperty("Settings", out var st) && st.ValueKind == JsonValueKind.Object)
                    foreach (var kv in st.EnumerateObject())
                        if (kv.Value.ValueKind == JsonValueKind.String) settings[kv.Name] = kv.Value.GetString() ?? "";
                _settings = settings;
                return opts;
            }
            catch (Exception ex)
            {
                Log.Warn($"[config] {ConfigFile} could not be read: {ex.Message}");
                return new AiOptions();
            }
        }
    }

    public static void Save(AiOptions options)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Dir);
            var tmp = ConfigFile + ".tmp";
            var shape = new ConfigFileShape { Profiles = options.Profiles, Settings = _settings.Count > 0 ? new Dictionary<string, string>(_settings) : null };
            File.WriteAllText(tmp, JsonSerializer.Serialize(shape, Json), new UTF8Encoding(false));
            File.Move(tmp, ConfigFile, overwrite: true);
            try { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(ConfigFile, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { }
        }
    }

    private sealed class ConfigFileShape
    {
        public string DecisionMode { get; set; } = "llm";
        public List<AiProfile> Profiles { get; set; } = [];
        public Dictionary<string, string>? Settings { get; set; }
    }

    // ── settings: small named values that belong to this machine, never to a repository ───────────────────────────

    private static Dictionary<string, string> _settings = new(StringComparer.OrdinalIgnoreCase);
    private static bool _settingsLoaded;

    /// <summary>A value from the "Settings" object of config.json (e.g. GoogleClientSecret), or null.</summary>
    public static string? Setting(string key)
    {
        lock (Gate)
        {
            if (!_settingsLoaded) { Load(); _settingsLoaded = true; }
            return _settings.TryGetValue(key, out var v) && v.Length > 0 ? v : null;
        }
    }

    /// <summary>Store a value in config.json's "Settings" object (an empty value removes it).</summary>
    public static void SetSetting(string key, string? value)
    {
        var opts = Load();
        lock (Gate)
        {
            if (string.IsNullOrEmpty(value)) _settings.Remove(key); else _settings[key] = value;
            _settingsLoaded = true;
        }
        Save(opts);
    }

    public static IReadOnlyDictionary<string, string> Settings { get { lock (Gate) { if (!_settingsLoaded) { Load(); _settingsLoaded = true; } return new Dictionary<string, string>(_settings); } } }

    /// <summary>Add or replace a profile by name. A new profile goes first, so it becomes the one the router asks
    /// first; an existing one keeps its place.</summary>
    public static AiOptions Upsert(AiProfile profile, bool makeDefault = true)
    {
        var opts = Load();
        var idx = opts.Profiles.FindIndex(p => p.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0) opts.Profiles[idx] = profile; else opts.Profiles.Insert(0, profile);
        if (makeDefault) MoveFirst(opts, profile.Name);
        Save(opts);
        return opts;
    }

    public static AiOptions Remove(string name)
    {
        var opts = Load();
        opts.Profiles.RemoveAll(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Save(opts);
        return opts;
    }

    public static void MoveFirst(AiOptions opts, string name)
    {
        var idx = opts.Profiles.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (idx <= 0) return;
        var p = opts.Profiles[idx];
        opts.Profiles.RemoveAt(idx);
        opts.Profiles.Insert(0, p);
    }

    /// <summary>Write refreshed OAuth tokens back to the profile row, so a renewed token survives a restart.
    /// Wired into <see cref="OAuthTokenRefresher.Persist"/> at startup.</summary>
    public static void PersistOAuth(AiProfile fresh)
    {
        try
        {
            var opts = Load();
            var p = opts.Profiles.FirstOrDefault(x => x.Name.Equals(fresh.Name, StringComparison.OrdinalIgnoreCase));
            if (p is null) return;
            if (!string.IsNullOrEmpty(fresh.OAuthAccessToken))  p.OAuthAccessToken  = fresh.OAuthAccessToken;
            if (!string.IsNullOrEmpty(fresh.OAuthRefreshToken)) p.OAuthRefreshToken = fresh.OAuthRefreshToken;
            if (fresh.OAuthExpiresAt > 0)                       p.OAuthExpiresAt    = fresh.OAuthExpiresAt;
            if (!string.IsNullOrEmpty(fresh.OAuthAccountId))    p.OAuthAccountId    = fresh.OAuthAccountId;
            Save(opts);
            Log.Info($"[oauth-persist] updated '{fresh.Name}'");
        }
        catch (Exception ex) { Log.Warn($"[oauth-persist] failed: {ex.Message}"); }
    }

    /// <summary>The latest stored tokens for a profile, for the refresher's adopt-before-refresh step.</summary>
    public static AiProfile? ReloadProfile(string name)
    {
        try { return Load().Profiles.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); }
        catch { return null; }
    }
}
