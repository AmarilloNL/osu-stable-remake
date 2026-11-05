using Raylib_cs;

namespace OsuStableRemake;

// Same bit values as stable.
[Flags]
enum Mods
{
    None = 0,
    NoFail = 1,
    Easy = 2,
    Hidden = 8,
    HardRock = 16,
    SuddenDeath = 32,
    DoubleTime = 64,
    Relax = 128,
    HalfTime = 256,
    Nightcore = 512, // always set together with DoubleTime
    Flashlight = 1024,
    Autoplay = 2048,
    SpunOut = 4096,
    Autopilot = 8192,
    Perfect = 16384, // always set together with SuddenDeath
    Key4 = 1 << 15, Key5 = 1 << 16, Key6 = 1 << 17, Key7 = 1 << 18, Key8 = 1 << 19, // osu!mania
    FadeIn = 1 << 20, Random = 1 << 21,
    Cinema = 1 << 22, // always set together with Autoplay
    Key9 = 1 << 24, KeyCoop = 1 << 25, Key1 = 1 << 26, Key3 = 1 << 27, Key2 = 1 << 28,
    ScoreV2 = 1 << 29,
    Mirror = 1 << 30,
}

static class ModInfo
{
    public const Mods Supported = (Mods)0x7FFF | Mods.ScoreV2 | ManiaOnly; // NF..PF, Score V2 and mania's; other stable mods (TD, key coop...) are dropped
    public const Mods KeyMods = Mods.Key1 | Mods.Key2 | Mods.Key3 | Mods.Key4 | Mods.Key5 | Mods.Key6 | Mods.Key7 | Mods.Key8 | Mods.Key9;
    public const Mods ManiaOnly = KeyMods | Mods.FadeIn | Mods.Random | Mods.Mirror;
    // stable's key mod button: 4K .. 9K, then 1K .. 3K
    static readonly Mods[] KeyCycle = [Mods.Key4, Mods.Key5, Mods.Key6, Mods.Key7, Mods.Key8, Mods.Key9, Mods.Key1, Mods.Key2, Mods.Key3];
    public static int Keys(this Mods m) { int i = Array.FindIndex(KeyCycle, k => (m & k) != 0); return i < 0 ? 0 : i < 6 ? i + 4 : i - 5; }
    static Mods CycleKeys(Mods m, int dir)
    {
        int i = Array.FindIndex(KeyCycle, k => (m & k) != 0), next = i < 0 ? (dir > 0 ? 0 : KeyCycle.Length - 1) : i + dir;
        m &= ~KeyMods;
        return next >= 0 && next < KeyCycle.Length ? m | KeyCycle[next] : m;
    }
    // the mods a mode offers (stable's mod select): mania's own, Autopilot and Spun Out only in osu!
    public static Mods ForMode(Mods m, int mode) => mode == 3 ? m & ~(Mods.Relax | Mods.Autopilot | Mods.SpunOut) : (m & ~ManiaOnly) & (mode == 0 ? ~Mods.None : ~(Mods.Autopilot | Mods.SpunOut));

    public static double Rate(this Mods m) => m.HasFlag(Mods.DoubleTime) ? 1.5 : m.HasFlag(Mods.HalfTime) ? 0.75 : 1;

    public static double ScoreMultiplier(this Mods m)
    {
        // stable's order (it matters for the last bit of the double); Relax and Autopilot score nothing
        double x = 1;
        if (m.HasFlag(Mods.NoFail)) x *= 0.5;
        if (m.HasFlag(Mods.Easy)) x *= 0.5;
        if (m.HasFlag(Mods.HalfTime)) x *= 0.3;
        if (m.HasFlag(Mods.Hidden) || m.HasFlag(Mods.FadeIn)) x *= 1.06;
        if (m.HasFlag(Mods.HardRock)) x *= 1.06;
        if (m.HasFlag(Mods.DoubleTime)) x *= 1.12;
        if (m.HasFlag(Mods.Flashlight)) x *= 1.12;
        if (m.HasFlag(Mods.SpunOut)) x *= 0.9;
        if (m.HasFlag(Mods.Relax) || m.HasFlag(Mods.Autopilot)) x *= 0.0;
        return x;
    }

    // Score V2's: HardRock 1.1x and DoubleTime 1.2x, NoFail 1x, the rest as above
    public static double ScoreMultiplierV2(this Mods m)
    {
        double x = 1;
        if (m.HasFlag(Mods.HardRock)) x *= 1.1;
        if (m.HasFlag(Mods.DoubleTime)) x *= 1.2;
        return x * (m & ~(Mods.HardRock | Mods.DoubleTime | Mods.NoFail)).ScoreMultiplier();
    }

