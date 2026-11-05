using System.Numerics;

namespace OsuStableRemake;

// stable's osu! -> osu!mania conversion: each osu! object becomes a pattern of notes and hold notes, chosen from how
// soon it follows the previous one, how far it is from it, the note density, its hitsounds and the map's "conversion
// difficulty", with stable's seeded random (from HP, CS, OD and AR) deciding the columns. Patterns remember the
// previous one (stacks, reversals, stairs, cycles).
static class ManiaConvert
{
    [Flags]
    enum P
    {
        None = 0, ForceStack = 1, ForceNotStack = 2, KeepSingle = 4, LowProbability = 8, Gathered = 128, Mirror = 256,
        Reverse = 512, Cycle = 1024, Stair = 2048, ReverseStair = 4096,
    }

    public readonly record struct Note(int Column, double Time, double End);

    sealed class Pattern
    {
        public readonly List<Note> Notes = [];
        readonly HashSet<int> columns = [];
        public bool Has(int c) => columns.Contains(c);
        public int Columns => columns.Count;
        public void Add(Note n) { Notes.Add(n); columns.Add(n.Column); }
        public void Add(Pattern p) { foreach (var n in p.Notes) Add(n); }
        public void Clear() { Notes.Clear(); columns.Clear(); }
    }

    // the state a conversion carries from object to object
    sealed class Ctx(Beatmap map, int total, Player.LegacyRandom rng)
    {
        public readonly Beatmap Map = map;
        public readonly int Total = total, Start = total == 8 ? 1 : 0; // (8 keys: the first is the special column)
        public readonly Player.LegacyRandom Rng = rng;
        double? difficulty;

        public int Column(float x, bool special = false)
        {
            if (special && Total == 8) return Math.Clamp((int)MathF.Floor(x / (512f / 7)), 0, 6) + 1;
            return Math.Clamp((int)MathF.Floor(x / (512f / Total)), 0, Total - 1);
        }

        public int RandomColumn(int? lo = null, int? hi = null) => Rng.Next(lo ?? Start, hi ?? Total);

        // stable's note count draw: 1, or 2-6 with the given chances of at least that many
        public int NoteCount(double p2, double p3, double p4 = 0, double p5 = 0, double p6 = 0)
        {
            double v = Rng.NextDouble();
            return v >= 1 - p6 ? 6 : v >= 1 - p5 ? 5 : v >= 1 - p4 ? 4 : v >= 1 - p3 ? 3 : v >= 1 - p2 ? 2 : 1;
        }

        // the map's conversion difficulty: HP and AR (4-7) plus the object density, at most 12
        public double Difficulty => difficulty ??= Math.Min(12, ComputeDifficulty());
        double ComputeDifficulty()
        {
            var objs = Map.Objects;
            double breaks = Map.Breaks.Sum(b => b.End - b.Start);
            int drain = objs.Count == 0 ? 0 : (int)((objs[^1].Time - objs[0].Time - breaks) / 1000);
            if (drain == 0) drain = 10000;
            return (((float)Map.RawHP + Math.Clamp((float)Map.RawAR, 4, 7)) / 1.5 + (double)objs.Count / drain * 9f) / 38f * 5f / 1.15;
        }

        // the first column from 'column' on that no pattern holds and 'valid' allows, others drawn by 'next' (random)
        public int Available(int column, int? lo = null, int? hi = null, Func<int, int>? next = null, Func<int, bool>? valid = null, params Pattern[] patterns)
        {
            int low = lo ?? Start, high = hi ?? Total;
            next ??= _ => RandomColumn(low, high);
            bool Ok(int c) => (valid?.Invoke(c) ?? true) && patterns.All(p => !p.Has(c));
            if (Ok(column)) return column;
            bool any = false;
            for (int c = low; c < high && !any; c++) any = Ok(c);
            if (!any) return column; // (stable would give up on the map; keep the note where it is)
            do column = next(column); while (!Ok(column));
            return column;
        }
    }

