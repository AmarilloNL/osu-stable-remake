using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// osu!catch, as stable plays it: fruits fall from y -100 to the catcher's plate at y 340 (osu!px, in a playfield 72
// units down) over the approach time, carrying on 20% further; the catcher (106.75 osu!px wide at CS 5, catching within
// 40% of that either side) walks 0.5 osu!px a ms and dashes at 1, and a hyperdash fruit flings it to the next one.
sealed partial class Player
{
    enum CKind { Fruit, Droplet, Tiny, Banana }

    sealed class CFruit
    {
        public CKind Kind;
        public double Time;
        public float X;
        public int Colour, Visual;
        public bool Hyper, Done, Caught, LastInCombo;
        public CFruit? HyperTarget;
    }

    readonly List<CFruit> cFruits = [];
    readonly List<(float Dx, float Dy, int Visual, int Colour)> cPlate = [];
    int cNext;
    float cX = 256, cWidth, cDir = 1;
    double cLastTime = double.NaN, cFailUntil = double.MinValue;
    float cHyperSpeed; int cHyperDir; CFruit? cHyperTarget;
    KeyboardKey cLeft, cRight, cDash;
    (float X, bool Dash) cRecorded = (float.NaN, false);
    (float X, double T) cAutoFrom = (256, double.MinValue);
    readonly LegacyRandom cPlateRng = new(1337);

    // stable's random (seeded 1337): tiny droplets' jitter, bananas' spots
    sealed class LegacyRandom(int seed)
    {
        uint x = (uint)seed, y = 842502087, z = 3579807591, w = 273326509;
        uint NextUInt() { uint t = x ^ (x << 11); x = y; y = z; z = w; return w = w ^ (w >> 19) ^ t ^ (t >> 8); }
        public int Next() => (int)(0x7FFFFFFF & NextUInt());
        public double NextDouble() => 1.0 / (int.MaxValue + 1.0) * Next();
        public int Next(int lo, int hi) => (int)(lo + NextDouble() * (hi - lo));
    }

