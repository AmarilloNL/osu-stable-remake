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
        public bool Done, Held, HeadHit, Broken;
        public double HeadErr;
        public int Result = -1;
        public bool Hold => End > Time;
    }

    readonly List<MNote> mNotes = [];
    List<MNote>[] mColumns = [];
    int[] mNext = [];
    int mKeys;
    KeyboardKey[] mBind = [];
    bool[] mDown = [];
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
        mKeys = Math.Clamp(native ? (int)Math.Round(map.RawCS) : ManiaConvertKeys(map), 1, 10);
        mBind = ManiaDefaultKeys[mKeys - 1];
        mDown = new bool[mKeys];
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
            if (!n.HeadHit && time > n.Time + mW50) { ManiaJudge(n, 0); continue; } // never pressed
            if (n.HeadHit && n.Hold && time >= n.End) // held to the end
                ManiaJudge(n, ManiaHoldResult(n.HeadErr, 0, n.Broken));
        }
    }

    void ManiaPress(int c, double at)
    {
        var list = mColumns[c];
        if (mNext[c] >= list.Count) return;
        var n = list[mNext[c]];
        if (n.HeadHit) return;
        double err = at - n.Time, a = Math.Abs(err);
        if (a > mWMiss) return;
        hitErrors.Add(err);
        if (n.Hold)
        {
            if (a > mW50) { ManiaJudge(n, 0); return; }
            n.HeadHit = true;
            n.HeadErr = err;
            return;
        }
        ManiaJudge(n, a <= mW300g ? 320 : a <= mW300 ? 300 : a <= mW200 ? 200 : a <= mW100 ? 100 : a <= mW50 ? 50 : 0);
    }

    void ManiaRelease(int c, double at)
    {
        var list = mColumns[c];
        if (mNext[c] >= list.Count) return;
        var n = list[mNext[c]];
        if (!n.Hold || !n.HeadHit || n.Done) return;
        double tail = n.End - at;
        if (tail > mW100) { n.Broken = true; BreakCombo(); } // let go too early: the combo breaks and a 50 is the best left
        ManiaJudge(n, ManiaHoldResult(n.HeadErr, tail, n.Broken));
    }

    // stable's hold judgement from the press and the release together
    int ManiaHoldResult(double head, double tail, bool broken)
    {
        double h = Math.Abs(head), sum = h + Math.Abs(tail);
        if (broken) return 50;
        if (h <= mW300g * 1.2 && sum <= mW300g * 2.4) return 320;
        if (h <= mW300 * 1.1 && sum <= mW300 * 2.2) return 300;
        if (h <= mW200 && sum <= mW200 * 2) return 200;
        if (h <= mW100 && sum <= mW100 * 2) return 100;
        return 50;
    }

    void ManiaJudge(MNote n, int result)
    {
        n.Done = true;
        n.Result = result;
        mJudged++;
        mLastJudge = (result, Game.NowMs);
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
            if (!n.Broken) { combo++; maxCombo = Math.Max(maxCombo, combo); mComboAt = Game.NowMs; }
        }
        else BreakCombo();

        // ScoreV1: half of a million split evenly as base score by judgement, half as bonus by the bonus meter
        double perNote = 500000.0 * modMul / Math.Max(1, mNotes.Count);
        int bonusValue = result switch { 320 or 300 => 32, 200 => 16, 100 => 8, 50 => 4, _ => 0 };
        mBonus = result switch
        {
            320 => mBonus + 2,
            300 => mBonus + 1,
            200 => mBonus - 8 / modMul,
            100 => mBonus - 24 / modMul,
            50 => mBonus - 44 / modMul,
            _ => -56 / modMul,
        };
        mBonus = Math.Clamp(mBonus, 0, 100);
        mScore += perNote * result / 320 + perNote * bonusValue * Math.Sqrt(mBonus) / 320;
        score = (long)mScore;
        if (health.Value <= 0 && !mods.HasFlag(Mods.NoFail)) Fail();
    }

    void ManiaDraw(int w, int h)
    {
        float u = h / 480f, sd = h / 768f;
        const float colW = 30, start = 136, hitY = 402;
        float stageW = colW * mKeys;
        float left = start * u;
        float alpha = playfieldAlpha;
        var tint = ColorAlpha(Color.White, alpha);

        DrawRectangle((int)left, 0, (int)(stageW * u), h, ColorAlpha(Color.Black, 0.8f * alpha));
        if (skin.Tex("mania-stage-left") is { } sl) DrawTexturePro(sl.Tex, new Rectangle(0, 0, sl.Tex.Width, sl.Tex.Height), new Rectangle(left - sl.Width * sd, 0, sl.Width * sd, h), Vector2.Zero, 0, tint);
        if (skin.Tex("mania-stage-right") is { } sr) DrawTexturePro(sr.Tex, new Rectangle(0, 0, sr.Tex.Width, sr.Tex.Height), new Rectangle(left + stageW * u, 0, sr.Width * sd, h), Vector2.Zero, 0, tint);
        for (int c = 1; c < mKeys; c++) DrawRectangle((int)(left + c * colW * u), 0, 1, (int)(hitY * u), ColorAlpha(Color.White, 0.15f * alpha));
        if (skin.Tex("mania-stage-hint") is { } hint)
            DrawTexturePro(hint.Tex, new Rectangle(0, 0, hint.Tex.Width, hint.Tex.Height), new Rectangle(left, hitY * u - hint.Height * sd / 2, stageW * u, hint.Height * sd), Vector2.Zero, 0, tint);
        else DrawRectangle((int)left, (int)(hitY * u), (int)(stageW * u), 2, tint);

        double now = ManiaDist(time);
        for (int c = 0; c < mKeys; c++)
        {
            string kind = ManiaColumnKind(c);
            float x = left + c * colW * u, cw = colW * u;
            foreach (var n in mColumns[c].Skip(Math.Max(0, mNext[c] - 1)))
            {
                float y = hitY - (float)(ManiaDist(n.Time) - now);
                if (y < -40) break;
                if (n.Done && (!n.Hold || n.Result > 0 || n.End < time)) continue;
                if (n.Hold)
                {
                    float yEnd = hitY - (float)(ManiaDist(n.End) - now);
                    float yHead = n.HeadHit && !n.Done ? hitY : y;
                    var bodyTint = ColorAlpha(n.Broken || n.Done ? Color.Gray : Color.White, alpha);
                    if ((skin.AnimFrame($"mania-note{kind}L", Game.NowMs) ?? skin.Tex($"mania-note{kind}L")) is { } body)
                        DrawTexturePro(body.Tex, new Rectangle(0, 0, body.Tex.Width, body.Tex.Height), new Rectangle(x, yEnd * u, cw, Math.Max(0, (yHead - yEnd) * u)), Vector2.Zero, 0, bodyTint);
                    ManiaNote(skin.Tex($"mania-note{kind}T") ?? skin.Tex($"mania-note{kind}H") ?? skin.Tex($"mania-note{kind}"), x, yEnd * u, cw, bodyTint, flipY: skin.Tex($"mania-note{kind}T") == null);
                    ManiaNote(skin.Tex($"mania-note{kind}H") ?? skin.Tex($"mania-note{kind}"), x, yHead * u, cw, bodyTint);
                }
                else ManiaNote(skin.AnimFrame($"mania-note{kind}", Game.NowMs) ?? skin.Tex($"mania-note{kind}"), x, y * u, cw, tint);
            }
            // the key at the bottom, lit while held
            var key = (mDown[c] ? skin.Tex($"mania-key{kind}D") : null) ?? skin.Tex($"mania-key{kind}");
            if (key is { } k)
            {
                float kh = k.Height * (cw / k.Width);
                DrawTexturePro(k.Tex, new Rectangle(0, 0, k.Tex.Width, k.Tex.Height), new Rectangle(x, h - kh, cw, kh), Vector2.Zero, 0, tint);
            }
            if (mDown[c] && skin.Tex("mania-stage-light") is { } light)
                DrawTexturePro(light.Tex, new Rectangle(0, 0, light.Tex.Width, light.Tex.Height), new Rectangle(x, hitY * u - light.Height * sd * 1.6f, cw, light.Height * sd * 1.6f), Vector2.Zero, 0, ColorAlpha(Color.White, 0.6f * alpha));
        }

        // the judgement (ScorePosition 325) popping in and the combo (ComboPosition 111), centred on the stage
        double age = Game.NowMs - mLastJudge.At;
        if (mLastJudge.Result >= 0 && age < 400)
        {
            string name = mLastJudge.Result switch { 320 => "mania-hit300g", 300 => "mania-hit300", 200 => "mania-hit200", 100 => "mania-hit100", 50 => "mania-hit50", _ => "mania-hit0" };
            float pop = age < 40 ? 0.8f + 0.2f * (float)(age / 40) : 1;
            skin.Tex(name)?.Draw(new(left + stageW * u / 2, 325 * u), sd * 0.8f * pop, ColorAlpha(Color.White, alpha * (age > 300 ? 1 - (float)((age - 300) / 100) : 1)));
        }
        if (combo > 0)
        {
            string text = combo.ToString();
            float size = 32 * u * (Game.NowMs - mComboAt < 80 ? 1.15f : 1);
            Game.TextBold(text, left + stageW * u / 2 - Game.TextWidth(text, size) / 2, 111 * u, size, tint);
        }
    }

    void ManiaNote(Sprite? s, float x, float bottomY, float cw, Color tint, bool flipY = false)
    {
        if (s is not { } n) return;
        float nh = n.Height * (cw / n.Width);
        DrawTexturePro(n.Tex, new Rectangle(0, 0, n.Tex.Width, flipY ? -n.Tex.Height : n.Tex.Height), new Rectangle(x, bottomY - nh, cw, nh), Vector2.Zero, 0, tint);
    }

    // stable's default column looks: from each edge 1, 2, 1, 2..., the middle of an odd count special
    string ManiaColumnKind(int c)
    {
        if (mKeys % 2 == 1 && c == mKeys / 2) return "S";
        return Math.Min(c, mKeys - 1 - c) % 2 == 0 ? "1" : "2";
    }
}
