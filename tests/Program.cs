using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;
using TurboToggle;

static class RegressionTests
{
    const BindingFlags Instance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8;

    [DllImport("kernel32.dll")]
    static extern uint GetOEMCP();

    [STAThread]
    static void Main()
    {
        var assembly = typeof(HotkeyEntry).Assembly;
        TestModifierSequences();
        TestModifierGenericMirror();
        TestShutdown(assembly);
        TestPendingSave(assembly);
        TestCopilotRepeat(assembly);
        TestResolveTarget(assembly);
        TestAutostartCommandMatch();
        TestAutostartTaskArguments();
        TestUntrustedWriteDacl();
        TestLocalizationCompleteness(assembly);
        TestConfigJsonRoundTrip();
        TestCommitTmp();
        TestKeyName();
        TestIsSupportedCombo();
        TestTruncateTooltip();
        TestSelectValues();
        TestParseConfig();
        TestTryParseTag();
    }

    static void Check(bool condition, [CallerArgumentExpression(nameof(condition))] string? what = null)
    {
        if (!condition)
            throw new InvalidOperationException($"Assertion failed: {what}");
    }

    static void TestKeyName()
    {
        foreach (uint vk in new uint[] { 0x30, 0x39, 0x41, 0x5A })
        {
            string expected = ((char)vk).ToString();
            Check(Hotkeys.KeyName(vk) == expected, $"KeyName(0x{vk:X})");
        }
        Check(Hotkeys.KeyName(0x70) == "F1" && Hotkeys.KeyName(0x87) == "F24");
        Check(Hotkeys.KeyName(0x60) == "Num0" && Hotkeys.KeyName(0x69) == "Num9");
        Check(Hotkeys.ModsToString(0) == "");
        Check(Hotkeys.ModsToString(ModControl | ModAlt) == "Ctrl+Alt");
        Console.WriteLine("PASS: precomputed hotkey name tables");
    }

    static void TestIsSupportedCombo()
    {
        const uint CtrlAlt = ModControl | ModAlt;
        Check(Hotkeys.IsSupportedCombo(CtrlAlt, 0x42));
        Check(Hotkeys.IsSupportedCombo(Hotkeys.ModAll, 0x42));
        Check(Hotkeys.IsSupportedCombo(0, 0x70));
        Check(Hotkeys.IsSupportedCombo(0, 0x87));
        Check(Hotkeys.IsCopilot(ModShift | ModWin, 0x86));
        Check(Hotkeys.IsCopilot(0, 0x86), "bare Copilot key must use the low-level hook, not RegisterHotKey");
        foreach (uint lockKey in new uint[] { 0x13, 0x14, 0x90, 0x91 })
            Check(Hotkeys.IsSupportedCombo(CtrlAlt, lockKey), $"IsSupportedCombo(Ctrl+Alt+0x{lockKey:X})");
        Check(!Hotkeys.IsSupportedCombo(CtrlAlt, 0x5D));
        Check(!Hotkeys.IsSupportedCombo(CtrlAlt, 0xDF));
        Check(!Hotkeys.IsSupportedCombo(CtrlAlt, 0));
        Check(!Hotkeys.IsSupportedCombo(0, 0x42));
        Check(!Hotkeys.IsSupportedCombo(0, 0x14));
        Check(!Hotkeys.IsSupportedCombo(ModWin, 0x42));
        Check(!Hotkeys.IsSupportedCombo(0x40, 0x42));
        Console.WriteLine("PASS: registrable-combination predicate (lock keys allowed by design)");
    }

    static void TestTruncateTooltip()
    {
        Check(TrayContext.TooltipMaxLength == 127);
        Check(TrayContext.TruncateTooltip("Turbo Boost [Ctrl+Alt+B]").Length
            <= TrayContext.TooltipMaxLength);
        Check(TrayContext.TruncateTooltip("short") == "short");

        string truncated = TrayContext.TruncateTooltip(new string('x', 500));
        Check(truncated.Length == TrayContext.TooltipMaxLength, $"truncated length {truncated.Length}");
        Check(truncated[^1] == '\u2026', "truncated tooltip must end with an ellipsis");

        string rocket = char.ConvertFromUtf32(0x1F680);
        string cutEmoji = TrayContext.TruncateTooltip(string.Concat(Enumerable.Repeat(rocket, 80)));
        for (int i = 0; i < cutEmoji.Length; i++)
        {
            if (char.IsLowSurrogate(cutEmoji[i]))
                Check(i > 0 && char.IsHighSurrogate(cutEmoji[i - 1]), $"orphan low surrogate at {i}");
        }
        Check(!char.IsHighSurrogate(cutEmoji[^1]), "must not end with a lone high surrogate");
        Console.WriteLine("PASS: tray tooltip truncation (NotifyIcon cap, no split surrogate)");
    }

