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
        public double FirstReleaseAt = double.NaN, HoldFrom, HeldMs; // (and how long it's been held: its body animates only then)
        public double LastPressAt = double.NaN, TickFrom = double.NaN; // stable judges a hold by its last press; ticks every 100ms from it while held
        public bool HeadDone; // Score V2: the head judged on its own
        public bool HeadHit => !double.IsNaN(PressAt);
        public int Result = -1;
        public bool Hold => End > Time;
    }

    readonly List<MNote> mNotes = [];
    List<MNote>[] mColumns = [];
    int[] mNext = [];
    int mKeys;
    bool mSplit; // two stages
    KeyboardKey[] mBind = [], mAltBind = [];
    bool[] mDown = [], mShownDown = [];
    double[] mPressedAt = [], mReleasedAt = []; // (the stage's: song time)
    List<double>? mBars; // the bar lines' times
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
    double mStartAt; // (the keys' names fade from it)
    double[] mAutoRelease = [];
    double mW300g, mW300, mW200, mW100, mW50, mWMiss;
    double mBonus = 100, mScore; // ScoreV1's bonus meter and the running score
    // each stage's last judgement (SeparateScore: a note's shows on its own stage only, else on both)
    readonly (int Result, double At, float Tilt)[] mJudges = [(-1, double.MinValue, 0), (-1, double.MinValue, 0)];

    // the scroll: distance (units) covered from the map's start up to each red / green point
    readonly List<(double Time, double Dist, double PerMs)> mScroll = [];

    // stable's default keys: one 18-key row (A S D F Space J K L ; for the first stage, Q W E R RightAlt U I O P for the
    // second); each stage takes its keys around its middle (Space, RightAlt), skipping that for an even count under 10
    static readonly KeyboardKey[] DefaultKeyRow = [KeyboardKey.A, KeyboardKey.S, KeyboardKey.D, KeyboardKey.F, KeyboardKey.Space, KeyboardKey.J,
        KeyboardKey.K, KeyboardKey.L, KeyboardKey.Semicolon, KeyboardKey.Q, KeyboardKey.W, KeyboardKey.E, KeyboardKey.R, KeyboardKey.RightAlt,
        KeyboardKey.U, KeyboardKey.I, KeyboardKey.O, KeyboardKey.P];
    // (a skin's SpecialStyle: that stage's special column on Shift - left Shift on the first stage, right on the second)
    static KeyboardKey[] DefaultKeys(int keys, bool split)
    {
        var result = new List<KeyboardKey>();
        int style = SpecialStyle(keys);
        int[] stages = split && keys > 1 ? [(keys + 1) / 2, keys / 2] : [keys];
        for (int s = 0; s < stages.Length; s++)
        {
            int special = SpecialColumn(stages[s], s, style), n = special >= 0 ? stages[s] - 1 : stages[s];
            int middle = s == 0 ? 4 : 13, at = Math.Max(0, middle - n / 2);
            var stage = new List<KeyboardKey>();
            for (int i = 0; i < n; i++)
            {
                if (n % 2 == 0 && n < 10 && at is 4 or 13) at++;
                stage.Add(at < DefaultKeyRow.Length ? DefaultKeyRow[at++] : KeyboardKey.Null);
            }
            if (special >= 0) stage.Insert(special, s == 0 ? KeyboardKey.LeftShift : KeyboardKey.RightShift);
            result.AddRange(stage);
        }
        return [.. result];
    }
    // stable's second key for a stage's special column: Ctrl (left on the first stage, right on the second)
    static KeyboardKey[] AltKeys(int keys, bool split)
    {
        var alt = new KeyboardKey[keys];
        int style = SpecialStyle(keys), first = 0;
        int[] stages = split && keys > 1 ? [(keys + 1) / 2, keys / 2] : [keys];
        for (int s = 0; s < stages.Length; first += stages[s++])
            if (SpecialColumn(stages[s], s, style) is >= 0 and var sp) alt[first + sp] = s == 0 ? KeyboardKey.LeftControl : KeyboardKey.RightControl;
        return alt;
    }
    // the skin's [Mania] SpecialStyle for a key count: 1 left, 2 right (0 none)
    internal static int SpecialStyle(int keys) =>
        Game.Skin.Mania.TryGetValue(keys, out var cfg) && cfg.TryGetValue("SpecialStyle", out var st) ? st.Trim() switch { "1" or "Left" => 1, "2" or "Right" => 2, _ => 0 } : 0;
    // a stage's special column under that style (-1: none): only stages of an even count over 4; stable mirrors the
    // style on the second stage
    static int SpecialColumn(int n, int stage, int style) => style == 0 || n <= 4 || n % 2 == 1 ? -1 : (style == 1) == (stage == 0) ? 0 : n - 1;
    // stable splits 10 keys and more (and any Key Coop play) into two stages, unless the skin says otherwise
    internal static bool SplitStages(int keys, bool coop) =>
        keys > 1 && (Game.Skin.Mania.TryGetValue(keys, out var cfg) && cfg.TryGetValue("SplitStages", out var ss) && ss.Trim().Length > 0 ? ss.Trim() == "1" : keys >= 10 || coop);

    // The player's keys for this key count, from stable's cfg: ManiaLayouts{n}K holds layouts split by ';' (each the
    // columns' key names, "None" for a column without one) and ManiaLayoutSelected{n}K picks one (-1 or none: stable's
    // default layout).
    internal static KeyboardKey[] ManiaKeys(int keys, bool? split = null)
    {
        if (keys is < 1 or > 18) return new KeyboardKey[Math.Max(0, keys)]; // (no layout: the columns unbound)
        bool sp = split ?? SplitStages(keys, false);
        var def = DefaultKeys(keys, sp);
        if (sp && keys < 10) return def; // (Key Coop: stable's two-stage keys, not the one-stage layout)
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
        // Key Coop: an osu! map's keys doubled, and two stages (one for each player)
        bool coop = mods.HasFlag(Mods.KeyCoop);
        mKeys = Math.Clamp(native ? (int)Math.Round(map.RawCS) : (mods.Keys() > 0 ? mods.Keys() : ManiaConvertKeys(map)) * (coop ? 2 : 1), 1, 18);
        mSplit = SplitStages(mKeys, coop);
        mBind = ManiaKeys(mKeys, mSplit);
        mAltBind = AltKeys(mKeys, mSplit);
        for (int c = 0; c < mAltBind.Length; c++) if (mBind.Contains(mAltBind[c])) mAltBind[c] = KeyboardKey.Null; // (a layout's own column on Ctrl)
        mDown = new bool[mKeys];
        mShownDown = new bool[mKeys];
        mPressedAt = Enumerable.Repeat(double.NegativeInfinity, mKeys).ToArray();
        mReleasedAt = [.. mPressedAt];
        mStartAt = Game.NowMs;
        mLitAt = Enumerable.Repeat(double.NegativeInfinity, mKeys).ToArray();
        mHoldOnAt = [.. mLitAt]; mHoldOffAt = [.. mLitAt];
        mHoldLit = new bool[mKeys];
        mAutoRelease = Enumerable.Repeat(double.MinValue, mKeys).ToArray();
        mColumns = Enumerable.Range(0, mKeys).Select(_ => new List<MNote>()).ToArray();
        mNext = new int[mKeys];

        // stable's windows: a mania map's from its OD (3ms a point, OD kept in 0-10), a converted map's fixed (two
        // sets, by OD rounded), Score V2's from OD with MAX too. Each one: HardRock / Easy divide / multiply by 1.4,
        // DoubleTime / HalfTime multiply by 1.5 / 0.75 (the same windows in real time), whole ms.
        float od = (float)map.RawOD;
        int W(double w)
        {
            w = mods.HasFlag(Mods.HardRock) ? w / 1.4 : mods.HasFlag(Mods.Easy) ? w * 1.4 : w;
            return (int)(mods.HasFlag(Mods.DoubleTime) ? w * 1.5 : mods.HasFlag(Mods.HalfTime) ? w * 0.75 : w);
        }
        double[] w;
        // (an own replay from before format 6 was played with the remake's earlier windows and column order: kept for it)
        bool legacy = watching is { FromStable: false, FormatVersion: < 6 };
        if (legacy)
        {
            double f = mods.HasFlag(Mods.HardRock) ? 1 / 1.4 : mods.HasFlag(Mods.Easy) ? 1.4 : 1;
            w = [16 * f, (64 - 3 * map.RawOD) * f, (97 - 3 * map.RawOD) * f, (127 - 3 * map.RawOD) * f, (151 - 3 * map.RawOD) * f, (188 - 3 * map.RawOD) * f];
        }
        else if (mods.HasFlag(Mods.ScoreV2))
            w = [W(Beatmap.Range(od, 22.4, 19.4, 13.9)), W(Beatmap.Range(od, 64, 49, 34)), W(Beatmap.Range(od, 97, 82, 67)),
                W(Beatmap.Range(od, 127, 112, 97)), W(Beatmap.Range(od, 151, 136, 121)), (int)Beatmap.Range(od, 188, 173, 158)]; // (miss: no mods, like stable's)
        else if (native)
        {
            float k = Math.Min(10f, Math.Max(0f, 10f - od));
            w = [W(16), W(34f + 3f * k), W(67f + 3f * k), W(97f + 3f * k), W(121f + 3f * k), W(158f + 3f * k)];
        }
        else w = Math.Round((double)od) > 4 ? [W(16), W(34), W(67), W(97), W(121), W(158)] : [W(16), W(47), W(77), W(97), W(121), W(158)];
        (mW300g, mW300, mW200, mW100, mW50, mWMiss) = (w[0], w[1], w[2], w[3], w[4], w[5]);

        // a mania map's notes go to the column under their x; an osu! map is converted (stable's patterns)
        var notes = ManiaConvert.Convert(map, mKeys, native);
        // stable's column order: Random shuffles with the play's seed (kept in its replay), each column drawn until it's
        // one not taken yet, a stage of an even count over 4 keeping its first (special) column at its side; without
        // Random a skin's special column on the right moves the first column there. Mirror then turns it round.
        int[] order = Enumerable.Range(0, mKeys).ToArray();
        bool special = !mSplit && mKeys > 4 && mKeys % 2 == 0, right = special && SpecialStyle(mKeys) == 2;
        if (legacy)
        {
            if (mods.HasFlag(Mods.Random)) { var rng = new Random(seed); order = [.. order.OrderBy(_ => rng.Next())]; }
            if (mods.HasFlag(Mods.Mirror)) order = [.. order.Reverse()];
        }
        else if (mods.HasFlag(Mods.Random))
        {
            var rng = new Random(seed);
            var taken = new bool[mKeys];
            for (int i = 0; i < mKeys; i++)
            {
                int k = special && i == 0 ? (right ? mKeys - 1 : 0) : rng.Next(0, mKeys);
                while (taken[k]) k = rng.Next(0, mKeys);
                (order[i], taken[k]) = (k, true);
            }
        }
        else if (right) order = [.. order.Select(c => c != 0 ? c - 1 : mKeys - 1)];
        if (!legacy && mods.HasFlag(Mods.Mirror)) order = [.. order.Select(c => mKeys - 1 - c)];
        notes = [.. notes.Select(n => n with { Column = order[Math.Clamp(n.Column, 0, mKeys - 1)] })];
        foreach (var (col, t, end) in notes.OrderBy(x => x.Time))
        {
            var list = mColumns[Math.Clamp(col, 0, mKeys - 1)];
            if (list.Count > 0 && list[^1].End + 1 > t) continue; // (one landing on a note still going in its column is dropped)
            var n = new MNote { Column = Math.Clamp(col, 0, mKeys - 1), Time = t, End = end };
            list.Add(n);
            mNotes.Add(n);
        }
        health.CalibrateMania(map, mNotes.Select(n => (n.Time, n.End)).ToList(), map.RawHP, ModeV2); // (stable's gain multiplier for the map)

        // the scroll: stable's 21 units a beat at speed 1, its "BPM scale" off by default: normalised to the map's main BPM
        // (the red point covering the most of it) and the mod's rate, so only BPM changes (and, in mania maps, slider
        // velocity) change the speed; the speed (1-40, 12 by default) multiplies it
        var reds = map.TimingPoints.Where(p => p.Uninherited && p.BeatLength > 0).ToList();
        double mainBeat = reds.Count == 0 ? 500 : reds.Select((p, i) => (p.BeatLength, Len: Math.Min(i + 1 < reds.Count ? reds[i + 1].Time : objs[^1].EndTime, objs[^1].EndTime) - Math.Max(p.Time, objs[0].Time)))
            .MaxBy(x => x.Len).BeatLength;
        // (Scale osu!mania scroll speed with BPM: the speed alone, so faster maps scroll faster)
        double perBeat = Config.GetBool("ManiaSpeedBPMScale", false) ? 21.0 : 21.0 * 100 / (60000 / mainBeat * rate);
        // Remember osu!mania scroll speed per beatmap: this map's own speed, once it has one
        if (Config.GetBool("UsePerBeatmapManiaSpeed", false) && Config.GetInt($"ManiaSpeed_{mapMd5 ??= Replays.Md5Of(map.File)}", 0) is int own and > 0) mSpeed = Math.Clamp(own, 1, 40);
        else mSpeed = Math.Clamp(Config.GetInt("ManiaSpeed", 12), 1, 40);
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
    float mHiddenH = 160, mHiddenA;
    int mSpeed = 12;

    // stable's scroll speed keys (F3 / F4 by default) during a play: saved as the speed (and, when remembering per
    // beatmap, this map's; not while watching)
    void ManiaSpeedKeys()
    {
        int step = Input.IsKeyPressed(Config.Key("keyIncreaseSpeed", KeyboardKey.F4)) ? 1 : Input.IsKeyPressed(Config.Key("keyDecreaseSpeed", KeyboardKey.F3)) ? -1 : 0;
        if (step == 0) return;
        mSpeed = Math.Clamp(mSpeed + step, 1, 40);
        if (playback == null && !auto)
        {
            Config.SetOwn("ManiaSpeed", mSpeed.ToString());
            if (Config.GetBool("UsePerBeatmapManiaSpeed", false)) Config.SetOwn($"ManiaSpeed_{mapMd5 ??= Replays.Md5Of(map.File)}", mSpeed.ToString());
        }
        Notification.Show($"osu!mania speed set to {mSpeed} ({(Config.GetBool("ManiaSpeedBPMScale", false) ? "BPM scale" : "fixed")})", 1750);
    }
    bool ManiaDone => mJudged >= mNotes.Count;

    void ManiaStep()
    {
        // what ran out before a press first (a live frame's time, a replay frame's own): a late press can't land on a note
        // whose window closed earlier in the frame, live and in its replay alike
        if (playback == null && !auto) ManiaTimeouts(time);
        if (playback != null)
            // a replay: stable's held columns as bits in x
            while (pbIndex < playback.Length && playback[pbIndex].Time <= time)
            {
                var f = playback[pbIndex++];
                ManiaTimeouts(f.Time);
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
            bool down = auto ? time < mAutoRelease[c] : playback == null && (Down(mBind[c]) || mAltBind[c] != KeyboardKey.Null && Down(mAltBind[c]));
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
            if (!down && mDown[c]) ManiaRelease(c, auto ? Math.Min(time, mAutoRelease[c]) : time); // (Auto: let go on the end)
            mDown[c] = down;
        }
        if (playback == null && !auto)
        {
            int mask = 0;
            for (int c = 0; c < mKeys; c++) if (mDown[c]) mask |= 1 << c;
            if (mask != mRecorded) { recording.Add(new ReplayFrame(time, mask, 0, 0)); mRecorded = mask; }
        }
        ManiaTimeouts(time);
    }

    // the timeouts run a whole ms at a time, each in turn up to now (older own replays: once at now), so a live play and
    // its replay, stepped at different moments, judge misses and hold ticks across the columns in the same order
    double mPassAt = double.NaN;

    void ManiaTimeouts(double now)
    {
        if (OldModeRules) { ManiaTimeoutPass(now); return; }
        if (double.IsNaN(mPassAt) || now - mPassAt > 600000) mPassAt = Math.Floor(now) - 1; // (a damaged replay's time jumping minutes)
        for (double t = mPassAt + 1; t <= now; t++)
        {
            ManiaTimeoutPass(t);
            mPassAt = t;
        }
    }

    void ManiaTimeoutPass(double now)
    {
        for (int c = 0; c < mKeys; c++)
        {
            var list = mColumns[c];
            while (mNext[c] < list.Count && list[mNext[c]].Done) mNext[c]++;
            // every note whose time has come, like stable (older own replays: the column's first one only)
            for (int j = mNext[c]; j < list.Count && (j == mNext[c] || !OldModeRules && list[j].Time <= now); j++)
                if (!list[j].Done) ManiaTimeout(list[j], now);
        }
    }

    void ManiaTimeout(MNote n, double now)
    {
        // stable: a note not pressed is a miss once its 100 window has passed late (a later press can't reach it)
        if (!n.Hold) { if (OldModeRules ? now > n.Time + mW50 : now >= n.Time + mW100) ManiaJudge(n, 0); return; }
        ManiaTicks(n, now);
        // Score V2: a hold's head not pressed by its 100 window is a miss of its own
        if (ModeV2 && !n.HeadDone && now >= n.Time + mW100) ManiaJudge(n, 0, head: true);
        // stable's hold: not pressed by its 50 window breaks the combo (once; under V2 its head's miss did); it's judged
        // once let go within the tail's 50 window, when still held 50 past the end, or (let go too early or never
        // pressed) once the tail's 100 window has passed (V2: one and a half of it, held or not)
        if (!n.HeadHit && now <= n.Time + mW50) return; // (not pressed yet: nothing before the head's window closes)
        if (!n.HeadHit && !n.Broken && !ModeV2) { n.Broken = true; BreakCombo(); }
        if (ModeV2) { if ((float)now >= (float)n.End + (float)mW100 * 1.5f) ManiaJudge(n, ManiaTailV2(n)); }
        else if (n.Holding ? now >= n.End + mW50 : OldModeRules ? now >= n.End - mW50 : (float)now >= (float)(n.End + mW100))
            ManiaJudge(n, ManiaHoldResult(n, Math.Min(now, n.End + mW50))); // (still held: as at the window's end, whatever the frame)
    }

    // the note a press goes to: the column's first one not judged yet; under Score V2 (stable: no note lock there) the
    // latest one whose time has passed while the press is within min(its 50 window, the next one's time - 1) (stretched by
    // how far the next one is beyond twice the 50 window), else the next one
    MNote? ManiaTarget(int c, double at)
    {
        var list = mColumns[c];
        while (mNext[c] < list.Count && list[mNext[c]].Done) mNext[c]++; // (a note judged earlier in this step is past)
        if (mNext[c] >= list.Count) return null;
        if (!ModeV2) return list[mNext[c]];
        int last = mNext[c];
        while (last + 1 < list.Count && list[last + 1].Time <= at + 300) last++;
        if (last + 1 < list.Count) last++; // (stable looks one beyond its 300ms too)
        MNote? pick = null;
        for (int j = last; j >= mNext[c]; j--)
        {
            var o = list[j];
            if (o.Done) break;
            if (pick == null || at < o.Time) { pick = o; continue; }
            if (at <= Math.Min(o.Time + mW50, pick.Time - 1) + Math.Max(0, pick.Time - o.Time - 2 * mW50)) pick = o;
            break;
        }
        return pick;
    }

    void ManiaPress(int c, double at)
    {
        if (ManiaTarget(c, at) is not { } n) return;
        double err = at - n.Time, a = Math.Abs(err);
        if (n.Hold)
        {
            // stable judges a hold by its last press (an early one mirrored; older own replays: the first); ticks count
            // from each press; under Score V2 the first press judges the head on its own
            if (!n.HeadHit)
            {
                if (err < -mWMiss) return;
                n.PressAt = at;
                if (a <= mW50) hitErrors.Add(err / rate); // (real milliseconds, like osu!'s)
                if (ModeV2 && !n.HeadDone) ManiaJudge(n, a <= mW300g ? 320 : a <= mW300 ? 300 : a <= mW200 ? 200 : a <= mW100 ? 100 : a <= mW50 ? 50 : 0, head: true);
            }
            n.LastPressAt = n.TickFrom = at;
            n.Holding = true;
            n.HoldFrom = at;
            return;
        }
        if (a > mWMiss) return;
        hitErrors.Add(err / rate);
        ManiaJudge(n, a <= mW300g ? 320 : a <= mW300 ? 300 : a <= mW200 ? 200 : a <= mW100 ? 100 : a <= mW50 ? 50 : 0);
    }

    void ManiaRelease(int c, double at)
    {
        // the column's hold being held (under Score V2 a press can skip to a later one)
        var list = mColumns[c];
        MNote? held = null;
        for (int j = mNext[c]; j < list.Count && held == null && list[j].Time <= at + mWMiss; j++)
            if (list[j] is { Hold: true, Holding: true, Done: false } h) held = h;
        if (held is not { } n) return;
        ManiaTicks(n, at);
        n.Holding = false;
        n.TickFrom = double.NaN;
        n.ReleaseAt = at;
        n.HeldMs += at - n.HoldFrom;
        if (double.IsNaN(n.FirstReleaseAt)) n.FirstReleaseAt = at;
        // let go before the tail's 50 window: the combo breaks (each time; older own replays: once) and the hold carries
        // on; within it, it's judged
        if (at < n.End - mW50) { if (!n.Broken || !OldModeRules) { n.Broken = true; BreakCombo(); } }
        else ManiaJudge(n, ModeV2 ? ManiaTailV2(n) : ManiaHoldResult(n, at));
    }

    // stable's hold judgement: the press's error (an early press mirrored late; none, or one too early, counts as the
    // end) and the release's (a late one mirrored early; still held: now) together; let go too early is a miss, and a
    // broken hold gets 200 at best
    int ManiaHoldResult(MNote n, double now)
    {
        double p = OldModeRules ? n.PressAt : n.LastPressAt;
        double press = !n.HeadHit || p < n.Time - mW50 ? n.End - 1 : p >= n.Time ? p : n.Time + (n.Time - p);
        double release = n.Holding ? now : double.IsNaN(n.ReleaseAt) ? 0 : n.ReleaseAt <= n.End ? n.ReleaseAt : n.End - (n.ReleaseAt - n.End);
        double h = Math.Abs(press - n.Time), sum = h + Math.Abs(release - n.End);
        int r = release < n.End - mW50 ? 0
            : h <= mW300g * 1.2 && sum <= mW300g * 2.4 ? 320
            : h <= mW300 * 1.1 && sum <= mW300 * 2.2 ? 300
            : h <= mW200 && sum <= mW200 * 2 ? 200
            : h <= mW100 && sum <= mW100 * 2 ? 100 : 50;
        return r > 200 && n.Broken ? 200 : r;
    }

    // stable's hold ticks: every 100ms from the last press while held, from the hold's start to its end, each healing
    // 0.5 - HP / 20 and (not under Score V2) adding one to the combo
    void ManiaTicks(MNote n, double upTo)
    {
        if (OldModeRules || !n.Holding || double.IsNaN(n.TickFrom) || upTo < n.Time) return;
        double to = Math.Min(upTo, n.End);
        while (n.TickFrom + 100 <= to)
        {
            n.TickFrom += 100;
            health.Add(0.5 - map.RawHP * 0.05);
            if (ModeV2) continue;
            if (combo != 0 && combo % 384 == 0) mBonus = 100; // (ScoreV1's bonus meter refills on every event)
            combo++; maxCombo = Math.Max(maxCombo, combo);
        }
    }

    // Score V2's tail: the release's error / 1.5 against the windows (still held or never let go by the end of the 1.5 x 100
    // window: a miss); one let go too early, or whose head wasn't judged, gets 50 at best
    int ManiaTailV2(MNote n)
    {
        float e = (float)Math.Abs((double.IsNaN(n.ReleaseAt) ? 0 : n.ReleaseAt) - n.End) / 1.5f;
        int r = e <= (float)mW300g ? 320 : e <= (float)mW300 ? 300 : e <= (float)mW200 ? 200 : e <= (float)mW100 ? 100 : e <= (float)mW50 ? 50 : 0;
        return r > 50 && (n.Broken || !n.HeadDone) ? 50 : r;
    }

    void ManiaJudge(MNote n, int result, bool head = false)
    {
        // stable: Perfect turns a note's (or tail's) judgement under a 300 into a miss
        if (!head && !OldModeRules && result is > 0 and < 300 && mods.HasFlag(Mods.Perfect)) result = 0;
        if (head) n.HeadDone = true;
        else
        {
            n.Done = true;
            n.Result = result;
            mJudged++;
        }
        int comboBefore = combo;
        var look = mLook ??= BuildLook();
        var judged = (result, Game.NowMs, result == 0 ? (float)(Random.Shared.NextDouble() * 0.2 - 0.1) : 0);
        int stage = look.Stages.Length > 1 && look.X[n.Column] >= look.Stages[1].X ? 1 : 0;
        for (int s = 0; s < mJudges.Length; s++) if (s == stage || !look.SeparateScore) mJudges[s] = judged;
        if (result > 0) mLitAt[n.Column] = Game.NowMs;
        double hp = map.RawHP;
        switch (result)
        {
            case 320: nGeki++; health.Add(health.mul * (1.1 - hp * 0.1)); break;
            case 300: n300++; health.Add(health.mul * (1.0 - hp * 0.1)); break;
            case 200: nKatu++; health.Add(health.mul * (0.8 - hp * 0.08)); break;
            case 100: n100++; break;
            case 50: n50++; health.Add(-(hp + 1) * 0.32); break;
            default: nMiss++; health.Add(-(hp + 1) * (ModeV2 && n.Hold ? 0.75 : 1.5)); break; // (stable: a hold's miss half under V2)
        }
        if (result > 0)
        {
            combo++; maxCombo = Math.Max(maxCombo, combo);
            if (result < 300 && mods.HasFlag(Mods.Perfect)) Fail(); // (Perfect: 300s and MAXes only)
        }
        else BreakCombo();
        if (ModeV2)
        {
            V2Hit(result >= 300 ? 300 : result);
            if (health.Value <= 0 && HealthFails) Fail();
            return;
        }

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
        if (health.Value <= 0 && HealthFails) Fail();
    }

    // the stage as the skin's [Mania] section for this key count sets it (stable's defaults otherwise): column widths
    // and spacing, where it starts, the hit / judgement / combo positions, the images and the columns' colours
    sealed class ManiaLook
    {
        public float Start = 136, HitY = 402, ScoreY = 325, ComboY = 111;
        public float[] X = [], W = [];
        public string[] Note = [], NoteH = [], NoteL = [], NoteT = [], Key = [], KeyD = [];
        public int[] BodyStyle = []; // NoteBodyStyle{n}: 0 stretched, 2 repeated from the head end, 3 from the tail end, 4 centred
        public bool UpsideDown; // the stage turned over (notes rising to the hit position near the top), and what's flipped then:
        public bool[] KeyFlip = [], NoteFlip = [], HeadFlip = [], BodyFlip = [], TailFlip = [];
        public string LightingN = "lightingN", LightingL = "lightingL", WarningArrow = "mania-warningarrow";
        public Color KeyWarning = Color.Black; // ColourKeyWarning: the keys' names on them at the start
        public float[] LightingNW = [], LightingLW = []; // (0: the column's width)
        public Color[] Colour = [], Light = []; // Colour{n}: the column (black), ColourLight{n}: its stage light (white)
        public float[] LineW = []; // ColumnLineWidth: the lines left of each column and right of the last (2)
        public Color ColumnLine = Color.White, Barline = Color.White, JudgementLine = Color.White, Hold = new(255, 199, 51, 255), Break = Color.Red;
        public float BarlineHeight = 1.2f, LightY = 413, NoteScaleW; // NoteScaleW: the width notes take their height from
        public bool Judgement = true, KeysUnderNotes, SeparateScore = true;
        public int LightFps = 60;
        public string StageBottom = "mania-stage-bottom";
        public string StageLeft = "mania-stage-left", StageRight = "mania-stage-right", StageHint = "mania-stage-hint", StageLight = "mania-stage-light";
        public readonly Dictionary<int, string> Hit = new() { [320] = "mania-hit300g", [300] = "mania-hit300", [200] = "mania-hit200", [100] = "mania-hit100", [50] = "mania-hit50", [0] = "mania-hit0" };
        public float Width => X.Length == 0 ? 0 : X[^1] + W[^1];
        public (float X, float W)[] Stages = []; // stable splits 10 keys and more into two stages (the skin's SplitStages decides)
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
        bool B(string k, bool def) => M(k) is { } v ? v.Trim() is "1" or "true" or "True" : def;
        var look = new ManiaLook
        {
            Start = F("ColumnStart", 136), HitY = Math.Clamp(F("HitPosition", 402), 240, 480), ScoreY = F("ScorePosition", 325), ComboY = F("ComboPosition", 111),
            W = [.. List("ColumnWidth", mKeys, 30).Select(x => Math.Clamp(x, 5, 100))],
        };
        var spacing = List("ColumnSpacing", mKeys - 1, 0);
        for (int c = 0; c < spacing.Length; c++) spacing[c] = Math.Max(spacing[c], -look.W[c + 1]); // (stable's: no further back than the next column)
        // two stages: the first has the (larger) half of the columns, the second starts StageSeparation after it
        bool split = mSplit;
        int half = split ? (mKeys + 1) / 2 : mKeys;
        float separation = Math.Max(5, F("StageSeparation", 40));
        // stable's fit: a stage too wide for the screen (from ColumnStart, keeping ColumnRight free) is narrowed
        float screenW = Game.W / (Game.H / 480f), right = F("ColumnRight", 19);
        float keepRight = Math.Min(right, screenW - look.Start); // (stable's: from where the stage would have started)
        look.Start = Math.Min(look.Start, screenW - right);
        float total = look.W.Sum() + spacing.Sum() + (split ? separation : 0), over = Math.Max(0, look.Start + total + keepRight - screenW);
        float fit = total > 0 ? (total - over) / total : 1;
        if (fit < 1) { for (int c = 0; c < mKeys; c++) look.W[c] *= fit; for (int c = 0; c < spacing.Length; c++) spacing[c] *= fit; separation *= fit; }
        look.X = new float[mKeys];
        for (int c = 1; c < mKeys; c++) look.X[c] = look.X[c - 1] + look.W[c - 1] + spacing[c - 1];
        for (int c = half; c < mKeys; c++) look.X[c] += separation;
        look.Stages = split ? [(0, look.X[half - 1] + look.W[half - 1]), (look.X[half], look.Width - look.X[half])] : [(0, look.Width)];
        string[] Per(Func<int, string> name, Func<int, string> def) => Enumerable.Range(0, mKeys).Select(c => M(name(c)) ?? def(c)).ToArray();
        look.Note = Per(c => $"NoteImage{c}", c => $"mania-note{ManiaColumnKind(c)}");
        look.NoteH = Per(c => $"NoteImage{c}H", c => $"mania-note{ManiaColumnKind(c)}H");
        look.NoteL = Per(c => $"NoteImage{c}L", c => $"mania-note{ManiaColumnKind(c)}L");
        look.NoteT = Per(c => $"NoteImage{c}T", c => $"mania-note{ManiaColumnKind(c)}T");
        // stable's NoteBodyStyle{n}, else NoteBodyStyle, else repeated from the tail end; skins before 2.5 stretch
        static int Style(string? v) => v?.Trim() switch { null => -1, var s when int.TryParse(s, out int i) => i, "Stretch" => 0, "RepeatTop" => 2, "RepeatBottom" => 3, "RepeatTopAndBottom" => 4, _ => -1 };
        look.BodyStyle = [.. Enumerable.Range(0, mKeys).Select(c => skin.Version < 2.5 ? 0 : Style(M($"NoteBodyStyle{c}")) is >= 0 and var s ? s : Style(M("NoteBodyStyle")) is >= 0 and var all ? all : 3)];
        // stable's UpsideDown: everything placed from the other end. Turned over are the hint, the stage lights, the warning
        // arrows, the keys (KeyFlipWhenUpsideDown{n}, else KeyFlipWhenUpsideDown: on) and, in skins from 2.5, the notes
        // (NoteFlipWhenUpsideDown{n}, else for all: on; heads, bodies and tails by their own H, L and T keys) - the tail
        // then the other way to the head, as it is standing up
        look.UpsideDown = B("UpsideDown", false);
        bool? Flip(string k) => M(k) is { } fv ? fv.Trim() is "1" or "true" or "True" : null;
        bool NoteFlip(int c, string part, bool def) => skin.Version >= 2.5 && (Flip($"NoteFlipWhenUpsideDown{c}{part}") ?? Flip($"NoteFlipWhenUpsideDown{part}") ?? def);
        var columns = Enumerable.Range(0, mKeys).ToArray();
        look.KeyFlip = [.. columns.Select(c => Flip($"KeyFlipWhenUpsideDown{c}") ?? Flip("KeyFlipWhenUpsideDown") ?? true)];
        look.NoteFlip = [.. columns.Select(c => NoteFlip(c, "", true))];
        look.HeadFlip = [.. columns.Select(c => NoteFlip(c, "H", look.NoteFlip[c]))];
        look.BodyFlip = [.. columns.Select(c => NoteFlip(c, "L", true))];
        look.TailFlip = [.. columns.Select(c => !NoteFlip(c, "T", true))];
        look.Key = Per(c => $"KeyImage{c}", c => $"mania-key{ManiaColumnKind(c)}");
        look.KeyD = Per(c => $"KeyImage{c}D", c => $"mania-key{ManiaColumnKind(c)}D");
        Color Colour(string k, Color def) => M(k) is { } v && Skin.ParseColour(v, alpha: true) is { } cv ? cv : def;
        look.Colour = [.. Enumerable.Range(0, mKeys).Select(c => Colour($"Colour{c + 1}", Color.Black))];
        look.Light = [.. Enumerable.Range(0, mKeys).Select(c => Colour($"ColourLight{c + 1}", Color.White))];
        look.ColumnLine = Colour("ColourColumnLine", look.ColumnLine);
        look.Barline = Colour("ColourBarline", look.Barline);
        look.JudgementLine = Colour("ColourJudgementLine", look.JudgementLine);
        look.Hold = Colour("ColourHold", look.Hold);
        look.Break = Colour("ColourBreak", look.Break);
        look.LineW = [.. List("ColumnLineWidth", mKeys + 1, 2).Select(x => x > 0 && x < 2 ? 2 : x)]; // (stable: under 2 counts as 2, 0 as none)
        look.BarlineHeight = F("BarlineHeight", 1.2f);
        look.LightY = F("LightPosition", 413);
        look.LightFps = (int)F("LightFramePerSecond", 60) is > 0 and var fps ? fps : 24;
        look.Judgement = B("JudgementLine", true);
        look.KeysUnderNotes = B("KeysUnderNotes", false);
        look.SeparateScore = B("SeparateScore", true);
        look.NoteScaleW = F("WidthForNoteHeightScale", 0) is > 0 and var ns ? ns : look.W.Min();
        look.StageBottom = M("StageBottom") ?? look.StageBottom;
        look.StageLeft = M("StageLeft") ?? look.StageLeft;
        look.StageRight = M("StageRight") ?? look.StageRight;
        look.StageHint = M("StageHint") ?? look.StageHint;
        look.StageLight = M("StageLight") ?? look.StageLight;
        look.LightingN = M("LightingN") ?? look.LightingN;
        look.WarningArrow = M("WarningArrow") ?? look.WarningArrow;
        look.KeyWarning = Colour("ColourKeyWarning", look.KeyWarning);
        look.LightingL = M("LightingL") ?? look.LightingL;
        look.LightingNW = List("LightingNWidth", mKeys, 0);
        look.LightingLW = List("LightingLWidth", mKeys, 0);
        foreach (var (key, result) in new[] { ("Hit300g", 320), ("Hit300", 300), ("Hit200", 200), ("Hit100", 100), ("Hit50", 50), ("Hit0", 0) })
            if (M(key) is { } img) look.Hit[result] = img;
        return look;
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

        // (presses and releases as the stage shows them, in song time)
        for (int c = 0; c < mKeys; c++)
            if (mDown[c] != mShownDown[c]) { mShownDown[c] = mDown[c]; if (mDown[c]) mPressedAt[c] = time; else mReleasedAt[c] = time; }
        Color A(Color c, float a = 1) => ColorAlpha(c, c.A / 255f * a * alpha);
        static Rectangle Src(Sprite sp, bool flip = false) => new(0, 0, sp.Tex.Width, flip ? -sp.Tex.Height : sp.Tex.Height);
        // (UpsideDown: places and spans measured from the bottom instead)
        bool up = look.UpsideDown;
        float mirror = up ? h : 0;
        Rectangle V(Rectangle r) => up ? new(r.X, h - r.Y - r.Height, r.Width, r.Height) : r;
        float Y(float y) => up ? h - y : y;

        // stable's stage, back to front. The columns (Colour{n}: black) and their lines (ColumnLineWidth, 0.74 of it wide;
        // ColourColumnLine) down to the hit position: left of each column, right of the last and (skins from 2.4) right of
        // any with a line after it
        for (int c = 0; c < mKeys; c++)
            DrawRectangleRec(new Rectangle(left + look.X[c] * u, 0, look.W[c] * u, h), A(look.Colour[c]));
        for (int c = 0; c < mKeys; c++)
        {
            float x = left + look.X[c] * u, cw = look.W[c] * u;
            if (look.LineW[c] > 0) DrawRectangleRec(V(new Rectangle(x, 0, 0.74f * look.LineW[c] * sd, hitY * u)), A(look.ColumnLine));
            if (look.LineW[c + 1] > 0 && skin.Version >= 2.4 || c == mKeys - 1)
                DrawRectangleRec(V(new Rectangle(x + cw - (c == mKeys - 1 ? 0.1f * u : 0), 0, 0.74f * look.LineW[c + 1] * sd, hitY * u)), A(look.ColumnLine));
        }
        // the hint across the hit position (0.9 alpha, 1.6026 x 0.9 its height) and the judgement line just under it
        foreach (var (stX, stW) in look.Stages)
        {
            float sl0 = left + stX * u, sw = stW * u * 1.6026f / 1.6f;
            if (skin.Tex(look.StageHint) is { } hint)
            {
                float hh = hint.Height * 0.9f * 1.6026f * sd;
                DrawTexturePro(hint.Tex, Src(hint, up), V(new Rectangle(sl0, hitY * u - hh / 2, sw, hh)), Vector2.Zero, 0, ColorAlpha(Color.White, 0.9f * alpha));
            }
            if (look.Judgement) DrawRectangleRec(V(new Rectangle(sl0, hitY * u, sw, 0.7f * 1.6026f * sd)), A(look.JudgementLine, 0.9f));
        }
        // the stage lights (StageLight, ColourLight{n}): on a press, standing on LightPosition at the image's height; let
        // go, they fade and sink to nothing over 400ms
        var lights = skin.Animation(look.StageLight, true);
        for (int c = 0; c < mKeys && lights.Length > 0; c++)
        {
            float la = mShownDown[c] ? 1 : 1 - (float)Math.Clamp((time - mReleasedAt[c]) / (400 / rate), 0, 1);
            if (la <= 0) continue;
            var fr = lights[(int)(Math.Max(0, time - mPressedAt[c]) / (1000.0 / look.LightFps)) % lights.Length];
            float lh = fr.Height * sd * la;
            DrawTexturePro(fr.Tex, Src(fr, up), V(new Rectangle(left + look.X[c] * u, look.LightY * u - lh, look.W[c] * u, lh)), Vector2.Zero, 0, A(look.Light[c], la));
        }
        // the keys (KeyImage{n}, as wide as the column at their own height), pressed for 80ms after a release too; under
        // the notes with KeysUnderNotes, else over them
        void Keys()
        {
            for (int c = 0; c < mKeys; c++)
            {
                float x = left + look.X[c] * u, cw = look.W[c] * u;
                bool lit = mShownDown[c] || time - mReleasedAt[c] < 80;
                if (((lit ? skin.Tex(look.KeyD[c]) : null) ?? skin.Tex(look.Key[c])) is not { } k) continue;
                float kh = k.Height * sd;
                DrawTexturePro(k.Tex, Src(k, up && look.KeyFlip[c]), V(new Rectangle(x, h - kh, cw, kh)), Vector2.Zero, 0, tint);
                // stable: the column's key on it as the play starts, fading out from 0.5 to 3.5 seconds (not watching)
                double shown = Game.NowMs - mStartAt;
                if (playback == null && !auto && shown < 3500 && c < mBind.Length && mBind[c] != KeyboardKey.Null)
                {
                    string name = KeyName(mBind[c]);
                    float size = look.W[c] / 2 * u, ka = shown < 500 ? 1 : 1 - (float)((shown - 500) / 3000);
                    Game.TextBold(name, x + cw / 2 - Game.TextWidth(name, size) / 2, Y(h - kh / 2) - size / 2, size, ColorAlpha(look.KeyWarning, look.KeyWarning.A / 255f * ka * alpha));
                }
            }
        }
        if (look.KeysUnderNotes) Keys();
        foreach (var (stX, stW) in look.Stages)
        {
            float sl0 = left + stX * u;
            if (skin.Tex(look.StageLeft) is { } sl) DrawTexturePro(sl.Tex, Src(sl), new Rectangle(sl0 - sl.Width * sd, 0, sl.Width * sd, h), Vector2.Zero, 0, tint);
            if (skin.Tex(look.StageRight) is { } sr) DrawTexturePro(sr.Tex, Src(sr), new Rectangle(sl0 + stW * u, 0, sr.Width * sd, h), Vector2.Zero, 0, tint);
        }

        double now = ManiaDist(time);
        // the bar lines (ColourBarline, BarlineHeight thick), scrolling like notes; IgnoreBarline leaves them out
        if (!Config.GetBool("IgnoreBarline", false))
        {
            mBars ??= ManiaBars();
            float bh = look.BarlineHeight * sd;
            foreach (double bt in mBars)
            {
                float y = hitY - (float)(ManiaDist(bt) - now);
                if (y > 490) continue;
                if (y < -10) break;
                foreach (var (stX, stW) in look.Stages) DrawRectangleRec(V(new Rectangle(left + stX * u, y * u - bh / 2, stW * u, bh)), A(look.Barline));
            }
        }
        // stable's warning arrows: down the middle of the stage like notes, 1, 2 and 3 seconds before the first one (not
        // before the first timing point); skins from 2.4 size them to the columns (at most 1x)
        if (mNotes.Count > 0 && mScroll.Count > 0 && skin.Tex(look.WarningArrow) is { } arrow)
        {
            float scale = (skin.Version >= 2.4 ? Math.Min(1, look.Width / (30f * mKeys)) : 1) * sd;
            double first = mNotes.Min(n => n.Time);
            for (int k = 1; k <= 3 && first - 1000 * k > mScroll[0].Time; k++)
            {
                float y = hitY - (float)(ManiaDist(first - 1000 * k) - now);
                if (y < -40 || y > 520) continue;
                foreach (var (stX, stW) in look.Stages) arrow.Draw(new(left + (stX + stW / 2) * u, Y(y * u - arrow.Height * scale / 2)), scale, tint, flipY: up);
            }
        }
        float noteScale = look.NoteScaleW * u; // (the width notes take their height from)
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
                    // stable: a pressed hold's head stays on the hit position to its end; one pressed outside the 50 window
                    // moves on once let go, from the first release. The body (the tail's centre to the head's) and the tail
                    // (flipped) scroll on regardless, cut off at the head's centre.
                    float yEnd = hitY - (float)(ManiaDist(n.End) - now);
                    bool pinned = n.Holding && !n.Done || n.HeadHit && Math.Abs(n.PressAt - n.Time) <= mW50;
                    float yHead = pinned ? hitY : n.HeadHit && !double.IsNaN(n.FirstReleaseAt) ? hitY - (float)(ManiaDist(Math.Max(n.Time, n.FirstReleaseAt)) - now) : y;
                    var bodyTint = ColorAlpha(n.Broken || n.Done ? Color.Gray : Color.White, alpha);
                    var head = skin.Tex(look.NoteH[c]) ?? skin.Tex(look.Note[c]);
                    float half = head is { } hs ? hs.Height * (noteScale / hs.Width) / 2 : 0, cut = yHead * u - half;
                    // (the body's frames, 30ms each, only run while it's held)
                    if (skin.Animation(look.NoteL[c], true) is { Length: > 0 } frames)
                    {
                        double held = n.HeldMs + (n.Holding && !n.Done ? time - n.HoldFrom : 0);
                        ManiaBody(frames[(int)(Math.Max(0, held) / 30) % frames.Length], x, cw, yEnd * u - half, y * u - half, cut, look.BodyStyle[c], sd, up && look.BodyFlip[c], mirror, bodyTint);
                    }
                    ManiaNote(skin.Tex(look.NoteT[c]) ?? head, x, yEnd * u, cw, noteScale, bodyTint, up ? look.TailFlip[c] : true, mirror, clip: cut);
                    ManiaNote(head, x, yHead * u, cw, noteScale, bodyTint, up && look.HeadFlip[c], mirror);
                }
                else ManiaNote(skin.AnimFrame(look.Note[c], Game.NowMs) ?? skin.Tex(look.Note[c]), x, y * u, cw, noteScale, tint, up && look.NoteFlip[c], mirror);
            }
        }
        if (!look.KeysUnderNotes) Keys();
        // the stage's bottom (StageBottom, 1.6x, 60fps) over the keys
        if (skin.Animation(look.StageBottom, true) is { Length: > 0 } bottom)
        {
            var bf = bottom[(int)(Game.NowMs / 16.666666) % bottom.Length];
            float bw = bf.Width * 1.6f * sd, bh2 = bf.Height * 1.6f * sd;
            foreach (var (stX, stW) in look.Stages)
                DrawTexturePro(bf.Tex, Src(bf), V(new Rectangle(left + (stX + stW / 2) * u - bw / 2, h - bh2, bw, bh2)), Vector2.Zero, 0, tint);
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
            foreach (var (stX, stW) in look.Stages)
            {
                float sl0 = left + stX * u;
                DrawRectangleRec(V(new Rectangle((int)sl0, (int)coverTop, (int)Math.Ceiling(stW * u), (int)Math.Ceiling(height))), ColorAlpha(Color.Black, a));
                if (skin.Tex("mania-stage-hidden") is { } fade)
                {
                    float fh = fade.Height * 0.72f * sd;
                    var src = new Rectangle(0, 0, fade.Tex.Width, fadeIn != up ? -fade.Tex.Height : fade.Tex.Height);
                    DrawTexturePro(fade.Tex, src, V(new Rectangle(sl0, fadeIn ? coverTop + height : coverTop - fh, stW * u, fh)), Vector2.Zero, 0, ColorAlpha(Color.White, a));
                }
            }
        }

        // the judgement (ScorePosition 325) popping in and the combo (ComboPosition 111), centred on the stage
        // stable: the judgement fades in over 20ms; a hit shrinks 1 -> 0.7 over 80ms, a miss settles 1.2 -> 1 over 100ms
        // with a slight random tilt; then 0.7 -> 0.4 (a miss stays) and gone between 180 and 220ms
        for (int s = 0; s < look.Stages.Length && s < mJudges.Length; s++)
        {
            var (result, at, tilt) = mJudges[s];
            double age = Game.NowMs - at;
            if (result < 0 || age >= 220) continue;
            string name = look.Hit.TryGetValue(result, out var hitImage) ? hitImage : look.Hit[0];
            float scale, rot = 0;
            if (result == 0)
            {
                float e = (float)Easing.Apply(1, Math.Min(1, age / 100));
                scale = 1.2f - 0.2f * e;
                rot = tilt * e;
            }
            else scale = age < 80 ? 1 - 0.3f * (float)(age / 80) : age < 180 ? 0.7f : 0.7f - 0.3f * (float)Easing.Apply(2, (age - 180) / 40);
            float a = Math.Min(1, (float)(age / 20)) * (age > 180 ? 1 - (float)((age - 180) / 40) : 1);
            var (stX, stW) = look.Stages[s];
            (skin.AnimFrame(name, age) ?? skin.Tex(name))?.Draw(new(left + (stX + stW / 2) * u, Y(look.ScoreY * u)), sd * scale, ColorAlpha(Color.White, alpha * a), rot * 180 / MathF.PI);
        }
        ManiaCombo(look, left, u, sd, alpha);
        DrawManiaStars(u, sd, alpha);
    }

    // stable's mania combo: the skin's combo font centred on each stage at ComboPosition (skins from 2.4 sized to the
    // columns, at most 1x; older ones 1.28x). It counts up a step a frame, each step stretching it 1.4x tall to settle
    // over 300ms; it takes the hold colour (ColourHold) over 300ms while a hold note is held. A break leaves the old
    // number showing as it fades (120ms) and throws a copy off in ColourBreak, growing 4x and fading over 200ms.
    int mComboShown;
    bool mComboVisible, mComboBreakArmed, mComboHolding;
    double mComboStepAt = double.NegativeInfinity, mComboVisibleAt = double.NegativeInfinity, mComboHoldAt = double.NegativeInfinity;
    readonly List<(string Text, double At)> mComboBreaks = [];

    void ManiaCombo(ManiaLook look, float left, float u, float sd, float alpha)
    {
        double now = Game.NowMs;
        if (combo > mComboShown) { mComboShown++; mComboStepAt = now; mComboBreakArmed = true; }
        else if (combo < mComboShown)
        {
            if (combo != 0) mComboShown = combo;
            else if (mComboBreakArmed) { mComboBreakArmed = false; mComboBreaks.Add((mComboShown.ToString(), now)); }
        }
        if (combo > 0 && !mComboVisible) { mComboVisible = true; mComboVisibleAt = now; }
        else if (mComboVisible && combo == 0) { mComboVisible = false; mComboVisibleAt = now; }
        bool holding = mHoldLit.Any(x => x);
        if (holding != mComboHolding) { mComboHolding = holding; mComboHoldAt = now; }
        float hold = (float)Math.Clamp((now - mComboHoldAt) / 300, 0, 1);
        if (!mComboHolding) hold = 1 - hold;
        var colour = new Color((byte)(255 + (look.Hold.R - 255) * hold), (byte)(255 + (look.Hold.G - 255) * hold), (byte)(255 + (look.Hold.B - 255) * hold), (byte)255);
        float shown = (float)Math.Clamp((now - mComboVisibleAt) / 120, 0, 1), a = mComboVisible ? shown : 1 - shown;
        float scale = skin.Version >= 2.4 ? Math.Min(look.W.Average() / 30, 1) : 1.28f;
        float stretch = 1 + 0.4f * (1 - (float)Easing.Apply(1, Math.Clamp((now - mComboStepAt) / 300, 0, 1)));
        float glyph = skin.Glyph(skin.ComboPrefix, '0')?.Height ?? 0;
        void Draw(string text, float s, float sy, Color c)
        {
            foreach (var (stX, stW) in look.Stages)
            {
                float cx = left + (stX + stW / 2) * u, cy = (look.UpsideDown ? 480 - look.ComboY : look.ComboY) * u;
                if (!skin.DrawFont(skin.ComboPrefix, skin.ComboOverlap, text, cx, cy - glyph * s * sd * sy / 2, s * sd, 0.5f, c, scaleY: sy))
                {
                    float size = 32 * u * s;
                    Game.TextBold(text, cx - Game.TextWidth(text, size) / 2, cy - size * sy / 2, size, c);
                }
            }
        }
        if (a > 0 && mComboShown > 0) Draw(mComboShown.ToString(), scale, stretch, ColorAlpha(colour, a * alpha));
        mComboBreaks.RemoveAll(b => now - b.At > 200);
        foreach (var (text, at) in mComboBreaks)
        {
            float t = (float)((now - at) / 200);
            Draw(text, 1 + 3 * t, 1, ColorAlpha(look.Break, look.Break.A / 255f * 0.8f * (1 - t) * alpha));
        }
    }

    // stable's bar lines: from each red point a meter's worth of beats apart up to the next (its first left out where
    // it omits it), the last running to the end of the notes
    List<double> ManiaBars()
    {
        var bars = new List<double>();
        var reds = map.TimingPoints.Where(p => p.Uninherited && p.BeatLength >= 0).OrderBy(p => p.Time).ToList();
        double end = mNotes.Count > 0 ? mNotes.Max(n => n.Hold ? n.End : n.Time) + 1 : 0;
        for (int j = 0; j < reds.Count; j++)
        {
            double to = j + 1 < reds.Count ? reds[j + 1].Time - 1 : end;
            for (double t = reds[j].Time; t < to;)
            {
                if (t > reds[j].Time || !reds[j].OmitFirstBarLine) bars.Add(t);
                double step = reds[j].BeatLength * reds[j].Meter;
                if (step < 0.001) break;
                t += step;
            }
        }
        bars.Sort();
        return bars;
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

    internal static string KeyName(KeyboardKey k) => k switch
    {
        >= KeyboardKey.A and <= KeyboardKey.Z or >= KeyboardKey.Zero and <= KeyboardKey.Nine => ((char)k).ToString(),
        >= KeyboardKey.Kp0 and <= KeyboardKey.Kp9 => $"NumPad{k - KeyboardKey.Kp0}",
        KeyboardKey.Semicolon => ";", KeyboardKey.Comma => ",", KeyboardKey.Period => ".", KeyboardKey.Slash => "/",
        KeyboardKey.Apostrophe => "'", KeyboardKey.LeftBracket => "[", KeyboardKey.RightBracket => "]", KeyboardKey.Backslash => "\\",
        KeyboardKey.LeftShift => "LShift", KeyboardKey.RightShift => "RShift", KeyboardKey.LeftControl => "LCtrl", KeyboardKey.RightControl => "RCtrl",
        _ => k.ToString(),
    };

    void DrawManiaLighting(ManiaLook look, float left, float u, float sd, float alpha)
    {
        double now = Game.NowMs;
        var burst = skin.Animation(look.LightingN, true);
        var held = skin.Animation(look.LightingL, true) is { Length: > 0 } l ? l : burst;
        BeginBlendMode(BlendMode.Additive);
        for (int c = 0; c < mKeys; c++)
        {
            var centre = new Vector2(left + (look.X[c] + look.W[c] / 2) * u, (look.UpsideDown ? 480 - look.HitY : look.HitY) * u);
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

    // a note: as wide as its column, as tall as the image is at scaleW wide (stable: the narrowest column's width, or
    // WidthForNoteHeightScale); nothing of it below clip
    // (placed as on a standing stage; mirror: the screen's height, turning it over)
    void ManiaNote(Sprite? s, float x, float bottomY, float cw, float scaleW, Color tint, bool flip = false, float mirror = 0, float clip = float.MaxValue)
    {
        if (s is not { } n) return;
        float nh = n.Height * (scaleW / n.Width), top = bottomY - nh;
        ManiaPart(n.Tex, 0, n.Tex.Height, new Rectangle(x, top, cw, nh), top, Math.Min(bottomY, clip), flip, mirror, tint);
    }

    // part of a sprite: full is where all of it goes on a standing stage, from..to the rows of that shown; srcY/srcH the
    // image rows it spans, top down (bottom up when flipped). mirror (the screen's height) turns the stage over.
    static void ManiaPart(Texture2D tex, float srcY, float srcH, Rectangle full, float from, float to, bool flip, float mirror, Color tint)
    {
        if (to <= from || full.Height <= 0) return;
        if (mirror > 0) { (from, to) = (mirror - to, mirror - from); full.Y = mirror - full.Y - full.Height; }
        float t0 = (from - full.Y) / full.Height, t1 = (to - full.Y) / full.Height;
        float a = flip ? 1 - t1 : t0, b = flip ? 1 - t0 : t1;
        DrawTexturePro(tex, new Rectangle(0, srcY + a * srcH, tex.Width, (flip ? -1 : 1) * (b - a) * srcH), new Rectangle(full.X, from, full.Width, to - from), Vector2.Zero, 0, tint);
    }

    // a hold's body from top (the tail's centre) to bottom (the head's), nothing of it below clip. Stable's NoteBodyStyle 0
    // stretches the image over it; the others repeat it at its own height, starting at the tail end (3, or any other),
    // the head end (2) or centred (4).
    readonly HashSet<uint> mRepeating = []; // (textures set to repeat downwards)
    void ManiaBody(Sprite b, float x, float cw, float top, float bottom, float clip, int style, float sd, bool flip, float mirror, Color tint)
    {
        float len = bottom - top;
        if (len <= 0) return;
        var full = new Rectangle(x, top, cw, len);
        if (style == 0) { ManiaPart(b.Tex, 0, b.Tex.Height, full, top, Math.Min(bottom, clip), flip, mirror, tint); return; }
        if (mRepeating.Add(b.Tex.Id)) Rlgl.TextureParameters(b.Tex.Id, 0x2803, 0x2901); // GL_TEXTURE_WRAP_T: GL_REPEAT
        float k = b.Tex.Height / b.Height, imageLen = len / sd; // (texture pixels per image pixel; the length in image pixels)
        float from = style switch { 2 => (int)(b.Height - imageLen), 4 => (int)((b.Height - imageLen) / 2), _ => 0 };
        ManiaPart(b.Tex, from * k, imageLen * k, full, top, Math.Min(bottom, clip), flip, mirror, tint);
    }

    // stable's default column looks: from each edge 1, 2, 1, 2..., the middle of an odd count special
    string ManiaColumnKind(int c) => ManiaColumnKind(c, mKeys, mSplit);
    // a column's kind (1, 2 or S, the middle one), stage by stage
    internal static string ManiaColumnKind(int c, int keys, bool? split = null)
    {
        int style = SpecialStyle(keys);
        if (split ?? SplitStages(keys, false))
        {
            int half = (keys + 1) / 2;
            return c < half ? StageColumnKind(c, half, 0, style) : StageColumnKind(c - half, keys - half, 1, style);
        }
        return StageColumnKind(c, keys, 0, style);
    }
    static string StageColumnKind(int c, int n, int stage, int style)
    {
        int special = SpecialColumn(n, stage, style);
        if (special >= 0) return c == special ? "S" : (special == 0 ? c % 2 == 0 : c % 2 == 1) ? "2" : "1"; // (the special column's side: the rest alternate from it)
        if (n % 2 == 1 && c == n / 2) return "S";
        return Math.Min(c, n - 1 - c) % 2 == 0 ? "1" : "2";
    }
}
