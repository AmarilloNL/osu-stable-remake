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

    Rectangle TapButton() { float u = U; return new Rectangle(Game.W - 360 * u, 82 * u, 136 * u, 15 * u); }

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
            // a gap far off the average: that tap (and the next) taken out, the later ones moved back
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
        Rebuild();
    }

    // Timing → Reset Current Section (stable's): after asking, the current section is untimed - its beat length 0, its
    // offset 0 for the first section, else a second after the one before - and the editor goes there, to tap it again
    void ResetCurrentSectionPrompt() => prompt = new("Are you sure you wish to reset the current section's timing?", null, _ =>
    {
        var reds = timing.Where(p => p.Red).OrderBy(p => p.Time).ToList();
        var cur = reds.LastOrDefault(p => p.Time <= time) ?? reds.FirstOrDefault();
        if (cur == null) return;
        int i = reds.IndexOf(cur);
        Change(() => { cur.Beat = 0; cur.Time = i == 0 ? 0 : reds[i - 1].Time + 1000; cur.Raw = null; });
        taps.Clear();
        Seek(cur.Time);
        Notification.Show("This timing section is not yet timed.\nTo begin, tap from the first beat.", 5000);
    }, "Yes", "No");

    // stable's: the tap after gap k taken out, and the next if there is one, the later taps moved back by the gap
    void DropTaps(int k)
    {
        int gap = taps[k + 1] - taps[k];
        taps.RemoveAt(k + 1);
        if (k < taps.Count - 1) { gap = taps[k + 1] - taps[k]; taps.RemoveAt(k + 1); }
        for (int i = k + 1; i < taps.Count; i++) taps[i] -= gap;
    }

    void DrawTapButton(float u)
    {
        var r = TapButton();
        bool hover = CheckCollisionPointRec(Input.GetMousePosition(), r);
        DrawRectangleRounded(r, 0.2f, 6, hover ? new Color(135, 206, 250, 255) : new Color(100, 170, 230, 230));
        string text = taps.Count >= 8 ? $"Tap Here! ({taps.Count})" : "Tap Here! (T)";
        Game.Text(text, r.X + r.Width / 2 - Game.TextWidth(text, 11 * u) / 2, r.Y + 2 * u, 11 * u, Color.White);
    }
}