    static void TestSelectValues()
    {
        var scheme = Guid.Parse("11111111-2222-3333-4444-555555555555");
        var other = Guid.Parse("66666666-7777-8888-9999-aaaaaaaaaaaa");

        var memory = new Dictionary<(Guid, bool), uint> { [(scheme, true)] = 4, [(other, true)] = 2 };
        var order = new Queue<(Guid, bool)>();
        var picked = PowerApi.SelectValues(memory, order, scheme, PowerApi.ValueOn, 3, null);
        Check(picked.Ac == 3 && picked.Dc == PowerApi.ValueOn, $"fresh read wins: {picked}");

        memory = new Dictionary<(Guid, bool), uint> { [(scheme, true)] = 2, [(scheme, false)] = 4 };
        order = new Queue<(Guid, bool)>();
        var off = PowerApi.SelectValues(memory, order, scheme, PowerApi.ValueOff, 2, 0);
        Check(off.Ac == 0 && off.Dc == 0, $"disable writes Off: {off}");
        var back = PowerApi.SelectValues(memory, order, scheme, PowerApi.ValueOn, 0, 0);
        Check(back.Ac == 2 && back.Dc == 4, $"AC and DC restore independently: {back}");

        memory = new Dictionary<(Guid, bool), uint> { [(scheme, true)] = 2 };
        order = new Queue<(Guid, bool)>();
        picked = PowerApi.SelectValues(memory, order, scheme, PowerApi.ValueOn, 0, null);
        Check(picked.Ac == 2 && picked.Dc == PowerApi.ValueOn, $"zero read ignored: {picked}");

        memory = new Dictionary<(Guid, bool), uint>();
        order = new Queue<(Guid, bool)>();
        picked = PowerApi.SelectValues(memory, order, scheme, PowerApi.ValueOn, null, null);
        Check(picked.Ac == PowerApi.ValueOn && picked.Dc == PowerApi.ValueOn, $"fallback: {picked}");

        memory = new Dictionary<(Guid, bool), uint>();
        order = new Queue<(Guid, bool)>();
        for (int i = 0; i < 200; i++)
        {
            var s = new Guid(i, 0, 0, new byte[8]);
            PowerApi.SelectValues(memory, order, s, PowerApi.ValueOn, (uint)(i % 5) + 1, 4);
        }
        Check(memory.Count <= 64, $"memory table bounded, got {memory.Count}");
        Check(order.Count == memory.Count, "eviction order stays in sync with the table");
        Console.WriteLine("PASS: boost value selection (per-source restore, fresh read wins, bounded memory)");
    }

    static void TestParseConfig()
    {
        var cfg = ConfigStore.ParseConfig("""{"lang":"ru","restoreOnExit":true,"notifications":0}""")!;
        Check(cfg.Lang == "ru" && cfg.RestoreOnExit && cfg.Notifications == false);
        var cfg2 = ConfigStore.ParseConfig("""{"Lang":"uk","Notifications":"true","HotkeyEnabled":"false"}""")!;
        Check(cfg2.Lang == "uk" && cfg2.Notifications == true && cfg2.HotkeyEnabled == false);

        var bare = ConfigStore.ParseConfig("{}")!;
        Check(bare.Notifications is null && bare.AutoUpdateCheck is null && bare.HotkeyEnabled is null);

        var asText = ConfigStore.ParseConfig("""{"Hotkey":{"Mods":"3","Vk":"66"}}""")!;
        Check(asText.Hotkey is { Mods: 3, Vk: 0x42 }, "string-encoded hotkey");

        var refused = ConfigStore.ParseConfig("""{"Hotkey":{"Mods":2,"Vk":93}}""")!;
        Check(refused.Hotkey is null, "Apps combo must be rejected by validation");
        var winB = ConfigStore.ParseConfig("""{"Hotkey":{"Mods":8,"Vk":66}}""")!;
        Check(winB.Hotkey is null, "Win+B must be rejected by validation");
        var lockKey = ConfigStore.ParseConfig("""{"Hotkey":{"Mods":3,"Vk":144}}""")!;
        Check(lockKey.Hotkey is { Mods: 3, Vk: 144 }, "Ctrl+Alt+NumLock must be preserved");

        Check(ConfigStore.ParseConfig("not json") is null);
        Check(ConfigStore.ParseConfig("[]") is null);
        Check(ConfigStore.ParseConfig("") is null);
        Console.WriteLine("PASS: forgiving config parser (casing, value shapes, reserved combos, garbage)");
    }