    void CatchSetup()
    {
        cLeft = Config.Key("keyFruitsLeft", KeyboardKey.Left);
        cRight = Config.Key("keyFruitsRight", KeyboardKey.Right);
        cDash = Config.Key("keyFruitsDash", KeyboardKey.LeftShift);
        float cs = (float)map.CS;
        cWidth = 106.75f * (1 - 0.7f * (cs - 5) / 5);
        var rng = new LegacyRandom(1337);
        int index = 0;
        foreach (var o in objs)
        {
            void Add(CKind k, double t, float x) => cFruits.Add(new CFruit { Kind = k, Time = t, X = Math.Clamp(x, 0, 512), Colour = o.ColourIndex, Visual = index++ % 4 });
            if (o.Type == ObjType.Circle) Add(CKind.Fruit, o.Time, o.Pos.X);
            else if (o.Type == ObjType.Slider)
            {
                // the head, ticks (droplets), repeats and the tail (fruits), with tiny droplets between them all (the
                // last tick 36ms before the end counting) when more than 80ms apart, at most 100ms apart
                var events = new List<(double T, CKind? K)> { (o.Time, CKind.Fruit) };
                foreach (var c in o.Checkpoints.OrderBy(c => c.Time))
                    events.Add((c.Time, c.Kind switch { CheckpointKind.Tick => CKind.Droplet, CheckpointKind.Repeat => CKind.Fruit, _ => null }));
                events.Add((o.EndTime, CKind.Fruit));
                for (int e = 0; e < events.Count; e++)
                {
                    if (e > 0)
                    {
                        double since = (int)events[e].T - (int)events[e - 1].T;
                        if (since > 80)
                        {
                            double step = since;
                            while (step > 100) step /= 2;
                            for (double t = step; t < since; t += step)
                            {
                                float tx = o.PositionAtTime(events[e - 1].T + t).X;
                                cFruits.Add(new CFruit { Kind = CKind.Tiny, Time = events[e - 1].T + t, X = Math.Clamp(tx + Math.Clamp(rng.Next(-20, 20), -tx, 512 - tx), 0, 512), Colour = o.ColourIndex });
                            }
                        }
                    }
                    if (events[e].K is { } k)
                    {
                        if (k == CKind.Droplet) rng.Next(); // (stable's droplet spin)
                        Add(k, events[e].T, o.PositionAtTime(events[e].T).X);
                    }
                }
            }
            else if (o.Type == ObjType.Spinner)
            {
                // a banana shower: every duration / 2^n (at most 100ms) from start to end, at random spots
                double spacing = o.EndTime - o.Time;
                while (spacing > 100) spacing /= 2;
                if (spacing <= 0) continue;
                for (double t = o.Time; t <= o.EndTime; t += spacing)
                {
                    cFruits.Add(new CFruit { Kind = CKind.Banana, Time = t, X = (float)(rng.NextDouble() * 512), Colour = o.ColourIndex });
                    rng.Next(); rng.Next(); rng.Next();
                }
            }
        }
        cFruits.Sort((a, b) => a.Time.CompareTo(b.Time));
        CFruit? nextFruit = null; // (walking back: the fruit after each)
        for (int i = cFruits.Count - 1; i >= 0; i--)
        {
            if (cFruits[i].Kind != CKind.Fruit) continue;
            cFruits[i].LastInCombo = nextFruit == null || nextFruit.Colour != cFruits[i].Colour;
            nextFruit = cFruits[i];
        }

        // stable's hyperdashes: where even a dash can't make the next fruit or droplet in time (a quarter frame's grace,
        // counting the plate's half width, or what's left of it when carrying on the same way), the catcher is flung there
        var palpable = cFruits.Where(f => f.Kind is CKind.Fruit or CKind.Droplet).ToList();
        float half = cWidth / 2 / 0.8f;
        int lastDir = 0;
        double lastExcess = half;
        for (int i = 0; i + 1 < palpable.Count; i++)
        {
            CFruit a = palpable[i], b = palpable[i + 1];
            int dir = b.X > a.X ? 1 : -1;
            double timeToNext = (int)b.Time - (int)a.Time - 1000f / 60f / 4;
            double distToNext = Math.Abs(b.X - a.X) - (lastDir == dir ? lastExcess : half);
            float toHyper = (float)(timeToNext - distToNext);
            if (toHyper < 0) { a.Hyper = true; a.HyperTarget = b; lastExcess = half; }
            else lastExcess = Math.Clamp(toHyper, 0, half);
            lastDir = dir;
        }
    }

    bool CatchDone => cNext >= cFruits.Count;

    void CatchStep()
    {
        double dt = double.IsNaN(cLastTime) ? 0 : Math.Max(0, time - cLastTime);
        cLastTime = time;
        if (auto) CatchAutoMove();
        else if (playback == null && dt > 0)
        {
            bool dash = Down(cDash);
            float speed = (dash ? 1f : 0.5f) * (cHyperDir != 0 ? cHyperSpeed : 1);
            int move = (Down(cRight) ? 1 : 0) - (Down(cLeft) ? 1 : 0);
            if (move != 0) { cX += move * speed * (float)dt; cDir = move; }
            // a hyperdash ends on reaching its fruit's spot
            if (cHyperDir != 0 && cHyperTarget != null && (cHyperDir > 0 ? cX >= cHyperTarget.X : cX <= cHyperTarget.X)) { cX = cHyperTarget.X; cHyperDir = 0; }
            cX = MathF.Round(Math.Clamp(cX, 0, 512) * Replays.PosScale) / Replays.PosScale; // (on the replay's grid, so playback is exact)
            if (cX != cRecorded.X || dash != cRecorded.Dash) { recording.Add(new ReplayFrame(time, cX, 0, (byte)(dash ? 1 : 0))); cRecorded = (cX, dash); }
        }
        else if (playback != null)
            // a replay: the catcher where its latest frame put it
            while (pbIndex < playback.Length && playback[pbIndex].Time <= time)
            {
                float x = Math.Clamp(playback[pbIndex++].X, 0, 512);
                if (x != cX) cDir = x > cX ? 1 : -1;
                cX = x;
            }

        for (int i = cNext; i < cFruits.Count && cFruits[i].Time <= time; i++)
        {
            var f = cFruits[i];
            if (f.Done) continue;
            f.Done = true;
            if (auto) { cX = f.X; cAutoFrom = (f.X, f.Time); } // (Auto lands exactly)
            f.Caught = Math.Abs(f.X - cX) < cWidth * 0.4f;
            CatchJudge(f);
        }
        while (cNext < cFruits.Count && cFruits[cNext].Done) cNext++;
    }

