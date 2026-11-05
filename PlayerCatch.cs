using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// osu!catch, as stable plays it: fruits fall from y -100 to the catcher's plate at y 340 (osu!px, in a playfield 72
// units down) over the approach time, carrying on 20% further; the catcher (106.75 osu!px wide at CS 5, catching within
// 40% of that either side) walks 0.5 osu!px a ms and dashes at 1, and a hyperdash fruit flings it to the next one.
sealed partial class Player
{
    internal enum CKind { Fruit, Droplet, Tiny, Banana }

    internal sealed class CFruit
    {
        public CKind Kind;
        public double Time;
        public float X;
        public int Colour, Visual;
        public bool Hyper, Done, Caught, LastInCombo, NewCombo;
        public CFruit? HyperTarget;
    }

    readonly List<CFruit> cFruits = [];
    readonly List<(float Dx, float Dy, int Visual, int Colour)> cPlate = [];
    int cNext;
    float cX = 256, cWidth, cDir = 1;
    double cLastTime = double.NaN, cFailUntil = double.MinValue;
    float cHyperSpeed; int cHyperDir; CFruit? cHyperTarget;

    // stable's catch effects. A caught fruit flashes up from the plate in its colour, additive, riding with the catcher
    // (scoreboard-explosion-2 stretching 1x0.9 -> 16x1.1 times the combo / 200 (0.35-1.125) over 160ms, gone over
    // 300ms; scoreboard-explosion-1 0.9x1 -> 0.9x1.3 over 500ms, gone over 700ms; a banana only the second). Caught
    // droplets and bananas pop off the plate (4x their spot across, 120 up), and at a combo's end so does the plate
    // (the end of a banana shower: 400 up, with a wide white flash); after a miss it drops. Thrown things fall at
    // 480/s² and fade over 2s (dropped: 1s). A hyperdashing catcher leaves half-faded copies of itself every 16ms
    // (gone over 250ms, 100 if it didn't move) and one in the after-image colour where it lands (growing 1.2x and
    // fading over 1.2s).
    readonly List<(float Dx, Color Colour, double At, float Stretch, bool Banana)> cFlashes = [];
    readonly List<(float X, float Y, float Vx, float Vy, string Tex, Color Colour, float Scale, double At, double Fade)> cThrown = [];
    readonly List<(float X, Sprite Tex, bool Flip, Color Tint, double At, double Fade, bool Landing)> cGhosts = [];
    double cShowerFlashAt = double.NegativeInfinity, cGhostAt = double.NegativeInfinity, cThrownTicks;
    float cGhostX = float.NaN;
    bool cWasHyper;
    KeyboardKey cLeft, cRight, cDash;
    (float X, bool Dash) cRecorded = (float.NaN, false);
    (float X, double T) cAutoFrom = (256, double.MinValue);
    readonly LegacyRandom cPlateRng = new(1337);

    // stable's random (seeded 1337): tiny droplets' jitter, bananas' spots
    internal sealed class LegacyRandom(int seed)
    {
        uint x = (uint)seed, y = 842502087, z = 3579807591, w = 273326509;
        uint NextUInt() { uint t = x ^ (x << 11); x = y; y = z; z = w; return w = w ^ (w >> 19) ^ t ^ (t >> 8); }
        public int Next() => (int)(0x7FFFFFFF & NextUInt());
        public double NextDouble() => 1.0 / (int.MaxValue + 1.0) * Next();
        public int Next(int lo, int hi) => (int)(lo + NextDouble() * (hi - lo));
        public int Next(double lo, double hi) => (int)(lo + NextDouble() * (hi - lo));
        uint bits; int bitIndex = 32;
        public bool NextBool()
        {
            if (bitIndex == 32) { bits = NextUInt(); bitIndex = 1; return (bits & 1) == 1; }
            bitIndex++;
            return ((bits >>= 1) & 1) == 1;
        }
    }

    void CatchSetup()
    {
        cLeft = Config.Key("keyFruitsLeft", KeyboardKey.Left);
        cRight = Config.Key("keyFruitsRight", KeyboardKey.Right);
        cDash = Config.Key("keyFruitsDash", KeyboardKey.LeftShift);
        float cs = (float)map.CS;
        cWidth = 106.75f * (1 - 0.7f * (cs - 5) / 5);
        cFruits.AddRange(CatchConvert(map, cWidth, mods.HasFlag(Mods.HardRock)));
        // stable's catch HP calibration runs over these objects (in time order)
        health.CalibrateCatch(map, cFruits.Select((f, i) => (f.Time, (int)f.Kind, f.Kind == CKind.Fruit && (i + 1 == cFruits.Count || cFruits[i + 1].NewCombo))).ToList(), mods.HasFlag(Mods.HalfTime));
    }

