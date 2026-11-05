using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// osu!taiko, as stable plays it. Notes scroll right to left along the bar at their own timing point's speed
// (100 x slider multiplier x 1.4 osu!px a beat) towards the hit target at (160, 125) in a left-aligned playfield
// (screen units: x 160, y 197). Centre (don) and rim (kat) keys, big notes for both hands, drumrolls and swells.
sealed partial class Player
{
    internal enum TKind { Don, Kat, Roll, Swell }

    internal sealed class TNote
    {
        public TKind Kind;
        public bool Big;
        public double Time, End, Speed; // Speed: units a millisecond
        public bool Done;
        public int Result = -1;
        public double JudgedAt;
        public bool Pending; public double FirstHitAt; public int FirstKey; // a big note waiting for the other hand (30ms)
        public double TickSpacing; public int TicksHit; public readonly List<double> Ticks = []; // drumroll
        public int HitsNeeded, Hits, LastSwellRim = -1; // swell
        public int AutoNext; // Auto's next tick / swell hit
        public double RollEnd => End + TickSpacing / 2; // (the last tick can sit up to half a tick past the end)
    }

    readonly List<TNote> tNotes = [];
    readonly List<(double Time, double Speed)> tBars = [];
    int tNext; // the first note not yet judged
    double tGreat, tGood, tMiss, tHpMul, tTravel;
    readonly double[] tDrumAt = [double.MinValue, double.MinValue, double.MinValue, double.MinValue]; // outer L, inner L, inner R, outer R
    readonly KeyboardKey[] tKeys = new KeyboardKey[4];
    readonly bool[] tHeld = new bool[4];
    static readonly byte[] TaikoBit = [2, 1, 4, 8]; // replays (stable's): left rim M2, left centre M1, right centre K1, right rim K2
    byte tRecorded;
    (int Result, bool Big, double At) tLastJudge = (-1, false, double.MinValue);
    // stable throws a hit note at the health bar's fill: each 60Hz tick it closes 6.5% of the gap (plus 2), shrinks to 96.5%
    // and rises by its speed / 11.5, the speed falling 7 a tick from 169; under 70 it fades out over 300ms, under -32 it's gone
    sealed class TFly { public float X, Y, Scale, Vy, Alpha = 1; public double FadeAt = double.NaN; public Color Colour; public bool Big; }
    readonly List<TFly> tFlying = [];
    double tFlyTicks;
    int tAutoHand;

    const float TaikoHitX = 160, TaikoLaneY = 197; // screen units

    void TaikoSetup()
    {
        health.Value = 0; // stable: the bar starts empty and only hits fill it; a pass needs it over half at the end
        double od = map.OD;
        tGreat = (int)Beatmap.Range(od, 50, 35, 20);
        tGood = (int)Beatmap.Range(od, 120, 80, 50);
        tMiss = (int)Beatmap.Range(od, 135, 95, 70);
        tKeys[0] = Config.Key("keyTaikoOuterLeft", KeyboardKey.Z);
        tKeys[1] = Config.Key("keyTaikoInnerLeft", KeyboardKey.X);
        tKeys[2] = Config.Key("keyTaikoInnerRight", KeyboardKey.C);
        tKeys[3] = Config.Key("keyTaikoOuterRight", KeyboardKey.V);

        float widthUnits = Game.W / (Game.H / 480f);
        bool hidden = mods.HasFlag(Mods.Hidden) && !mods.HasFlag(Mods.HardRock);
        tTravel = hidden ? 600 : (int)Math.Ceiling(widthUnits) - 40;
        // stable: HardRock scrolls 1.4x faster (times the screen's width over 640), Easy 0.8x
        double speedMul = mods.HasFlag(Mods.HardRock) ? 1.4 * (int)Math.Ceiling(widthUnits) / 640.0 : mods.HasFlag(Mods.Easy) ? 0.8 : 1;
        tNotes.AddRange(TaikoConvert(map, mods, speedMul));
        double Speed(double t) => 100 * map.SliderMultiplier * 1.4 / (map.UninheritedAt(t).BeatLength / map.SliderVelocityAt(t)) * speedMul; // (bar lines)
        int circles = Math.Max(1, tNotes.Count(n => n.Kind is TKind.Don or TKind.Kat));
        tHpMul = 200.0 / (0.36 * circles * Beatmap.Range(map.HP, 0.5, 0.75, 0.98));

        // bar lines: every meter's worth of beats from the first red point (wound back before the start), each red
        // point starting over, and none at a point that omits its first bar line
        var reds = map.TimingPoints.Where(p => p.Uninherited && p.BeatLength > 0).ToList();
        if (reds.Count > 0)
        {
            double last = tNotes.Count > 0 ? tNotes.Max(n => n.End) + 1 : 0;
            int k = 0;
            double bar = reds[0].BeatLength * reds[0].Meter, t = reds[0].Time;
            t -= bar * (int)(t / bar);
            if (t < 0) t += bar;
            while (t <= last)
            {
                var p = reds[k];
                if (t > p.Time || !p.OmitFirstBarLine) tBars.Add((t, Speed(t)));
                double step = p.BeatLength * p.Meter;
                if (step < 0.001) { if (k + 1 >= reds.Count) break; t = reds[++k].Time; continue; }
                t += step;
                if (k + 1 < reds.Count && t >= reds[k + 1].Time) t = reds[++k].Time;
            }
        }
    }

