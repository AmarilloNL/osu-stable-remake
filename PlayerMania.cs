using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// osu!mania, as stable plays it with its default layout: a stage of 30-unit columns from x 136 (480-unit screen), notes
// scrolling down to the hit position at y 402 at 21 x scroll speed (12) units a beat, stable's hit windows, scoring
// (ScoreV1: a base half and a bonus half that swings with the judgements) and health. osu! maps convert with stable's
// key count, notes going to the column under their x (not stable's pattern generator).
sealed partial class Player
{
    sealed class MNote
    {
        public int Column;
        public double Time, End; // End > Time: a hold note
        public bool Done, Holding, Broken;
        public double PressAt = double.NaN, ReleaseAt = double.NaN; // a hold note's first press and last release
        public bool HeadHit => !double.IsNaN(PressAt);
        public int Result = -1;
        public bool Hold => End > Time;
    }

    readonly List<MNote> mNotes = [];
    List<MNote>[] mColumns = [];
    int[] mNext = [];
    int mKeys;
    KeyboardKey[] mBind = [];
    bool[] mDown = [];
    // stable's lighting: every judgement that isn't a miss bursts the column's lightingN once at the hit position
    // (in over 80ms, out from 80 to 200ms); a hold being held loops lightingL there (in over 80ms, out over 120ms)
    double[] mLitAt = [], mHoldOnAt = [], mHoldOffAt = [];

    // stable's mania combo bursts: min(16, combo / 16) star2 sparks fly off each side of the stage from random heights
    // (100-500 out, up to 120 up, falling; growing to 1-3x, turning and fading over 300-1000ms) and, with Combo bursts
    // on, the skin's next comboburst-mania slides out from behind the stage to the side its ComboBurstStyle says
    // (Right by default; Both: either) over 1.4s (OutCirc), in over 250ms, out from 400ms
    int mBurstBase;
    readonly List<(Sprite Tex, bool Right, double At)> mBursts = [];
    readonly List<(float X, float Y, float Vx, float Vy, float Grow, float Rot0, float Rot1, double At, double Life)> mStars = [];
    double mStarTicks;
    int mBurstNext;
    bool[] mHoldLit = [];
    double[] mAutoRelease = [];
    double mW300g, mW300, mW200, mW100, mW50, mWMiss;
    double mBonus = 100, mScore; // ScoreV1's bonus meter and the running score
    (int Result, double At) mLastJudge = (-1, double.MinValue);
    double mComboAt = double.MinValue;
    // the scroll: distance (units) covered from the map's start up to each red / green point
    readonly List<(double Time, double Dist, double PerMs)> mScroll = [];

    static readonly KeyboardKey[][] ManiaDefaultKeys =
    [
        [KeyboardKey.Space],
        [KeyboardKey.F, KeyboardKey.J],
        [KeyboardKey.F, KeyboardKey.Space, KeyboardKey.J],
        [KeyboardKey.D, KeyboardKey.F, KeyboardKey.J, KeyboardKey.K],
        [KeyboardKey.D, KeyboardKey.F, KeyboardKey.Space, KeyboardKey.J, KeyboardKey.K],
        [KeyboardKey.S, KeyboardKey.D, KeyboardKey.F, KeyboardKey.J, KeyboardKey.K, KeyboardKey.L],
        [KeyboardKey.S, KeyboardKey.D, KeyboardKey.F, KeyboardKey.Space, KeyboardKey.J, KeyboardKey.K, KeyboardKey.L],
        [KeyboardKey.A, KeyboardKey.S, KeyboardKey.D, KeyboardKey.F, KeyboardKey.J, KeyboardKey.K, KeyboardKey.L, KeyboardKey.Semicolon],
        [KeyboardKey.A, KeyboardKey.S, KeyboardKey.D, KeyboardKey.F, KeyboardKey.Space, KeyboardKey.J, KeyboardKey.K, KeyboardKey.L, KeyboardKey.Semicolon],
        [KeyboardKey.Q, KeyboardKey.W, KeyboardKey.E, KeyboardKey.R, KeyboardKey.V, KeyboardKey.N, KeyboardKey.U, KeyboardKey.I, KeyboardKey.O, KeyboardKey.P],
    ];

    // The player's keys for this key count, from stable's cfg: ManiaLayouts{n}K holds layouts split by ';' (each the
    // columns' key names, "None" for a column without one) and ManiaLayoutSelected{n}K picks one (-1 or none: stable's
    // default layout).
    internal static KeyboardKey[] ManiaKeys(int keys)
    {
        var def = ManiaDefaultKeys[keys - 1];
        var layouts = Config.Get($"ManiaLayouts{keys}K", "").Split(';');
        int pick = Config.GetInt($"ManiaLayoutSelected{keys}K", -1);
        if (pick < 0 || pick >= layouts.Length || layouts[pick].Trim().Length == 0) return def;
        var names = layouts[pick].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return [.. Enumerable.Range(0, keys).Select(i => i < names.Length ? Config.ParseKey(names[i]) ?? KeyboardKey.Null : KeyboardKey.Null)];
    }

