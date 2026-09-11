using System;
using System.IO;
using System.Text.Json;
using System.Threading;

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

    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    static AppConfig? TryLoad(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
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
            && uint.TryParse(v.GetString()?.Trim(), out var s))
            return s;
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
        if (hk.Mods > 15 || hk.Vk == 0 || hk.Vk > 0xDE
            || (hk.Mods == 0 && (hk.Vk < 0x70 || hk.Vk > 0x87))
            || (hk.Mods == Hotkeys.ModWin && hk.Vk == 0x42))
            cfg.Hotkey = null;
    }

    public static AppConfig Load()
    {
        CleanupStaleTmps();
        var cfg = TryLoadFile(FilePath, recoverTmp: true);
        if (cfg is not null)
            return cfg;

        try
        {
            var legacy = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (!string.Equals(legacy, FilePath, StringComparison.OrdinalIgnoreCase)
                && File.Exists(legacy))
            {
                var migrated = TryLoadFile(legacy, recoverTmp: false);
                if (migrated is not null)
                {
                    Save(migrated);
                    try { File.Delete(legacy); } catch { }
                    return migrated;
                }
            }
        }
        catch { }

        return new AppConfig();
    }

    static AppConfig? TryLoadFile(string path, bool recoverTmp)
    {
        if (File.Exists(path))
        {
            try
            {
                var cfg = TryLoad(path);
                if (cfg is not null)
                    return cfg;
            }
            catch (Exception ex)
            {
                Program.LogError(ex);
            }
        }
        if (!recoverTmp)
            return null;

        var tmp = path + ".tmp";
        if (File.Exists(tmp))
        {
            try
            {
                var cfg = TryLoad(tmp);
                if (cfg is not null)
                {
                    File.Move(tmp, path, overwrite: true);
                    return cfg;
                }
            }
            catch { }
            DeleteIfStale(tmp);
        }
        foreach (var orphan in NewestFirst(path))
        {
            try
            {
                var cfg = TryLoad(orphan);
                if (cfg is not null)
                {
                    File.Move(orphan, path, overwrite: true);
                    return cfg;
                }
            }
            catch { }
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
                return Array.Empty<string>();
            var files = Directory.GetFiles(dir, name + ".*.tmp");
            Array.Sort(files, (a, b) => File.GetLastWriteTimeUtc(b).CompareTo(File.GetLastWriteTimeUtc(a)));
            return files;
        }
        catch
        {
            return Array.Empty<string>();
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
                    if ((DateTime.UtcNow - File.GetLastWriteTimeUtc(f)).TotalDays > 1)
                        File.Delete(f);
                }
                catch { }
            }
        }
        catch { }
    }

    public static void Save(AppConfig cfg)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var json = JsonSerializer.Serialize(cfg, Options);
            var tmp = FilePath + "." + Path.GetRandomFileName() + ".tmp";
            File.WriteAllText(tmp, json);
            const int SaveRetryCount = 3;
            const int SaveRetryDelayMs = 25;
            for (int attempt = 0; attempt < SaveRetryCount; attempt++)
            {
                try
                {
                    File.Move(tmp, FilePath, overwrite: true);
                    return;
                }
                catch (IOException) when (attempt < SaveRetryCount - 1)
                {
                    Thread.Sleep(SaveRetryDelayMs * (attempt + 1));
                }
                catch (IOException)
                {
                    File.WriteAllText(FilePath, json);
                    try { File.Delete(tmp); } catch { }
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            Program.LogError(ex);
        }
    }
}