    // stable's taiko conversion (an osu! map's sliders become hits or drumrolls, spinners swells), each note with its
    // scroll speed (units a ms, times speedMul)
    internal static List<TNote> TaikoConvert(Beatmap map, Mods mods, double speedMul = 1)
    {
        var notes = new List<TNote>();
        double sm = map.SliderMultiplier * 1.4;
        double BeatSv(double t) => map.UninheritedAt(t).BeatLength / map.SliderVelocityAt(t);
        double Speed(double t) => 100 * sm / BeatSv(t) * speedMul; // units a ms

        bool native = map.Mode == 1;
        foreach (var o in map.Objects)
        {
            bool big = (o.HitSound & 4) != 0;
            if (o.Type == ObjType.Circle)
                notes.Add(new TNote { Kind = (o.HitSound & 10) != 0 ? TKind.Kat : TKind.Don, Big = big, Time = o.Time, End = o.Time, Speed = Speed(o.Time) });
            else if (o.Type == ObjType.Slider)
            {
                // stable's conversion: the slider's length x 1.4 over its spans gives a drumroll's length; an osu! slider
                // short enough (under two beats at the slider's speed) becomes hits every min(beat / tick rate,
                // span) instead, their sounds taken from the slider's edges in turn. The arithmetic is stable's to the
                // bit (1.4 is a float; a duration of 999.99... is 999), which decides borderline sliders.
                const double vm = 1.4f;
                double dist = o.Length;
                dist *= vm;
                dist *= o.Slides;
                var red = map.UninheritedAt(o.Time);
                var point = map.PointAt(o.Time);
                double beat = red.BeatLength * (!point.Uninherited && point.Time >= red.Time && point.BeatLength < 0 ? Math.Clamp((float)-point.BeatLength, 10f, 10000f) / 100.0 : 1);
                double taikoSpeed = 100 * (map.SliderMultiplier * vm) / map.SliderTickRate * map.SliderTickRate;
                double end = o.Time + (int)(dist / taikoSpeed * beat);
                double osuSpeed = taikoSpeed * (1000f / beat);
                double splitBeat = map.FormatVersion >= 8 ? red.BeatLength : beat;
                double spacing = Math.Min(splitBeat / map.SliderTickRate, (double)(int)(dist / taikoSpeed * beat) / o.Slides);
                if (spacing > 0 && !native && dist / osuSpeed * 1000 < 2 * splitBeat)
                {
                    int edge = 0;
                    for (double t = o.Time; t <= end + spacing / 8; t += spacing)
                    {
                        int sound = o.EdgeSounds.Length == 0 ? o.HitSound : o.EdgeSounds[edge];
                        if (o.EdgeSounds.Length > 0) edge = (edge + 1) % o.EdgeSounds.Length;
                        notes.Add(new TNote { Kind = (sound & 10) != 0 ? TKind.Kat : TKind.Don, Big = (sound & 4) != 0, Time = (int)t, End = (int)t, Speed = Speed((int)t) });
                    }
                }
                else
                {
                    var roll = new TNote { Kind = TKind.Roll, Big = big, Time = o.Time, End = end, Speed = Speed(o.Time) };
                    roll.TickSpacing = map.UninheritedAt(o.Time).BeatLength / (map.SliderTickRate == 3 ? 3 : 4);
                    if (roll.TickSpacing > 0)
                        for (double t = o.Time; t < end + roll.TickSpacing / 2; t += roll.TickSpacing) roll.Ticks.Add(t);
                    notes.Add(roll);
                }
            }
            else if (o.Type == ObjType.Spinner)
            {
                // stable: the spinner's spins x 1.65 hits (x 0.75 under DoubleTime, x 1.5 under HalfTime)
                int hits = (int)Math.Max(1f, (int)((float)(o.EndTime - o.Time) / 1000f * map.SpinsPerSecond) * 1.65f);
                if (mods.HasFlag(Mods.DoubleTime)) hits = Math.Max(1, (int)(hits * 0.75f));
                if (mods.HasFlag(Mods.HalfTime)) hits = Math.Max(1, (int)(hits * 1.5f));
                notes.Add(new TNote { Kind = TKind.Swell, Time = o.Time, End = o.EndTime, HitsNeeded = hits, Speed = Speed(o.Time) });
            }
        }
        notes.Sort((a, b) => a.Time.CompareTo(b.Time));
        return notes;
    }