    // stable's key count for an osu! map: by how much of it is sliders and spinners, and its (rounded) CS and OD
    internal static int ManiaConvertKeys(Beatmap m)
    {
        double share = (double)m.Objects.Count(o => o.Type != ObjType.Circle) / Math.Max(1, m.Objects.Count); // (stable divides in double: 0.6 is not over 0.6)
        double cs = Math.Round(m.RawCS), od = Math.Round(m.RawOD);
        if (share < 0.2) return 7;
        if (share < 0.3 || cs >= 5) return od > 5 ? 7 : 6;
        if (share > 0.6) return od > 4 ? 5 : 4;
        return (int)Math.Max(4, Math.Min(od + 1, 7));
    }

    void ManiaSetup()
    {
        bool native = map.Mode == 3;
        // stable's key mods set an osu! map's key count
        mKeys = Math.Clamp(native ? (int)Math.Round(map.RawCS) : mods.Keys() > 0 ? mods.Keys() : ManiaConvertKeys(map), 1, 10);
        mBind = ManiaKeys(mKeys);
        mDown = new bool[mKeys];
        mLitAt = Enumerable.Repeat(double.NegativeInfinity, mKeys).ToArray();
        mHoldOnAt = [.. mLitAt]; mHoldOffAt = [.. mLitAt];
        mHoldLit = new bool[mKeys];
        mAutoRelease = Enumerable.Repeat(double.MinValue, mKeys).ToArray();
        mColumns = Enumerable.Range(0, mKeys).Select(_ => new List<MNote>()).ToArray();
        mNext = new int[mKeys];

        // stable's windows: a fixed 16ms for MAX, the rest from OD (3ms a point); HardRock / Easy divide / multiply by 1.4
        double od = map.RawOD, f = mods.HasFlag(Mods.HardRock) ? 1 / 1.4 : mods.HasFlag(Mods.Easy) ? 1.4 : 1;
        mW300g = 16 * f;
        mW300 = (64 - 3 * od) * f;
        mW200 = (97 - 3 * od) * f;
        mW100 = (127 - 3 * od) * f;
        mW50 = (151 - 3 * od) * f;
        mWMiss = (188 - 3 * od) * f;

        // a mania map's notes go to the column under their x; an osu! map is converted (stable's patterns)
        var notes = ManiaConvert.Convert(map, mKeys, native);
        // Mirror: the columns the other way round; Random: shuffled (the play's seed, kept in its replay)
        int[] order = Enumerable.Range(0, mKeys).ToArray();
        if (mods.HasFlag(Mods.Random)) { var rng = new Random(seed); order = [.. order.OrderBy(_ => rng.Next())]; }
        if (mods.HasFlag(Mods.Mirror)) order = [.. order.Reverse()];
        notes = [.. notes.Select(n => n with { Column = order[Math.Clamp(n.Column, 0, mKeys - 1)] })];
        foreach (var (col, t, end) in notes.OrderBy(x => x.Time))
        {
            var list = mColumns[Math.Clamp(col, 0, mKeys - 1)];
            if (list.Count > 0 && list[^1].End + 1 > t) continue; // (one landing on a note still going in its column is dropped)
            var n = new MNote { Column = Math.Clamp(col, 0, mKeys - 1), Time = t, End = end };
            list.Add(n);
            mNotes.Add(n);
        }

        // the scroll: stable's 21 units a beat at speed 1, its "BPM scale" off by default: normalised to the map's main BPM
        // (the red point covering the most of it) and the mod's rate, so only BPM changes (and, in mania maps, slider
        // velocity) change the speed; the speed (1-40, 12 by default) multiplies it
        var reds = map.TimingPoints.Where(p => p.Uninherited && p.BeatLength > 0).ToList();
        double mainBeat = reds.Count == 0 ? 500 : reds.Select((p, i) => (p.BeatLength, Len: Math.Min(i + 1 < reds.Count ? reds[i + 1].Time : objs[^1].EndTime, objs[^1].EndTime) - Math.Max(p.Time, objs[0].Time)))
            .MaxBy(x => x.Len).BeatLength;
        double perBeat = 21.0 * 100 / (60000 / mainBeat * rate);
        double dist = 0, last = double.NegativeInfinity, perMs = 0;
        var points = map.TimingPoints.Count > 0 ? map.TimingPoints : [new TimingPoint { BeatLength = 500, Uninherited = true }];
        foreach (var p in points)
        {
            double beat = map.UninheritedAt(p.Time).BeatLength / (native ? map.SliderVelocityAt(p.Time) : 1);
            if (mScroll.Count > 0) dist += (p.Time - last) * perMs;
            perMs = perBeat / Math.Max(1, beat);
            mScroll.Add((p.Time, dist, perMs));
            last = p.Time;
        }
    }