    // native: a mania map, its notes and holds as they are (in the column under their x); only spinners (old mania maps
    // have them) go through the conversion
    public static List<Note> Convert(Beatmap map, int total, bool native = false)
    {
        var rng = new Player.LegacyRandom((int)MathF.Round((float)map.RawHP + (float)map.RawCS) * 20 + (int)((float)map.RawOD * 41.2) + (int)MathF.Round((float)map.RawAR));
        var c = new Ctx(map, total, rng);
        var result = new List<Note>();
        var last = new Pattern();
        var times = new Queue<double>();
        double density = int.MaxValue, lastTime = 0;
        Vector2 lastPos = Vector2.Zero;
        P lastStair = P.Stair;

        void Density(double t)
        {
            times.Enqueue(t);
            if (times.Count > 7) times.Dequeue();
            if (times.Count >= 2) density = (times.Last() - times.Peek()) / times.Count;
        }

        foreach (var o in map.Objects)
        {
            if (native && o.Type != ObjType.Spinner)
            {
                result.Add(new Note(c.Column(o.Pos.X), o.Time, o.Type == ObjType.Circle ? o.Time : o.EndTime));
                continue;
            }
            switch (o.Type)
            {
                case ObjType.Circle:
                {
                    Density(o.Time);
                    var (p, stair) = Circle(c, o, last, lastTime, lastPos, density, lastStair);
                    lastTime = o.Time; lastPos = o.Pos;
                    lastStair = stair;
                    last = p;
                    result.AddRange(p.Notes);
                    break;
                }
                case ObjType.Slider:
                {
                    var s = new SliderInfo(c, o);
                    for (int i = 0; i <= s.Spans; i++)
                    {
                        double t = o.Time + s.Segment * i;
                        lastTime = t; lastPos = o.Pos;
                        Density(t);
                    }
                    foreach (var p in Slider(c, o, s, last))
                    {
                        last = p;
                        result.AddRange(p.Notes);
                    }
                    break;
                }
                case ObjType.Spinner:
                {
                    result.AddRange(Spinner(c, o, last).Notes);
                    lastTime = o.EndTime; lastPos = new Vector2(256, 192);
                    Density(o.EndTime);
                    break;
                }
                default: // (a hold note: as it is)
                    result.Add(new Note(c.Column(o.Pos.X), o.Time, o.EndTime));
                    lastTime = o.EndTime; lastPos = o.Pos;
                    Density(o.EndTime);
                    break;
            }
        }
        return result;
    }

    static bool Finish(int bits) => (bits & 4) != 0;
    static bool Clap(int bits) => (bits & 8) != 0;

    // ---- circles
    static (Pattern, P) Circle(Ctx c, HitObject o, Pattern prev, double prevTime, Vector2 prevPos, double density, P lastStair)
    {
        P stair = lastStair, type = P.None;
        double beat = c.Map.UninheritedAt(o.Time).BeatLength;
        bool kiai = c.Map.PointAt(o.Time).Kiai;
        float apart = Vector2.Distance(o.Pos, prevPos);
        double gap = o.Time - prevTime;
        if (gap <= 80) type |= P.ForceNotStack | P.KeepSingle;
        else if (gap <= 95) type |= P.ForceNotStack | P.KeepSingle | lastStair;
        else if (gap <= 105) type |= P.ForceNotStack | P.LowProbability;
        else if (gap <= 125) type |= P.ForceNotStack;
        else if (gap <= 135 && apart < 20) type |= P.Cycle | P.KeepSingle;
        else if (gap <= 150 && apart < 20) type |= P.ForceStack | P.LowProbability;
        else if (apart < 20 && density >= beat / 2.5) type |= P.Reverse | P.LowProbability;
        else if (density < beat / 2.5 || kiai) { } // dense: as it is
        else type |= P.LowProbability;
        if (!type.HasFlag(P.KeepSingle))
        {
            if (Finish(o.HitSound) && c.Total != 8) type |= P.Mirror;
            else if (Clap(o.HitSound)) type |= P.Gathered;
        }

        var pattern = CirclePattern(c, o, prev, type);
        foreach (var n in pattern.Notes)
        {
            if (type.HasFlag(P.Stair) && n.Column == c.Total - 1) stair = P.ReverseStair;
            if (type.HasFlag(P.ReverseStair) && n.Column == c.Start) stair = P.Stair;
        }
        return (pattern, stair);
    }