    bool TaikoDone => tNext >= tNotes.Count;

    void TaikoStep()
    {
        // input: a key going down hits; Auto presses at each note's time
        if (auto) TaikoAuto();
        else if (playback == null)
        {
            byte bits = 0;
            for (int k = 0; k < 4; k++)
            {
                bool down = Down(tKeys[k]);
                if (down && !tHeld[k]) TaikoPress(k, time);
                tHeld[k] = down;
                if (down) bits |= TaikoBit[k];
            }
            if (bits != tRecorded) { recording.Add(new ReplayFrame(time, 0, 0, bits)); tRecorded = bits; } // (changes only)
        }
        else
            // a replay: stable's bits, each key going down hits at its frame's time
            while (pbIndex < playback.Length && playback[pbIndex].Time <= time)
            {
                var f = playback[pbIndex++];
                for (int k = 0; k < 4; k++)
                {
                    bool down = (f.Keys & TaikoBit[k]) != 0;
                    if (down && !tHeld[k]) TaikoPress(k, f.Time);
                    tHeld[k] = down;
                }
            }

        // misses and ends
        for (int i = tNext; i < tNotes.Count && tNotes[i].Time - tMiss <= time; i++)
        {
            var n = tNotes[i];
            if (n.Done) continue;
            if (n.Kind is TKind.Don or TKind.Kat && time > n.Time + tMiss) TaikoJudge(n, 0, n.Time + tMiss);
            else if (n.Kind == TKind.Roll && time > n.RollEnd) { n.Done = true; n.JudgedAt = n.End; }
            else if (n.Kind == TKind.Swell && time > n.End)
            {
                TaikoJudge(n, 0, n.End); // (not cleared in time)
            }
        }
        while (tNext < tNotes.Count && tNotes[tNext].Done) tNext++;
    }

    void TaikoAuto()
    {
        for (int i = tNext; i < tNotes.Count && tNotes[i].Time <= time; i++)
        {
            var n = tNotes[i];
            if (n.Done) continue;
            switch (n.Kind)
            {
                case TKind.Don or TKind.Kat when !n.Pending:
                    int a = n.Kind == TKind.Don ? 1 : 0, b = 3 - a;
                    if (n.Big) { TaikoPress(a, n.Time); TaikoPress(b, n.Time); }
                    else TaikoPress((tAutoHand++ & 1) == 0 ? a : b, n.Time);
                    break;
                case TKind.Roll:
                    for (; n.AutoNext < n.Ticks.Count && n.Ticks[n.AutoNext] <= time; n.AutoNext++)
                        TaikoPress((tAutoHand++ & 1) == 0 ? 1 : 2, n.Ticks[n.AutoNext]);
                    break;
                case TKind.Swell:
                    // stable's Auto: centre and rim in turn, the count plus one hits from the swell's start, (int)(duration /
                    // (count + 1)) apart
                    int gap = Math.Max(1, (int)(n.End - n.Time) / (n.HitsNeeded + 1));
                    for (; n.AutoNext <= n.HitsNeeded && n.Time + gap * n.AutoNext < n.End && n.Time + gap * n.AutoNext <= time; n.AutoNext++)
                        TaikoPress((n.AutoNext % 4) switch { 0 => 1, 1 => 0, 2 => 2, _ => 3 }, n.Time + gap * n.AutoNext);
                    break;
            }
        }
    }