    // the scroll distance at a time (units from the map's start)
    double ManiaDist(double t)
    {
        int lo = 0, hi = mScroll.Count - 1;
        while (lo < hi) { int mid = (lo + hi + 1) / 2; if (mScroll[mid].Time <= t) lo = mid; else hi = mid - 1; }
        var s = mScroll[lo];
        return (s.Dist + (t - s.Time) * s.PerMs) * mSpeed;
    }

    int mJudged, mRecorded;
    float mHiddenH = 160, mHiddenA, mJudgeTilt;
    static int mSpeed = Math.Clamp(Config.GetInt("ManiaSpeed", 12), 1, 40);

    // stable's scroll speed keys (F3 / F4 by default) during a play
    void ManiaSpeedKeys()
    {
        int step = Input.IsKeyPressed(Config.Key("keyIncreaseSpeed", KeyboardKey.F4)) ? 1 : Input.IsKeyPressed(Config.Key("keyDecreaseSpeed", KeyboardKey.F3)) ? -1 : 0;
        if (step == 0) return;
        mSpeed = Math.Clamp(mSpeed + step, 1, 40);
        Config.SetOwn("ManiaSpeed", mSpeed.ToString());
        Notification.Show($"osu!mania speed set to {mSpeed} (fixed)", 1750);
    }
    bool ManiaDone => mJudged >= mNotes.Count;

    void ManiaStep()
    {
        if (playback != null)
            // a replay: stable's held columns as bits in x
            while (pbIndex < playback.Length && playback[pbIndex].Time <= time)
            {
                var f = playback[pbIndex++];
                int held = (int)f.X;
                for (int c = 0; c < mKeys; c++)
                {
                    bool down = (held >> c & 1) != 0;
                    if (down && !mDown[c]) ManiaPress(c, f.Time);
                    if (!down && mDown[c]) ManiaRelease(c, f.Time);
                    mDown[c] = down;
                }
            }
        for (int c = 0; c < mKeys && playback == null; c++)
        {
            bool down = auto ? time < mAutoRelease[c] : playback == null && Down(mBind[c]);
            if (auto)
            {
                // Auto: each note pressed on time, held to a hold's end (a tap for 40ms)
                var list = mColumns[c];
                if (mNext[c] < list.Count && list[mNext[c]] is { Done: false } an && !an.HeadHit && an.Time <= time)
                {
                    mAutoRelease[c] = an.Hold ? an.End : an.Time + 40;
                    down = true;
                    ManiaPress(c, an.Time);
                    mDown[c] = true;
                    continue;
                }
            }
            if (down && !mDown[c]) ManiaPress(c, time);
            if (!down && mDown[c]) ManiaRelease(c, time);
            mDown[c] = down;
        }
        if (playback == null && !auto)
        {
            int mask = 0;
            for (int c = 0; c < mKeys; c++) if (mDown[c]) mask |= 1 << c;
            if (mask != mRecorded) { recording.Add(new ReplayFrame(time, mask, 0, 0)); mRecorded = mask; }
        }

        for (int c = 0; c < mKeys; c++)
        {
            var list = mColumns[c];
            while (mNext[c] < list.Count && list[mNext[c]].Done) mNext[c]++;
            if (mNext[c] >= list.Count) continue;
            var n = list[mNext[c]];
            if (!n.Hold) { if (time > n.Time + mW50) ManiaJudge(n, 0); continue; } // never pressed
            // stable's hold: not pressed by its 50 window breaks the combo (once); it's judged once let go within the
            // tail's 50 window (or never pressed by then), or when still held 50 past the end
            if (!n.HeadHit && time <= n.Time + mW50) continue; // (not pressed yet: nothing before the head's window closes)
            if (!n.HeadHit && !n.Broken) { n.Broken = true; BreakCombo(); }
            if (!n.Holding && time >= n.End - mW50 || n.Holding && time >= n.End + mW50) ManiaJudge(n, ManiaHoldResult(n, time));
        }
    }

    void ManiaPress(int c, double at)
    {
        var list = mColumns[c];
        if (mNext[c] >= list.Count) return;
        var n = list[mNext[c]];
        double err = at - n.Time, a = Math.Abs(err);
        if (n.Hold)
        {
            // the first press counts for the judgement (an early one mirrored); pressing again just holds on
            if (n.HeadHit) { n.Holding = true; return; }
            if (err < -mWMiss) return;
            n.PressAt = at;
            n.Holding = true;
            if (a <= mW50) hitErrors.Add(err);
            return;
        }
        if (a > mWMiss) return;
        hitErrors.Add(err);
        ManiaJudge(n, a <= mW300g ? 320 : a <= mW300 ? 300 : a <= mW200 ? 200 : a <= mW100 ? 100 : a <= mW50 ? 50 : 0);
    }

