namespace OsuStableRemake;

// stable's Score V2. Each judgement that changes the score is an event worth some points, weighted by the combo after it;
// the sum is measured against what a perfect play collects (the one stable simulates for HP drain at load), then
//   osu!:  700,000 x combo part + 300,000 x accuracy^10 x share judged + spinner bonus
//   taiko: 250,000 x combo part + 750,000 x accuracy^3.6 x share judged + drumroll bonus
//   catch: (600,000 + 400,000 x (1 - r)) x combo part + 400,000 x r x tiny droplets caught + banana bonus, r being the
//          tiny droplets' share of the map's judgements
//   mania: 200,000 x combo part + 800,000 x accuracy^(2 + 2 x accuracy) x share judged (MAX counts 305 of 300)
// all times the V2 mod multiplier. osu! weighs an event by 1 + combo / 10, the others by log4(combo) between 0.5 and
// log4(400) (catch: log4(200)); mania counts only events with a combo.
sealed partial class Player
{
    bool ScoreV2 => mods.HasFlag(Mods.ScoreV2);
    double v2Combo, v2ComboMax, v2Bonus, v2Mul;
    int v2Total;
    double v2TinyRatio; int v2Tiny, v2TinyMax; // catch's tiny droplets

    // (an own replay from before format 7 was played with V1's judging and score in taiko, catch and mania)
    bool ModeV2 => ScoreV2 && !OldModeRules;
    bool OldModeRules => watching is { FromStable: false, FormatVersion: < 7 };

    static double V2Weight(int combo, int cap) => Math.Min(Math.Max(0.5, Math.Log(combo, 4.0)), Math.Log(cap, 4.0));

    // The perfect play: osu!'s objects one by one (a slider's head, repeats and end, 30 each, then its ticks, 10 each,
    // then its 300, which adds no combo; circles and spinners a 300 that does); taiko's notes, drumroll ends and
    // swells a 300 each (big notes and big drumrolls 8 times that); catch's fruits 300, droplets 100 (tiny droplets
    // counted apart, bananas nothing); mania's notes and holds' heads and tails a MAX each.
    void InitScoreV2()
    {
        if (!ScoreV2 || mode != 0 && !ModeV2) return;
        v2Mul = mods.ScoreMultiplierV2(mode, maniaKeyChange);
        int c = 0;
        void Add(int value, bool addsCombo) { if (addsCombo) c++; v2ComboMax += value * (1 + c / 10.0); }
        void AddLog(int value, int cap) { c++; v2ComboMax += value * V2Weight(c, cap); v2Total++; }
        switch (mode)
        {
            case 1:
                foreach (var n in tNotes) AddLog(n.Big && n.Kind != TKind.Swell ? 2400 : 300, 400);
                break;
            case 2:
                foreach (var f in cFruits)
                    if (f.Kind == CKind.Tiny) v2TinyMax++;
                    else if (f.Kind == CKind.Fruit) AddLog(300, 200);
                    else if (f.Kind == CKind.Droplet) { c++; v2ComboMax += 100 * V2Weight(c, 200); }
                v2TinyRatio = v2TinyMax + v2Total == 0 ? 0 : (double)v2TinyMax / (v2TinyMax + v2Total);
                break;
            case 3:
                foreach (var n in mNotes) { AddLog(300, 400); if (n.Hold) AddLog(300, 400); }
                break;
            default:
                v2Total = objs.Count;
                foreach (var o in objs)
                {
                    if (o.Type == ObjType.Slider)
                    {
                        for (int k = 0; k < o.Slides + 1; k++) Add(30, true);
                        for (int k = 0; k < o.Checkpoints.Count(cp => cp.Kind == CheckpointKind.Tick); k++) Add(10, true);
                        Add(300, false);
                    }
                    else Add(300, true);
                }
                break;
        }
    }

    // a judgement worth points, after the combo has counted it
    void V2Hit(int value)
    {
        if (!ScoreV2 || mode != 0 && !ModeV2) return;
        if (mode == 0) v2Combo += value * (1 + combo / 10.0);
        else if (mode != 3 || combo > 0) v2Combo += value * V2Weight(combo, mode == 2 ? 200 : 400);
        UpdateV2();
    }

    void V2Bonus(int value)
    {
        if (!ScoreV2 || mode != 0 && !ModeV2) return;
        v2Bonus += value;
        UpdateV2();
    }

    void V2Tiny()
    {
        if (!ModeV2) return;
        v2Tiny++;
        UpdateV2();
    }

    // (--auto-check: a perfect play fills every part; osu!'s combo part can end a little off, as stable's perfect play takes
    // a slider's head, repeats and end before its ticks)
    int V2Judged => mode == 3 ? n300 + n100 + n50 + nMiss + nGeki + nKatu : n300 + n100 + n50 + nMiss;
    internal bool V2Full => v2ComboMax > 0 && (mode == 0 || Math.Abs(v2Combo / v2ComboMax - 1) < 1e-9) && (mode == 2 || V2Judged == v2Total) && v2Tiny == v2TinyMax;
    internal string V2Parts => $"combo {v2Combo:0.##}/{v2ComboMax:0.##} judged {V2Judged}/{v2Total} tiny {v2Tiny}/{v2TinyMax} bonus {v2Bonus} x{v2Mul} score {score}";

    void UpdateV2()
    {
        if (!ScoreV2 || mode != 0 && !ModeV2) return;
        if (v2Total == 0 || v2ComboMax == 0) { score = 0; return; }
        double s;
        switch (mode)
        {
            case 1:
            {
                int judged = n300 + n100 + n50 + nMiss;
                float acc = judged <= 0 ? 0f : (n100 * 150 + n300 * 300) / (float)(judged * 300);
                s = 250000.0 * v2Combo / v2ComboMax + 750000.0 * Math.Pow(acc, 3.6) * ((double)judged / v2Total) + v2Bonus;
                break;
            }
            case 2:
                s = (600000.0 + 400000.0 * (1.0 - v2TinyRatio)) * v2Combo / v2ComboMax + 400000.0 * v2TinyRatio * (v2TinyMax == 0 ? 0.0 : (double)v2Tiny / v2TinyMax) + v2Bonus;
                break;
            case 3:
            {
                int judged = n300 + n100 + n50 + nMiss + nGeki + nKatu;
                float acc = judged == 0 ? 1f : (50 * n50 + 100 * n100 + 200 * nKatu + 300 * n300 + 305 * nGeki) / (float)(judged * 305);
                s = 200000.0 * v2Combo / v2ComboMax + 800000.0 * Math.Pow(acc, 2f + 2f * acc) * ((double)judged / v2Total);
                break;
            }
            default:
            {
                int judged = n300 + n100 + n50 + nMiss;
                double acc = judged == 0 ? 1 : (300.0 * n300 + 100.0 * n100 + 50.0 * n50) / (300.0 * judged);
                s = 700000.0 * v2Combo / v2ComboMax + 300000.0 * Math.Pow(acc, 10.0) * ((double)judged / v2Total) + v2Bonus;
                break;
            }
        }
        score = (long)Math.Round(s * v2Mul);
    }
}
