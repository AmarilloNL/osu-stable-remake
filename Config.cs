using System.Globalization;
using Raylib_cs;

namespace OsuStableRemake;

// Reads stable's own osu!.cfg / osu!.<user>.cfg, plus remake.cfg (ours, next to the exe) on top.
static class Config
{
    // The osu!stable install: OsuDir in remake.cfg, else stable's default install folder, else F:\osu!.
    public static string OsuDir = @"F:\osu!";
    static readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> own = new(StringComparer.OrdinalIgnoreCase);
    static string OwnPath => Path.Combine(AppContext.BaseDirectory, "remake.cfg");
    // stable's songs folder: its BeatmapDirectory (relative to the install or a full path), Songs by default
    public static string SongsDir => Path.Combine(OsuDir, Get("BeatmapDirectory", "Songs"));

    public static void Load()
    {
        Read(OwnPath, own);
        // ours kept its universal offset as RemakeOffset, the opposite way round; it's stable's Offset now
        if (own.Remove("RemakeOffset", out var old) && !own.ContainsKey("Offset") && double.TryParse(old, System.Globalization.CultureInfo.InvariantCulture, out double ms))
            own["Offset"] = (0 - ms).ToString(System.Globalization.CultureInfo.InvariantCulture);
        string defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu!");
        OsuDir = own.TryGetValue("OsuDir", out var dir) ? dir : File.Exists(Path.Combine(defaultDir, "osu!.exe")) ? defaultDir : @"F:\osu!";
        Read(Path.Combine(OsuDir, "osu!.cfg"), values);
        Read(Path.Combine(OsuDir, $"osu!.{Environment.UserName}.cfg"), values);
        foreach (var kv in own) values[kv.Key] = kv.Value;
        Beatmap.PlayfieldHeight = GetBool("Fullscreen", true) ? GetInt("HeightFullscreen", GetInt("Height", 720)) : GetInt("Height", 720);
    }

    static void Read(string path, Dictionary<string, string> into)
    {
        if (!File.Exists(path)) return;
        foreach (var line in File.ReadLines(path))
        {
            int eq = line.IndexOf('=');
            if (eq <= 0 || line.StartsWith('#')) continue;
            into[line[..eq].Trim()] = line[(eq + 1)..].Trim();
        }
    }

    public static string Get(string key, string def) => values.TryGetValue(key, out var v) ? v : def;

    public static int GetInt(string key, int def) =>
        int.TryParse(Get(key, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;

    public static double GetDouble(string key, double def) =>
        double.TryParse(Get(key, ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;

    // stable's defaults: universal 100, music and effect 80
    public static float MusicVolume => GetInt("VolumeUniversal", 100) / 100f * GetInt("VolumeMusic", 80) / 100f;

    public static bool GetBool(string key, bool def) => Get(key, def ? "1" : "0") is "1" or "True" or "true";

    // For this run only, never saved (SetOwn writes just its own keys).
    public static void SetTemp(string key, string value) => values[key] = value;

    // Settings this client owns (offset etc.) go to remake.cfg so stable's config is never touched.
    public static void SetOwn(string key, string value)
    {
        own[key] = value;
        values[key] = value;
        File.WriteAllLines(OwnPath, own.Select(kv => $"{kv.Key} = {kv.Value}"));
    }

    public static KeyboardKey Key(string key, KeyboardKey def) => ParseKey(Get(key, "")) ?? def;

    // a key by the .NET name stable's cfg uses: D0-D9, NumPad0-9, the Oem keys ... (null: none or unknown)
    public static KeyboardKey? ParseKey(string v)
    {
        if (v.Length == 2 && v[0] == 'D' && char.IsDigit(v[1])) return (KeyboardKey)v[1];
        if (v.Length == 7 && v.StartsWith("NumPad") && char.IsDigit(v[6])) return KeyboardKey.Kp0 + (v[6] - '0');
        KeyboardKey? oem = v switch
        {
            "Back" => KeyboardKey.Backspace, "Multiply" => KeyboardKey.KpMultiply, "Add" => KeyboardKey.KpAdd,
            "Subtract" => KeyboardKey.KpSubtract, "Divide" => KeyboardKey.KpDivide, "Decimal" => KeyboardKey.KpDecimal,
            "OemTilde" => KeyboardKey.Grave, "OemMinus" => KeyboardKey.Minus, "OemPlus" => KeyboardKey.Equal,
            "OemOpenBrackets" => KeyboardKey.LeftBracket, "OemCloseBrackets" => KeyboardKey.RightBracket, "OemPipe" => KeyboardKey.Backslash,
            "OemSemicolon" => KeyboardKey.Semicolon, "OemQuotes" => KeyboardKey.Apostrophe, "OemComma" => KeyboardKey.Comma,
            "OemPeriod" => KeyboardKey.Period, "OemQuestion" => KeyboardKey.Slash, "LeftControl" => KeyboardKey.LeftControl,
            _ => null,
        };
        if (oem is { } o) return o;
        if (v.Length == 1 && char.IsAsciiLetterOrDigit(v[0])) return (KeyboardKey)char.ToUpperInvariant(v[0]);
        return Enum.TryParse<KeyboardKey>(v, true, out var k) && k != KeyboardKey.Null ? k : null;
    }
}
