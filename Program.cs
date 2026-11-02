using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using System.Runtime.InteropServices;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

abstract class Screen
{
    public abstract void Update(double dtMs);
    public abstract void Draw();
    public virtual void Entered() { } // each time it becomes Game.Screen
}

static class Game
{
    static Screen screen = null!;
    public static Screen Screen { get => screen; set { if (value == screen) return; Previous = screen; screen = value; value.Entered(); } }
    public static Screen? Previous; // the screen before the current one

    // stable's fading mode change: a black overlay (0-100) rises 8 a 60Hz tick, the screen switches at 100, then it
    // falls 4 a tick from 140 (so it holds black a moment). The old screen sits still meanwhile.
    public static bool Fading => fadeTo != null;

    // stable's Boss key (Insert): the game goes silent (master volume 0) and out of sight (stable hides it to a tray
    // icon; here it's minimised), pausing a play first. The volume comes back with the window.
    static string? bossVolume;
    static void UpdateBossKey(SongSelect songs)
    {
        if (bossVolume == null && Input.IsKeyPressed(Config.Key("keyBossKey", KeyboardKey.Insert)))
        {
            bossVolume = Config.Get("VolumeUniversal", "100");
            Config.SetTemp("VolumeUniversal", "0");
            (Screen as Player)?.BossPause();
            MinimizeWindow();
        }
        else if (bossVolume != null && !IsWindowMinimized() && IsWindowFocused())
        {
            Config.SetTemp("VolumeUniversal", bossVolume);
            bossVolume = null;
        }
        else return;
        songs.ApplyMusicVolume();
        (Screen as Player)?.ApplyMusicVolume();
    }
    static Func<Screen>? fadeTo;
    static Action? fadeSwitch;
    static double fadeLevel;
    public static void FadeTo(Screen next, Action? onSwitch = null) => FadeTo(() => next, onSwitch);
    // the screen is made at the switch, after onSwitch (a new play's clock starts there)
    // black: already black (stable's quick retry), switch on the next frame
    public static void FadeTo(Func<Screen> make, Action? onSwitch = null, bool black = false)
    {
        if (fadeTo != null) return;
        (fadeTo, fadeSwitch) = (make, onSwitch);
        if (black) fadeLevel = 100;
    }

    // false while fading out (the current screen gets no update)
    static bool UpdateFade(double dtMs)
    {
        double ticks = Math.Min(dtMs, 33) / (1000 / 60.0);
        if (fadeTo == null) { fadeLevel = Math.Max(0, fadeLevel - 4 * ticks); return true; }
        fadeLevel = Math.Min(100, fadeLevel + 8 * ticks);
        if (fadeLevel < 100) return false;
        var (make, onSwitch) = (fadeTo, fadeSwitch);
        (fadeTo, fadeSwitch, fadeLevel) = (null, null, 140);
        onSwitch?.Invoke();
        Screen = make();
        return true;
    }

    static void DrawFade()
    {
        if (fadeLevel > 0) DrawRectangle(0, 0, GetScreenWidth(), GetScreenHeight(), ColorAlpha(Color.Black, (float)Math.Min(100, fadeLevel) / 100));
    }
    public static Skin Skin = null!;
    public static Font Font, Bold, Light, Mono, Icons; // Icons: osu!ui's FontAwesome (options sidebar etc.)
    public static bool Quit;
    public static readonly Stopwatch Clock = Stopwatch.StartNew();

    public static double NowMs => Clock.Elapsed.TotalMilliseconds;

    // stable's text renderer: Aller Light by default but regular Aller below size 20 in its 640x480 units (not pixels:
    // at 1080p a size-16 line is still regular), and old-style digits (Aller's private-use glyphs at digit + 63500) in
    // every Aller weight.
    static Font Ui(float size) => size / (GetScreenHeight() / 480f) < 20 ? Font : Light;

    // Cached: UI text is mostly the same strings every frame (main thread only).
    static readonly Dictionary<string, string> oldStyle = [];

    static string OldStyle(string s)
    {
        if (oldStyle.TryGetValue(s, out var cached)) return cached;
        bool digits = false;
        foreach (char ch in s) if (char.IsAsciiDigit(ch)) { digits = true; break; }
        string result = !digits ? s : string.Create(s.Length, s, (span, src) =>
        {
            for (int i = 0; i < src.Length; i++) span[i] = char.IsAsciiDigit(src[i]) ? (char)(src[i] + 63500) : src[i];
        });
        if (oldStyle.Count > 4096) oldStyle.Clear(); // changing text (timers, scores) mustn't grow it forever
        return oldStyle[s] = result;
    }

    public static void Text(string s, float x, float y, float size, Color c, float spacing = 0) =>
        Draw(Ui(size), OldStyle(s), x, y, size, spacing, c);

    public static float TextWidth(string s, float size) => Measure(Ui(size), OldStyle(s), size, 0);

    // Regular Aller at any size (stable picks the weight by its nominal size, not pixels).
    public static void TextRegular(string s, float x, float y, float size, Color c) =>
        Draw(Font, OldStyle(s), x, y, size, 0, c);

    public static float RegularWidth(string s, float size) => Measure(Font, OldStyle(s), size, 0);

    public static void TextBold(string s, float x, float y, float size, Color c) =>
        Draw(Bold, OldStyle(s), x, y, size, 0, c);

    public static float BoldWidth(string s, float size) => Measure(Bold, OldStyle(s), size, 0);

    public static float LightWidth(string s, float size) => Measure(Light, OldStyle(s), size, 0);

    public static void TextLight(string s, float x, float y, float size, Color c) =>
        Draw(Light, OldStyle(s), x, y, size, 0, c);

    // ---- characters outside Aller (Japanese, Chinese, Korean, Cyrillic ...): stable's GDI+ text falls back to a system
    // font for them, MS UI Gothic here (Malgun Gothic for Hangul). Those glyphs go into extra atlases that are rebuilt
    // whenever a new character shows up; a string is drawn as runs of Aller and fallback text.
    static bool InUiFont(int cp) => cp is >= 32 and <= 0x24F or 0x2605 or 0x266A || cp >= '0' + 63500 && cp <= '9' + 63500;
    static bool Plain(string s) { foreach (char ch in s) if (!InUiFont(ch)) return false; return true; }
    static bool IsHangul(int cp) => cp is >= 0xAC00 and <= 0xD7AF or >= 0x1100 and <= 0x11FF or >= 0x3130 and <= 0x318F;