    // the multiplier the selected mods play with
    public static double Multiplier(this Mods m) => m.HasFlag(Mods.ScoreV2) ? m.ScoreMultiplierV2() : m.ScoreMultiplier();

    public static string Short(this Mods m)
    {
        var parts = new List<string>();
        void Add(Mods f, string s) { if (m.HasFlag(f)) parts.Add(s); }
        // stable's order (its score lists): ScoreV2, Auto, SO, EZ, NF, HD, NC/DT, HT, HR, RX/AP, PF/SD, FL
        Add(Mods.ScoreV2, "ScoreV2"); if (m.HasFlag(Mods.Cinema)) parts.Add("Cinema"); else Add(Mods.Autoplay, "Auto"); Add(Mods.SpunOut, "SO");
        Add(Mods.Easy, "EZ"); Add(Mods.NoFail, "NF"); Add(Mods.Hidden, "HD");
        if (m.HasFlag(Mods.Nightcore)) parts.Add("NC"); else Add(Mods.DoubleTime, "DT");
        Add(Mods.HalfTime, "HT"); Add(Mods.HardRock, "HR");
        if (m.HasFlag(Mods.Relax)) parts.Add("RX"); else Add(Mods.Autopilot, "AP");
        if (m.HasFlag(Mods.Perfect)) parts.Add("PF"); else Add(Mods.SuddenDeath, "SD");
        Add(Mods.Flashlight, "FL");
        Add(Mods.FadeIn, "FI"); Add(Mods.Random, "RD"); Add(Mods.Mirror, "MR");
        if (m.Keys() is > 0 and var k) parts.Add($"{k}K");
        return parts.Count == 0 ? "No mods" : string.Join(" ", parts);
    }

    // stable's mod-select hotkeys. S, D and V cycle SD -> PF -> off, DT -> NC -> off and Auto -> Cinema -> off.
    public static Mods Toggle(Mods m, KeyboardKey key) => key switch
    {
        KeyboardKey.Q => Flip(m, Mods.Easy, Mods.HardRock),
        KeyboardKey.W => Flip(m, Mods.NoFail, Mods.SuddenDeath | Mods.Perfect),
        KeyboardKey.E => Flip(m, Mods.HalfTime, Mods.DoubleTime | Mods.Nightcore),
        KeyboardKey.A => Flip(m, Mods.HardRock, Mods.Easy),
        KeyboardKey.S => m.HasFlag(Mods.Perfect) ? m & ~(Mods.SuddenDeath | Mods.Perfect)
                   : m.HasFlag(Mods.SuddenDeath) ? m | Mods.Perfect
                   : Flip(m, Mods.SuddenDeath, Mods.NoFail),
        KeyboardKey.D => m.HasFlag(Mods.Nightcore) ? m & ~(Mods.DoubleTime | Mods.Nightcore)
                   : m.HasFlag(Mods.DoubleTime) ? m | Mods.Nightcore
                   : Flip(m, Mods.DoubleTime, Mods.HalfTime),
        // mania: Hidden's button goes Fade In -> Hidden -> off, Relax's cycles the key mods, Autopilot's is Random
        KeyboardKey.F when Mania => m.HasFlag(Mods.FadeIn) ? m & ~Mods.FadeIn | Mods.Hidden : m.HasFlag(Mods.Hidden) ? m & ~Mods.Hidden : m | Mods.FadeIn,
        KeyboardKey.Z when Mania => CycleKeys(m, 1),
        KeyboardKey.X when Mania => Flip(m, Mods.Random, Mods.None),
        KeyboardKey.Null => Mania ? Flip(m, Mods.Mirror, Mods.None) : m, // (Mirror has no key)
        KeyboardKey.X or KeyboardKey.C when SongSelect.PlayMode != 0 => m, // (taiko and catch have no Autopilot or Spun Out)
        KeyboardKey.F => Flip(m, Mods.Hidden, Mods.None),
        KeyboardKey.G => Flip(m, Mods.Flashlight, Mods.None),
        KeyboardKey.Z => Flip(m, Mods.Relax, Mods.Autopilot | Mods.Autoplay),
        KeyboardKey.X => Flip(m, Mods.Autopilot, Mods.Relax | Mods.Autoplay | Mods.SpunOut),
        KeyboardKey.C => Flip(m, Mods.SpunOut, Mods.Autopilot | Mods.Autoplay),
        KeyboardKey.V => m.HasFlag(Mods.Cinema) ? m & ~(Mods.Autoplay | Mods.Cinema)
                   : m.HasFlag(Mods.Autoplay) ? m | Mods.Cinema
                   : Flip(m, Mods.Autoplay, Mods.Relax | Mods.Autopilot),
        KeyboardKey.B => Flip(m, Mods.ScoreV2, Mods.None),
        _ => m,
    };