    void TaikoPress(int key, double at)
    {
        tDrumAt[key] = Game.NowMs;
        bool rim = key is 0 or 3;
        var tp = map.PointAt(at + 5);
        samples.Play(tp.SampleSet, rim ? "hitclap" : "hitnormal", tp.SampleIndex, Math.Max(8, tp.Volume), SamplePitch, key < 2 ? -0.2f : 0.2f); // (stable pans by side)
        if (!Resting(at) && !paused) keyCounts[key is 1 or 2 ? key - 1 : key == 0 ? 2 : 3]++;

        for (int i = tNext; i < tNotes.Count && tNotes[i].Time - tMiss <= at; i++)
        {
            var n = tNotes[i];
            switch (n.Kind)
            {
                case TKind.Swell when !n.Done && at >= n.Time && at <= n.End:
                    // stable: centre and rim in turn (the first either)
                    if (n.LastSwellRim == (rim ? 1 : 0) && !relax) return; // (Relax: any order)
                    n.LastSwellRim = rim ? 1 : 0;
                    n.Hits++;
                    score += 300;
                    if (n.Hits > n.HitsNeeded) { TaikoJudge(n, 300, at); tClearAt = Game.NowMs; } // (stable: one more than its count)
                    return;
                case TKind.Roll when !n.Done && at >= n.Time && (at <= n.End || at <= n.RollEnd && (i + 1 >= tNotes.Count || tNotes[i + 1].Time > at)):
                    if (n.TicksHit < n.Ticks.Count && Math.Abs(n.Ticks[n.TicksHit] - at) <= n.TickSpacing / 2 + 1)
                    {
                        n.TicksHit++;
                        // stable: 300 a tick, 1.2x in kiai (the roll's start), a big roll's a fifth more
                        int tick = map.PointAt(n.Time).Kiai ? 360 : 300;
                        score += n.Big ? tick + tick / 5 : tick;
                        health.Add(0.0001);
                    }
                    return;
                case TKind.Don or TKind.Kat when n.Pending && n.Done:
                    // the other hand on a big note within 30ms: the bonus
                    if (rim == (n.Kind == TKind.Kat) && key != n.FirstKey && at - n.FirstHitAt < 30)
                    {
                        n.Pending = false;
                        TaikoBigBonus(n);
                        return;
                    }
                    continue;
                case TKind.Don or TKind.Kat when !n.Done:
                    double err = at - n.Time;
                    if (Math.Abs(err) > tMiss) return;
                    bool right = relax || rim == (n.Kind == TKind.Kat); // (Relax: either colour)
                    int result = !right ? 0 : Math.Abs(err) < tGreat ? 300 : Math.Abs(err) < tGood ? 100 : 0;
                    TaikoJudge(n, result, at);
                    if (result > 0) hitErrors.Add(err);
                    if (result > 0 && n.Big) { n.Pending = true; n.FirstHitAt = at; n.FirstKey = key; } // (both keys in one frame: the second at once)
                    return;
            }
        }
    }