    void ManiaRelease(int c, double at)
    {
        var list = mColumns[c];
        if (mNext[c] >= list.Count) return;
        var n = list[mNext[c]];
        if (!n.Hold || !n.Holding || n.Done) return;
        n.Holding = false;
        n.ReleaseAt = at;
        // let go before the tail's 50 window: the combo breaks (once) and the hold carries on; within it, it's judged
        if (at < n.End - mW50) { if (!n.Broken) { n.Broken = true; BreakCombo(); } }
        else ManiaJudge(n, ManiaHoldResult(n, at));
    }

    // stable's hold judgement: the press's error (an early press mirrored late; none, or one too early, counts as the
    // end) and the release's (a late one mirrored early; still held: now) together; let go too early is a miss, and a
    // broken hold gets 200 at best
    int ManiaHoldResult(MNote n, double now)
    {
        double press = !n.HeadHit || n.PressAt < n.Time - mW50 ? n.End - 1 : n.PressAt >= n.Time ? n.PressAt : n.Time + (n.Time - n.PressAt);
        double release = n.Holding ? now : double.IsNaN(n.ReleaseAt) ? 0 : n.ReleaseAt <= n.End ? n.ReleaseAt : n.End - (n.ReleaseAt - n.End);
        double h = Math.Abs(press - n.Time), sum = h + Math.Abs(release - n.End);
        int r = release < n.End - mW50 ? 0
            : h <= mW300g * 1.2 && sum <= mW300g * 2.4 ? 320
            : h <= mW300 * 1.1 && sum <= mW300 * 2.2 ? 300
            : h <= mW200 && sum <= mW200 * 2 ? 200
            : h <= mW100 && sum <= mW100 * 2 ? 100 : 50;
        return r > 200 && n.Broken ? 200 : r;
    }

    void ManiaJudge(MNote n, int result)
    {
        n.Done = true;
        n.Result = result;
        mJudged++;
        int comboBefore = combo;
        mLastJudge = (result, Game.NowMs);
        if (result > 0) mLitAt[n.Column] = Game.NowMs;
        if (result == 0) mJudgeTilt = (float)(Random.Shared.NextDouble() * 0.2 - 0.1);
        double hp = map.RawHP;
        switch (result)
        {
            case 320: nGeki++; health.Add(1.1 - hp * 0.1); break;
            case 300: n300++; health.Add(1.0 - hp * 0.1); break;
            case 200: nKatu++; health.Add(0.8 - hp * 0.08); break;
            case 100: n100++; break;
            case 50: n50++; health.Add(-(hp + 1) * 0.32); break;
            default: nMiss++; health.Add(-(hp + 1) * 1.5); break;
        }
        if (result > 0)
        {
            combo++; maxCombo = Math.Max(maxCombo, combo); mComboAt = Game.NowMs;
            if (result < 300 && mods.HasFlag(Mods.Perfect)) Fail(); // (Perfect: 300s and MAXes only)
        }
        else BreakCombo();

        // ScoreV1 in stable's arithmetic: a million split evenly by note, half by judgement and half by the bonus meter
        // (refilled every 384 combo); each judgement adds its part, rounded down, to the score kept from the exact sum
        double perNote = 500000.0 / Math.Max(1, mNotes.Count) * modMul;
        int bonusValue = result switch { 320 or 300 => 32, 200 => 16, 100 => 8, 50 => 4, _ => 0 };
        mBonus = result switch
        {
            320 => mBonus + 2,
            300 => mBonus + 1,
            // (stable divides these by only the score-increasing mods' multiplier, which mania counts as 1)
            200 => mBonus - 8,
            100 => mBonus - 24,
            50 => mBonus - 44,
            _ => -56,
        };
        if (comboBefore != 0 && comboBefore % 384 == 0) mBonus = 100;
        mBonus = Math.Clamp(mBonus, 0, 100);
        double add = Math.Sqrt(mBonus) * bonusValue * perNote / 320;
        add += (double)result * perNote / 320;
        score = (long)mScore + (int)add;
        mScore += add;
        // the last note: stable rounds the exact sum, and makes 999,999 a million
        if (mJudged == mNotes.Count) { score = (long)Math.Round(mScore); if (score == 999999) score = 1000000; }
        if (health.Value <= 0 && !mods.HasFlag(Mods.NoFail)) Fail();
    }

