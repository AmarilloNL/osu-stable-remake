using System.Globalization;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// Timing's "Tap Here!" (or T): tap along with the song and stable's tap algorithm times the current section - from 8
// taps on it drops taps far off the rest, finds the BPM that fits them best (to more decimals the more taps there are)
// and the offset they agree on most, and sets them (an untimed section takes the offset outright, a timed one moves
// halfway towards it).
sealed partial class Editor
{
    readonly List<int> taps = [];
    ETiming? tapRed; // the section a run of taps times (the picked red point, else the one in effect at the first tap)
    double tapUndoAt = double.NegativeInfinity;

    // stable's metronome (top right): osu!ui's panel 200 units from the right edge, Reset on
    // its left, four beat lights, Tap Here! under them
    const float MetronomeY = 0;
    static Rectangle TapButton() { float u = U; return new Rectangle(Game.W - 140 * u, (MetronomeY + 34) * u, 136 * u, 25 * u); }
    static Rectangle MetronomeReset() { float u = U; return new Rectangle(Game.W - 197 * u, (MetronomeY + 4) * u, 58 * u, 58 * u); }

    void TimingTap()
    {
        if (!timing.Any(p => p.Red)) { Notification.Show("At least one timing point is required.", 3000); return; }
        if (!playing) SetPlaying(true);
        int now = (int)Math.Round(time);
        if (taps.Count > 0 && now == taps[^1]) return;
        if (taps.Count > 0 && taps[^1] > now) taps.Clear();
        if (taps.Count == 1 && now - taps[0] > 1000) taps.Clear();
        if (taps.Count == 0 || tapRed == null || !timing.Contains(tapRed))
            tapRed = timingSel >= 0 && timingSel < timing.Count && timing[timingSel].Red ? timing[timingSel]
                : timing.Where(p => p.Red).OrderBy(p => p.Time).LastOrDefault(p => p.Time <= time) ?? timing.Where(p => p.Red).OrderBy(p => p.Time).First();
        var red = tapRed;
        if (taps.Count == 0) Notification.Show("Tap in time to the beat!", 5000);
        else if (taps.Count == 2) Notification.Show("Keep going...", 5000);
        else if (taps.Count >= 4 && taps.Count < 8) Notification.Show($"Tap another {7 - taps.Count} times...", 5000);
        else if (taps.Count == 8) Notification.Show("Keep tapping to increase accuracy.\nHit reset to start from scratch if the metronome sounds very wrong.", 5000);
        else if (taps.Count is 16 or 32 or 64) Notification.Show($"BPM Sensitivity {(taps.Count == 16 ? 0 : taps.Count == 32 ? 1 : 2)}", 5000);
        taps.Add(now);
        if (taps.Count < 8) return;

        double average, bestBpm, beat, offset;
        while (true)
        {
            if (taps.Count < 2) return;
            average = (float)(taps[^1] - taps[1]) / ((float)taps.Count - 2f);
            // a gap far off the average: that tap (and the next) taken out, the later ones moved back (in the taps kept, like
            // stable's - so taps after it, timed from where they really were, keep being taken out until reset or a seek back)
            int k = 0;
            while (k < taps.Count - 1 && Math.Abs(taps[k + 1] - taps[k]) <= average * 1.4) k++;
            if (k < taps.Count - 1) { DropTaps(k); continue; }

            double deviation = 0;
            for (int i = 1; i < taps.Count - 1; i++) deviation += Math.Abs(taps[i + 1] - (taps[i] + average)) * (1 + i / 64.0);
            deviation /= (float)taps.Count - 1f;
            double hi = (taps[^1] - taps[1] + deviation) / ((float)taps.Count - 2f), lo = (taps[^1] - taps[1] - deviation) / ((float)taps.Count - 2f);
            bestBpm = 0;
            double closest = 10000;
            for (; lo < hi; lo += taps.Count >= 64 ? 0.05 : taps.Count < 32 ? 0.1 : 0.5)
            {
                double bpm = Math.Round(60000 / lo, taps.Count >= 64 ? 2 : taps.Count >= 32 ? 1 : 0);
                if (Math.Abs(60000 / bpm - average) < closest) { closest = Math.Abs(60000 / bestBpm - average); bestBpm = bpm; } // (stable's: measured against the previous best)
            }
            if (bestBpm == 0) bestBpm = Math.Round(60000 / average, taps.Count >= 64 ? 2 : taps.Count >= 32 ? 1 : 0); // (taps too even to search around)
            beat = 60000 / bestBpm;
            // the offset: from each tap, the one the others stray from least (a tap the rest disagree with by over 200ms
            // on average is dropped)
            offset = -1;
            double leastStray = 1000;
            int bad = -1;
            for (int j0 = 0; j0 < taps.Count - 1; j0++)
            {
                double start = taps[j0] - beat * j0, sum = 0, worst = 0;
                int n = 0;
                for (int j = 1; j < taps.Count - 1; j++)
                {
                    if (j == j0) continue;
                    n++;
                    sum += start + beat * j - taps[j];
                    worst = Math.Max(worst, Math.Abs(start + beat * j - taps[j]));
                }
                if (n > 0 && Math.Abs(sum) / n > 200) { bad = j0; break; }
                if (worst < leastStray) { leastStray = worst; offset = start; }
            }
            if (bad < 0) break;
            if (bad == 0)
            {
                taps.RemoveAt(0);
                for (int i = 0; i < taps.Count; i++) taps[i] -= (int)Math.Round(average);
                continue;
            }
            DropTaps(bad - 1);
        }
        if (double.IsInfinity(beat) || beat <= 0) return;
        double last = taps[^1] - (taps.Count - 1) * beat;
        // one undo state for a run of taps
        if (Game.NowMs - tapUndoAt > 3000) { undo.Add(Snapshot()); if (undo.Count > 100) undo.RemoveAt(0); redo.Clear(); }
        tapUndoAt = Game.NowMs;
        red.Beat = beat;
        red.Time = red.Time == 0 ? offset : red.Time - (red.Time - last) / 2;
        red.Raw = null;
        modified = true;
        info = ReloadInfo();
        Rebuild();
    }