    void CatchAutoMove()
    {
        // stable's Auto glides to everything in time (tiny droplets and bananas too), from the last thing it caught
        CFruit? next = null;
        for (int i = cNext; i < cFruits.Count; i++)
            if (cFruits[i].Time > time) { next = cFruits[i]; break; }
        if (next == null) return;
        float from = cAutoFrom.X;
        double t0 = Math.Max(cAutoFrom.T, next.Time - 1000); // (a long gap: setting off a second before)
        float x = next.Time > t0 ? from + (next.X - from) * (float)Math.Clamp((time - t0) / (next.Time - t0), 0, 1) : next.X;
        if (x != cX) cDir = x > cX ? 1 : -1;
        cX = x;
    }

    void CatchJudge(CFruit f)
    {
        if (f.Caught)
        {
            switch (f.Kind)
            {
                case CKind.Fruit: n300++; ScoreHit(300); combo++; health.Apply(HpEvent.Hit300); break;
                case CKind.Droplet: n100++; ScoreHit(100); combo++; health.Apply(HpEvent.SliderTick); break;
                case CKind.Tiny: n50++; score += 10; break;
                case CKind.Banana: score += 1000; break;
            }
            maxCombo = Math.Max(maxCombo, combo);
            cFailUntil = double.MinValue;
            if (f.Kind is CKind.Fruit or CKind.Droplet)
            {
                // fruits and droplets land on the plate (stable stacks them 10px apart), cleared at each combo's end
                var spot = new Vector2(f.X - cX, -5);
                for (int tries = 0; tries < 50 && cPlate.Any(p => Vector2.DistanceSquared(new(p.Dx, p.Dy), spot) < 100); tries++)
                    spot += new Vector2(cPlateRng.Next(-5, 5), -cPlateRng.Next(0, 5));
                if (f.Kind == CKind.Fruit && cPlate.Count < 100) cPlate.Add((spot.X, spot.Y, f.Visual, f.Colour));
                if (f.LastInCombo) cPlate.Clear();
            }
            // catching a hyperdash fruit flings the catcher towards the next one, arriving 16.7ms early
            if (f.Hyper && f.HyperTarget is { } target && !auto)
            {
                cHyperTarget = target;
                cHyperSpeed = Math.Abs(target.X - cX) / Math.Max(1f, (float)(target.Time - time) - 16.666666f);
                cHyperDir = target.X > cX ? 1 : -1;
            }
            else if (f.Kind is CKind.Fruit or CKind.Droplet) cHyperDir = 0;
        }
        else
        {
            switch (f.Kind)
            {
                case CKind.Fruit: nMiss++; health.Apply(HpEvent.Miss); BreakCombo(); cFailUntil = time + 1000; break;
                case CKind.Droplet: nMiss++; health.Apply(HpEvent.SliderBreak); BreakCombo(); cFailUntil = time + 1000; break;
                case CKind.Tiny: nKatu++; break;
            }
            if (f.LastInCombo) cPlate.Clear();
            if (f.Kind is CKind.Fruit or CKind.Droplet) cHyperDir = 0;
        }
    }