    sealed class FallbackFont(string path, int index)
    {
        public Font Font;
        public bool Loaded, Failed;
        readonly HashSet<int> codepoints = [];
        byte[]? ttf;
        public void Add(List<int> cps)
        {
            int before = codepoints.Count;
            codepoints.UnionWith(cps);
            if (codepoints.Count == before || Failed) return;
            try { ttf ??= path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase) ? FontCollection.Extract(File.ReadAllBytes(path), index) : File.ReadAllBytes(path); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Failed = true; return; }
            if (Loaded) UnloadFont(Font);
            var cp = codepoints.ToArray();
            Font = LoadFontFromMemory(".ttf", ttf, 48, cp, cp.Length);
            var tex = Font.Texture;
            GenTextureMipmaps(ref tex);
            Font.Texture = tex;
            SetTextureFilter(Font.Texture, TextureFilter.Trilinear);
            Loaded = true;
        }
    }
    static readonly FallbackFont Gothic = new(@"C:\Windows\Fonts\msgothic.ttc", 1), Hangul = new(@"C:\Windows\Fonts\malgun.ttf", 0);
    // vertical shift of the fallback glyphs against Aller's, in text sizes (measured on stable's song select)
    const float FallbackDy = 0.13f, FallbackScale = 0.85f;

    // Runs of one font each, cached per string (song select draws the same titles every frame).
    static readonly Dictionary<string, (string Text, int Font)[]> runs = [];
    static (string Text, int Font)[] Runs(string s)
    {
        if (runs.TryGetValue(s, out var r)) return r;
        var list = new List<(string, int)>();
        List<int> gothic = [], hangul = [];
        int start = 0, current = -1;
        for (int i = 0; i < s.Length; i += char.IsSurrogatePair(s, i) ? 2 : 1)
        {
            int cp = char.IsSurrogatePair(s, i) ? char.ConvertToUtf32(s, i) : s[i];
            int font = InUiFont(cp) ? 0 : IsHangul(cp) ? 2 : 1;
            if (font == 1) gothic.Add(cp); else if (font == 2) hangul.Add(cp);
            if (font != current) { if (current >= 0) list.Add((s[start..i], current)); start = i; current = font; }
        }
        if (current >= 0) list.Add((s[start..], current));
        if (gothic.Count > 0) Gothic.Add(gothic);
        if (hangul.Count > 0) Hangul.Add(hangul);
        if (runs.Count > 2048) runs.Clear();
        return runs[s] = [.. list];
    }

    public static void PreloadGlyphs(IEnumerable<string> texts)
    {
        var start = Stopwatch.GetTimestamp();
        List<int> gothic = [], hangul = [];
        foreach (var s in texts)
            for (int i = 0; i < s.Length; i += char.IsSurrogatePair(s, i) ? 2 : 1)
            {
                int cp = char.IsSurrogatePair(s, i) ? char.ConvertToUtf32(s, i) : s[i];
                if (!InUiFont(cp)) (IsHangul(cp) ? hangul : gothic).Add(cp);
            }
        if (gothic.Count > 0) Gothic.Add(gothic);
        if (hangul.Count > 0) Hangul.Add(hangul);
        if (gothic.Count + hangul.Count > 0)
            Console.Error.WriteLine($"fallback glyphs: {gothic.Distinct().Count()} + {hangul.Distinct().Count()} in {Stopwatch.GetElapsedTime(start).TotalMilliseconds:0}ms");
    }

    static Font FontFor(int i, Font ui) => i == 0 ? ui : i == 2 && Hangul.Loaded ? Hangul.Font : Gothic.Loaded ? Gothic.Font : ui;

    static void Draw(Font f, string s, float x, float y, float size, float spacing, Color c)
    {
        if (Plain(s)) { DrawTextEx(f, s, new Vector2(x, y), size, spacing, c); return; }
        foreach (var (text, i) in Runs(s))
        {
            var font = FontFor(i, f);
            float sz = i == 0 ? size : size * FallbackScale;
            DrawTextEx(font, text, new Vector2(x, y + (i == 0 ? 0 : FallbackDy * size)), sz, spacing, c);
            x += MeasureTextEx(font, text, sz, spacing).X + spacing;
        }
    }

    static float Measure(Font f, string s, float size, float spacing)
    {
        if (Plain(s)) return MeasureTextEx(f, s, size, spacing).X;
        float w = 0;
        foreach (var (text, i) in Runs(s)) w += MeasureTextEx(FontFor(i, f), text, i == 0 ? size : size * FallbackScale, spacing).X + spacing;
        return w - spacing;
    }

    // stable's UI font (Aller) from osu!ui.dll; a system font if that's unavailable.
    static Font LoadUiFont(string name, string fallback, int[] codepoints)
    {
        var f = DefaultSkin.Get(name) is { } ttf && ttf[0] == 0 ? LoadFontFromMemory(".ttf", ttf, 64, codepoints, codepoints.Length) : LoadFontEx(fallback, 64, codepoints, codepoints.Length);
        var tex = f.Texture; // mipmapped: small text downscaled from the 64px atlas stays thin, like stable's
        GenTextureMipmaps(ref tex);
        f.Texture = tex;
        SetTextureFilter(f.Texture, TextureFilter.Trilinear);
        return f;
    }

    // Window size: remake.cfg's Resolution ("WxH"), else stable's: WidthFullscreen/HeightFullscreen in fullscreen,
    // Width/Height otherwise (9999: the monitor's own; before the window exists that reads 0, which raylib takes as
    // the monitor's size too).
    public static (int W, int H) Resolution()
    {
        var r = Config.Get("Resolution", "").Split('x');
        if (r.Length == 2 && int.TryParse(r[0], out int w) && int.TryParse(r[1], out int h)) return (w, h);
        bool full = Config.Get("DisplayMode", Config.GetBool("Fullscreen", false) ? "Fullscreen" : "Windowed") == "Fullscreen";
        w = Config.GetInt(full ? "WidthFullscreen" : "Width", full ? 9999 : 1280);
        h = Config.GetInt(full ? "HeightFullscreen" : "Height", full ? 9999 : 720);
        int m = GetCurrentMonitor();
        return (w == 9999 ? GetMonitorWidth(m) : w, h == 9999 ? GetMonitorHeight(m) : h);
    }

    // Display mode: remake.cfg's DisplayMode, else stable's Fullscreen flag, else borderless when the window
    // would cover the monitor.
    public static string DisplayMode()
    {
        var (w, h) = Resolution();
        int m = GetCurrentMonitor();
        return Config.Get("DisplayMode", Config.GetBool("Fullscreen", false) ? "Fullscreen"
            : w >= GetMonitorWidth(m) && h >= GetMonitorHeight(m) ? "Borderless" : "Windowed");
    }

    // The Fullscreen setting (the options' toggle and Alt+Enter): fullscreen at the fullscreen resolution, else the
    // window, borderless when the windowed size covers the monitor.
    public static void SetFullscreen(bool on)
    {
        Config.SetOwn("DisplayMode", on ? "Fullscreen" : "Windowed");
        int m = GetCurrentMonitor();
        if (!on && Resolution() is var (w, h) && w >= GetMonitorWidth(m) && h >= GetMonitorHeight(m)) Config.SetOwn("DisplayMode", "Borderless");
        ApplyDisplay();
    }

    // Safe to call again at runtime (the options screen does).
    public static void ApplyDisplay()
    {
        var (w, h) = Resolution();
        int m = GetCurrentMonitor();
        string mode = DisplayMode();
        if (IsWindowFullscreen()) ToggleFullscreen();
        if (IsWindowState(ConfigFlags.BorderlessWindowMode)) ToggleBorderlessWindowed();
        if (mode == "Borderless") { ToggleBorderlessWindowed(); return; }
        SetWindowSize(w, h);
        if (mode == "Fullscreen") ToggleFullscreen();
        else SetWindowPosition((GetMonitorWidth(m) - w) / 2, (GetMonitorHeight(m) - h) / 2);
    }

    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
    [DllImport("user32.dll")] static extern bool ClipCursor(ref ClipRect r);
    [DllImport("user32.dll", EntryPoint = "ClipCursor")] static extern bool ClipCursorOff(IntPtr none);
    struct ClipRect { public int L, T, R, B; }
    static ClipRect? clipped;
    static double clippedAt;

    // The ConfineMouse option (Never / Fullscreen / Always): keeps the cursor inside the window while it has focus.
    static void ConfineCursor()
    {
        string mode = Config.Get("ConfineMouse", "Fullscreen");
        bool want = IsWindowFocused() && !IsWindowMinimized() && (mode == "Always" || mode == "Fullscreen" && DisplayMode() != "Windowed");
        ClipRect? r = null;
        if (want) { var p = GetWindowPosition(); r = new ClipRect { L = (int)p.X, T = (int)p.Y, R = (int)p.X + GetScreenWidth(), B = (int)p.Y + GetScreenHeight() }; }
        // Windows drops the clip on focus changes and the like, so it's re-applied every 250ms while wanted
        if (r.Equals(clipped) && (r == null || NowMs - clippedAt < 250)) return;
        clipped = r;
        clippedAt = NowMs;
        if (r is { } c) ClipCursor(ref c); else ClipCursorOff(IntPtr.Zero);
    }

    // The Sensitivity option (MouseSpeed), the way stable applies it without raw input: each frame the system cursor's
    // movement since the last frame is scaled and the cursor put where that lands. Outside the window it's left alone,
    // and the first frame back in starts from where it is. Like stable it reads where the cursor is now: the window
    // messages raylib goes by would lose a movement made just before the cursor is put back.
    // With Raw input on, stable scales the device's own (unaccelerated) counts instead. Here that's GLFW's raw motion,
    // which needs the cursor captured, so it's used while the cursor is confined; the game's cursor is kept here then.
    internal static Vector2? RawCursor;
    [DllImport("user32.dll")] static extern bool GetCursorPos(out CursorPoint p);
    struct CursorPoint { public int X, Y; }
    static Vector2 sensAt;
    static bool sensInside;
    static void ApplySensitivity()
    {
        float s = (float)Config.GetDouble("MouseSpeed", 1);
        string confine = Config.Get("ConfineMouse", "Fullscreen"); // (not focus: GLFW frees and recaptures the cursor itself)
        bool raw = Config.GetBool("RawInput", false) && (confine == "Always" || confine == "Fullscreen" && DisplayMode() != "Windowed");
        if (raw != RawCursor.HasValue)
        {
            if (raw) { GetCursorPos(out var c0); RawCursor = new Vector2(c0.X, c0.Y) - GetWindowPosition(); DisableCursor(); }
            else { EnableCursor(); HideCursor(); SetMousePosition((int)RawCursor!.Value.X, (int)RawCursor.Value.Y); RawCursor = null; }
        }
        if (RawCursor is { } at)
        {
            RawCursor = Vector2.Clamp(at + GetMouseDelta() * s, Vector2.Zero, new Vector2(GetScreenWidth() - 1, GetScreenHeight() - 1));
            return;
        }
        if (s == 1) { sensInside = false; return; }
        GetCursorPos(out var c);
        var p = new Vector2(c.X, c.Y) - GetWindowPosition();
        bool inside = IsWindowFocused() && p.X >= 0 && p.Y >= 0 && p.X < GetScreenWidth() && p.Y < GetScreenHeight();
        if (!inside || !sensInside) { sensAt = p; sensInside = inside; return; }
        sensAt += (p - new Vector2(MathF.Round(sensAt.X), MathF.Round(sensAt.Y))) * s;
        if (clipped != null) sensAt = Vector2.Clamp(sensAt, Vector2.Zero, new Vector2(GetScreenWidth() - 1, GetScreenHeight() - 1));
        var to = new Vector2(MathF.Round(sensAt.X), MathF.Round(sensAt.Y));
        if (to != p) SetMousePosition((int)to.X, (int)to.Y);
    }

    static void Main(string[] args)
    {
        // a normal start writes what it reports (load times, problems, a crash) to remake.log next to the exe;
        // test runs keep stderr
        if (args.Length == 0 || !args.Any(a => a.StartsWith("--")))
            try
            {
                var log = new StreamWriter(Path.Combine(AppContext.BaseDirectory, "remake.log"), append: false) { AutoFlush = true };
                Console.SetError(log);
                log.WriteLine($"osu!stable remake {typeof(Game).Assembly.GetName().Version?.ToString(3)} started {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                AppDomain.CurrentDomain.UnhandledException += (_, e) => log.WriteLine($"crash: {e.ExceptionObject}");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        if (args.Contains("--check")) { Check(); return; }
        if (args.Contains("--check-stars")) // test hook: our star ratings against the ones stable stored in osu!.db
        {
            Config.Load();
            Beatmap.PlayfieldHeight = 1080;
            var combos = new[] { Mods.None, Mods.HardRock, Mods.Easy, Mods.DoubleTime, Mods.HalfTime, Mods.HardRock | Mods.DoubleTime, Mods.Easy | Mods.DoubleTime, Mods.HardRock | Mods.HalfTime, Mods.Easy | Mods.HalfTime };
            int n = 0, close = 0;
            double worst = 0;
            foreach (var db in OsuDb.Load(Path.Combine(Config.OsuDir, "osu!.db")).Where(b => b.Mode == 0))
                foreach (var m in combos)
                {
                    if (db.StoredStars(m) is not { } stored) continue;
                    double ours = StarRating.Compute(Beatmap.Load(db.FilePath, mods: m), m);
                    double diff = Math.Abs(ours - stored);
                    n++;
                    if (diff < 0.005) close++;
                    worst = Math.Max(worst, diff);
                    if (diff >= 0.005 || args.Contains("--verbose")) Console.WriteLine($"{(m == Mods.None ? "NM" : m.Short()),-6} stored {stored:0.0000} ours {ours:0.0000}  {db.OsuFile}");
                }
            Console.WriteLine($"{close}/{n} within 0.005 stars; worst {worst:0.0000}");
            return;
        }
        if (args.Contains("--check-readmap")) // test hook: our .osu reader against what osu!.db recorded for the same maps
        {
            Config.Load();
            int same = 0, diff = 0;
            foreach (var db in OsuDb.Load(Path.Combine(Config.OsuDir, "osu!.db")).Where(b => b.Mode == 0))
            {
                if (SongSelect.ReadMap(db.FilePath) is not { } m) { Console.WriteLine($"unreadable {db.OsuFile}"); continue; }
                var a = (db.Circles, db.Sliders, db.Spinners, db.DrainSeconds, db.TotalMs / 1000, Math.Round(db.MinBpm), Math.Round(db.MaxBpm), Math.Round(db.MainBpm), db.SetId);
                var b = (m.Circles, m.Sliders, m.Spinners, m.DrainSeconds, m.TotalMs / 1000, Math.Round(m.MinBpm), Math.Round(m.MaxBpm), Math.Round(m.MainBpm), m.SetId);
                if (a == b) same++;
                else { diff++; Console.WriteLine($"{db.OsuFile}: db {a}, ours {b}"); }
                if (Environment.GetEnvironmentVariable("READMAP_TIMES") == "1" && Beatmap.Load(db.FilePath) is { Objects.Count: > 0 } full)
                    Console.WriteLine($"  total {db.TotalMs} drain {db.DrainSeconds}: last {full.Objects[^1].Type} {full.Objects[^1].Time}-{full.Objects[^1].EndTime}, max end {full.Objects.Max(o => o.EndTime)}, first {full.Objects[0].Time}");
            }
            Console.WriteLine($"{same} same, {diff} different");
            return;
        }
        if (args.SkipWhile(a => a != "--count-search").Skip(1).FirstOrDefault() is { } searches) // test hook: match counts for '|'-separated searches
        {
            Config.Load();
            Config.SetTemp("RemakeGroupMode", "None");
            SetTraceLogLevel(TraceLogLevel.None);
            SetConfigFlags(ConfigFlags.HiddenWindow);
            InitWindow(640, 480, "count-search");
            foreach (var (q, n) in new SongSelect().CountMatches(searches.Split('|'))) Console.WriteLine($"{n,4}  {q}");
            CloseWindow();
            return;
        }
        if (args.Contains("--check-ui"))
        {
            Config.Load();
            var kinds = DefaultSkin.Names.GroupBy(n => DefaultSkin.Get(n) is { } b && b.Length > 4
                ? b[0] == 0x89 ? "png" : b[0] == 'R' && b[1] == 'I' ? "wav" : b[0] == 'O' && b[1] == 'g' ? "ogg" : b[0] == 0 && b[1] == 1 ? "ttf" : b[0] == 'I' && b[1] == 'D' || b[0] == 0xFF ? "mp3" : "other"
                : "empty");
            foreach (var k in kinds) Console.WriteLine($"{k.Key}: {k.Count()} ({string.Join(", ", k.Key == "ttf" || Environment.GetEnvironmentVariable("LIST_UI") == "1" ? k : k.Take(6))})");
            static string Size(byte[]? b) => b is { Length: > 24 } && b[0] == 0x89 ? $"{(b[16] << 24) | (b[17] << 16) | (b[18] << 8) | b[19]}x{(b[20] << 24) | (b[21] << 16) | (b[22] << 8) | b[23]}" : "-";
            var skinDir = Path.Combine(Config.OsuDir, "Skins", Config.Get("Skin", ""));
            foreach (var n in args.SkipWhile(a => a != "--check-ui").Skip(1))
            {
                string user = new[] { n + "@2x.png", n + ".png" }.Select(f => Path.Combine(skinDir, f)).FirstOrDefault(File.Exists) is { } p ? $"{Path.GetFileName(p)} {Size(File.ReadAllBytes(p))}" : "-";
                Console.WriteLine($"{n}: skin {user}; default {Size(DefaultSkin.Get(n + "@2x"))} (@2x) {Size(DefaultSkin.Get(n))}");
                if (Environment.GetEnvironmentVariable("DUMP_UI") is { } dumpDir && DefaultSkin.Get(n) is { } png) File.WriteAllBytes(Path.Combine(dumpDir, n + ".png"), png);
            }
            return;
        }
        if (args.Length >= 4 && args[0] == "--fuzz-osr") { FuzzOsr(args[1], int.Parse(args[2]), args[3], args.Length > 4 ? args[4] : ""); return; }
        if (args.Length >= 2 && args[0] == "--dump-objects") { DumpObjects(args[1], args.Length > 2 ? args[2] : ""); return; }
        if (args.Length >= 2 && args[0] == "--dump-osr") { Console.WriteLine(string.Join("\n", Replays.OsrText(args[1]).Split(',').Take(args.Length > 2 ? int.Parse(args[2]) : 12))); return; }
        if (args.Contains("--auto-check")) { AutoCheck(args.SkipWhile(a => a != "--auto-check").Skip(1).FirstOrDefault() is { } fl && !fl.StartsWith("--") ? fl : "", ModInfo.Parse(args.SkipWhile(a => a != "--mods").Skip(1).FirstOrDefault() ?? "")); return; }
        if (args.Contains("--verify-osr")) { VerifyOsr(int.TryParse(args.SkipWhile(a => a != "--verify-osr").Skip(1).FirstOrDefault(), out var n) ? n : 100); return; }
        if (args.Length > 0 && !args[0].StartsWith("--")) Config.OsuDir = args[0];
        // Test hooks: --play <.osu> [--auto] [--shots 5,10] [--exit-after 20] (seconds since launch), --song-shots 12000 (song ms)
        string? Arg(string name) => Array.IndexOf(args, name) is int i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        double[] shots = Arg("--shots")?.Split(',').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray() ?? [];
        double[] songShots = Arg("--song-shots")?.Split(',').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToArray() ?? []; // song ms, during play
        double exitAfter = Arg("--exit-after") is { } ea ? double.Parse(ea, System.Globalization.CultureInfo.InvariantCulture) : double.MaxValue;
        string? testDrop = Arg("--drop");
        int shotIndex = 0;
        if (Arg("--input") is { } script) Input.Load(script); // test hook: scripted input
        Config.Load();

        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
        using (var p = Process.GetCurrentProcess()) p.PriorityClass = ProcessPriorityClass.High;
        timeBeginPeriod(1);

        SetTraceLogLevel(TraceLogLevel.Warning);
        string frameSync = Config.Get("FrameSync", "Unlimited");
        if (frameSync == "VSync") SetConfigFlags(ConfigFlags.VSyncHint);
        // like stable, keep running while minimised (raylib would otherwise stop the loop until the window comes back,
        // while the music plays on); the loop throttles itself to 15fps there
        SetConfigFlags(ConfigFlags.AlwaysRunWindow);
        bool testRun = exitAfter < double.MaxValue;
        if (testRun) { SetConfigFlags(ConfigFlags.UnfocusedWindow); Input.ScriptOnly = true; } // test runs don't take focus or real input
        if (testRun) Config.SetTemp("VolumeUniversal", "2"); // quiet: the user may be at the PC
        var (resW, resH) = Resolution();
        InitWindow(resW, resH, "osu!stable remake");
        SetExitKey(KeyboardKey.Null);
        int monitor = GetCurrentMonitor();
        ApplyDisplay();
        SetTargetFPS(0);
        HideCursor();
        Bass.Init();
        // JIT everything up front on a low-priority thread: otherwise each screen's first frame compiles its code
        // (~30ms when gameplay starts). Generic instantiations still compile on first use.
        new Thread(() =>
        {
            foreach (var t in typeof(Game).Assembly.GetTypes())
                foreach (var m in t.GetMethods(System.Reflection.BindingFlags.DeclaredOnly | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic))
                    if (!m.IsAbstract && !m.ContainsGenericParameters)
                        try { System.Runtime.CompilerServices.RuntimeHelpers.PrepareMethod(m.MethodHandle); } catch { } // P/Invoke stubs etc.
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal }.Start();

        var codepoints = Enumerable.Range(32, 0x24F - 31).Append(0x2605).Append(0x266A)
            .Concat(Enumerable.Range('0' + 63500, 10)).ToArray(); // + star (U+2605), note (U+266A), Aller's old-style digits
        Font = LoadUiFont("Aller", @"C:\Windows\Fonts\segoeui.ttf", codepoints);
        Bold = LoadUiFont("Aller_Bd", @"C:\Windows\Fonts\segoeuib.ttf", codepoints);
        Light = LoadUiFont("Aller_Lt", @"C:\Windows\Fonts\segoeuil.ttf", codepoints);
        Mono = LoadUiFont("", @"C:\Windows\Fonts\consola.ttf", codepoints); // the FPS counter's font (Windows')
        // search, gear, desktop, circle, volume, brush, gamepad, pencil, globe, wrench, chevron-circle-left, chevron-down
        Icons = LoadUiFont("FontAwesome", @"C:\Windows\Fonts\segmdl2.ttf", [0xf002, 0xf013, 0xf108, 0xf10c, 0xf028, 0xf026, 0xf1fc, 0xf11b, 0xf040, 0xf0ac, 0xf0ad, 0xf137, 0xf078, 0xf048, 0xf04b, 0xf04c, 0xf04d, 0xf051, 0xf129, 0xf0c9, 0xf005, 0xf01b, 0xf054]);

        Skin = new Skin(Path.Combine(Config.OsuDir, "Skins", Config.Get("Skin", "")));
        var testMods = ModInfo.Parse(Arg("--mods") ?? "") | (args.Contains("--auto") ? Mods.Autoplay : Mods.None);
        var songSelect = new SongSelect(Arg("--search") ?? "", testMods);
        Player.Bot = args.Contains("--bot");
        if (Arg("--test-speed") is { } ts) Player.TestSpeedAt = ts.Split(',').Select(s => double.Parse(s, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        if (Arg("--test-pause") is { } tp && tp.Split(',') is [var pa, var pc]) Player.TestPause = (double.Parse(pa, System.Globalization.CultureInfo.InvariantCulture), double.Parse(pc, System.Globalization.CultureInfo.InvariantCulture));
        if (Arg("--watch") is { } watch && Arg("--play") is { } watchMap)
        {
            var info = Replays.ReadAnyInfo(watch);
            Screen = args.Contains("--summary") ? Player.Summary(Beatmap.Load(watchMap, mods: info.Mods), songSelect, info) // test hook: the replay's ranking screen
                : new Player(Beatmap.Load(watchMap, mods: info.Mods), songSelect, info.Mods, Replays.Frames(info), info);
        }
        else Screen = Arg("--play") is { } play ? new Player(Beatmap.Load(play, mods: testMods), songSelect, testMods)
            : Arg("--search") != null ? songSelect : new MainMenu(songSelect, open: args.Contains("--menu-open"), play: args.Contains("--menu-play"));
        if (args.Contains("--options") || Arg("--options-at") != null) Screen = new Options(Screen, songSelect, Arg("--options-at")); // test hooks

        double refresh = Math.Max(60, GetMonitorRefreshRate(monitor));
        double nextFrame = NowMs, last = NowMs;
        var stats = new FrameStats();
        var work = new List<float>();
        double cpuSum = 0; // test runs: the part before EndDrawing (present and any GPU wait excluded)
        if (Environment.GetEnvironmentVariable("ALLOC_PROFILE") == "1") AllocProfile.Instance = new AllocProfile();

        while (!WindowShouldClose() && !Quit)
        {
            double now = NowMs;
            double dt = now - last;
            last = now;
            stats.Add(dt);

            if (Input.AltEnterPressed) SetFullscreen(DisplayMode() != "Fullscreen"); // like the options' toggle

            Input.Update(now / 1000);
            if (!testRun) { ConfineCursor(); ApplySensitivity(); }
            if (IsFileDropped()) BeatmapImport.Open(GetDroppedFiles());
            if (testDrop != null && now >= 1500) { BeatmapImport.Open([testDrop]); testDrop = null; } // test hook: --drop <file>
            BeatmapImport.Poll();
            VolumeControl.Update(songSelect);
            // minimised (a 0x0 window) only a play keeps running, so its time stays with the music; menus wait
            if (UpdateFade(dt) && (!IsWindowMinimized() || Screen is Player)) Screen.Update(dt);

            BeginDrawing();
            ClearBackground(Color.Black);
            if (!IsWindowMinimized()) // a minimised window is 0x0: nothing to draw (the game itself keeps running, like stable)
            {
                Screen.Draw();
                DrawFade();
                VolumeControl.Draw();
                Notification.Draw();
                Toast.Draw();
                SongSelect.FlushCursor();
                stats.Draw(TargetFps(Config.Get("FrameSync", "Unlimited"), refresh));
            }
            double secs = now / 1000;
            if (shotIndex < shots.Length && secs >= shots[shotIndex] || shotIndex < songShots.Length && Screen is Player sp && sp.SongTime >= songShots[shotIndex])
            {
                Rlgl.DrawRenderBatchActive(); // screenshots read the framebuffer, so flush pending draws first
                TakeScreenshot($"shot-{shotIndex++}.png");
            }
            UpdateBossKey(songSelect);
            // F7 (Toggle Framerate Limit): stable's cycle VSync / Power saving -> Optimal -> Unlimited -> Power saving
            if (Input.IsKeyPressed(Config.Key("keyToggleFrameLimiter", KeyboardKey.F7)))
            {
                string next = Config.Get("FrameSync", "Unlimited") switch { "VSync" or "PowerSaving" => "Optimal", "Optimal" or "Limit120" => "Unlimited", _ => "PowerSaving" };
                Config.SetOwn("FrameSync", next);
                ClearWindowState(ConfigFlags.VSyncHint);
                // stable's notice: "Frame limiter: Power Saving (330fps)", "Optimal (960fps)", "Unlimited (gameplay)"
                Notification.Show(next switch
                {
                    "PowerSaving" => $"Frame limiter: Power Saving ({TargetFps(next, refresh):0}fps)",
                    "Optimal" => $"Frame limiter: Optimal ({TargetFps(next, refresh):0}fps)",
                    _ => "Frame limiter: Unlimited (gameplay)",
                }, 1000);
            }
            // F12 (Save Screenshot): the frame as Screenshots/screenshotN.jpg (or .png, per Screenshot format) next to the remake (never in stable's folder)
            if (Input.IsKeyPressed(Config.Key("keyScreenshot", KeyboardKey.F12)))
            {
                Rlgl.DrawRenderBatchActive();
                var dir = Path.Combine(AppContext.BaseDirectory, "Screenshots");
                Directory.CreateDirectory(dir);
                int n = 1;
                string ext = Config.Get("ScreenshotFormat", "Jpg") == "Png" ? "png" : "jpg";
                while (File.Exists(Path.Combine(dir, $"screenshot{n:000}.{ext}"))) n++;
                var img = LoadImageFromScreen();
                string file = Path.Combine(dir, $"screenshot{n:000}.{ext}");
                ExportImage(img, file);
                UnloadImage(img);
                Toast.Show($"Saved screenshot to {file}", new Color(138, 43, 226, 255), 6000); // stable: BlueViolet, 6s
            }
            if (secs >= exitAfter) Quit = true;
            if (AllocProfile.Instance is { } ap) ap.On = secs > 8; // gameplay only, after loading
            if (exitAfter < double.MaxValue) cpuSum += NowMs - now;
            EndDrawing();
            if (exitAfter < double.MaxValue) // test runs: update+draw+present time per frame, slow ones listed
            {
                float w = (float)(NowMs - now);
                work.Add(w);
                if (w > 5) Console.Error.WriteLine($"slow frame: {w:0.0}ms at {secs:0.000}s ({Screen.GetType().Name}, GCs {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)})");
            }

            // Unfocused/minimized: throttle like stable so the game doesn't burn a core in the background.
            double fps = IsWindowMinimized() ? 15 : !IsWindowFocused() && !testRun ? 60 : TargetFps(Config.Get("FrameSync", "Unlimited"), refresh);
            nextFrame = Limit(fps, nextFrame);
        }

        if (work.Count > 0) // test runs report how long frames took to build (the limiter's wait excluded)
        {
            work.Sort();
            Console.Error.WriteLine($"frame work: {work.Count} frames, mean {work.Average():0.000}ms (cpu {cpuSum / work.Count:0.000}ms), p99 {work[(int)(work.Count * 0.99)]:0.000}ms, max {work[^1]:0.000}ms; main thread allocated {GC.GetAllocatedBytesForCurrentThread() / 1048576.0:0} MB");
        }
        AllocProfile.Instance?.Print();
        Skin.Unload();
        Bass.BASS_Free();
        CloseWindow();
        timeEndPeriod(1);
        ClipCursorOff(IntPtr.Zero); // the cursor clip is system-wide
    }

    // --fuzz-osr <file.osu> <seed> <out.osr> [mods]: plays the map with the test bot (Auto's cursor, jittered presses),
    // then roughens the input more (each key press shifted by a random amount, the cursor pushed around by a slow wobble up to ~1.5 radii) and saves it as a stable
    // .osr with NoFail, then prints our own judgement of that file. Stable can watch the same file: a judging test.
    static void FuzzOsr(string file, int seed, string outPath, string mods)
    {
        Config.Load();
        SetTraceLogLevel(TraceLogLevel.None);
        SetConfigFlags(ConfigFlags.HiddenWindow);
        InitWindow(640, 480, "fuzz");
        Skin = new Skin(Path.Combine(Config.OsuDir, "Skins", Config.Get("Skin", "")));
        Player.VirtualClock = true;
        var play = ModInfo.Parse(mods) | Mods.NoFail;
        var back = new SongSelect();
        Player.Bot = true; // Auto's cursor through the recorded input path, presses with seeded jitter
        var auto = new Player(Beatmap.Load(file, mods: play), back, play);
        for (int step = 0; step < 50_000_000 && !auto.Result.Done; step++) auto.Update(1000 / 60.0);
        var src = auto.Recording.ToList();
        auto.Dispose();
        Player.Bot = false;

        var rng = new Random(seed);
        var map = Beatmap.Load(file, mods: play);
        double timing = rng.NextDouble() * 25, wobble = map.Radius * (0.2 + rng.NextDouble() * 1.3);
        double p1 = 250 + rng.NextDouble() * 600, p2 = 400 + rng.NextDouble() * 900, a1 = rng.NextDouble() * 7, a2 = rng.NextDouble() * 7;
        double Gauss() => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());
        // Key-down intervals of the Auto input, each shifted as a whole (no overlap with the next press of that key).
        var shifted = new List<(double Start, double End, byte Bit)>();
        for (int bit = 0; bit < 4; bit++)
        {
            double? down = null;
            foreach (var f in src)
            {
                bool on = (f.Keys & (1 << bit)) != 0;
                if (on && down == null) down = f.Time;
                else if (!on && down != null) { double d = Gauss() * timing; shifted.Add((down.Value + d, f.Time + d, (byte)(1 << bit))); down = null; }
            }
        }
        byte KeysAt(double t) { byte k = 0; foreach (var s in shifted) if (t >= s.Start && t < s.End) k |= s.Bit; return k; }
        Vector2 Wobble(double t) => new((float)(wobble * Math.Sin(t / p1 + a1)), (float)(wobble * Math.Sin(t / p2 + a2)));
        // A frame every 5ms: stable's replay watcher takes one frame per update and runs game logic only then, at the
        // frame's time; at 5ms it keeps up (it skips frames when it falls behind), so its judging is exactly frame-stepped.
        var frames = new List<ReplayFrame>();
        int j = 0;
        for (double t = Math.Round(src[0].Time); t <= Math.Round(src[^1].Time); t += 5)
        {
            while (j + 1 < src.Count && src[j + 1].Time <= t) j++;
            var a = src[j]; var b = j + 1 < src.Count ? src[j + 1] : a;
            float k = b.Time > a.Time ? (float)((t - a.Time) / (b.Time - a.Time)) : 0;
            var basePos = Vector2.Lerp(new Vector2(a.X, a.Y), new Vector2(b.X, b.Y), k) + Wobble(t);
            frames.Add(new ReplayFrame(t, MathF.Round(basePos.X * 64) / 64, MathF.Round(basePos.Y * 64) / 64, KeysAt(t)));
        }
        var info = new ReplayInfo { BeatmapMd5 = Replays.Md5Of(file), Player = Path.GetFileNameWithoutExtension(outPath), Mods = play, Date = DateTime.Now, FromStable = true, Path = outPath };
        Replays.SaveOsr(outPath, info, frames);

        Player.FrameStepped = true;
        Console.Error.WriteLine("--- judging the .osr"); // separates the bot run's VERIFY_LOG lines from the judgement's
        var judge = new Player(Beatmap.Load(file, mods: play), back, play, Replays.ReadOsrFrames(outPath), info);
        bool scoreLog = Environment.GetEnvironmentVariable("FUZZ_SCORELOG") == "1"; // "score <ms> <total>" at every change
        long lastScore = -1;
        for (int step = 0; step < 50_000_000 && !judge.Result.Done; step++)
        {
            judge.Update(scoreLog ? 1 : 1000 / 60.0);
            if (scoreLog && judge.Score != lastScore) Console.Error.WriteLine($"score {judge.Time:0} {lastScore = judge.Score}");
        }
        var x = judge.Result;
        // The header gets our judgement, so stable's ranking screen and leaderboard show real numbers for the file.
        (info.N300, info.N100, info.N50, info.NMiss, info.MaxCombo, info.Score) = (x.N300, x.N100, x.N50, x.NMiss, x.MaxCombo, judge.Score);
        (info.NGeki, info.NKatu, info.Perfect) = (judge.Geki, judge.Katu, x.MaxCombo == judge.MapMaxCombo);
        Replays.SaveOsr(outPath, info, frames);
        Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0} seed {1}: timing sd {2:0}ms wobble {3:0.0}px -> ours {4}/{5}/{6}/{7} {8}x score {9}",
            Path.GetFileName(outPath), seed, timing, wobble, x.N300, x.N100, x.N50, x.NMiss, x.MaxCombo, judge.Score));
        judge.Dispose();
    }

    // Self-check: parse a sample of the real Songs folder and assert the results are sane.
    // --dump-objects <file.osu> [mods]: every hit object as we compute it, one line each (times, stack, stacked and
    // base position, and for sliders the path end, velocity in px/s, segment count, last segment and checkpoint times),
    // for comparing against the same values taken from stable.
    static void DumpObjects(string file, string mods)
    {
        Config.Load();
        var parsed = ModInfo.Parse(mods);
        var map = Beatmap.Load(file, mods: parsed);
        var health = new Health(map, parsed.HasFlag(Mods.HalfTime));
        Console.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "hp rate {0:R} mul {1:R} geki {2:R}", health.DropRate, health.mul, health.mulGeki));
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string V(Vector2 v) => string.Format(inv, "{0:R},{1:R}", v.X, v.Y);
        for (int i = 0; i < map.Objects.Count; i++)
        {
            var o = map.Objects[i];
            string kind = o.Type switch { ObjType.Slider => "slider", ObjType.Spinner => "spinner", _ => "circle" };
            var line = string.Format(inv, "{0} {1} {2} {3} stack {4} pos {5} base {6} hpsim {7:R}", i, kind, (int)o.Time, (int)o.EndTime, o.Stack, V(o.Pos + map.StackOffset(o)), V(o.Pos), health.SimHp[i]);
            if (o.Type == ObjType.Slider && o.Path.Length > 1)
                line += string.Format(inv, " end {0} v {1:R} segs {2} lastseg {3}->{4} cps {5}", V(o.EndPos + map.StackOffset(o)), o.StableVelocity, o.Path.Length - 1,
                    V(o.Path[^2] + map.StackOffset(o)), V(o.Path[^1] + map.StackOffset(o)), string.Join(",", o.Checkpoints.Select(c => (int)c.Time)));
            Console.WriteLine(line);
        }
    }

    static void Check()
    {
        Config.Load(); // OsuDir comes from remake.cfg
        var files = Directory.EnumerateDirectories(Config.SongsDir)
            .Where((_, i) => i % 10 == 0)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.osu").Take(2))
            .ToList();
        int ok = 0, std = 0;
        foreach (var f in files)
        {
            if (Environment.GetEnvironmentVariable("CHECK_TRACE") == "1") Console.Error.WriteLine(f);
            var map = Beatmap.Load(f);
            if (map.Mode != 0) { ok++; continue; }
            std++;
            for (int i = 0; i < map.Objects.Count; i++)
            {
                var o = map.Objects[i];
                Debug.Assert(i == 0 || map.Objects[i - 1].Time <= o.Time, $"unsorted: {f}");
                if (o.EndTime < o.Time) throw new Exception($"end before start at {o.Time}: {f}");
                if (o.Type != ObjType.Slider) continue;
                if (o.Path.Length == 0 || float.IsNaN(o.Path[^1].X)) throw new Exception($"bad path at {o.Time}: {f}");
                if (o.Length > 1 && Math.Abs(o.Cumulative[^1] - o.Length) > Math.Max(1, o.Length * 0.01))
                    throw new Exception($"path length {o.Cumulative[^1]:0.0} != {o.Length:0.0} at {o.Time}: {f}");
                if (o.Checkpoints.Count(c => c.Kind == CheckpointKind.End) != 1) throw new Exception($"need exactly one end checkpoint: {f}");
            }
            ok++;
        }
        Console.WriteLine($"check passed: {ok}/{files.Count} maps parsed, {std} osu!standard validated");

        for (int id = 0; id <= 34; id++)
            if (Math.Abs(Easing.Apply(id, 0)) > 1e-9 || Math.Abs(Easing.Apply(id, 1) - 1) > 1e-9) throw new Exception($"easing {id} doesn't run 0..1");
        int boards = 0;
        foreach (var f in files) if (Storyboard.Load(Beatmap.Load(f, metadataOnly: true), textures: false) != null) boards++; // must not throw
        Console.WriteLine($"storyboards: {boards} of the sampled maps have one (parsed), easings ok");

        var sw = Stopwatch.StartNew();
        var db = OsuDb.Load(Path.Combine(Config.OsuDir, "osu!.db")); // throws unless every byte is consumed
        var stdDb = db.Where(b => b.Mode == 0).ToList();
        Debug.Assert(stdDb.All(b => b.Folder.Length > 0 && b.OsuFile.EndsWith(".osu", StringComparison.OrdinalIgnoreCase)));
        var sample = stdDb.Where((_, i) => i % 500 == 0).ToList();
        int existing = sample.Count(b => File.Exists(b.FilePath));
        int withStars = stdDb.Count(b => b.StarsFor(Mods.None) > 0);
        Console.WriteLine($"osu!.db: {db.Count} beatmaps ({stdDb.Count} std, {withStars} with stars) in {sw.ElapsedMilliseconds}ms; {existing}/{sample.Count} sampled files exist");
        var top = stdDb.MaxBy(b => b.StarsFor(Mods.None))!;
        Console.WriteLine($"  hardest: {top.Artist} - {top.Title} [{top.Version}] {top.StarsFor(Mods.None):0.00}* (DT {top.StarsFor(Mods.DoubleTime):0.00}*), {top.MinBpm:0}-{top.MaxBpm:0} BPM");
    }

    // Fidelity check: re-judge the user's own stable replays with this engine (virtual clock, 1ms steps,
    // headless) and compare with the 300/100/50/miss/combo stable recorded in each .osr.
    static void VerifyOsr(int max)
    {
        Config.Load();
        Player.LiveJudging = Environment.GetEnvironmentVariable("VERIFY_STEP") != "1"; // stable recorded these live; VERIFY_STEP=1 judges them as its replay viewer does
        SetTraceLogLevel(TraceLogLevel.None);
        SetConfigFlags(ConfigFlags.HiddenWindow);
        InitWindow(640, 480, "verify");
        Skin = new Skin(Path.Combine(Config.OsuDir, "Skins", Config.Get("Skin", "")));
        Player.VirtualClock = true;

        string db = Path.Combine(Config.OsuDir, "osu!.db");
        var maps = (File.Exists(db) ? OsuDb.Load(db) : SongSelect.ScanFolders())
            .GroupBy(b => b.Md5).ToDictionary(g => g.Key, g => g.First());
        var only = Environment.GetEnvironmentVariable("VERIFY_MD5");
        var stable = Replays.Index().Values.SelectMany(l => l).Where(r => r.FromStable && (only == null || r.BeatmapMd5 == only)).ToList();
        var replays = stable.Where(r => maps.ContainsKey(r.BeatmapMd5)).OrderBy(r => r.Path, StringComparer.Ordinal).Take(max).ToList();
        Console.WriteLine($"{stable.Count} stable osu!standard replays, {stable.Count(r => maps.ContainsKey(r.BeatmapMd5))} with the map installed; checking {replays.Count}");
        var back = new SongSelect();
        int exact = 0, done = 0, failedLoad = 0, incomplete = 0;
        long d300 = 0, d100 = 0, d50 = 0, dMiss = 0, dCombo = 0;
        double hpDiff = 0; long hpPoints = 0;
        foreach (var r in replays)
        {
            var m = maps[r.BeatmapMd5];
            if (r.N300 + r.N100 + r.N50 + r.NMiss < m.Circles + m.Sliders + m.Spinners) { incomplete++; continue; } // failed/quit play

            Player p;
            try
            {
                var mods = (r.Mods & ~(Mods.SuddenDeath | Mods.Perfect)) | Mods.NoFail; // judge everything, never stop early
                p = new Player(Beatmap.Load(maps[r.BeatmapMd5].FilePath, mods: mods), back, mods, Replays.ReadOsrFrames(r.Path), r);
            }
            catch (Exception e) when (e is IOException or InvalidDataException or FormatException or IndexOutOfRangeException)
            {
                failedLoad++;
                continue;
            }
            for (int step = 0; step < 3_600_000 && !p.Result.Done; step++) p.Update(1);
            var x = p.Result;
            // HP against stable's recorded life bar: stable writes a point (~2s apart) right after a judgement, so it's
            // compared with our first sample (10ms apart in verify runs) at or after that time. VERIFY_LOG also
            // prints where they first part: a break stable took and we didn't, or the reverse.
            bool parted = Environment.GetEnvironmentVariable("VERIFY_LOG") != "1";
            if (p.hpGraph.Count > 0)
                foreach (var sp in Replays.OsrLifeBar(r.Path))
                {
                    var ours = p.hpGraph.FirstOrDefault(g => g.X >= sp.X, p.hpGraph[^1]);
                    hpDiff += Math.Abs(ours.Y - sp.Y); hpPoints++;
                    if (!parted && Math.Abs(ours.Y - sp.Y) > 0.05f) { Console.Error.WriteLine($"  life bar parts at {sp.X:0}: stable {sp.Y:0.00}, ours {ours.Y:0.00}"); parted = true; }
                }
            p.Dispose();
            done++;
            bool same = x.N300 == r.N300 && x.N100 == r.N100 && x.N50 == r.N50 && x.NMiss == r.NMiss && x.MaxCombo == r.MaxCombo;
            if (same) exact++;
            d300 += Math.Abs(x.N300 - r.N300); d100 += Math.Abs(x.N100 - r.N100); d50 += Math.Abs(x.N50 - r.N50);
            dMiss += Math.Abs(x.NMiss - r.NMiss); dCombo += Math.Abs(x.MaxCombo - r.MaxCombo);
            if (!same)
                Console.WriteLine($"{(r.Mods == Mods.None ? "" : r.Mods.Short() + " ")}{Path.GetFileName(r.Path)} [{maps[r.BeatmapMd5].Artist} - {maps[r.BeatmapMd5].Title} ({maps[r.BeatmapMd5].Version})]: stable {r.N300}/{r.N100}/{r.N50}/{r.NMiss} {r.MaxCombo}x, ours {x.N300}/{x.N100}/{x.N50}/{x.NMiss} {x.MaxCombo}x, mean err {x.Mean:+0.0;-0.0}ms");
        }
        Console.WriteLine($"verified {done} complete stable replays ({incomplete} incomplete plays skipped, {failedLoad} unreadable): {exact} exact; total |diff| 300 {d300} 100 {d100} 50 {d50} miss {dMiss} combo {dCombo}; life bar mean |diff| {hpDiff / Math.Max(1, hpPoints):0.0000}");
        CloseWindow();
    }

    // Auto must never miss a 300: plays osu!standard maps (title/version containing the filter) headlessly at
    // several frame lengths and lists any run that isn't all 300s.
    static void AutoCheck(string filter, Mods extra)
    {
        Config.Load();
        SetTraceLogLevel(TraceLogLevel.None);
        SetConfigFlags(ConfigFlags.HiddenWindow);
        InitWindow(640, 480, "auto-check");
        Skin = new Skin(Path.Combine(Config.OsuDir, "Skins", Config.Get("Skin", "")));
        Player.VirtualClock = true;
        var maps = OsuDb.Load(Path.Combine(Config.OsuDir, "osu!.db"))
            .Where(b => b.Mode == 0 && $"{b.Artist} {b.Title} [{b.Version}]".Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
        var back = new SongSelect();
        double[] frames = [1, 1000 / 144.0, 1000 / 60.0, 0.37, 1000 / 30.0, 50]; // long frames = stutters
        bool loadOnly = Environment.GetEnvironmentVariable("AUTOCHECK_LOAD") == "1"; // just build every Player
        int runs = 0, bad = 0;
        foreach (var b in maps)
            foreach (var dt in frames)
            {
                Player p;
                try
                {
                    var map = Beatmap.Load(b.FilePath, mods: Mods.Autoplay | extra);
                    if (map.Objects.Count == 0) break; // song select won't start these either
                    p = new Player(map, back, Mods.Autoplay | extra);
                }
                catch (Exception e) when (e is IOException or InvalidDataException or FormatException) { break; }
                catch (Exception e)
                {
                    Console.WriteLine($"{b.Artist} - {b.Title} [{b.Version}] CRASHED loading: {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                    bad++;
                    break;
                }
                if (loadOnly) { p.Dispose(); break; }
                for (int step = 0; step < 50_000_000 && !p.Result.Done; step++) p.Update(dt);
                var x = p.Result;
                p.Dispose();
                runs++;
                if (x.N100 + x.N50 + x.NMiss == 0) continue;
                // Objects overlapping in time (2B maps) can't all be followed by one cursor - stable's Auto drops
                // points there too - so those are tagged rather than counted.
                var objs = Beatmap.Load(b.FilePath, mods: Mods.Autoplay | extra).Objects;
                bool twoB = objs.Zip(objs.Skip(1)).Any(p => p.Second.Time < p.First.EndTime);
                if (!twoB) bad++;
                Console.WriteLine($"{b.Artist} - {b.Title} [{b.Version}] at {dt:0.##}ms frames: {x.N300}/{x.N100}/{x.N50}/{x.NMiss} {x.MaxCombo}x{(twoB ? " (2B map)" : "")}");
            }
        Console.WriteLine($"{runs} auto runs over {maps.Count} maps: {bad} not all 300s");
        CloseWindow();
    }

    static double TargetFps(string frameSync, double refresh) => frameSync switch
    {
        "Custom" => Config.GetInt("CustomFrameLimit", 0),
        "Optimal" or "Limit120" => Math.Min(960, refresh * 8),
        "PowerSaving" => refresh * 2,
        "Unlimited" when Screen is not Player => refresh * 2, // stable: "Unlimited (gameplay)" only lifts the cap in play
        _ => 0, // Unlimited / VSync: no software limit
    };

    // Deadline-based limiter: sleeps until ~1ms before the deadline, then spins. Unlike stable's
    // floor(ms) Thread.Sleep, frame pacing stays exact at any target and never drifts.
    static double Limit(double fps, double nextFrame)
    {
        double now = NowMs;
        if (fps <= 0) return now;
        double frameMs = 1000.0 / fps;
        nextFrame += frameMs;
        if (now > nextFrame + frameMs) nextFrame = now; // fell behind: don't burst to catch up
        while (nextFrame - NowMs > 1.2) Thread.Sleep(1);
        while (NowMs < nextFrame) Thread.SpinWait(16);
        return nextFrame;
    }
}

// stable's FPS counter (FpsCounter, F11 toggles), measured from its captures in 640x480 units: rounded boxes 40.2
// wide and 12.7 tall, 3 in from the right; the bottom one shows the frame time on (172,220,25), and when a frame
// limit applies a (255,204,34) box above it shows "fps/limit" with the limit smaller and grey. Consolas.
// TODO: box colours for slow frames are guesses (the captures only show good ones).
sealed class FrameStats
{
    // stable's counter: frame time eased 0.05 per second; fps counted over
    // 250ms windows and eased the same way toward min(fps, limit); fps box green at the limit, yellow within 5%
    // (and 10fps), else orange, and it fades out 4s after settling on the limit (the ms box then dims to 80%); ms box
    // green under 8ms, yellow under 16, else orange; a frame over 30ms lights one more of ten drop dots, which go out
    // one per 800ms. With no limit (Unlimited in play) only the frame time box shows, like stable.
    static readonly Color Green = new(172, 220, 25, 255), Yellow = new(255, 204, 34, 255), Orange = new(255, 149, 24, 255), Red = new(255, 36, 0, 255);
    readonly CachedText<double> msText = new(v => v >= 10 ? $"{v:0}" : v >= 1 ? $"{v:0.0}" : $"{v:0.00}"), limitText = new(v => $"/{v:0}");
    readonly CachedText<long> fpsText = new(v => v.ToString());
    double frameMs = 1, fpsShown, windowLeft = 250, windowMs, drops, atLimitSince = double.NaN, lastNow;
    int windowFrames, fpsCounted;

    public void Add(double dt)
    {
        double k = Math.Pow(0.05, dt / 1000);
        frameMs = k * frameMs + (1 - k) * dt;
        windowFrames++;
        windowMs += dt;
        if ((windowLeft -= dt) <= 0)
        {
            windowLeft += 250;
            fpsCounted = windowMs > 0 ? (int)Math.Ceiling(windowFrames * 1000 / windowMs) : 0;
            windowFrames = 0; windowMs = 0;
        }
        drops = dt > 30 ? Math.Min(10, Math.Ceiling(drops + 1)) : Math.Max(0, drops - dt / 800);
    }

    public void Draw(double limit)
    {
        if (Input.IsKeyPressed(KeyboardKey.F11)) Config.SetOwn("FpsCounter", Config.GetBool("FpsCounter", false) ? "0" : "1"); // the option and F11 share it
        double now = Game.NowMs, dt = now - lastNow;
        lastNow = now;
        double k = Math.Pow(0.05, Math.Max(0, dt) / 1000);
        bool limited = limit > 0;
        double target = limited ? Math.Min(fpsCounted, limit) : fpsCounted;
        if (Math.Abs(target - fpsShown) > 0.5) fpsShown = fpsShown * k + target * (1 - k);
        bool atLimit = limited && Math.Abs(fpsShown - limit) < 0.5;
        if (!atLimit) atLimitSince = double.NaN;
        else if (double.IsNaN(atLimitSince)) atLimitSince = now;
        if (!Config.GetBool("FpsCounter", false)) return;

        // stable draws it from osu!ui sprites: fps-box (tinted) boxes centred 23 units from the right at 25 and 10
        // up, the frame time in the big "fps-" font with its "ms" glyph, the fps centred 35 in with "/limit" in the
        // small "fpss-" font after it, and fps-dropped dots for stutters.
        float u = GetScreenHeight() / 480f, sd = GetScreenHeight() / 768f;
        float W = GetScreenWidth(), H = GetScreenHeight();
        double settled = double.IsNaN(atLimitSince) ? 0 : now - atLimitSince;

        string ms = msText.Of(frameMs >= 10 ? Math.Round(frameMs) : frameMs >= 1 ? Math.Round(frameMs, 1) : Math.Round(frameMs, 2));
        var msColour = frameMs < 8 ? Green : frameMs < 16 ? Yellow : Orange;
        float msAlpha = limited ? 1 - 0.2f * (float)Math.Clamp(settled / 10000, 0, 1) : 0.8f; // alone (unlimited play): 80%, measured
        var msCentre = new Vector2(W - 23 * u, H - 10 * u);
        Ui("fps-box")?.Draw(msCentre, sd, ColorAlpha(msColour, msAlpha));
        float msW = RunWidth(Big, ms, "fps-ms", sd);
        DrawRun(Big, ms, "fps-ms", msCentre.X - msW / 2, msCentre.Y, sd, ColorAlpha(Color.White, msAlpha), centreY: true);

        int lit = (int)Math.Ceiling(drops);
        for (int i = 0; i < lit; i++)
            Ui("fps-dropped")?.Draw(new Vector2(W - (49 + i * 7) * u, H - 6 * u), sd, i == 0 ? Yellow : i < 3 ? Orange : Red);

        float fpsAlpha = 1 - (float)Math.Clamp((settled - 4000) / 400, 0, 1);
        if (fpsAlpha <= 0 || !limited) return;
        var fpsCentre = new Vector2(W - 23 * u, H - 25 * u);
        var fpsColour = atLimit || !limited ? Green : fpsShown > limit - 10 && fpsShown > limit * 0.95 ? Yellow : Orange;
        Ui("fps-box")?.Draw(fpsCentre, sd, ColorAlpha(fpsColour, fpsAlpha));
        string big = fpsText.Of((long)Math.Round(fpsShown)), small = limited ? limitText.Of(Math.Round(limit)) : "";
        var tint = ColorAlpha(Color.White, fpsAlpha);
        float bigW = RunWidth(Big, big, null, sd);
        // stable: the number centred 35 in (the limit text has its own spot below). Unlimited ("5805fps") is
        // centred in the box instead, so wide numbers don't run out of it.
        float bigX = limited ? W - 35 * u - bigW / 2 : fpsCentre.X - (bigW + RunWidth(Small, "", "fpss-fps", sd)) / 2;
        DrawRun(Big, big, null, bigX, fpsCentre.Y, sd, tint, centreY: true);
        // stable: "/limit fps" has its top left at a fixed 29 units in from the right (it can touch the number)
        DrawRun(Small, small, "fpss-fps", limited ? W - 29 * u : bigX + bigW, fpsCentre.Y, sd, tint, centreY: false);
    }

    static readonly string[] Big = [.. Enumerable.Range(0, 10).Select(i => $"fps-{i}")], Small = [.. Enumerable.Range(0, 10).Select(i => $"fpss-{i}")];
    static Sprite? Ui(string name) => Game.Skin.DefaultTex(name);
    static Sprite? Glyph(string[] font, char c) => c switch
    {
        >= '0' and <= '9' => Ui(font[c - '0']),
        '.' => Ui(font == Big ? "fps-dot" : "fpss-dot"),
        ',' => Ui(font == Big ? "fps-comma" : "fpss-comma"),
        '/' => Ui("fpss-slash"),
        _ => null,
    };

    // stable's sprite text here overlaps neighbouring glyphs by 1 texel (half an SD pixel with the @2x sprites)
    static float RunWidth(string[] font, string text, string? tail, float s)
    {
        float w = 0, last = 0;
        foreach (char c in text) if (Glyph(font, c) is { } g) { w += (g.Width - g.Scale) * s; last = g.Scale * s; }
        if (tail != null && Ui(tail) is { } t) { w += (t.Width - t.Scale) * s; last = t.Scale * s; }
        return w + last; // the last glyph is drawn whole
    }

    // Draws glyphs left to right from x; vertically centred on y, or with their tops at y.
    static void DrawRun(string[] font, string text, string? tail, float x, float y, float s, Color tint, bool centreY)
    {
        void One(Sprite? g)
        {
            if (g is not { } sp) return;
            float gw = sp.Width * s, gh = sp.Height * s;
            sp.Draw(new Vector2(x + gw / 2, centreY ? y : y + gh / 2), s, tint);
            x += gw - sp.Scale * s;
        }
        foreach (char c in text) One(Glyph(font, c));
        if (tail != null) One(Ui(tail));
    }
}

// One face out of a TrueType collection (.ttc) as a standalone .ttf (raylib loads only single fonts): its table
// directory with the tables copied after it and the offsets rewritten.
static class FontCollection
{
    public static byte[] Extract(byte[] ttc, int index)
    {
        static uint U32(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
        static int U16(byte[] b, int o) => b[o] << 8 | b[o + 1];
        if (ttc.Length < 12 || ttc[0] != 't' || ttc[1] != 't' || ttc[2] != 'c' || ttc[3] != 'f') return ttc;
        int fontOff = (int)U32(ttc, 12 + 4 * index), tables = U16(ttc, fontOff + 4);
        int headerLen = 12 + 16 * tables, total = headerLen;
        for (int t = 0; t < tables; t++) total += ((int)U32(ttc, fontOff + 12 + 16 * t + 12) + 3) & ~3;
        var ttf = new byte[total];
        Array.Copy(ttc, fontOff, ttf, 0, headerLen);
        int at = headerLen;
        for (int t = 0; t < tables; t++)
        {
            int rec = 12 + 16 * t, off = (int)U32(ttc, fontOff + rec + 8), len = (int)U32(ttc, fontOff + rec + 12);
            Array.Copy(ttc, off, ttf, at, len);
            ttf[rec + 8] = (byte)(at >> 24); ttf[rec + 9] = (byte)(at >> 16); ttf[rec + 10] = (byte)(at >> 8); ttf[rec + 11] = (byte)at;
            at += (len + 3) & ~3;
        }
        return ttf;
    }
}