    // Timing → Reset Current Section (stable's): after asking, the current section is untimed - its beat length 0, its
    // offset 0 for the first section, else a second after the one before - and the editor goes there, to tap it again
    void ResetCurrentSectionPrompt() => prompt = new("Are you sure you wish to reset the current section's timing?", null, _ => ResetCurrentSection(), "Yes", "No");
    void ResetCurrentSection() // (the metronome's Reset: no asking)
    {
        var reds = timing.Where(p => p.Red).OrderBy(p => p.Time).ToList();
        var cur = reds.LastOrDefault(p => p.Time <= time) ?? reds.FirstOrDefault();
        if (cur == null) return;
        int i = reds.IndexOf(cur);
        Change(() => { cur.Beat = 0; cur.Time = i == 0 ? 0 : reds[i - 1].Time + 1000; cur.Raw = null; });
        taps.Clear();
        timingSel = timing.IndexOf(cur); // (the section the taps will time)
        Seek(cur.Time);
        Notification.Show("This timing section is not yet timed.\nTo begin, tap from the first beat.", 5000);
    }

    // stable's: the tap after gap k taken out, and the next if there is one, the later taps moved back by the gap
    void DropTaps(int k)
    {
        int gap = taps[k + 1] - taps[k];
        taps.RemoveAt(k + 1);
        if (k < taps.Count - 1) { gap = taps[k + 1] - taps[k]; taps.RemoveAt(k + 1); }
        for (int i = k + 1; i < taps.Count; i++) taps[i] -= gap;
    }