    void TaikoJudge(TNote n, int result, double at)
    {
        n.Done = true;
        n.Result = result;
        n.JudgedAt = at;
        if (n.Kind == TKind.Swell)
        {
            // stable: a cleared swell is a bonus (300 with the combo's bonus, 1.2x in kiai, doubled) leaving the combo
            // and the counts alone; one not cleared just costs some health
            if (result > 0) { tLastJudge = (300, false, Game.NowMs); score += 2 * TaikoValue(300, map.PointAt(at).Kiai); }
            else health.Add(Beatmap.Range(map.HP, -4, -15, -28));
            tFailing = false;
            return;
        }
        tLastJudge = (result, false, Game.NowMs);
        tFailing = result == 0;
        Count(result);
        if (result > 0 && n.Kind is TKind.Don or TKind.Kat)
            tFlying.Add(new TFly { X = TaikoHitX, Y = TaikoLaneY, Scale = n.Big ? 1 : 0.65f, Vy = 169, Colour = n.Kind == TKind.Don ? new Color(235, 69, 44, 255) : new Color(67, 142, 172, 255), Big = n.Big });
        if (result > 0)
        {
            // (scored on the combo before this hit, like stable)
            score += TaikoValue(result == 300 ? 300 : 150, map.PointAt(n.Time).Kiai);
            combo++;
            maxCombo = Math.Max(maxCombo, combo);
            if (result != 300 && mods.HasFlag(Mods.Perfect)) Fail(); // (Perfect: greats only)
            health.Add(result == 300 ? tHpMul * 6 : tHpMul * Beatmap.Range(map.HP, 17.6, 2.2, 2.2));
        }
        else
        {
            health.Add(Beatmap.Range(map.HP, -6, -25, -40));
            BreakCombo();
        }
    }

    void TaikoBigBonus(TNote n)
    {
        if (n.Result == 300) nGeki++; else nKatu++;
        score += TaikoValue(n.Result == 300 ? 300 : 150, map.PointAt(n.Time).Kiai, comboOffset: -1); // stable: the second hand's bonus counts one combo less
        tLastJudge = (n.Result, true, Game.NowMs);
    }

    // stable: the hit's value plus (value / 35 x 2 x (difficulty + 1) x mod multiplier) for every 10 combo (up to 100),
    // 1.2x in kiai
    int TaikoValue(int value, bool kiai, int comboOffset = 0)
    {
        int v = value + (int)((double)(value / 35 * 2 * (diffMul + 1)) * modMul) * (Math.Min(100, combo + comboOffset) / 10);
        return kiai ? (int)(v * 1.2f) : v;
    }