    static Pattern CirclePattern(Ctx c, HitObject o, Pattern prev, P type)
    {
        var pattern = new Pattern();
        void At(int column) => pattern.Add(new Note(column, o.Time, o.Time));
        if (c.Total == 1) { At(0); return pattern; }
        int lastColumn = prev.Notes.Count > 0 ? prev.Notes[0].Column : 0;
        if (type.HasFlag(P.Reverse) && prev.Notes.Count > 0)
        {
            for (int i = c.Start; i < c.Total; i++) if (prev.Has(i)) At(c.Start + c.Total - i - 1);
            return pattern;
        }
        if (type.HasFlag(P.Cycle) && prev.Notes.Count == 1 && (c.Total != 8 || lastColumn != 0) && (c.Total % 2 == 0 || lastColumn != c.Total / 2))
        {
            At(c.Start + c.Total - lastColumn - 1);
            return pattern;
        }
        if (type.HasFlag(P.ForceStack) && prev.Notes.Count > 0)
        {
            for (int i = c.Start; i < c.Total; i++) if (prev.Has(i)) At(i);
            return pattern;
        }
        if (prev.Notes.Count == 1)
        {
            if (type.HasFlag(P.Stair)) { At(lastColumn + 1 == c.Total ? c.Start : lastColumn + 1); return pattern; }
            if (type.HasFlag(P.ReverseStair)) { At(lastColumn - 1 == c.Start - 1 ? c.Total - 1 : lastColumn - 1); return pattern; }
        }
        if (type.HasFlag(P.KeepSingle)) return RandomNotes(c, o, prev, type, 1);
        double d = c.Difficulty;
        if (type.HasFlag(P.Mirror))
            return d > 6.5 ? Mirrored(c, o, prev, type, 0.12, 0.38, 0.12) : d > 4 ? Mirrored(c, o, prev, type, 0.12, 0.17, 0) : Mirrored(c, o, prev, type, 0.12, 0, 0);
        bool low = type.HasFlag(P.LowProbability);
        if (d > 6.5) return low ? RandomPattern(c, o, prev, type, 0.78, 0.42, 0, 0) : RandomPattern(c, o, prev, type, 1, 0.62, 0, 0);
        if (d > 4) return low ? RandomPattern(c, o, prev, type, 0.35, 0.08, 0, 0) : RandomPattern(c, o, prev, type, 0.52, 0.15, 0, 0);
        if (d > 2) return low ? RandomPattern(c, o, prev, type, 0.18, 0, 0, 0) : RandomPattern(c, o, prev, type, 0.45, 0, 0, 0);
        return RandomPattern(c, o, prev, type, 0, 0, 0, 0);
    }

    static Pattern RandomNotes(Ctx c, HitObject o, Pattern prev, P type, int count)
    {
        var pattern = new Pattern();
        bool stack = !type.HasFlag(P.ForceNotStack);
        if (!stack) count = Math.Min(count, c.Total - c.Start - prev.Columns);
        int column = c.Column(o.Pos.X, true);
        int Next(int last)
        {
            if (!type.HasFlag(P.Gathered)) return c.RandomColumn();
            last++;
            return last == c.Total ? c.Start : last;
        }
        for (int i = 0; i < count; i++)
        {
            column = stack ? c.Available(column, next: Next, patterns: pattern) : c.Available(column, next: Next, patterns: [pattern, prev]);
            pattern.Add(new Note(column, o.Time, o.Time));
        }
        return pattern;
    }

    static bool Special(HitObject o) => Clap(o.HitSound) && Finish(o.HitSound);

    static Pattern RandomPattern(Ctx c, HitObject o, Pattern prev, P type, double p2, double p3, double p4, double p5)
    {
        switch (c.Total)
        {
            case 2: p2 = p3 = p4 = p5 = 0; break;
            case 3: p2 = Math.Min(p2, 0.1); p3 = p4 = p5 = 0; break;
            case 4: p2 = Math.Min(p2, 0.23); p3 = Math.Min(p3, 0.04); p4 = p5 = 0; break;
            case 5: p3 = Math.Min(p3, 0.15); p4 = Math.Min(p4, 0.03); p5 = 0; break;
        }
        if (Clap(o.HitSound)) p2 = 1;
        var pattern = new Pattern();
        pattern.Add(RandomNotes(c, o, prev, type, c.NoteCount(p2, p3, p4, p5)));
        if (c.Start > 0 && Special(o)) pattern.Add(new Note(0, o.Time, o.Time));
        return pattern;
    }