    static void TestTryParseTag()
    {
        static void Parts(string? tag, int maj, int min, int build, int rev)
        {
            Check(Updater.TryParseTag(tag, out var v), $"must accept \"{tag}\"");
            Check(v.Major == maj && v.Minor == min && v.Build == build && v.Revision == rev,
                $"\"{tag}\" -> {v.Major}.{v.Minor}.{v.Build}.{v.Revision}");
        }

        Parts("v1.2.3", 1, 2, 3, 0);
        Parts("V1.2", 1, 2, 0, 0);
        Parts(" 1.0 ", 1, 0, 0, 0);
        Parts("2", 2, 0, 0, 0);
        Parts("v1.2.3-beta.1", 1, 2, 3, 0);
        Parts("1.2.3+45", 1, 2, 3, 0);
        Parts("1.2.3.4", 1, 2, 3, 4);

        foreach (string? bad in new string?[] { null, "", "   ", "v", "abc", "1.x", "1.2.3.4.5", "-1.0", "v1..2" })
        {
            Check(!Updater.TryParseTag(bad, out var g), $"must reject \"{bad}\" (got {g})");
        }
        Console.WriteLine("PASS: release tag parsing (prefixes, suffixes, rejects)");
    }

    static void TestConfigJsonRoundTrip()
    {
        var cfg = new AppConfig
        {
            Lang = "ru",
            RestoreOnExit = true,
            Notifications = false,
            Hotkey = new HotkeyEntry(Hotkeys.ModControl | Hotkeys.ModAlt, 0x42),
        };
        var json = JsonSerializer.Serialize(cfg, ConfigJsonContext.Default.AppConfig);
        Check(!json.Contains('\n', StringComparison.Ordinal), "source-gen JSON must be compact");
        var back = JsonSerializer.Deserialize(json, ConfigJsonContext.Default.AppConfig)!;
        Check(back.Lang == "ru" && back.RestoreOnExit && back.Notifications == false
            && back.Hotkey is { Vk: 0x42, Mods: Hotkeys.ModControl | Hotkeys.ModAlt },
            "config JSON round-trip mismatch");
        Console.WriteLine("PASS: source-generated config JSON round-trip (in-memory, no disk writes)");
    }