    void TaikoDraw(int w, int h)
    {
        float u = h / 480f, sd = h / 768f;
        var tint = ColorAlpha(Color.White, playfieldAlpha);
        float laneY = TaikoLaneY * u, hitX = TaikoHitX * u;

        UpdateComboBursts();
        DrawFlowers(u, sd);

        // the bar: a black backing, the stretched right part (its glow in kiai), then the drum on the left
        DrawRectangle(0, (int)(143 * u), w, (int)(109 * u), ColorAlpha(Color.Black, playfieldAlpha));
        bool kiai = map.PointAt(time).Kiai;
        foreach (var name in kiai ? new[] { "taiko-bar-right", "taiko-bar-right-glow" } : ["taiko-bar-right"])
            if (skin.Tex(name) is { } bar)
                DrawTexturePro(bar.Tex, new Rectangle(0, 0, bar.Tex.Width, bar.Tex.Height), new Rectangle(0, 135 * u, w, bar.Height * sd), Vector2.Zero, 0, tint);
        DrawMascot(kiai, u, sd, tint);

        // the hit target: approach ring and a faint big circle (and the kiai glow)
        if (kiai) skin.Tex("taiko-glow")?.Draw(new(hitX, laneY), 0.7f * sd, ColorAlpha(new Color(255, 228, 0, 255), playfieldAlpha));
        skin.Tex("approachcircle")?.Draw(new(hitX, laneY), 0.73f * sd, ColorAlpha(Color.White, 180 / 255f * playfieldAlpha));
        skin.Tex("taikobigcircle")?.Draw(new(hitX, laneY), 0.7f * sd, ColorAlpha(Color.White, 120 / 255f * playfieldAlpha));

        // bar lines
        var barline = skin.Tex("taiko-barline");
        foreach (var (bt, bs) in tBars)
        {
            float x = (float)(TaikoHitX + (bt - time) * bs);
            if (x > TaikoHitX + tTravel || x < -20) continue;
            barline?.Draw(new(x * u, laneY), 0.88f * sd, tint);
        }

        // notes, later ones underneath
        float noteScale = 90.88f / 80 * sd; // stable: a CS 2 circle's size; small notes 0.65 of it
        for (int i = tNotes.Count - 1; i >= tNext - 64 && i >= 0; i--)
        {
            var n = tNotes[i];
            if ((n.Time - time) * n.Speed > tTravel) continue;
            if (tTravel == 600 && TaikoHitX + (n.Time - time) * n.Speed > 640) continue; // (Hidden: stable clips the field 640 wide)
            if (n.Done && n.Kind != TKind.Roll && (n.Result > 0 || n.Kind == TKind.Swell)) continue; // (misses scroll on)
            float x = (float)(TaikoHitX + (n.Time - time) * n.Speed);
            float sc = noteScale * (n.Big ? 1 : 0.65f);
            switch (n.Kind)
            {
                case TKind.Don or TKind.Kat:
                    if (x < -60) continue;
                    var colour = n.Kind == TKind.Don ? new Color(235, 69, 44, 255) : new Color(67, 142, 172, 255);
                    DrawTaikoCircle(new(x * u, laneY), sc, colour, n.Big);
                    break;
                case TKind.Roll:
                    float x2 = (float)(TaikoHitX + (n.End - time) * n.Speed);
                    if (x2 < -60) continue;
                    var rollColour = new Color(252, 184, 6, 255);
                    float bodyH = 128 * sc; // the roll's body is as tall as the note
                    if (skin.Tex("taiko-roll-middle") is { } mid)
                        DrawTexturePro(mid.Tex, new Rectangle(0, 0, mid.Tex.Width, mid.Tex.Height), new Rectangle(x * u, laneY - bodyH / 2, Math.Max(0, (x2 - x) * u), bodyH), Vector2.Zero, 0, ColorAlpha(rollColour, playfieldAlpha));
                    skin.Tex("taiko-roll-end")?.Draw(new(x2 * u + 32 * sc, laneY), sc, ColorAlpha(rollColour, playfieldAlpha));
                    DrawTaikoCircle(new(Math.Max(x, n.Done ? x : TaikoHitX) * u, laneY), sc, rollColour, n.Big);
                    break;
                case TKind.Swell:
                    // stable: the warning rides the lane, then at the swell's start flies to (256, 196) in the field,
                    // growing 1 -> 3 and fading out over 200ms
                    if (skin.Tex("spinner-warning") is { } warn)
                    {
                        if (time < n.Time) warn.Draw(new(x * u, laneY), noteScale * 0.65f, tint);
                        else if (time < n.Time + 200)
                        {
                            float f = (float)((time - n.Time) / 200);
                            var from = new Vector2(TaikoHitX, TaikoLaneY);
                            var at = Vector2.Lerp(from, new Vector2(256, 72 + 196), f) * u;
                            warn.Draw(at, noteScale * 0.65f * (1 + 2 * f), ColorAlpha(Color.White, (1 - f) * playfieldAlpha));
                        }
                    }
                    break;
            }
        }

        // the active swell: stable's spinner below the bar, centred on the screen (y 264): the circle at 0.8 (turning
        // with each hit), its approach circle shrinking over the swell (not with Hidden) and the hits left in the
        // score font 80 under it; it fades in over 200ms
        foreach (var n in tNotes.Skip(tNext).TakeWhile(n => n.Time <= time))
        {
            if (n.Kind != TKind.Swell || n.Done) continue;
            var c = new Vector2(w / 2f, 264 * u);
            float p = (float)Math.Clamp((time - n.Time) / Math.Max(1, n.End - n.Time), 0, 1);
            var st = ColorAlpha(mods.HasFlag(Mods.SpunOut) ? Color.Gray : Color.White, (float)Math.Clamp((time - n.Time + 1) / 201, 0, 1) * playfieldAlpha);
            skin.Tex("spinner-circle")?.Draw(c, 0.8f * sd, st, n.Hits * 20 % 360);
            if (!mods.HasFlag(Mods.Hidden)) skin.Tex("spinner-approachcircle")?.Draw(c, 0.8f * sd * (1.86f - 1.76f * p), st);
            string left = (n.HitsNeeded + 1 - n.Hits).ToString();
            float gh = (skin.Glyph(skin.ScorePrefix, '0')?.Height ?? 30) * sd;
            skin.DrawFont(skin.ScorePrefix, skin.ScoreOverlap, left, c.X, c.Y + 80 * u - gh / 2, sd, 0.5f, st);
        }

        // the thrown notes
        float barX = 160;
        if (skin.Tex("scorebar-colour") is { } bar2) barX = (12 + bar2.Width * (float)(hpShown / Health.Max)) * sd / u;
        for (tFlyTicks += GetFrameTime() * 60; tFlyTicks >= 1; tFlyTicks--)
            foreach (var f in tFlying)
            {
                f.X += (barX - f.X + 2) * 0.065f;
                f.Scale *= 0.965f;
                f.Y -= f.Vy / 11.5f;
                f.Vy -= 7;
                if (f.Vy < 70 && double.IsNaN(f.FadeAt)) f.FadeAt = Game.NowMs;
            }
        tFlying.RemoveAll(f => f.Vy < -32);
        foreach (var f in tFlying)
        {
            float a = double.IsNaN(f.FadeAt) ? 1 : 1 - (float)Math.Clamp((Game.NowMs - f.FadeAt) / 300, 0, 1);
            DrawTaikoCircle(new(f.X * u, f.Y * u), noteScale * f.Scale, f.Colour, f.Big, a);
        }

        // the judgement at the target, fading out
        double age = Game.NowMs - tLastJudge.At;
        if (age < 400 && tLastJudge.Result >= 0)
        {
            // stable: a big note hit with both hands shows the "k" judgement; a great in kiai the "g" one
            string name = tLastJudge.Result switch { 300 => tLastJudge.Big ? "taiko-hit300k" : map.PointAt(time).Kiai ? "taiko-hit300g" : "taiko-hit300", 100 => tLastJudge.Big ? "taiko-hit100k" : "taiko-hit100", _ => "taiko-hit0" };
            float a = 1 - (float)(age / 400);
            (skin.AnimFrame(name, age) ?? skin.Tex(name) ?? skin.Tex(name.Replace("g", "").Replace("k", "")))?.Draw(new(hitX, tLastJudge.Result == 0 ? laneY - 40 * u : laneY), 0.7f * sd, ColorAlpha(Color.White, a * playfieldAlpha));
        }

        // the drum, each half lighting up when hit
        if (skin.Tex("taiko-bar-left") is { } left2) left2.Draw(new(left2.Width * sd / 2, 135 * u + left2.Height * sd / 2), sd, tint);
        for (int k = 0; k < 4; k++)
        {
            // stable: lit at once, fading out from 100 to 150ms
            double since = Game.NowMs - tDrumAt[k];
            float a = since < 100 ? 1 : 1 - (float)Math.Clamp((since - 100) / 50, 0, 1);
            if (a <= 0) continue;
            bool rim = k is 0 or 3, rightHalf = k >= 2;
            if (skin.Tex(rim ? "taiko-drum-outer" : "taiko-drum-inner") is { } half)
                half.Draw(new((rightHalf ? 56 * u : 0) + half.Width * sd / 2, 135 * u + half.Height * sd / 2), sd, ColorAlpha(Color.White, a * playfieldAlpha), flipX: rim != rightHalf);
        }
        if (combo > 0)
        {
            string c = combo.ToString();
            float size = 24 * u;
            float cx = 56 * u - Game.TextWidth(c, size) / 2;
            Game.TextBold(c, cx + 2 * u / 2.25f, laneY - size / 2 + 2 * u / 2.25f, size, ColorAlpha(Color.Black, 0.6f * playfieldAlpha)); // (a shadow: the drum is pale)
            Game.TextBold(c, cx, laneY - size / 2, size, tint);
        }
    }