    void DrawMetronome(float u)
    {
        var mouse = EMouse;
        float x = Game.W - 200 * u, y = MetronomeY * u;
        if (skin.DefaultTex("editor-metronome-bg") is { } bg)
            DrawTexturePro(bg.Tex, new Rectangle(0, 0, bg.Tex.Width, bg.Tex.Height), new Rectangle(x, y, bg.Width * u / 1.6f, bg.Height * u / 1.6f), Vector2.Zero, 0, Color.White);
        // the lights: the bar's first beat yellow-green, the others white in turn (3 of them, whatever the meter), each
        // fading over its beat
        var lit = new Color[4];
        if (TimedSection is { } red && time >= red.Time)
        {
            double beat = (time - red.Time) % (red.Beat * Math.Max(1, red.Meter)) / red.Beat;
            if (beat < 1) lit[0] = new Color((int)(187 * (1 - beat)), (int)(255 * (1 - beat)), (int)(51 * (1 - beat)), 255);
            else
            {
                beat %= 3;
                int i = beat < 1 ? 3 : beat < 2 ? 1 : 2;
                int v = (int)(255 * ((beat < 1 ? 1 : beat < 2 ? 2 : 3) - beat));
                lit[i] = new Color(v, v, v, 255);
            }
        }
        if (skin.DefaultTex("editor-metronome-beat-lighting") is { } light)
        {
            BeginBlendMode(BlendMode.Additive);
            for (int i = 0; i < 4; i++)
            {
                float w = light.Width * u / 1.6f, h = light.Height * u / 1.6f, cx = Game.W - (123 - 34 * i) * u, cy = (MetronomeY + 18) * u;
                DrawTexturePro(light.Tex, new Rectangle(0, 0, light.Tex.Width, light.Tex.Height), new Rectangle(cx - w / 2, cy - h / 2, w, h), Vector2.Zero, 0, lit[i]);
            }
            EndBlendMode();
        }
        // stable's buttons: Reset (orange) and Tap Here! (sky blue), lighter under the cursor
        void Button(Rectangle r, string text, Color c, float size)
        {
            bool over = CheckCollisionPointRec(mouse, r);
            DrawRectangleRounded(r, 0.2f, 6, over ? new Color(Math.Min(255, c.R + 30), Math.Min(255, c.G + 30), Math.Min(255, c.B + 30), 255) : c);
            Game.Text(text, r.X + r.Width / 2 - Game.TextWidth(text, size * u) / 2, r.Y + r.Height / 2 - size * u / 2, size * u, Color.White);
        }
        Button(MetronomeReset(), "Reset", new Color(255, 165, 0, 255), 12);
        Button(TapButton(), "Tap Here!", new Color(135, 206, 235, 255), 12);
        if (CheckCollisionPointRec(mouse, TapButton()))
            Tip("The more you tap, the more \"precise\" the timing algorithm will become. \nYou can click this button or use the 'T' key.");
    }

    // ---- stable's Timing tab: osu!ui's panel (at 120,85) with the section's BPM and offset and the map's slider velocity
    // and tick rate, each on a blue bar between - and + (BPM 1, Shift 5, Ctrl 0.25; offset 2ms, Shift 10, Ctrl 1;
    // velocity 0.1, Ctrl 0.01, 0.4 - 3.6; tick rate 1 to 4 in whole steps). "Move already placed notes" (on) takes the
    // section's objects along: an offset change shifts them, a BPM change stretches them from the offset. Its button on
    // the right, a click on the BPM or offset, or F6 open the timing setup panel (the table) instead.
    bool moveNotes = true;
    static readonly float[] RowY = [120, 186, 276, 306];
    static readonly string[] RowLabels = ["BPM:", "Offset:", "Slider Velocity:", "Slider Tick Rate:"];
    readonly double[] rowClickAt = [.. Enumerable.Repeat(double.NegativeInfinity, 8)];
    readonly (bool On, double At)[] rowHover = new (bool, double)[8], valueHover = new (bool, double)[2];
    (bool On, double At) manualHover;

    Rectangle TimingPic(string name, float cx, float cy, float scale = 1)
    {
        float u = U, sd = u / 1.6f * scale;
        var s = skin.DefaultTex(name);
        float w = (s?.Width ?? 26) * sd, h = (s?.Height ?? 24) * sd;
        return new Rectangle(cx * u - w / 2, cy * u - h / 2, w, h);
    }
    Rectangle RowButton(int row, int dir, float scale = 1) => TimingPic(dir < 0 ? "editor-beat-selector-buttonleft" : "editor-beat-selector-buttonright", dir < 0 ? 270 : 382, RowY[row] + 10, scale);
    static Rectangle RowValue(int row) { float u = U; return new Rectangle(296 * u, RowY[row] * u, 56 * u, 20 * u); }
    static Rectangle MoveNotesBox() { float u = U; return new Rectangle(145 * u, 216 * u, 360 * u, 16 * u); }
    // (the timing point in effect - a reset, untimed one too - and null when that one isn't timed)
    ETiming? TimedSection => (timing.Where(p => p.Red).OrderBy(p => p.Time).LastOrDefault(p => p.Time <= time) ?? timing.Where(p => p.Red).OrderBy(p => p.Time).FirstOrDefault()) is { Beat: > 0 } r ? r : null;