    static void TestCommitTmp()
    {
        string dir = Path.Combine(Path.GetTempPath(), "TurboToggleTests-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string dest = Path.Combine(dir, "config.json");
            File.WriteAllText(dest, "old");

            string tmp = dest + "." + Path.GetRandomFileName() + ".tmp";
            File.WriteAllText(tmp, "new");
            ConfigStore.CommitTmp(tmp, dest, "new");
            Check(File.ReadAllText(dest) == "new" && !File.Exists(tmp), "atomic rename must consume the tmp");

            string tmp2 = dest + "." + Path.GetRandomFileName() + ".tmp";
            File.WriteAllText(tmp2, "newer");
            using (new FileStream(dest, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                bool threw = false;
                try { ConfigStore.CommitTmp(tmp2, dest, "newer"); }
                catch (IOException) { threw = true; }
                catch (UnauthorizedAccessException) { threw = true; }
                Check(threw, "a locked destination must make both the rename and the fallback write fail");
                Check(File.Exists(tmp2),
                    "when both fail, the tmp is the only complete copy and must survive for the orphan scan in TryLoadFile");
            }
            Check(File.ReadAllText(dest) == "new", "the locked destination must be left untouched");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
        Console.WriteLine("PASS: config commit keeps the only good copy when the fallback write fails (scratch dir)");
    }

    static void TestAutostartCommandMatch()
    {
        const string exe = @"C:\Apps\TurboToggle.exe";
        static string Xml(string cmd) =>
            $"<Task><Actions><Exec><Command>{cmd}</Command></Exec></Actions></Task>";

        Check(Autostart.CommandMatches(Xml(exe), exe), "exact command must match");
        Check(Autostart.CommandMatches(Xml(exe), exe.ToUpperInvariant()), "match must be case-insensitive");
        Check(Autostart.CommandMatches(Xml("\"" + exe + "\""), exe), "quoted command must match");
        Check(!Autostart.CommandMatches(Xml(@"C:\Other\TurboToggle.exe"), exe), "different path must not match");
        Check(!Autostart.CommandMatches("<Task/>", exe), "missing command must not match");
        Check(Autostart.CommandMatches(Xml(@"C:\a&amp;b\TurboToggle.exe"), @"C:\a&b\TurboToggle.exe"),
            "XML entities in the command must be decoded");

        Check(Path.IsPathRooted(Autostart.SchtasksPath)
            && Autostart.SchtasksPath.EndsWith("schtasks.exe", StringComparison.OrdinalIgnoreCase)
            && File.Exists(Autostart.SchtasksPath),
            "schtasks must resolve to the absolute System32 executable");

        var enc = Autostart.SchtasksOutputEncoding;
        Check(ReferenceEquals(enc, Autostart.SchtasksOutputEncoding), "encoding must be cached, not rebuilt per call");
        Check(enc.CodePage == GetOEMCP(), $"expected the OEM code page, got {enc.CodePage}");
        foreach (byte b in new byte[] { 0x80, 0x9F, 0xA0, 0xAF })
        {
            Check(enc.GetBytes(enc.GetString(new[] { b })).SequenceEqual(new[] { b }), $"byte 0x{b:X2} must round-trip");
        }
        Console.WriteLine("PASS: autostart task command path matching (no system changes)");
    }

    static void TestAutostartTaskArguments()
    {
        const string exe = @"C:\Program Files\TurboToggle\TurboToggle.exe";
        var args = Autostart.CreateTaskArguments(exe);
        Check(args.Length == 10, $"argument count {args.Length}");
        int slashTr = Array.IndexOf(args, "/tr");
        Check(slashTr > 0 && slashTr + 1 < args.Length, "/tr must be present");
        Check(args[slashTr + 1] == exe, $"path must be passed raw, got \"{args[slashTr + 1]}\"");
        Check(args.All(a => !a.Contains('"', StringComparison.Ordinal)), "no argument may carry manual quotes");
        Check(args[1] == "/tn" && args[^1] == "/f" && args[3] == "/tr", "schtasks argument order unchanged");
        Console.WriteLine("PASS: schtasks argument list passes the exe path raw (ArgumentList quotes it)");
    }

    static void TestUntrustedWriteDacl()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var self = identity.User!;

        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
        var auth = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var world = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var interactive = new SecurityIdentifier(WellKnownSidType.InteractiveSid, null);

        static byte[] BuildSd(SecurityIdentifier owner, params GenericAce[] aces)
        {
            var dacl = new RawAcl(0, Math.Max(aces.Length, 1));
            foreach (var ace in aces)
                dacl.InsertAce(0, ace);
            var sd = new RawSecurityDescriptor(ControlFlags.DiscretionaryAclPresent, owner, owner, null, dacl);
            byte[] form = new byte[sd.BinaryLength];
            sd.GetBinaryForm(form, 0);
            return form;
        }
        static CommonAce Allow(SecurityIdentifier sid, FileSystemRights rights, AceFlags flags = AceFlags.None) =>
            new(flags, AceQualifier.AccessAllowed, (int)rights, sid, false, null);
        static CommonAce Deny(SecurityIdentifier sid, FileSystemRights rights) =>
            new(AceFlags.None, AceQualifier.AccessDenied, (int)rights, sid, false, null);
        bool Grants(SecurityIdentifier sid, FileSystemRights rights, AceFlags flags, FileSystemRights probe) =>
            Autostart.DaclGrantsUntrustedWrite(BuildSd(self, Allow(sid, rights, flags)), self, probe);

        var leaf = Autostart.LeafWriteRights;
        var ancestor = Autostart.AncestorWriteRights;

        Check(Grants(users, leaf, AceFlags.None, leaf), "BUILTIN\\Users with write rights");
        Check(Grants(auth, leaf, AceFlags.None, leaf), "Authenticated Users with write rights");
        Check(Grants(world, leaf, AceFlags.None, leaf), "Everyone with write rights");
        Check(Grants(interactive, leaf, AceFlags.None, leaf), "Interactive with write rights");
        Check(!Grants(admins, leaf, AceFlags.None, leaf), "BUILTIN\\Administrators with write rights");
        Check(!Grants(admins, leaf, AceFlags.None, ancestor), "Administrators on an ancestor");
        var readable = FileSystemRights.ReadData | FileSystemRights.ReadAttributes | FileSystemRights.ReadPermissions;
        Check(!Grants(users, readable, AceFlags.None, leaf), "BUILTIN\\Users read-only");
        Check(!Autostart.DaclGrantsUntrustedWrite(BuildSd(self, Deny(users, leaf)), self, leaf),
            "a deny ACE must not be treated as an allowance");
        const AceFlags inheritOnly = AceFlags.ContainerInherit | AceFlags.ObjectInherit | AceFlags.InheritOnly;
        Check(!Grants(users, leaf, inheritOnly, leaf), "inherit-only write must not count for the leaf");
        Check(!Grants(users, leaf, inheritOnly, ancestor), "inherit-only write must not count for an ancestor");
        Check(!Grants(auth, FileSystemRights.AppendData, AceFlags.None, ancestor),
            "append-only must not make an ancestor untrusted");
        Check(Grants(auth, FileSystemRights.AppendData, AceFlags.None, leaf),
            "append-only still counts for the leaf directory itself");
        Check(Grants(users, Autostart.DeleteChild, AceFlags.None, ancestor), "FILE_DELETE_CHILD on an ancestor");
        Check(Grants(users, FileSystemRights.Delete, AceFlags.None, ancestor), "DELETE on an ancestor");
        Console.WriteLine("PASS: untrusted-identity DACL audit (inherit-only, deny, append-only, delete-child)");
    }

    static void TestLocalizationCompleteness(Assembly assembly)
    {
        var strings = (IReadOnlyDictionary<string, string[]>)assembly
            .GetType("TurboToggle.Localization")!
            .GetField("Strings", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;

        Check(Localization.AllLangs.Count > 0, "at least one language must exist");
        foreach (var (key, values) in strings)
        {
            Check(key.Length > 0, "empty localization key");
            Check(values.Length == Localization.AllLangs.Count,
                $"\"{key}\" has {values.Length} translations, expected {Localization.AllLangs.Count}");
            foreach (string value in values)
                Check(!string.IsNullOrWhiteSpace(value), $"\"{key}\" has an empty translation");
            foreach (string lang in Localization.AllLangs)
            {
                Localization.Language = lang;
                Check(Localization.Tr(key) != key || values[0] == key, $"Tr(\"{key}\") fell back to the key");
            }
        }
        Localization.Language = "en";

        string? sources = FindSources();
        if (sources is null)
        {
            Console.WriteLine("SKIP: source tree not found - localization key scan skipped");
            Console.WriteLine("PASS: localization table shape (key set, one entry per language, no blanks)");
            return;
        }
        var known = strings.Keys.ToHashSet(StringComparer.Ordinal);
        var missing = new SortedSet<string>(StringComparer.Ordinal);
        int calls = 0;
        foreach (string file in Directory.EnumerateFiles(sources, "*.cs", SearchOption.AllDirectories))
        {
            if (UnderBuildOutput(sources, file))
                continue;
            string text = File.ReadAllText(file);
            foreach (string key in LocalizedKeys(text))
            {
                calls++;
                if (!known.Contains(key))
                    missing.Add(Path.GetFileName(file) + ": " + key);
            }
        }
        Check(calls > 0, "the scan must actually find Localization.Tr calls");
        Check(missing.Count == 0,
            "unknown localization keys:\n  " + string.Join("\n  ", missing));
        Console.WriteLine($"PASS: {calls} Localization.Tr call sites resolve ({strings.Count} keys x {Localization.AllLangs.Count} languages)");
    }

    static IEnumerable<string> LocalizedKeys(string source)
    {
        const string marker = "Localization.Tr(";
        int at = 0;
        while ((at = source.IndexOf(marker, at, StringComparison.Ordinal)) >= 0)
        {
            int start = at + marker.Length;
            int depth = 1, i = start;
            bool inString = false;
            while (i < source.Length && depth > 0)
            {
                char c = source[i];
                if (inString)
                {
                    if (c == '\\') i++;
                    else if (c == '"') inString = false;
                }
                else if (c == '"') inString = true;
                else if (c == '(') depth++;
                else if (c == ')') depth--;
                i++;
            }
            string call = source[start..Math.Min(i, source.Length)];
            foreach (Match m in Regex.Matches(call, "\"((?:[^\"\\\\]|\\\\.)*)\""))
                yield return m.Groups[1].Value;
            at = Math.Max(i, at + marker.Length);
        }
    }

    static string? FindSources()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int up = 0; dir is not null && up < 10; up++, dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "Localization.cs")))
                return Path.Combine(dir.FullName, "src");
        }
        return null;
    }

    static bool UnderBuildOutput(string root, string file)
    {
        foreach (string segment in Path.GetRelativePath(root, file)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            if (segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
                || segment.Equals("obj", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    static void TestResolveTarget(Assembly assembly)
    {
        var type = assembly.GetType("TurboToggle.TrayContext")!;
        var method = type.GetMethod("ResolveTarget", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool? Call(bool? requested, bool? current) => (bool?)method.Invoke(null, [requested, current]);
        Check(Call(true, null) == true && Call(false, true) == false, "explicit request must win");
        Check(Call(null, true) == false && Call(null, false) == true, "toggle must invert current state");
        Check(Call(null, null) is null, "unknown state must block toggling");
        Console.WriteLine("PASS: toggle target resolution (explicit/unknown/invert)");
    }

    static void TestModifierSequences()
    {
        var state = new ModifierState();
        void Key(uint vk, bool down, uint expected)
        {
            Check(state.Update(vk, down), $"VK {vk:X} must be recognized as a modifier");
            Check(state.Mods == expected, $"modifier {vk:X}: expected {expected}, got {state.Mods}");
        }
        Key(0xA2, true, ModControl);
        Key(0xA3, true, ModControl);
        Key(0xA2, false, ModControl);
        Key(0xA4, true, ModControl | ModAlt);
        Key(0xA0, true, ModControl | ModAlt | ModShift);
        Key(0xA3, false, ModAlt | ModShift);
        Key(0xA4, false, ModShift);
        Key(0xA0, false, 0);
        Key(0x5B, true, ModWin);
        Key(0x5C, true, ModWin);
        Key(0x5B, false, ModWin);
        Key(0x5C, false, 0);

        Check(state.Update(0xA0, true) && state.Update(0xA0, true), "repeat accepted");
        Check(state.Mods == ModShift, $"after two Shift downs: {state.Mods:X}");
        state.Update(0xA0, false);
        Check(state.Mods == 0, $"one key-up must clear even after auto-repeat: {state.Mods:X}");
        state.Update(0xA0, false);
        state.Update(0xA0, false);
        Check(state.Mods == 0, $"stray key-ups must stay clear: {state.Mods:X}");
        Check(!state.Update(0x42, true), "non-modifier key must be rejected");
        Check(ModifierState.Bit(0x5D) == 0, "Apps is not a modifier");
        Check(ModifierState.Bit(0xA1) == ModShift, "RShift maps to Shift");
        Console.WriteLine("PASS: modifier sequences, repeats, and both Ctrl/Win keys");
    }

    static void TestModifierGenericMirror()
    {
        var state = new ModifierState();
        Check(state.Update(0x10, true), "generic Shift down");
        Check(state.Update(0xA0, true), "left Shift down");
        Check(state.Mods == ModShift, $"both Shift slots: {state.Mods:X}");
        Check(state.Update(0xA0, false), "left Shift up");
        Check(state.Mods == 0, $"generic must clear with the specific: {state.Mods:X}");
        Check(state.Update(0x11, true), "generic Ctrl down");
        Check(state.Update(0xA2, true), "left Ctrl down");
        Check(state.Update(0xA3, true), "right Ctrl down");
        Check(state.Update(0xA2, false), "left Ctrl up");
        Check(state.Mods == ModControl, $"sibling keeps Ctrl and generic: {state.Mods:X}");
        Check(state.Update(0xA3, false), "right Ctrl up");
        Check(state.Mods == 0, $"generic must clear with the last specific: {state.Mods:X}");
        Console.WriteLine("PASS: generic modifier slots mirror specific keys");
    }

    static void TestShutdown(Assembly assembly)
    {
        var type = assembly.GetType("TurboToggle.TrayContext")!;
        var context = RuntimeHelpers.GetUninitializedObject(type);
        foreach (bool exiting in new[] { false, true })
        foreach (bool disposed in new[] { false, true })
        {
            type.GetField("_exiting", Instance)!.SetValue(context, exiting);
            type.GetField("_disposed", Instance)!.SetValue(context, disposed);
            bool actual = (bool)type.GetProperty("IsShuttingDown", Instance)!.GetValue(context)!;
            Check(actual == (exiting || disposed), $"exiting={exiting} disposed={disposed}");
        }
        Console.WriteLine("PASS: all four shutdown flag combinations");
    }

    static void TestPendingSave(Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var type = assembly.GetType("TurboToggle.ConfigStore")!;
        var flush = type.GetMethod("FlushPending", flags, [typeof(Func<AppConfig, bool>)])!;
        var pendingField = type.GetField("PendingConfig", flags)!;
        var timerField = type.GetField("_saveTimer", flags)!;
        timerField.SetValue(null, new System.Threading.Timer(
            _ => throw new InvalidOperationException("debounce must not fire during the test"),
            null, Timeout.Infinite, Timeout.Infinite));
        void Arm(AppConfig cfg) => pendingField.SetValue(null, cfg);

        var first = new AppConfig { Lang = "uk", Notifications = false };
        Arm(first);
        int firstSaves = 0;
        flush.Invoke(null, [(Func<AppConfig, bool>)(saved =>
        {
            Check(ReferenceEquals(first, saved), "wrong pending configuration");
            firstSaves++;
            return true;
        })]);
        flush.Invoke(null, [(Func<AppConfig, bool>)(_ => { firstSaves++; return true; })]);
        Check(firstSaves == 1, $"flush must save once, got {firstSaves}");
        Check(timerField.GetValue(null) is null, "flush must dispose the timer");

        var second = new AppConfig { Lang = "ru" };
        Arm(second);
        flush.Invoke(null, [(Func<AppConfig, bool>)(_ => false)]);
        var pending = (AppConfig?)pendingField.GetValue(null);
        Check(pending is not null && ReferenceEquals(second, pending),
            "flush must keep the pending config when the save fails");

        int retrySaves = 0;
        flush.Invoke(null, [(Func<AppConfig, bool>)(saved =>
        {
            Check(ReferenceEquals(second, saved), "wrong retry configuration");
            retrySaves++;
            return true;
        })]);
        Check(retrySaves == 1, $"retry flush must save once, got {retrySaves}");
        Check(pendingField.GetValue(null) is null, "retry flush must clear the pending config");
        Console.WriteLine("PASS: immediate exit flushes once (in-memory sink, no user config writes)");
        Console.WriteLine("PASS: failed save keeps pending config for retry");
    }

    static void TestCopilotRepeat(Assembly assembly)
    {
        var type = assembly.GetType("TurboToggle.CopilotHook")!;
        using var hook = (IDisposable)Activator.CreateInstance(type, nonPublic: true)!;
        int presses = 0;
        type.GetEvent("Pressed")!.AddEventHandler(hook, (Action)(() => presses++));
        var callback = type.GetMethod("HookProc", Instance)!;
        var memory = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(memory, 0x86);
            void Send(int message) => callback.Invoke(hook, [0, (IntPtr)message, memory]);
            Send(0x100);
            type.GetField("_copilotEnteredAt", Instance)!.SetValue(hook, Environment.TickCount64 - 5000);
            Send(0x100);
            Send(0x100);
            Check(presses == 1, $"held Copilot repeated: {presses} presses");
            Send(0x101);
            Send(0x100);
            Send(0x101);
            Check(presses == 2, $"Copilot did not rearm on key-up: {presses} presses");
        }
        finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(memory); }
        Console.WriteLine("PASS: Copilot autorepeat suppressed until key-up (hook not installed)");
    }
}