    // stable's mascot on the bar's top left (bottom-left at 0,160, at 0.6): pippidonidle, pippidonkiai in kiai time or
    // pippidonfail after a miss (until a hit, or kiai starting or ending), a frame a beat; pippidonclear once (frames
    // 0-6, 5, 6, 5-0 at 100ms) on a swell cleared, at the combo milestones and at the end
    bool tFailing, tKiaiWas, tEndCleared;
    double tClearAt = double.NegativeInfinity;
    static readonly int[] TClearFrames = [0, 1, 2, 3, 4, 5, 6, 5, 6, 5, 4, 3, 2, 1, 0];

    void DrawMascot(bool kiai, float u, float sd, Color tint)
    {
        if (kiai != tKiaiWas) { tKiaiWas = kiai; tFailing = false; }
        if (finished && !tEndCleared) { tEndCleared = true; tClearAt = Game.NowMs; }
        Sprite? frame = null;
        double sinceClear = Game.NowMs - tClearAt;
        if (sinceClear < TClearFrames.Length * 100 && skin.Animation("pippidonclear", false) is { Length: > 0 } clear)
            frame = clear[Math.Min(clear.Length - 1, TClearFrames[(int)(sinceClear / 100)])];
        else if (skin.Animation(tFailing ? "pippidonfail" : kiai ? "pippidonkiai" : "pippidonidle", false) is { Length: > 0 } loop)
        {
            var red = map.UninheritedAt(time);
            long beat = (long)Math.Floor((time - red.Time) / Math.Max(1, red.BeatLength));
            frame = loop[(int)(((beat % loop.Length) + loop.Length) % loop.Length)];
        }
        if (frame is { } f)
            DrawTexturePro(f.Tex, new Rectangle(0, 0, f.Tex.Width, f.Tex.Height), new Rectangle(0, 160 * u - f.Height * 0.6f * sd, f.Width * 0.6f * sd, f.Height * 0.6f * sd), Vector2.Zero, 0, tint);
    }