    void UpdateTimingView(Vector2 mouse)
    {
        if (CheckCollisionPointRec(mouse, TimingPic("editor-timing-manual", 455, 146))) { OpenTimingDialog(); return; }
        for (int v = 0; v < 2; v++) if (TimedSection != null && CheckCollisionPointRec(mouse, RowValue(v))) { OpenTimingDialog(); return; }
        if (CheckCollisionPointRec(mouse, MoveNotesBox()))
        {
            moveNotes = !moveNotes;
            skin.Sound(moveNotes ? "check-on" : "check-off")?.Play(BeatmapSamples.EffectVolume);
            return;
        }
        for (int row = 0; row < RowY.Length; row++)
            for (int dir = -1; dir <= 1; dir += 2)
                if (CheckCollisionPointRec(mouse, RowButton(row, dir))) { rowClickAt[row * 2 + (dir + 1) / 2] = Game.NowMs; StepTiming(row, dir); return; }
    }

    void StepTiming(int row, int dir)
    {
        bool shift = Input.ShiftHeld, ctrl = Input.CtrlHeld;
        if (row == 2)
        {
            double sv = Math.Clamp(SliderMultiplier + dir * (ctrl ? 0.01 : 0.1), 0.4, 3.6);
            if (sv != SliderMultiplier) Change(() => SetKv("Difficulty", "SliderMultiplier", Num(Math.Round(sv, 2))));
            return;
        }
        if (row == 3)
        {
            double rate = double.TryParse(Kv("Difficulty", "SliderTickRate"), NumberStyles.Float, Inv, out double r) ? r : 1;
            if (dir < 0 ? rate > 1 : rate < 4) Change(() => SetKv("Difficulty", "SliderTickRate", Num(Math.Max(1, (int)rate + dir)))); // (whole steps; never below 1)
            return;
        }
        if (TimedSection is not { } red) return;
        double bpm = 60000 / red.Beat + dir * (shift ? 5 : ctrl ? 0.25 : 1);
        if (row == 0 && bpm <= 0) return;
        double end = timing.Where(p => p.Red && p.Time > red.Time).Select(p => p.Time).DefaultIfEmpty(length).Min();
        Change(() =>
        {
            double beat0 = red.Beat, offset0 = red.Time;
            if (row == 0) red.Beat = 60000 / bpm;
            else red.Time += dir * (shift ? 10 : ctrl ? 1 : 2);
            if (!moveNotes) return;
            foreach (var o in objs.Where(o => o.Time >= offset0 && o.Time < end))
            {
                double to = row == 0 ? (int)((o.Time - offset0) * (red.Beat / beat0) + offset0) : o.Time + (int)(red.Time - offset0), d = o.End - o.Time;
                o.Time = to; o.End = to + d; o.Raw = null;
            }
        });
    }

