using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
namespace TurboToggle;
public sealed class HotkeyEntry
{
    public uint Mods { get; set; } = Hotkeys.ModControl | Hotkeys.ModAlt;
    public uint Vk { get; set; } = 0x42;
    public HotkeyEntry() { }
    public HotkeyEntry(uint mods, uint vk)
    {
        Mods = mods;
        Vk = vk;
    }
}
public sealed class AppConfig
{
    public string Lang { get; set; } = "";
    public HotkeyEntry? Hotkey { get; set; }
    public bool RestoreOnExit { get; set; }
    public bool? Notifications { get; set; }
    public bool? AutoUpdateCheck { get; set; }
    public bool? HotkeyEnabled { get; set; }
}
static class ConfigStore
{
    static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TurboToggle");
    static readonly string FilePath = Path.Combine(Dir, "config.json");
    static readonly object _saveLock = new();
    static System.Threading.Timer? _saveTimer;
    static long _pendingSinceTicks;
    const int DebounceMs = 500;
    const int MinSaveDelayMs = 50;
    static void SaveDebounced()
    {
        lock (_saveLock)
        {
            long now = Environment.TickCount64;
            if (_pendingSinceTicks == 0)
                _pendingSinceTicks = now;
            long delay = Math.Clamp(DebounceMs - (now - _pendingSinceTicks), MinSaveDelayMs, DebounceMs);
            _saveTimer ??= new System.Threading.Timer(_ => SaveTick(), null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change((int)delay, Timeout.Infinite);
        }
    }
    static void SaveTick()
    {
        lock (_saveLock)
        {
            if (PendingConfig is { } cfg && Save(cfg))
                PendingConfig = null;
            _pendingSinceTicks = 0;
        }
    }
    static AppConfig? PendingConfig;
    public static void RequestSave(AppConfig cfg)
    {
        lock (_saveLock)
        {
            PendingConfig = Snapshot(cfg);
        }
        SaveDebounced();
    }
    internal static AppConfig Snapshot(AppConfig cfg) => new()
    {
        Lang = cfg.Lang,
        Hotkey = cfg.Hotkey is { } hk ? new HotkeyEntry(hk.Mods, hk.Vk) : null,
        RestoreOnExit = cfg.RestoreOnExit,
        Notifications = cfg.Notifications,
        AutoUpdateCheck = cfg.AutoUpdateCheck,
        HotkeyEnabled = cfg.HotkeyEnabled,
    };
    public static void FlushPending() => FlushPending(Save);
    internal static void FlushPending(Func<AppConfig, bool> save)
    {
        System.Threading.Timer? t;
        lock (_saveLock)
        {
            t = _saveTimer;
            _saveTimer = null;
            if (PendingConfig is { } cfg && save(cfg))
                PendingConfig = null;
            _pendingSinceTicks = 0;
        }
        if (t is not null)
        {
            try { t.Dispose(); } catch (Exception ex) { Program.LogError(ex); }
        }
    }
    static AppConfig? TryLoad(string path, out bool corrupt)
    {
        corrupt = false;
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return null;
        }
        var cfg = ParseConfig(text);
        corrupt = cfg is null;
        return cfg;
    }
    static void PreserveCorruptConfig(string path)
    {
        try
        {
            if (!string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase))
                return;
            string stamped = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            if (File.Exists(stamped))
                stamped += "-" + Path.GetRandomFileName();
            File.Move(path, stamped, overwrite: false);
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }
    internal static AppConfig? ParseConfig(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            var cfg = new AppConfig();
            var root = doc.RootElement;
            if (root.TryGetProperty("Lang", out var lang) || root.TryGetProperty("lang", out lang))
                cfg.Lang = lang.ValueKind == JsonValueKind.String ? (lang.GetString() ?? "") : "";
            if ((root.TryGetProperty("Hotkey", out var hk) || root.TryGetProperty("hotkey", out hk))
                && hk.ValueKind == JsonValueKind.Object)
            {
                uint mods = ReadUint(hk, "Mods", "mods", uint.MaxValue);
                uint vk = ReadUint(hk, "Vk", "vk", 0);
                if (mods != uint.MaxValue && vk != 0)
                    cfg.Hotkey = new HotkeyEntry(mods, vk);
            }
            cfg.RestoreOnExit = ReadBool(root, "RestoreOnExit", "restoreOnExit", false);
            cfg.Notifications = ReadBoolNullable(root, "Notifications", "notifications");
            cfg.AutoUpdateCheck = ReadBoolNullable(root, "AutoUpdateCheck", "autoUpdateCheck");
            cfg.HotkeyEnabled = ReadBoolNullable(root, "HotkeyEnabled", "hotkeyEnabled");
            Validate(cfg);
            return cfg;
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return null;
        }
    }
    static bool Prop(JsonElement obj, string a, string b, out JsonElement value)
    {
        return obj.TryGetProperty(a, out value) || obj.TryGetProperty(b, out value);
    }
    static uint ReadUint(JsonElement obj, string a, string b, uint fallback)
    {
        if (!Prop(obj, a, b, out var v))
            return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetUInt32(out var n))
            return n;
        if (v.ValueKind == JsonValueKind.String
            && uint.TryParse(v.GetString()?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            return value;
        return fallback;
    }
    static bool ReadBool(JsonElement obj, string a, string b, bool fallback)
    {
        return ReadBoolNullable(obj, a, b) ?? fallback;
    }
    static bool? ReadBoolNullable(JsonElement obj, string a, string b)
    {
        if (!Prop(obj, a, b, out var v))
            return null;
        if (v.ValueKind == JsonValueKind.True)
            return true;
        if (v.ValueKind == JsonValueKind.False)
            return false;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
            return n != 0;
        if (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString()?.Trim(), out var s))
            return s;
        return null;
    }
    static void Validate(AppConfig cfg)
    {
        if (cfg.Hotkey is not { } hk)
            return;
        if (!Hotkeys.IsSupportedCombo(hk.Mods, hk.Vk))
            cfg.Hotkey = null;
    }
    public static AppConfig Load()
    {
        var cfg = TryLoadFile(FilePath, recoverTmp: true);
        if (cfg is not null)
        {
            CleanupStaleTmps();
            return cfg;
        }
        try
        {
            var legacy = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (!string.Equals(legacy, FilePath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(legacy))
            {
                var migrated = TryLoadFile(legacy, recoverTmp: false);
                if (migrated is not null)
                {
                    if (Save(migrated))
                    {
                        try { File.Delete(legacy); } catch { }
                    }
                    return migrated;
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
        return new AppConfig();
    }
    static AppConfig? TryLoadFile(string path, bool recoverTmp)
    {
        if (File.Exists(path))
        {
            AppConfig? cfg = null;
            try
            {
                cfg = TryLoad(path, out bool corrupt);
                if (cfg is null && corrupt)
                    PreserveCorruptConfig(path);
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
            if (cfg is not null)
                return cfg;
        }
        if (!recoverTmp)
            return null;
        var tmp = path + ".tmp";
        if (File.Exists(tmp))
        {
            try
            {
                var cfg = TryLoad(tmp, out _);
                if (cfg is not null)
                {
                    try { File.Move(tmp, path, overwrite: true); }
                    catch (Exception moveEx) { Program.LogError(moveEx); }
                    return cfg;
                }
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
            DeleteIfStale(tmp);
        }
        foreach (var orphan in NewestFirst(path))
        {
            try
            {
                var cfg = TryLoad(orphan, out _);
                if (cfg is not null)
                {
                    try { File.Move(orphan, path, overwrite: true); }
                    catch (Exception moveEx) { Program.LogError(moveEx); }
                    return cfg;
                }
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
        }
        return null;
    }
    static void DeleteIfStale(string path)
    {
        try
        {
            if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(path)).TotalSeconds > 30)
                File.Delete(path);
        }
        catch { }
    }
    static string[] NewestFirst(string path)
    {
        try
        {
            string? dir = Path.GetDirectoryName(path);
            string name = Path.GetFileName(path);
            if (dir is null || !Directory.Exists(dir))
                return [];
            var files = Directory.GetFiles(dir, name + ".*.tmp");
            var stamped = new (string Path, DateTime Time)[files.Length];
            for (int i = 0; i < files.Length; i++)
            {
                DateTime t;
                try { t = File.GetLastWriteTimeUtc(files[i]); }
                catch { t = DateTime.MinValue; }
                stamped[i] = (files[i], t);
            }
            Array.Sort(stamped, (a, b) => b.Time.CompareTo(a.Time));
            for (int i = 0; i < files.Length; i++)
                files[i] = stamped[i].Path;
            return files;
        }
        catch
        {
            return [];
        }
    }
    static void CleanupStaleTmps()
    {
        try
        {
            if (!Directory.Exists(Dir))
                return;
            foreach (var f in Directory.GetFiles(Dir, "config.json.*.tmp"))
            {
                try
                {
                    if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalMinutes > 15)
                        File.Delete(f);
                }
                catch { }
            }
        }
        catch { }
    }
    static void WriteConfigFile(string path, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var stream = SafeFile.OpenVerified(path, truncateExisting: true, FileShare.None)
            ?? throw new IOException($"Cannot open for writing: {path}");
        using (stream)
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }
    }
    internal static void CommitTmp(string tmp, string dest, string json)
    {
        try
        {
            File.Move(tmp, dest, overwrite: true);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        bool committed = false;
        try
        {
            WriteConfigFile(dest, json);
            committed = true;
        }
        finally
        {
            if (committed)
            {
                try { File.Delete(tmp); } catch { }
            }
        }
    }
    public static bool Save(AppConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(cfg, ConfigJsonContext.Default.AppConfig);
            var tmp = FilePath + "." + Path.GetRandomFileName() + ".tmp";
            WriteConfigFile(tmp, json);
            CommitTmp(tmp, FilePath, json);
            return true;
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
            return false;
        }
    }
}
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(AppConfig))]
internal sealed partial class ConfigJsonContext : JsonSerializerContext
{
}