    // a combo milestone: the mascot's cheer and (Combo bursts on) the skin's next taiko-flower-group frame popping up
    // above the drum (bottom-centre 54,141): scaling 0.6 to 1 over 800ms (Out), in over 200ms, out from 1600 to 1900ms
    readonly List<(Sprite Tex, double At)> tFlowers = [];
    int tFlowerNext;

    void TaikoBurst()
    {
        tClearAt = Game.NowMs;
        if (!Config.GetBool("ComboBurst", false) || skin.Animation("taiko-flower-group", true) is not { Length: > 0 } frames) return;
        if (skin.ComboBurstRandom) tFlowerNext = Random.Shared.Next(frames.Length);
        tFlowers.Add((frames[tFlowerNext % frames.Length], Game.NowMs));
        tFlowerNext = (tFlowerNext + 1) % frames.Length;
    }

    void DrawFlowers(float u, float sd)
    {
        tFlowers.RemoveAll(f => Game.NowMs - f.At > 1900);
        foreach (var (tex, at) in tFlowers)
        {
            double age = Game.NowMs - at;
            float s = (0.6f + 0.4f * (float)Easing.Apply(1, Math.Min(1, age / 800))) * sd;
            float a = Math.Min(1, (float)(age / 200)) * (1 - (float)Easing.Apply(2, Math.Clamp((age - 1600) / 300, 0, 1)));
            DrawTexturePro(tex.Tex, new Rectangle(0, 0, tex.Tex.Width, tex.Tex.Height), new Rectangle(54 * u - tex.Width * s / 2, 141 * u - tex.Height * s, tex.Width * s, tex.Height * s), Vector2.Zero, 0, ColorAlpha(Color.White, a * playfieldAlpha));
        }
    }

    void DrawTaikoCircle(Vector2 at, float scale, Color colour, bool big, float alpha = 1)
    {
        string body = big ? "taikobigcircle" : "taikohitcircle";
        skin.Tex(body)?.Draw(at, scale, ColorAlpha(colour, playfieldAlpha * alpha * colour.A / 255f));
        (skin.AnimFrame(body + "overlay", Game.NowMs) ?? skin.Tex(body + "overlay"))?.Draw(at, scale, ColorAlpha(Color.White, playfieldAlpha * alpha));
    }
}