    void DrawTimingView(float u)
    {
        var mouse = EMouse;
        void Picture(string name, Rectangle at, Color tint)
        {
            if (skin.DefaultTex(name) is { } s) DrawTexturePro(s.Tex, new Rectangle(0, 0, s.Tex.Width, s.Tex.Height), at, Vector2.Zero, 0, tint);
        }
        if (skin.DefaultTex("editor-timing-window-bg") is { } bg)
            Picture("editor-timing-window-bg", new Rectangle(120 * u, 85 * u, bg.Width * u / 1.6f, bg.Height * u / 1.6f), Color.White);
        var red = TimedSection;
        double tick = double.TryParse(Kv("Difficulty", "SliderTickRate"), NumberStyles.Float, Inv, out double tr) ? tr : 1;
        string[] values = [red != null ? (60000 / red.Beat).ToString("0.###", Inv) : "-", red != null ? red.Time.ToString("#,0", Inv) : "-", SliderMultiplier.ToString("N2", Inv), tick.ToString(Inv)];
        string? tip = null;
        for (int row = 0; row < RowY.Length; row++)
        {
            float y = RowY[row];
            Game.Text(RowLabels[row], 255 * u - Game.TextWidth(RowLabels[row], 14 * u), (y + 3) * u, 14 * u, Color.White);
            if (skin.DefaultTex("editor-beat-selector-back") is { } back) Picture("editor-beat-selector-back", new Rectangle(258 * u, y * u, back.Width * u / 1.6f, back.Height * u / 1.6f), Color.White);
            for (int dir = -1; dir <= 1; dir += 2)
            {
                int k = row * 2 + (dir + 1) / 2;
                double since = Game.NowMs - rowClickAt[k];
                float scale = since < 200 ? 1.2f - 0.2f * (float)(since / 200) : 1;
                bool over = CheckCollisionPointRec(mouse, RowButton(row, dir));
                var tint = Lerp(Color.White, new Color(255, 165, 0, 255), Hovered(ref rowHover[k], over, 0, 1)); // (orange hovered)
                Picture(dir < 0 ? "editor-beat-selector-buttonleft" : "editor-beat-selector-buttonright", RowButton(row, dir, scale), tint);
                if (over) tip = row switch
                {
                    0 => (dir < 0 ? "Decrease BPM" : "Increase BPM") + "\nHold 'Shift' while clicking for larger changes\nHold 'Ctrl' while clicking for smaller changes",
                    1 => (dir < 0 ? "Decrease Offset" : "Increase Offset") + "\nHold 'Shift' while clicking for larger changes\nHold 'Ctrl' while clicking for smaller changes",
                    _ => null,
                };
            }
            // (the BPM and offset brighten to yellow-green under the cursor: a click fine-tunes them)
            bool valueOver = row < 2 && CheckCollisionPointRec(mouse, RowValue(row));
            var vc = row < 2 ? Lerp(Color.White, new Color(154, 205, 50, 255), Hovered(ref valueHover[row], valueOver, 0, 1)) : Color.White;
            Game.Text(values[row], 324 * u - Game.TextWidth(values[row], 14 * u) / 2, (y + 10) * u - 7 * u, 14 * u, vc);
            if (valueOver) tip = "Click to fine-tune using the Timing Setup Panel.";
        }
        Game.Text("Note: you should resnap all notes from the", 145 * u, 150 * u, 14 * u, Color.White);
        Game.Text("timing menu after changing the BPM.", 145 * u, 150 * u + 14 * u, 14 * u, Color.White);
        // the checkbox: osu!ui's circle (full when on) and its text
        var box = MoveNotesBox();
        skin.DefaultTex(moveNotes ? "circle-full" : "circle-empty")?.Draw(new Vector2(153 * u, 224 * u), u / 1.6f, Color.White);
        Game.Text("Move already placed notes when changing the offset/BPM", 165 * u, 224 * u - 7.5f * u, 15 * u, Color.White);
        if (CheckCollisionPointRec(mouse, box)) tip = "Use this when making minor adjustments to an ALREADY SNAPPED beatmap.\nIf your beatmap is not snapped, you will want to disable this before adjusting above.";
        // the timing setup panel's button (1.1x hovered, over 200ms)
        bool manualOver = CheckCollisionPointRec(mouse, TimingPic("editor-timing-manual", 455, 146));
        Picture("editor-timing-manual", TimingPic("editor-timing-manual", 455, 146, Hovered(ref manualHover, manualOver, 1, 1.1f)), Color.White);
        if (manualOver) tip = "Fine-tune multiple specific timing sections (F6)";
        if (tip != null) Tip(tip);
    }

    // top right (over the timeline): the metronome's bar:beat (from 0), or the untimed section's note
    void DrawTimingStatus(float u)
    {
        var red = TimedSection;
        string status;
        if (red == null) status = "This timing section is not yet timed.\nTo begin, tap from the first beat.";
        else
        {
            double beats = (time - red.Time) / red.Beat;
            int meter = Math.Max(1, red.Meter), at = (int)Math.Floor(beats), beat = (int)beats % meter, bar = at / meter;
            if (time - red.Time < 0) { beat += 3; bar--; }
            status = $"{bar}:{beat}";
        }
        float sy = 20 * u;
        foreach (var line in status.Split('\n')) { Game.Text(line, Game.W - 200 * u - Game.TextWidth(line, 13 * u), sy, 13 * u, Color.White); sy += 14 * u; }
    }

    static Color Lerp(Color a, Color b, float t) => new((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t), 255);
}