    static Pattern Mirrored(Ctx c, HitObject o, Pattern prev, P type, double centre, double p2, double p3)
    {
        if (type.HasFlag(P.ForceNotStack)) return RandomPattern(c, o, prev, type, 1 / 2f + p2 / 2, p2, (p2 + p3) / 2, p3);
        switch (c.Total)
        {
            case 2: centre = p2 = p3 = 0; break;
            case 3: centre = Math.Min(centre, 0.03); p2 = p3 = 0; break;
            case 4: centre = 0; p2 = 1 - Math.Max((1 - p2) * 2, 0.8); p3 = 0; break;
            case 5: centre = Math.Min(centre, 0.03); p3 = 0; break;
            case 6: centre = 0; p2 = 1 - Math.Max((1 - p2) * 2, 0.5); p3 = 1 - Math.Max((1 - p3) * 2, 0.85); break;
        }
        p2 = Math.Clamp(p2, 0, 1);
        p3 = Math.Clamp(p3, 0, 1);
        double centreDraw = c.Rng.NextDouble();
        int count = c.NoteCount(p2, p3);
        bool toCentre = c.Total % 2 != 0 && count != 3 && centreDraw > 1 - centre;

        var pattern = new Pattern();
        int limit = (c.Total % 2 == 0 ? c.Total : c.Total - 1) / 2;
        int column = c.RandomColumn(hi: limit);
        for (int i = 0; i < count; i++)
        {
            column = c.Available(column, hi: limit, patterns: pattern);
            pattern.Add(new Note(column, o.Time, o.Time));
            pattern.Add(new Note(c.Start + c.Total - column - 1, o.Time, o.Time));
        }
        if (toCentre) pattern.Add(new Note(c.Total / 2, o.Time, o.Time));
        if (c.Start > 0 && Special(o)) pattern.Add(new Note(0, o.Time, o.Time));
        return pattern;
    }

    // ---- sliders: its spans' timing as stable's mania counts it (slider velocity clamped 0.01-10x)
    sealed class SliderInfo
    {
        public readonly int Start, End, Segment, Spans;
        public readonly bool Kiai;
        public SliderInfo(Ctx c, HitObject o)
        {
            var red = c.Map.UninheritedAt(o.Time);
            var point = c.Map.PointAt(o.Time);
            double mul = !point.Uninherited && point.Time >= red.Time && point.BeatLength < 0 ? Math.Clamp((float)-point.BeatLength, 10f, 10000f) / 100.0 : 1;
            double beat = red.BeatLength * mul;
            Spans = o.Slides;
            Start = (int)Math.Round(o.Time);
            End = (int)Math.Floor(Start + o.Length * beat * Spans * 0.01 / c.Map.SliderMultiplier);
            Segment = (End - Start) / Spans;
            Kiai = point.Kiai;
        }
    }

    static IEnumerable<Pattern> Slider(Ctx c, HitObject o, SliderInfo s, Pattern prev)
    {
        var pattern = SliderPattern(c, o, s, prev);
        if (pattern.Notes.Count == 1) { yield return pattern; yield break; }
        // stable hands on the notes not ending with the slider, then those that do (the next object follows those)
        Pattern mid = new(), atEnd = new();
        foreach (var n in pattern.Notes) (s.End != (int)Math.Round(n.End) ? mid : atEnd).Add(n);
        yield return mid;
        yield return atEnd;
    }

    // the sounds at a slider's edge (its own when it has none listed)
    static int EdgeSound(HitObject o, SliderInfo s, int time)
    {
        int i = s.Segment == 0 ? 0 : (time - s.Start) / s.Segment;
        return o.EdgeSounds.Length == 0 ? o.HitSound : o.EdgeSounds[Math.Clamp(i, 0, o.EdgeSounds.Length - 1)];
    }