    static readonly string[] FruitNames = ["fruit-pear", "fruit-grapes", "fruit-apple", "fruit-orange"];

    void CatchDraw(int w, int h)
    {
        float s = h / 480f;
        var o = new Vector2((w - 512 * s) / 2, 72 * s);
        Vector2 Screen(float x, float y) => o + new Vector2(x, y) * s;
        float circle = (float)(64 * (1 - 0.7 * (map.CS - 5) / 5)) / 128 * s; // px per sprite px: an osu! circle's size
        double preempt = map.Preempt;
        float alpha = playfieldAlpha;
        var hyperRed = new Color(255, 0, 0, 255);

        for (int i = cFruits.Count - 1; i >= 0; i--)
        {
            var f = cFruits[i];
            if (f.Time - preempt > time || f.Caught) continue;
            if (f.Done && time > f.Time + preempt * 0.2) continue; // (a miss falls on a little, then is gone)
            float y = -100 + 440 * (float)((time - (f.Time - preempt)) / preempt);
            var at = Screen(f.X, y);
            var colour = ColorAlpha(colours[f.Colour % colours.Length], alpha);
            switch (f.Kind)
            {
                case CKind.Fruit:
                    string name = FruitNames[f.Visual];
                    if (f.Hyper) skin.Tex(name)?.Draw(at, circle * 1.2f, ColorAlpha(hyperRed, 0.6f * alpha));
                    skin.Tex(name)?.Draw(at, circle, colour);
                    skin.Tex(name + "-overlay")?.Draw(at, circle, ColorAlpha(Color.White, alpha));
                    break;
                case CKind.Droplet:
                    if (f.Hyper) skin.Tex("fruit-drop")?.Draw(at, circle * 0.96f, ColorAlpha(hyperRed, 0.6f * alpha));
                    skin.Tex("fruit-drop")?.Draw(at, circle * 0.8f, colour);
                    break;
                case CKind.Tiny:
                    skin.Tex("fruit-drop")?.Draw(at, circle * 0.4f, colour);
                    break;
                case CKind.Banana:
                    skin.Tex("fruit-bananas")?.Draw(at, circle, ColorAlpha(new Color(255, 240, 0, 255), alpha));
                    skin.Tex("fruit-bananas-overlay")?.Draw(at, circle, ColorAlpha(Color.White, alpha));
                    break;
            }
        }

        // the catcher: idle, failing after a miss, or kiai; facing the way it last moved; red while hyperdashing
        string state = time < cFailUntil ? "fruit-catcher-fail" : map.PointAt(time).Kiai ? "fruit-catcher-kiai" : "fruit-catcher-idle";
        var catcher = skin.AnimFrame(state, Game.NowMs) ?? skin.Tex(state) ?? skin.AnimFrame("fruit-catcher-idle", Game.NowMs) ?? skin.Tex("fruit-ryuuta");
        float cs = cWidth / 305f * s; // stable: the catch width is 305 sprite px at the catcher's scale
        foreach (var p in cPlate)
        {
            var at = Screen(cX + p.Dx, 340 + p.Dy);
            skin.Tex(FruitNames[p.Visual])?.Draw(at, circle * 0.5f, ColorAlpha(colours[p.Colour % colours.Length], alpha));
            skin.Tex(FruitNames[p.Visual] + "-overlay")?.Draw(at, circle * 0.5f, ColorAlpha(Color.White, alpha));
        }
        if (catcher is { } c)
        {
            var tint = cHyperDir != 0 ? hyperRed : Color.White;
            var top = Screen(cX, 340) - new Vector2(0, 16 * cs);
            c.Draw(top + new Vector2(0, c.Height * cs / 2), cs, ColorAlpha(tint, alpha), flipX: cDir < 0);
        }
    }
}