    static bool Mania => SongSelect.PlayMode == 3;

    // stable's Shift+S / D / V: straight to the second state (PF, NC, Cinema), or off from it; other keys as without Shift
    // (mania's Shift+F: Hidden)
    public static Mods ShiftToggle(Mods m, KeyboardKey key) => key switch
    {
        KeyboardKey.F when Mania => m.HasFlag(Mods.Hidden) ? m & ~Mods.Hidden : m & ~Mods.FadeIn | Mods.Hidden,
        KeyboardKey.S => m.HasFlag(Mods.Perfect) ? m & ~(Mods.SuddenDeath | Mods.Perfect) : m & ~Mods.NoFail | Mods.SuddenDeath | Mods.Perfect,
        KeyboardKey.D => m.HasFlag(Mods.Nightcore) ? m & ~(Mods.DoubleTime | Mods.Nightcore) : m & ~Mods.HalfTime | Mods.DoubleTime | Mods.Nightcore,
        KeyboardKey.V => m.HasFlag(Mods.Cinema) ? m & ~(Mods.Autoplay | Mods.Cinema) : m & ~(Mods.Relax | Mods.Autopilot) | Mods.Autoplay | Mods.Cinema,
        _ => Toggle(m, key),
    };

    // stable's right-click on a mod button: the cycle backwards (off -> PF -> SD -> off and the like)
    public static Mods ToggleBack(Mods m, KeyboardKey key) => key switch
    {
        KeyboardKey.S => m.HasFlag(Mods.Perfect) ? m & ~Mods.Perfect : m.HasFlag(Mods.SuddenDeath) ? m & ~Mods.SuddenDeath : ShiftToggle(m, key),
        KeyboardKey.D => m.HasFlag(Mods.Nightcore) ? m & ~Mods.Nightcore : m.HasFlag(Mods.DoubleTime) ? m & ~Mods.DoubleTime : ShiftToggle(m, key),
        KeyboardKey.V => m.HasFlag(Mods.Cinema) ? m & ~Mods.Cinema : m.HasFlag(Mods.Autoplay) ? m & ~Mods.Autoplay : ShiftToggle(m, key),
        KeyboardKey.F when Mania => m.HasFlag(Mods.Hidden) ? m & ~Mods.Hidden | Mods.FadeIn : m.HasFlag(Mods.FadeIn) ? m & ~Mods.FadeIn : m | Mods.Hidden,
        KeyboardKey.Z when Mania => CycleKeys(m, -1),
        _ => Toggle(m, key),
    };

    // "HDHR", "HD DT", "NC" ... (used by the --mods test flag)
    public static Mods Parse(string s)
    {
        var m = Mods.None;
        s = s.Replace(" ", "").ToUpperInvariant();
        for (int i = 0; i + 1 < s.Length; i += 2)
            m |= s.Substring(i, 2) switch
            {
                "EZ" => Mods.Easy, "NF" => Mods.NoFail, "HT" => Mods.HalfTime, "HR" => Mods.HardRock,
                "SD" => Mods.SuddenDeath, "PF" => Mods.SuddenDeath | Mods.Perfect, "DT" => Mods.DoubleTime,
                "NC" => Mods.DoubleTime | Mods.Nightcore, "HD" => Mods.Hidden, "FL" => Mods.Flashlight,
                "RX" => Mods.Relax, "AP" => Mods.Autopilot, "SO" => Mods.SpunOut, "AT" => Mods.Autoplay, "CN" => Mods.Autoplay | Mods.Cinema, "V2" => Mods.ScoreV2,
                "FI" => Mods.FadeIn, "RD" => Mods.Random, "MR" => Mods.Mirror, "1K" => Mods.Key1, "2K" => Mods.Key2, "3K" => Mods.Key3, "4K" => Mods.Key4,
                "5K" => Mods.Key5, "6K" => Mods.Key6, "7K" => Mods.Key7, "8K" => Mods.Key8, "9K" => Mods.Key9,
                _ => Mods.None,
            };
        return m;
    }

    static Mods Flip(Mods m, Mods f, Mods incompatible) => m.HasFlag(f) ? m & ~f : (m & ~incompatible) | f;
}