    static Pattern SliderPattern(Ctx c, HitObject o, SliderInfo s, Pattern prev)
    {
        P type = s.Kiai ? P.None : P.LowProbability;
        int start = s.Start;
        if (c.Total == 1) { var p = new Pattern(); p.Add(new Note(0, start, s.End)); return p; }
        if (s.Spans > 1)
        {
            if (s.Segment <= 90) return RandomHolds(c, s, prev, start, 1);
            if (s.Segment <= 120) { type |= P.ForceNotStack; return SliderNotes(c, o, s, prev, type, start, s.Spans + 1); }
            if (s.Segment <= 160) return Stair(c, o, s, start);
            if (s.Segment <= 200 && c.Difficulty > 3) return MultipleNotes(c, o, s, start);
            if (s.End - s.Start >= 4000) return NRandomHolds(c, o, s, prev, type, start, 0.23, 0, 0);
            if (s.Segment > 400 && s.Spans < c.Total - 1 - c.Start) return TiledHolds(c, o, s, prev, type, start);
            return HoldAndNotes(c, o, s, prev, type, start);
        }
        if (s.Segment <= 110)
        {
            if (prev.Columns < c.Total) type |= P.ForceNotStack; else type &= ~P.ForceNotStack;
            return SliderNotes(c, o, s, prev, type, start, s.Segment < 80 ? 1 : 2);
        }
        bool low = type.HasFlag(P.LowProbability);
        double d = c.Difficulty;
        if (d > 6.5) return low ? NRandomHolds(c, o, s, prev, type, start, 0.78, 0.3, 0) : NRandomHolds(c, o, s, prev, type, start, 0.85, 0.36, 0.03);
        if (d > 4) return low ? NRandomHolds(c, o, s, prev, type, start, 0.43, 0.08, 0) : NRandomHolds(c, o, s, prev, type, start, 0.56, 0.18, 0);
        if (d > 2.5) return low ? NRandomHolds(c, o, s, prev, type, start, 0.3, 0, 0) : NRandomHolds(c, o, s, prev, type, start, 0.37, 0.08, 0);
        return low ? NRandomHolds(c, o, s, prev, type, start, 0.17, 0, 0) : NRandomHolds(c, o, s, prev, type, start, 0.27, 0, 0);
    }

    static Pattern RandomHolds(Ctx c, SliderInfo s, Pattern prev, int start, int count)
    {
        var pattern = new Pattern();
        int usable = c.Total - c.Start - prev.Columns;
        int column = c.RandomColumn();
        for (int i = 0; i < Math.Min(usable, count); i++)
        {
            column = c.Available(column, patterns: [pattern, prev]);
            pattern.Add(new Note(column, start, s.End));
        }
        for (int i = 0; i < count - usable; i++)
        {
            column = c.Available(column, patterns: pattern);
            pattern.Add(new Note(column, start, s.End));
        }
        return pattern;
    }

    static Pattern SliderNotes(Ctx c, HitObject o, SliderInfo s, Pattern prev, P type, int start, int count)
    {
        var pattern = new Pattern();
        int column = c.Column(o.Pos.X, true);
        if (type.HasFlag(P.ForceNotStack) && prev.Columns < c.Total) column = c.Available(column, patterns: prev);
        int last = column;
        for (int i = 0; i < count; i++)
        {
            pattern.Add(new Note(column, start, start));
            int was = last;
            column = c.Available(column, valid: x => x != was);
            last = column;
            start += s.Segment;
        }
        return pattern;
    }

    static Pattern Stair(Ctx c, HitObject o, SliderInfo s, int start)
    {
        var pattern = new Pattern();
        int column = c.Column(o.Pos.X, true);
        bool up = c.Rng.NextDouble() > 0.5;
        for (int i = 0; i <= s.Spans; i++)
        {
            pattern.Add(new Note(column, start, start));
            start += s.Segment;
            if (up)
            {
                if (column >= c.Total - 1) { up = false; column--; } else column++;
            }
            else if (column <= c.Start) { up = true; column++; }
            else column--;
        }
        return pattern;
    }

