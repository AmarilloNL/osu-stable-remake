using System.Globalization;
using Raylib_cs;

namespace StableRemake;

// Reads stable's own osu!.cfg / osu!.<user>.cfg, plus remake.cfg (ours, next to the exe) on top.
static class Config
{
    // The osu!stable install: OsuDir in remake.cfg, else stable's default install folder, else F:\osu!.
    public static string OsuDir = @"F:\osu!";
    static readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string> own = new(StringComparer.OrdinalIgnoreCase);
    static string OwnPath => Path.Combine(AppContext.BaseDirectory, "remake.cfg");

    public static void Load()
    {
        Read(OwnPath, own);
        string defaultDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "osu!");
        OsuDir = own.TryGetValue("OsuDir", out var dir) ? dir : File.Exists(Path.Combine(defaultDir, "osu!.exe")) ? defaultDir : @"F:\osu!";
        Read(Path.Combine(OsuDir, "osu!.cfg"), values);
        Read(Path.Combine(OsuDir, $"osu!.{Environment.UserName}.cfg"), values);
        foreach (var kv in own) values[kv.Key] = kv.Value;
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

    public static float MusicVolume => GetInt("VolumeUniversal", 100) / 100f * GetInt("VolumeMusic", 100) / 100f;

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

    public static KeyboardKey Key(string key, KeyboardKey def)
    {
        string v = Get(key, "");
        if (v.Length == 1 && char.IsAsciiLetterOrDigit(v[0])) return (KeyboardKey)char.ToUpperInvariant(v[0]);
        return Enum.TryParse<KeyboardKey>(v, true, out var k) ? k : def;
    }
}