    // the stage as the skin's [Mania] section for this key count sets it (stable's defaults otherwise): column widths
    // and spacing, where it starts, the hit / judgement / combo positions, the images and the columns' colours
    sealed class ManiaLook
    {
        public float Start = 136, HitY = 402, ScoreY = 325, ComboY = 111;
        public float[] X = [], W = [];
        public string[] Note = [], NoteH = [], NoteL = [], NoteT = [], Key = [], KeyD = [];
        public string LightingN = "lightingN", LightingL = "lightingL";
        public float[] LightingNW = [], LightingLW = []; // (0: the column's width)
        public Color?[] Colour = [];
        public string StageLeft = "mania-stage-left", StageRight = "mania-stage-right", StageHint = "mania-stage-hint", StageLight = "mania-stage-light";
        public readonly Dictionary<int, string> Hit = new() { [320] = "mania-hit300g", [300] = "mania-hit300", [200] = "mania-hit200", [100] = "mania-hit100", [50] = "mania-hit50", [0] = "mania-hit0" };
        public float Width => X.Length == 0 ? 0 : X[^1] + W[^1];
    }
    ManiaLook? mLook;

    ManiaLook BuildLook()
    {
        var cfg = skin.Mania.TryGetValue(mKeys, out var d) ? d : null;
        string? M(string k) => cfg != null && cfg.TryGetValue(k, out var v) && v.Length > 0 ? v.Replace('\\', '/') : null;
        float F(string k, float def) => float.TryParse(M(k), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : def;
        float[] List(string k, int n, float def)
        {
            var a = Enumerable.Repeat(def, Math.Max(0, n)).ToArray();
            var parts = M(k)?.Split(',') ?? [];
            for (int i = 0; i < Math.Min(n, parts.Length); i++)
                if (float.TryParse(parts[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f)) a[i] = f;
            return a;
        }
        var look = new ManiaLook
        {
            Start = F("ColumnStart", 136), HitY = Math.Clamp(F("HitPosition", 402), 240, 480), ScoreY = F("ScorePosition", 325), ComboY = F("ComboPosition", 111),
            W = List("ColumnWidth", mKeys, 30),
        };
        var spacing = List("ColumnSpacing", mKeys - 1, 0);
        look.X = new float[mKeys];
        for (int c = 1; c < mKeys; c++) look.X[c] = look.X[c - 1] + look.W[c - 1] + spacing[c - 1];
        string[] Per(Func<int, string> name, Func<int, string> def) => Enumerable.Range(0, mKeys).Select(c => M(name(c)) ?? def(c)).ToArray();
        look.Note = Per(c => $"NoteImage{c}", c => $"mania-note{ManiaColumnKind(c)}");
        look.NoteH = Per(c => $"NoteImage{c}H", c => $"mania-note{ManiaColumnKind(c)}H");
        look.NoteL = Per(c => $"NoteImage{c}L", c => $"mania-note{ManiaColumnKind(c)}L");
        look.NoteT = Per(c => $"NoteImage{c}T", c => $"mania-note{ManiaColumnKind(c)}T");
        look.Key = Per(c => $"KeyImage{c}", c => $"mania-key{ManiaColumnKind(c)}");
        look.KeyD = Per(c => $"KeyImage{c}D", c => $"mania-key{ManiaColumnKind(c)}D");
        look.Colour = Enumerable.Range(0, mKeys).Select(c => M($"Colour{c + 1}") is { } v ? ParseRgba(v) : null).ToArray();
        look.StageLeft = M("StageLeft") ?? look.StageLeft;
        look.StageRight = M("StageRight") ?? look.StageRight;
        look.StageHint = M("StageHint") ?? look.StageHint;
        look.StageLight = M("StageLight") ?? look.StageLight;
        look.LightingN = M("LightingN") ?? look.LightingN;
        look.LightingL = M("LightingL") ?? look.LightingL;
        look.LightingNW = List("LightingNWidth", mKeys, 0);
        look.LightingLW = List("LightingLWidth", mKeys, 0);
        foreach (var (key, result) in new[] { ("Hit300g", 320), ("Hit300", 300), ("Hit200", 200), ("Hit100", 100), ("Hit50", 50), ("Hit0", 0) })
            if (M(key) is { } img) look.Hit[result] = img;
        return look;
    }

    static Color? ParseRgba(string v)
    {
        var p = v.Split(',').Select(s => int.TryParse(s.Trim(), out int x) ? Math.Clamp(x, 0, 255) : -1).ToArray();
        return p.Length >= 3 && p.All(x => x >= 0) ? new Color(p[0], p[1], p[2], p.Length > 3 ? p[3] : 255) : null;
    }

    void ManiaDraw(int w, int h)
    {
        float u = h / 480f, sd = h / 768f;
        var look = mLook ??= BuildLook();
        float hitY = look.HitY, stageW = look.Width;
        float left = look.Start * u;
        float alpha = playfieldAlpha;
        var tint = ColorAlpha(Color.White, alpha);
        UpdateComboBursts();
        DrawManiaBursts(look, left, u, sd, h, alpha);

        for (int c = 0; c < mKeys; c++)
            DrawRectangle((int)(left + look.X[c] * u), 0, (int)Math.Ceiling(look.W[c] * u), h, look.Colour[c] is { } cc ? ColorAlpha(cc, cc.A / 255f * alpha) : ColorAlpha(Color.Black, 0.8f * alpha));
        if (skin.Tex(look.StageLeft) is { } sl) DrawTexturePro(sl.Tex, new Rectangle(0, 0, sl.Tex.Width, sl.Tex.Height), new Rectangle(left - sl.Width * sd, 0, sl.Width * sd, h), Vector2.Zero, 0, tint);
        if (skin.Tex(look.StageRight) is { } sr) DrawTexturePro(sr.Tex, new Rectangle(0, 0, sr.Tex.Width, sr.Tex.Height), new Rectangle(left + stageW * u, 0, sr.Width * sd, h), Vector2.Zero, 0, tint);
        for (int c = 1; c < mKeys; c++) DrawRectangle((int)(left + look.X[c] * u), 0, 1, (int)(hitY * u), ColorAlpha(Color.White, 0.15f * alpha));
        if (skin.Tex(look.StageHint) is { } hint)
            DrawTexturePro(hint.Tex, new Rectangle(0, 0, hint.Tex.Width, hint.Tex.Height), new Rectangle(left, hitY * u - hint.Height * sd / 2, stageW * u, hint.Height * sd), Vector2.Zero, 0, tint);
        else DrawRectangle((int)left, (int)(hitY * u), (int)(stageW * u), 2, tint);

        double now = ManiaDist(time);
        for (int c = 0; c < mKeys; c++)
        {
            float x = left + look.X[c] * u, cw = look.W[c] * u;
            foreach (var n in mColumns[c].Skip(Math.Max(0, mNext[c] - 1)))
            {
                float y = hitY - (float)(ManiaDist(n.Time) - now);
                if (y < -40) break;
                if (n.Done && (!n.Hold || n.Result > 0 || n.End < time)) continue;
                if (n.Hold)
                {
                    float yEnd = hitY - (float)(ManiaDist(n.End) - now);
                    float yHead = n.Holding && !n.Done ? hitY : y;
                    var bodyTint = ColorAlpha(n.Broken || n.Done ? Color.Gray : Color.White, alpha);
                    if ((skin.AnimFrame(look.NoteL[c], Game.NowMs) ?? skin.Tex(look.NoteL[c])) is { } body)
                        DrawTexturePro(body.Tex, new Rectangle(0, 0, body.Tex.Width, body.Tex.Height), new Rectangle(x, yEnd * u, cw, Math.Max(0, (yHead - yEnd) * u)), Vector2.Zero, 0, bodyTint);
                    ManiaNote(skin.Tex(look.NoteT[c]) ?? skin.Tex(look.NoteH[c]) ?? skin.Tex(look.Note[c]), x, yEnd * u, cw, bodyTint, flipY: skin.Tex(look.NoteT[c]) == null);
                    ManiaNote(skin.Tex(look.NoteH[c]) ?? skin.Tex(look.Note[c]), x, yHead * u, cw, bodyTint);
                }
                else ManiaNote(skin.AnimFrame(look.Note[c], Game.NowMs) ?? skin.Tex(look.Note[c]), x, y * u, cw, tint);
            }
            // the key at the bottom, lit while held
            var key = (mDown[c] ? skin.Tex(look.KeyD[c]) : null) ?? skin.Tex(look.Key[c]);
            if (key is { } k)
            {
                float kh = k.Height * (cw / k.Width);
                DrawTexturePro(k.Tex, new Rectangle(0, 0, k.Tex.Width, k.Tex.Height), new Rectangle(x, h - kh, cw, kh), Vector2.Zero, 0, tint);
            }
            if (mDown[c] && skin.Tex(look.StageLight) is { } light)
                DrawTexturePro(light.Tex, new Rectangle(0, 0, light.Tex.Width, light.Tex.Height), new Rectangle(x, hitY * u - light.Height * sd * 1.6f, cw, light.Height * sd * 1.6f), Vector2.Zero, 0, ColorAlpha(Color.White, 0.6f * alpha));
        }

        DrawManiaLighting(look, left, u, sd, alpha);

        // stable's Hidden: black up from the hit position, taller with combo (160 + combo / 2 sprite px, at most 400, giving
        // back 2 a frame after a break), under the skin's stage-hidden fade; it fades in over 37 frames
        // (Fade In: the same cover down from the top, the fade under it turned over)
        if (mods.HasFlag(Mods.Hidden) || mods.HasFlag(Mods.FadeIn))
        {
            double frames = GetFrameTime() * 60;
            float target = Math.Min(400, 160 + combo / 2);
            mHiddenH = mHiddenH > target ? Math.Max(target, mHiddenH - 2 * (float)frames) : target;
            mHiddenA = Math.Min(1, mHiddenA + 0.026667f * (float)frames);
            float a = mHiddenA * alpha, height = mHiddenH / 1.6f * u;
            bool fadeIn = mods.HasFlag(Mods.FadeIn);
            float coverTop = fadeIn ? 0 : hitY * u - height;
            DrawRectangle((int)left, (int)coverTop, (int)Math.Ceiling(stageW * u), (int)Math.Ceiling(height), ColorAlpha(Color.Black, a));
            if (skin.Tex("mania-stage-hidden") is { } fade)
            {
                float fh = fade.Height * 0.72f * sd;
                var src = new Rectangle(0, 0, fade.Tex.Width, fadeIn ? -fade.Tex.Height : fade.Tex.Height);
                DrawTexturePro(fade.Tex, src, new Rectangle(left, fadeIn ? coverTop + height : coverTop - fh, stageW * u, fh), Vector2.Zero, 0, ColorAlpha(Color.White, a));
            }
        }

        // the judgement (ScorePosition 325) popping in and the combo (ComboPosition 111), centred on the stage
        double age = Game.NowMs - mLastJudge.At;
        // stable: the judgement fades in over 20ms; a hit shrinks 1 -> 0.7 over 80ms, a miss settles 1.2 -> 1 over 100ms
        // with a slight random tilt; then 0.7 -> 0.4 (a miss stays) and gone between 180 and 220ms
        if (mLastJudge.Result >= 0 && age < 220)
        {
            string name = look.Hit.TryGetValue(mLastJudge.Result, out var hitImage) ? hitImage : look.Hit[0];
            bool miss = mLastJudge.Result == 0;
            float scale, rot = 0;
            if (miss)
            {
                float e = (float)Easing.Apply(1, Math.Min(1, age / 100));
                scale = 1.2f - 0.2f * e;
                rot = mJudgeTilt * e;
            }
            else scale = age < 80 ? 1 - 0.3f * (float)(age / 80) : age < 180 ? 0.7f : 0.7f - 0.3f * (float)Easing.Apply(2, (age - 180) / 40);
            float a = Math.Min(1, (float)(age / 20)) * (age > 180 ? 1 - (float)((age - 180) / 40) : 1);
            (skin.AnimFrame(name, age) ?? skin.Tex(name))?.Draw(new(left + stageW * u / 2, look.ScoreY * u), sd * scale, ColorAlpha(Color.White, alpha * a), rot * 180 / MathF.PI);
        }
        if (combo > 0)
        {
            string text = combo.ToString();
            float size = 32 * u * (Game.NowMs - mComboAt < 80 ? 1.15f : 1);
            Game.TextBold(text, left + stageW * u / 2 - Game.TextWidth(text, size) / 2, look.ComboY * u, size, tint);
        }
        DrawManiaStars(u, sd, alpha);
    }

    void ManiaBurst()
    {
        mBurstBase = combo - combo % 100;
        var look = mLook ??= BuildLook();
        var rng = Random.Shared;
        for (int i = 0; i < Math.Min(16, combo / 16); i++)
            foreach (int side in new[] { -1, 1 })
                mStars.Add((side < 0 ? look.Start + 10 : look.Start + look.Width - 10, rng.Next(0, 480), side * rng.Next(100, 500), -rng.Next(0, 120),
                    1 + 2 * rng.NextSingle(), -2 + 4 * rng.NextSingle(), -2 + 4 * rng.NextSingle(), Game.NowMs, rng.Next(300, 1000)));
        if (!Config.GetBool("ComboBurst", false)) return;
        var frames = skin.Animation("comboburst-mania", true);
        if (frames.Length == 0) return;
        if (skin.ComboBurstRandom) mBurstNext = rng.Next(frames.Length);
        string style = skin.Mania.TryGetValue(mKeys, out var cfg) && cfg.TryGetValue("ComboBurstStyle", out var s) ? s.Trim() : "1";
        bool right = style is "0" or "Left" ? false : style is "2" or "Both" ? rng.NextDouble() > 0.5 : true;
        mBursts.Add((frames[mBurstNext % frames.Length], right, Game.NowMs));
        mBurstNext = (mBurstNext + 1) % frames.Length;
    }

    // the burst images, under the stage
    void DrawManiaBursts(ManiaLook look, float left, float u, float sd, int h, float alpha)
    {
        double now = Game.NowMs;
        mBursts.RemoveAll(b => now - b.At > 1400);
        float right = left + look.Width * u;
        foreach (var (tex, toRight, at) in mBursts)
        {
            double age = now - at;
            float t = (float)Easing.Apply(22, age / 1400), bw = tex.Width * sd, bh = tex.Height * sd;
            float x = toRight ? left + (right - left) * t : right - (right - left) * t - bw;
            float a = (float)Easing.Apply(2, age / 250) * (1 - (float)Easing.Apply(2, (age - 400) / 1000));
            DrawTexturePro(tex.Tex, new Rectangle(0, 0, toRight ? -tex.Tex.Width : tex.Tex.Width, tex.Tex.Height), new Rectangle(x, h - bh, bw, bh), Vector2.Zero, 0, ColorAlpha(Color.White, a * alpha));
        }
    }

    // the sparks (stable's physics: 8 more units/s down each 60Hz frame)
    void DrawManiaStars(float u, float sd, float alpha)
    {
        double now = Game.NowMs;
        for (mStarTicks += GetFrameTime() * 60; mStarTicks >= 1; mStarTicks--)
            for (int i = 0; i < mStars.Count; i++)
            {
                var st = mStars[i];
                st.Vy += 8; st.X += st.Vx / 60; st.Y += st.Vy / 60;
                mStars[i] = st;
            }
        mStars.RemoveAll(st => now - st.At > st.Life);
        if (skin.Tex("star2") is not { } star) return;
        foreach (var st in mStars)
        {
            float e = (float)Easing.Apply(1, (now - st.At) / st.Life);
            star.Draw(new(st.X * u, st.Y * u), (1 + (st.Grow - 1) * e) * sd, ColorAlpha(Color.White, (1 - e) * alpha), (st.Rot0 + (st.Rot1 - st.Rot0) * e) * 180 / MathF.PI);
        }
    }

    void DrawManiaLighting(ManiaLook look, float left, float u, float sd, float alpha)
    {
        double now = Game.NowMs;
        var burst = skin.Animation(look.LightingN, true);
        var held = skin.Animation(look.LightingL, true) is { Length: > 0 } l ? l : burst;
        BeginBlendMode(BlendMode.Additive);
        for (int c = 0; c < mKeys; c++)
        {
            var centre = new Vector2(left + (look.X[c] + look.W[c] / 2) * u, look.HitY * u);
            // (skins older than 2.5 keep the sprites' own size)
            float Scale(float[] widths) => (skin.Version < 2.5 ? 1 : (widths[c] > 0 ? widths[c] : look.W[c]) / 30) * sd;
            double age = now - mLitAt[c];
            if (burst.Length > 0 && age < 200)
            {
                double delay = Math.Max(170.0 / burst.Length, 16.666666666666668);
                float a = age < 80 ? (float)(age / 80) : 1 - (float)((age - 80) / 120);
                burst[Math.Min(burst.Length - 1, (int)(age / delay))].Draw(centre, Scale(look.LightingNW), ColorAlpha(Color.White, a * alpha));
            }
            // the held hold's light: on from its press, off from its release or judgement
            bool on = mNext[c] < mColumns[c].Count && mColumns[c][mNext[c]] is { Hold: true, Holding: true, Done: false };
            if (on != mHoldLit[c]) { mHoldLit[c] = on; if (on) mHoldOnAt[c] = now; else mHoldOffAt[c] = now; }
            float ha = on ? Math.Min(1, (float)((now - mHoldOnAt[c]) / 80)) : 1 - (float)Math.Min(1, (now - mHoldOffAt[c]) / 120);
            if (held.Length > 0 && ha > 0)
            {
                double delay = Math.Max(170.0 / held.Length, 16.666666666666668);
                held[(int)((now - mHoldOnAt[c]) / delay) % held.Length].Draw(centre, Scale(look.LightingLW), ColorAlpha(Color.White, ha * alpha));
            }
        }
        EndBlendMode();
    }

    void ManiaNote(Sprite? s, float x, float bottomY, float cw, Color tint, bool flipY = false)
    {
        if (s is not { } n) return;
        float nh = n.Height * (cw / n.Width);
        DrawTexturePro(n.Tex, new Rectangle(0, 0, n.Tex.Width, flipY ? -n.Tex.Height : n.Tex.Height), new Rectangle(x, bottomY - nh, cw, nh), Vector2.Zero, 0, tint);
    }

    // stable's default column looks: from each edge 1, 2, 1, 2..., the middle of an odd count special
    string ManiaColumnKind(int c) => ManiaColumnKind(c, mKeys);
    internal static string ManiaColumnKind(int c, int keys)
    {
        if (keys % 2 == 1 && c == keys / 2) return "S";
        return Math.Min(c, keys - 1 - c) % 2 == 0 ? "1" : "2";
    }
}