    // stable's catch conversion: the fruits, droplets, tiny droplets and bananas, with the hyperdashes for a catcher this wide
    internal static List<CFruit> CatchConvert(Beatmap map, float cWidth, bool hardRock = false)
    {
        var fruits = new List<CFruit>();
        var rng = new LegacyRandom(1337);
        // HardRock: stable nudges fruits (not slider fruits) apart: a fruit closer than a third of its time gap to the
        // last fruit or slider end moves that much further, and one right on it moves randomly (up to 20, a quarter of
        // the gap), unless over a second apart
        float? hrLast = null;
        double hrLastTime = 0;
        float HardRockX(float x, double t)
        {
            if (hrLast is null or 0) { hrLast = x; hrLastTime = t; return x; }
            float diff = x - hrLast.Value;
            int gap = (int)(t - hrLastTime);
            if (gap > 1000) { hrLast = x; hrLastTime = t; return x; }
            if (diff == 0)
            {
                bool right = rng.NextBool();
                float rand = Math.Min(20, (float)rng.Next(0, Math.Max(0, gap / 4d)));
                return right ? x + rand <= 512 ? x + rand : x - rand : x - rand >= 0 ? x - rand : x + rand;
            }
            if (Math.Abs(diff) < gap / 3)
            {
                if (diff > 0) { if (x + diff < 512) x += diff; }
                else if (x + diff > 0) x += diff;
            }
            hrLast = x; hrLastTime = t;
            return x;
        }
        int index = 0;
        foreach (var o in map.Objects)
        {
            bool first = true; // (the object's first fruit carries its new combo)
            void Add(CKind k, double t, float x) { fruits.Add(new CFruit { Kind = k, Time = t, X = Math.Clamp(x, 0, 512), Colour = o.ColourIndex, Visual = index++ % 4, NewCombo = first && o.StartsCombo }); first = false; }
            if (o.Type == ObjType.Circle) Add(CKind.Fruit, o.Time, hardRock ? HardRockX(o.Pos.X, o.Time) : o.Pos.X);
            else if (o.Type == ObjType.Slider)
            {
                // the head, ticks (droplets), repeats and the tail (fruits), placed by how far along the path they are, at
                // the slider's own speed (slider velocity 0.1-10x; old maps' ticks spaced with it too); tiny droplets go
                // between them all (the last tick 36ms before the end counting) when more than 80ms apart, at most 100ms apart
                if (hardRock) { hrLast = o.LastControlPoint.X; hrLastTime = o.Time; } // (a slider: its last point and start)
                var red = map.UninheritedAt(o.Time);
                var point = map.PointAt(o.Time);
                double svBeat = !point.Uninherited && point.Time >= red.Time && point.BeatLength < 0 ? Math.Clamp((float)-point.BeatLength, 10f, 1000f) / 100.0 : 1;
                double velocity = 100 * map.SliderMultiplier / (red.BeatLength * svBeat);
                double length = Math.Min(100000, o.Length);
                double tickDist = Math.Clamp(velocity * red.BeatLength / map.SliderTickRate * (map.FormatVersion < 8 ? svBeat : 1), 0, length);
                double spanDur = length / velocity, fromEnd = velocity * 10;
                var events = new List<(double T, double P, CKind? K)> { (o.Time, 0, CKind.Fruit) };
                for (int span = 0; span < o.Slides; span++)
                {
                    double spanStart = o.Time + span * spanDur;
                    bool back = span % 2 == 1;
                    var ticks = new List<(double T, double P, CKind? K)>();
                    for (double d = tickDist; tickDist != 0 && d <= length && d < length - fromEnd; d += tickDist)
                        ticks.Add((spanStart + (back ? 1 - d / length : d / length) * spanDur, d / length, CKind.Droplet));
                    if (back) ticks.Reverse();
                    events.AddRange(ticks);
                    if (span < o.Slides - 1) events.Add((spanStart + spanDur, (span + 1) % 2, CKind.Fruit));
                }
                double total = o.Slides * spanDur, lastSpan = o.Time + (o.Slides - 1) * spanDur;
                double lastTick = Math.Max(o.Time + total / 2, lastSpan + spanDur - 36), lastP = (lastTick - lastSpan) / spanDur;
                events.Add((lastTick, o.Slides % 2 == 0 ? 1 - lastP : lastP, null));
                events.Add((o.Time + total, o.Slides % 2, CKind.Fruit));
                float XAt(double p) => o.PointAt(Math.Clamp(p, 0, 1) * length).X;
                for (int e = 0; e < events.Count; e++)
                {
                    if (e > 0)
                    {
                        var (t0, p0, _) = events[e - 1];
                        double since = (int)events[e].T - (int)t0;
                        if (since > 80)
                        {
                            double step = since;
                            while (step > 100) step /= 2;
                            for (double t = step; t < since; t += step)
                            {
                                float tx = XAt(p0 + t / since * (events[e].P - p0));
                                fruits.Add(new CFruit { Kind = CKind.Tiny, Time = t0 + t, X = Math.Clamp(tx + Math.Clamp(rng.Next(-20, 20), -tx, 512 - tx), 0, 512), Colour = o.ColourIndex });
                            }
                        }
                    }
                    if (events[e].K is { } k)
                    {
                        if (k == CKind.Droplet) rng.Next(); // (stable's droplet spin)
                        Add(k, events[e].T, XAt(events[e].P));
                    }
                }
            }
            else if (o.Type == ObjType.Spinner)
            {
                // a banana shower: every duration / 2^n (at most 100ms) from start to end, at random spots
                // (stable counts in single precision from the whole-ms start to the whole-ms end)
                float spacing = (float)(o.EndTime - o.Time);
                while (spacing > 100) spacing /= 2;
                if (spacing <= 0) continue;
                for (float t = (int)o.Time; t <= (int)o.EndTime; t += spacing)
                {
                    fruits.Add(new CFruit { Kind = CKind.Banana, Time = t, X = (float)(rng.NextDouble() * 512), Colour = o.ColourIndex });
                    rng.Next(); rng.Next(); rng.Next();
                }
            }
        }
        fruits.Sort((a, b) => a.Time.CompareTo(b.Time));
        CFruit? nextFruit = null; // (walking back: the fruit after each)
        for (int i = fruits.Count - 1; i >= 0; i--)
        {
            if (fruits[i].Kind != CKind.Fruit) continue;
            fruits[i].LastInCombo = nextFruit == null || nextFruit.Colour != fruits[i].Colour;
            nextFruit = fruits[i];
        }

        // stable's hyperdashes: where even a dash can't make the next fruit or droplet in time (a quarter frame's grace,
        // counting the plate's half width, or what's left of it when carrying on the same way), the catcher is flung there
        var palpable = fruits.Where(f => f.Kind is CKind.Fruit or CKind.Droplet).ToList();
        float half = cWidth / 2; // (stable: the full catcher, margins included)
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
        return fruits;
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
            if (relax)
            {
                // Relax: the catcher follows the mouse
                float s = Game.H / 480f, mx = (Input.GetMousePosition().X - (Game.W - 512 * s) / 2) / s;
                if (Math.Abs(mx - cX) > 0.01f) cDir = mx > cX ? 1 : -1;
                cX = mx;
            }
            else if (move != 0) { cX += move * speed * (float)dt; cDir = move; }
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
                case CKind.Droplet: n100++; score += 100; combo++; health.Apply(HpEvent.SliderTick); break; // (stable: a flat 100)
                case CKind.Tiny: n50++; score += 10; health.Add(health.mul * 3 * 0.1f); break;
                case CKind.Banana: score += 1100; health.Add(health.mul * 2); break; // (a bonus: no combo)
            }
            maxCombo = Math.Max(maxCombo, combo);
            cFailUntil = double.MinValue;
            var colour = colours[f.Colour % colours.Length];
            if (f.Kind is CKind.Fruit or CKind.Droplet)
            {
                // fruits land on the plate (stable stacks them 10px apart); droplets pop off it
                var spot = new Vector2(f.X - cX, -5);
                for (int tries = 0; tries < 50 && cPlate.Any(p => Vector2.DistanceSquared(new(p.Dx, p.Dy), spot) < 100); tries++)
                    spot += new Vector2(cPlateRng.Next(-5, 5), -cPlateRng.Next(0, 5));
                if (f.Kind == CKind.Fruit)
                {
                    if (cPlate.Count < 100) cPlate.Add((spot.X, spot.Y, f.Visual, f.Colour));
                    CatchFlash(f.X - cX, colour, banana: false);
                }
                else cThrown.Add((cX + spot.X, 340 + spot.Y, spot.X * 4, -120, "fruit-drop", colour, 0.8f * 0.5f, Game.NowMs, 2000));
            }
            if (f.Kind == CKind.Banana)
            {
                CatchFlash(f.X - cX, new Color(255, 240, 0, 255), banana: true);
                cThrown.Add((f.X, 340 - 5, (f.X - cX) * 4, -120, "fruit-bananas", new Color(255, 240, 0, 255), 0.5f, Game.NowMs, 2000));
            }
            if (f.LastInCombo) ClearPlate(thrown: true, shower: f.Kind == CKind.Banana);
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
            if (f.LastInCombo) ClearPlate(thrown: false, shower: f.Kind == CKind.Banana);
            if (f.Kind is CKind.Fruit or CKind.Droplet) cHyperDir = 0;
        }
    }

    void CatchFlash(float dx, Color colour, bool banana)
    {
        float half = cWidth / 2, margin = cWidth * 0.1f; // (the flash stays over the plate)
        cFlashes.Add((Math.Clamp(dx, -half + margin * 3, half - margin * 3), colour, Game.NowMs, Math.Clamp(combo / 200f, 0.35f, 1.125f), banana));
    }

    // a combo's end: the plate's fruits thrown off (a banana shower's higher, with a flash), or dropped after a miss
    void ClearPlate(bool thrown, bool shower)
    {
        foreach (var p in cPlate)
            cThrown.Add((cX + p.Dx, 340 + p.Dy, thrown ? p.Dx * 4 : 0, thrown ? (shower ? -400 : -120) : 0, FruitNames[p.Visual], colours[p.Colour % colours.Length], 0.5f, Game.NowMs, thrown ? 2000 : 1000));
        cPlate.Clear();
        if (shower && thrown) cShowerFlashAt = Game.NowMs;
    }

    static readonly string[] FruitNames = ["fruit-pear", "fruit-grapes", "fruit-apple", "fruit-orange"];

    void DrawCatchEffects(Func<float, float, Vector2> screen, float s, float cs, Sprite? catcher, float alpha)
    {
        double now = Game.NowMs;
        // the hyperdash's after-images
        bool hyper = cHyperDir != 0;
        if (catcher is { } ct)
        {
            if (hyper && now - cGhostAt > 16)
            {
                cGhosts.Add((cX, ct, cDir < 0, skin.HyperDash, now, cX != cGhostX ? 250 : 100, false));
                cGhostAt = now; cGhostX = cX;
            }
            if (cWasHyper && !hyper) cGhosts.Add((cX, ct, cDir < 0, skin.HyperDashAfterImage ?? skin.HyperDash, now, 1200, true));
        }
        cWasHyper = hyper;
        cGhosts.RemoveAll(g => now - g.At > g.Fade);
        BeginBlendMode(BlendMode.Additive);
        foreach (var g in cGhosts)
        {
            double age = now - g.At;
            float a = (g.Landing ? 1 : 0.5f) * (1 - (float)(age / g.Fade)), grow = g.Landing ? 1 + 0.2f * (float)Easing.Apply(2, age / g.Fade) : 1;
            var top = screen(g.X, 340) - new Vector2(0, 16 * cs);
            g.Tex.Draw(top + new Vector2(0, g.Tex.Height * cs / 2), cs * grow, ColorAlpha(g.Tint, a * alpha), flipX: g.Flip);
        }

        // the flashes over the plate
        cFlashes.RemoveAll(f => now - f.At > 700);
        var streak = skin.Tex("scoreboard-explosion-2");
        var glow = skin.Tex("scoreboard-explosion-1");
        foreach (var f in cFlashes)
        {
            double age = now - f.At;
            var at = screen(cX + f.Dx, 340);
            if (!f.Banana && streak is { } st && age < 300)
            {
                float e = (float)Easing.Apply(1, Math.Min(1, age / 160));
                DrawUp(st, at, (1 + (16 * f.Stretch - 1) * e) * s, (0.9f + 0.2f * e) * s, ColorAlpha(f.Colour, (1 - (float)(age / 300)) * alpha));
            }
            if (glow is { } gl)
            {
                float e = (float)Easing.Apply(1, Math.Min(1, age / 500));
                DrawUp(gl, at, 0.9f * s, (1 + 0.3f * e) * s, ColorAlpha(f.Colour, (1 - (float)(age / 700)) * alpha));
            }
        }
        // a banana shower's end: a wide white flash
        if (now - cShowerFlashAt < 800 && glow is { } sg)
        {
            double age = now - cShowerFlashAt;
            float e = (float)Easing.Apply(1, Math.Min(1, age / 500));
            DrawUp(sg, screen(cX, 340), 4 * e * s, (1.8f + 0.4f * e) * s, ColorAlpha(Color.White, (1 - (float)(age / 800)) * alpha));
        }
        EndBlendMode();

        // thrown and dropped fruit, falling (stable: 8 osu!px/s more each 60Hz frame)
        for (cThrownTicks += GetFrameTime() * 60; cThrownTicks >= 1; cThrownTicks--)
            for (int i = 0; i < cThrown.Count; i++)
            {
                var t = cThrown[i];
                t.Vy += 8; t.X += t.Vx / 60; t.Y += t.Vy / 60;
                cThrown[i] = t;
            }
        cThrown.RemoveAll(t => now - t.At > t.Fade);
        float circle = (float)(64 * (1 - 0.7 * (map.CS - 5) / 5)) / 128 * s;
        foreach (var t in cThrown)
        {
            float a = (1 - (float)((now - t.At) / t.Fade)) * alpha;
            var at = screen(t.X, t.Y);
            skin.Tex(t.Tex)?.Draw(at, circle * t.Scale, ColorAlpha(t.Colour, a));
            skin.Tex(t.Tex + "-overlay")?.Draw(at, circle * t.Scale, ColorAlpha(Color.White, a));
        }
    }

    // a sprite standing up from a point (stable: centre-left origin turned a quarter anticlockwise), length x thickness
    static void DrawUp(Sprite sp, Vector2 at, float length, float thick, Color tint)
    {
        float w = sp.Width * length, h = sp.Height * thick;
        DrawTexturePro(sp.Tex, new Rectangle(0, 0, sp.Tex.Width, sp.Tex.Height), new Rectangle(at.X, at.Y, w, h), new Vector2(0, h / 2), -90, tint);
    }

    void CatchDraw(int w, int h)
    {
        UpdateComboBursts();
        DrawComboBursts(w, h);
        float s = h / 480f;
        var o = new Vector2((w - 512 * s) / 2, 72 * s);
        Vector2 Screen(float x, float y) => o + new Vector2(x, y) * s;
        float circle = (float)(64 * (1 - 0.7 * (map.CS - 5) / 5)) / 128 * s; // px per sprite px: an osu! circle's size
        double preempt = map.Preempt;
        float alpha = playfieldAlpha;
        var hyperRed = skin.HyperDashFruit ?? skin.HyperDash;

        for (int i = cFruits.Count - 1; i >= 0; i--)
        {
            var f = cFruits[i];
            if (f.Time - preempt > time || f.Caught) continue;
            if (f.Done && time > f.Time + preempt * 0.2) continue; // (a miss falls on a little, then is gone)
            float y = -100 + 440 * (float)((time - (f.Time - preempt)) / preempt);
            var at = Screen(f.X, y);
            // stable's Hidden: fading out from 60% to 44% of the approach time before the catch
            float fa = !mods.HasFlag(Mods.Hidden) ? alpha : alpha * (float)Math.Clamp(((f.Time - (int)(preempt * 0.44)) - time) / ((int)(preempt * 0.6) - (int)(preempt * 0.44)), 0, 1);
            if (fa <= 0) continue;
            var colour = ColorAlpha(colours[f.Colour % colours.Length], fa);
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
        DrawCatchEffects(Screen, s, cs, catcher, alpha);
        foreach (var p in cPlate)
        {
            var at = Screen(cX + p.Dx, 340 + p.Dy);
            skin.Tex(FruitNames[p.Visual])?.Draw(at, circle * 0.5f, ColorAlpha(colours[p.Colour % colours.Length], alpha));
            skin.Tex(FruitNames[p.Visual] + "-overlay")?.Draw(at, circle * 0.5f, ColorAlpha(Color.White, alpha));
        }
        if (catcher is { } c)
        {
            var tint = cHyperDir != 0 ? skin.HyperDash : Color.White;
            var top = Screen(cX, 340) - new Vector2(0, 16 * cs);
            c.Draw(top + new Vector2(0, c.Height * cs / 2), cs, ColorAlpha(tint, alpha), flipX: cDir < 0);
        }
    }
}