    static Pattern MultipleNotes(Ctx c, HitObject o, SliderInfo s, int start)
    {
        var pattern = new Pattern();
        bool legacy = c.Total >= 4 && c.Total <= 8;
        int interval = c.Rng.Next(1, c.Total - (legacy ? 1 : 0));
        int column = c.Column(o.Pos.X, true);
        for (int i = 0; i <= s.Spans; i++)
        {
            pattern.Add(new Note(column, start, start));
            column += interval;
            if (column >= c.Total - c.Start) column = column - c.Total - c.Start + (legacy ? 1 : 0);
            column += c.Start;
            if (c.Total > 2) pattern.Add(new Note(column, start, start));
            column = c.RandomColumn();
            start += s.Segment;
        }
        return pattern;
    }

    static Pattern NRandomHolds(Ctx c, HitObject o, SliderInfo s, Pattern prev, P type, int start, double p2, double p3, double p4)
    {
        switch (c.Total)
        {
            case 2: p2 = p3 = p4 = 0; break;
            case 3: p2 = Math.Min(p2, 0.1); p3 = p4 = 0; break;
            case 4: p2 = Math.Min(p2, 0.3); p3 = Math.Min(p3, 0.04); p4 = 0; break;
            case 5: p2 = Math.Min(p2, 0.34); p3 = Math.Min(p3, 0.1); p4 = Math.Min(p4, 0.03); break;
        }
        // two at once for a clap or finish (on the slider or its head), unless it's a low-probability one
        bool two = !type.HasFlag(P.LowProbability) && ((o.HitSound & 12) != 0 || (EdgeSound(o, s, s.Start) & 12) != 0);
        if (two) p2 = 1;
        return RandomHolds(c, s, prev, start, c.NoteCount(p2, p3, p4));
    }

    static Pattern TiledHolds(Ctx c, HitObject o, SliderInfo s, Pattern prev, P type, int start)
    {
        var pattern = new Pattern();
        int repeat = Math.Min(s.Spans, c.Total);
        int end = start + s.Segment * s.Spans;
        int column = c.Column(o.Pos.X, true);
        if (type.HasFlag(P.ForceNotStack) && prev.Columns < c.Total) column = c.Available(column, patterns: prev);
        for (int i = 0; i < repeat; i++)
        {
            column = c.Available(column, patterns: pattern);
            pattern.Add(new Note(column, start, end));
            start += s.Segment;
        }
        return pattern;
    }

    static Pattern HoldAndNotes(Ctx c, HitObject o, SliderInfo s, Pattern prev, P type, int start)
    {
        var pattern = new Pattern();
        int hold = c.Column(o.Pos.X, true);
        if (type.HasFlag(P.ForceNotStack) && prev.Columns < c.Total) hold = c.Available(hold, patterns: prev);
        pattern.Add(new Note(hold, start, s.End));
        int column = c.RandomColumn();
        double d = c.Difficulty;
        int count = d > 6.5 ? c.NoteCount(0.63, 0) : d > 4 ? c.NoteCount(c.Total < 6 ? 0.12 : 0.45, 0) : d > 2.5 ? c.NoteCount(c.Total < 6 ? 0 : 0.24, 0) : 0;
        count = Math.Min(c.Total - 1, count);
        bool ignoreHead = (EdgeSound(o, s, start) & 14) == 0; // (no whistle, finish or clap on the head)
        var row = new Pattern();
        for (int i = 0; i <= s.Spans; i++)
        {
            if (!(ignoreHead && start == s.Start))
                for (int j = 0; j < count; j++)
                {
                    column = c.Available(column, valid: x => x != hold, patterns: row);
                    row.Add(new Note(column, start, start));
                }
            pattern.Add(row);
            row.Clear();
            start += s.Segment;
        }
        return pattern;
    }

    // ---- spinners: one note, a hold when 100ms or longer, away from the previous pattern unless that filled every column
    static Pattern Spinner(Ctx c, HitObject o, Pattern prev)
    {
        int end = (int)o.EndTime;
        bool notStack = prev.Columns != c.Total;
        bool hold = end - o.Time >= 100;
        int Random(int? lo) => notStack ? c.Available(c.RandomColumn(lo), lo, patterns: prev) : c.Available(c.RandomColumn(lo), lo);
        int column = c.Total == 8 && Finish(o.HitSound) && end - o.Time < 1000 ? 0 : c.Total == 8 ? Random(null) : Random(0);
        var pattern = new Pattern();
        pattern.Add(new Note(column, o.Time, hold ? end : o.Time));
        return pattern;
    }
}
