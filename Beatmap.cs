using System.Globalization;
using System.Numerics;

namespace StableRemake;

enum ObjType { Circle, Slider, Spinner }

sealed class TimingPoint
{
    public double Time, BeatLength;
    public bool Uninherited, Kiai;
    public int Meter = 4;
    public int SampleSet, SampleIndex, Volume;
}

enum CheckpointKind { Tick, Repeat, End }

readonly record struct Checkpoint(double Time, CheckpointKind Kind, int Span);

sealed class HitObject
{
    public ObjType Type;
    public Vector2 Pos;
    public double Time, EndTime;
    public bool NewCombo;
    public int ComboSkip, ComboNumber, ColourIndex, Stack;
    public int HitSound, NormalSet, AdditionSet, SampleIndex, Volume;

    // Sliders
    public Vector2[] Path = [];
    public float[] Cumulative = [];
    public double Length, SpanDuration;
    public double StableVelocity; // px per second, stable's arithmetic (slider timing walks the path with it)
    public int Slides = 1;
    public int[] EdgeSounds = [];
    public (int Normal, int Addition)[] EdgeSets = [];
    public List<Checkpoint> Checkpoints = [];

    // stable: the path's own end point (start after an even number of slides), not the ball at EndTime (EndTime is
    // rounded, which put it ~0.01px short and flipped stacking's 3px test).
    public Vector2 EndPos => Type != ObjType.Slider || Path.Length == 0 ? Pos : Slides % 2 == 1 ? Path[^1] : Path[0];

    public Vector2 PointAt(double distance)
    {
        if (Path.Length == 1) return Path[0];
        distance = Math.Clamp(distance, 0, Cumulative[^1]);
        int i = Array.BinarySearch(Cumulative, (float)distance);
        if (i >= 0) return Path[i];
        i = ~i;
        if (i <= 0) return Path[0];
        if (i >= Path.Length) return Path[^1];
        float a = Cumulative[i - 1], b = Cumulative[i];
        return Vector2.Lerp(Path[i - 1], Path[i], b > a ? (float)((distance - a) / (b - a)) : 0);
    }

    public double ProgressAt(double time)
    {
        double p = Math.Clamp((time - Time) / SpanDuration, 0, Slides);
        int span = Math.Min((int)p, Slides - 1);
        double f = p - span;
        return span % 2 == 1 ? 1 - f : f;
    }

    public Vector2 PositionAtTime(double time) => PointAt(ProgressAt(time) * Length);

    // stable judges slider tracking against its ball sprite, which moves through one linear transform per path
    // segment per span, with start/end cut to whole ms - not the exact curve position.
    (int Start, int End, Vector2 From, Vector2 To)[]? ballMoves;

    public Vector2 StableBallAt(int t)
    {
        var moves = ballMoves ??= BuildBallMoves();
        if (moves.Length == 0) return Pos;
        int lo = 0, hi = moves.Length - 1, k = 0;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (moves[mid].Start <= t) { k = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        var m = moves[k];
        if (t >= m.End) return m.To;
        if (t <= m.Start) return m.From;
        return m.From + (m.To - m.From) * (1 - (float)(m.End - t) / (m.End - m.Start));
    }

    // stable: (double)(1000f * segment length) / velocity, the segment length in float.
    public double SegmentMs(int k) => (double)(1000f * Vector2.Distance(Path[k - 1], Path[k])) / StableVelocity;

    (int, int, Vector2, Vector2)[] BuildBallMoves()
    {
        var moves = new List<(int, int, Vector2, Vector2)>();
        double clock = Time;
        for (int span = 0; span < Slides; span++)
        {
            bool back = span % 2 == 1;
            for (int j = 1; j < Path.Length; j++)
            {
                int k = back ? Path.Length - j : j; // segment k-1 -> k
                double duration = SegmentMs(k);
                var (a, b) = back ? (Path[k], Path[k - 1]) : (Path[k - 1], Path[k]);
                moves.Add(((int)clock, (int)(clock + duration), a, b));
                clock += duration;
            }
        }
        return moves.ToArray();
    }
}

sealed class Beatmap
{
    public string Folder = "", File = "";
    public int FormatVersion = 14, Mode;
    public string AudioFilename = "", Background = "", Title = "", Artist = "", Version = "", Creator = "";
    public double AudioLeadIn, PreviewTime, StackLeniency = 0.7;
    public int Countdown = 1, CountdownOffset; // 0 none, 1 normal, 2 half speed, 3 double speed
    public double HP = 5, CS = 5, OD = 5, AR = -1, SliderMultiplier = 1.4, SliderTickRate = 1;
    // HP for the drain calibration's thresholds: the map's HP before HardRock's x1.4 (EZ still counts). Measured against
    // stable's recorded life bars (HR plays 0.031 -> 0.019 mean error); the hit gains do use the HR value.
    public double CalibrationHP = 5;
    public int DefaultSampleSet = 1;
    public List<System.Drawing.Color> ComboColours = [];
    public List<TimingPoint> TimingPoints = [];
    public List<HitObject> Objects = [];

    // stable's DifficultyRange: linear between the values at 0, 5 and 10.
    public static double Range(double d, double min, double mid, double max) =>
        d > 5 ? mid + (max - mid) * (d - 5) / 5 : d < 5 ? mid - (mid - min) * (5 - d) / 5 : mid;

    public double Preempt => Range(AR, 1800, 1200, 450);
    public double FadeIn => 400 * Math.Min(1, Preempt / 450);
    // stable keeps OD as a float and truncates the windows to whole milliseconds.
    public int Hit300 => (int)Range((float)OD, 80, 50, 20);
    public int Hit100 => (int)Range((float)OD, 140, 100, 60);
    public int Hit50 => (int)Range((float)OD, 200, 150, 100);
    public double SpinsPerSecond => Range(OD, 3, 5, 7.5);
    public float Radius => (float)(54.4 - 4.48 * CS) * 1.00041f; // stable's fudge factor

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static double D(string s) => double.Parse(s.Trim(), Inv);
    static int I(string s) => (int)double.Parse(s.Trim(), Inv);

    public static Beatmap Load(string path, bool metadataOnly = false, Mods mods = Mods.None)
    {
        var map = new Beatmap { Folder = System.IO.Path.GetDirectoryName(path)!, File = path, flipY = mods.HasFlag(Mods.HardRock) };
        string section = "";
        foreach (var raw in System.IO.File.ReadLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//")) continue;
            if (line.StartsWith("osu file format v"))
            {
                int.TryParse(line["osu file format v".Length..], out map.FormatVersion);
                continue;
            }
            if (line[0] == '[')
            {
                section = line.Trim('[', ']');
                if (metadataOnly && section is "TimingPoints" or "HitObjects") break;
                continue;
            }
            try { map.ParseLine(section, line); }
            catch (FormatException) { }
            catch (IndexOutOfRangeException) { }
        }
        if (map.AR < 0) map.AR = map.OD; // old maps had no separate AR
        double hpBeforeHr = map.HP;
        if (mods.HasFlag(Mods.HardRock)) map.ScaleDifficulty(1.3f, 1.4f);
        if (mods.HasFlag(Mods.Easy)) map.ScaleDifficulty(0.5f, 0.5f);
        map.CalibrationHP = mods.HasFlag(Mods.HardRock) ? hpBeforeHr : map.HP;
        if (!metadataOnly) map.Finish();
        return map;
    }

    bool flipY;
    public List<(double Start, double End)> Breaks = [];

    // In float, like stable, so window truncation lands on the same millisecond.
    void ScaleDifficulty(float cs, float rest)
    {
        CS = Math.Min(10f, (float)CS * cs);
        AR = Math.Min(10f, (float)AR * rest);
        OD = Math.Min(10f, (float)OD * rest);
        HP = Math.Min(10f, (float)HP * rest);
    }

    Vector2 ReadPoint(string x, string y) => new((float)D(x), flipY ? 384 - (float)D(y) : (float)D(y));

    void ParseLine(string section, string line)
    {
        switch (section)
        {
            case "General" or "Metadata" or "Difficulty":
                int c = line.IndexOf(':');
                if (c < 0) return;
                string k = line[..c].Trim(), v = line[(c + 1)..].Trim();
                switch (k)
                {
                    case "AudioFilename": AudioFilename = v; break;
                    case "AudioLeadIn": AudioLeadIn = D(v); break;
                    case "Countdown": Countdown = (int)D(v); break;
                    case "CountdownOffset": CountdownOffset = (int)D(v); break;
                    case "PreviewTime": PreviewTime = D(v); break;
                    case "StackLeniency": StackLeniency = D(v); break;
                    case "Mode": Mode = I(v); break;
                    case "SampleSet": DefaultSampleSet = v switch { "Soft" => 2, "Drum" => 3, _ => 1 }; break;
                    case "Title": Title = v; break;
                    case "Artist": Artist = v; break;
                    case "Version": Version = v; break;
                    case "Creator": Creator = v; break;
                    case "HPDrainRate": HP = D(v); break;
                    case "CircleSize": CS = D(v); break;
                    case "OverallDifficulty": OD = D(v); break;
                    case "ApproachRate": AR = D(v); break;
                    case "SliderMultiplier": SliderMultiplier = D(v); break;
                    case "SliderTickRate": SliderTickRate = D(v); break;
                }
                break;
            case "Events":
                var e = line.Split(',');
                if (e.Length >= 3 && e[0] == "0" && Background == "") Background = e[2].Trim('"');
                if (e.Length >= 3 && e[0] is "2" or "Break") Breaks.Add((D(e[1]), D(e[2])));
                break;
            case "Colours":
                if (!line.StartsWith("Combo")) return;
                var rgb = line[(line.IndexOf(':') + 1)..].Split(',');
                if (rgb.Length < 3) return;
                static int C(string v) => Math.Clamp(I(v), 0, 255); // some maps have e.g. 511
                ComboColours.Add(System.Drawing.Color.FromArgb(C(rgb[0]), C(rgb[1]), C(rgb[2])));
                break;
            case "TimingPoints":
                var t = line.Split(',');
                bool uninherited = t.Length <= 6 || t[6].Trim() == "1";
                double beat = D(t[1]);
                TimingPoints.Add(new TimingPoint
                {
                    Time = D(t[0]),
                    BeatLength = uninherited ? Math.Clamp(double.IsNaN(beat) ? 500 : beat, 6, 60000) : beat,
                    SampleSet = t.Length > 3 ? I(t[3]) : DefaultSampleSet,
                    SampleIndex = t.Length > 4 ? I(t[4]) : 1,
                    Volume = t.Length > 5 ? I(t[5]) : 100,
                    Uninherited = uninherited,
                    Meter = t.Length > 2 && int.TryParse(t[2], out int meter) && meter > 0 ? meter : 4,
                    Kiai = t.Length > 7 && int.TryParse(t[7], out int fx) && (fx & 1) != 0,
                });
                break;
            case "HitObjects":
                ParseObject(line.Split(','));
                break;
        }
    }

    void ParseObject(string[] f)
    {
        int type = I(f[3]);
        var o = new HitObject
        {
            Pos = ReadPoint(f[0], f[1]),
            Time = D(f[2]),
            NewCombo = (type & 4) != 0,
            ComboSkip = (type >> 4) & 7,
            HitSound = I(f[4]),
        };
        string? sample = null;
        if ((type & 1) != 0)
        {
            o.Type = ObjType.Circle;
            o.EndTime = o.Time;
            if (f.Length > 5) sample = f[5];
        }
        else if ((type & 2) != 0)
        {
            o.Type = ObjType.Slider;
            var curve = f[5].Split('|');
            var pts = new List<Vector2> { o.Pos };
            for (int i = 1; i < curve.Length; i++)
            {
                var xy = curve[i].Split(':');
                pts.Add(ReadPoint(xy[0], xy[1]));
            }
            // Aspire maps use absurd values (e.g. length 3e300); clamp to sane coordinate limits.
            o.Slides = Math.Clamp(I(f[6]), 1, 10000);
            double declared = f.Length > 7 ? D(f[7]) : 0;
            o.Length = double.IsNaN(declared) ? 0 : Math.Clamp(declared, 0, 131072); // NaN (old files) = the drawn length
            o.Path = SliderPath.Build(curve[0].Length > 0 ? curve[0][0] : 'B', pts, ref o.Length, FormatVersion);
            if (f.Length > 8) o.EdgeSounds = f[8].Split('|').Select(I).ToArray();
            if (f.Length > 9) o.EdgeSets = f[9].Split('|').Select(s => { var p = s.Split(':'); return (I(p[0]), I(p[1])); }).ToArray();
            if (f.Length > 10) sample = f[10];
        }
        else if ((type & 8) != 0)
        {
            o.Type = ObjType.Spinner;
            o.Pos = new Vector2(256, 192);
            o.EndTime = Math.Max(o.Time, D(f[5])); // aspire maps use negative/backwards spinners
            o.NewCombo = true;
            if (f.Length > 6) sample = f[6];
        }
        else return; // mania hold notes etc.

        if (sample != null)
        {
            var s = sample.Split(':');
            if (s.Length > 1) { o.NormalSet = I(s[0]); o.AdditionSet = I(s[1]); }
            if (s.Length > 2) o.SampleIndex = I(s[2]);
            if (s.Length > 3) o.Volume = I(s[3]);
        }
        Objects.Add(o);
    }

    public TimingPoint UninheritedAt(double time)
    {
        TimingPoint? best = null;
        foreach (var tp in TimingPoints)
        {
            if (!tp.Uninherited) continue;
            if (best == null || tp.Time <= time) best = tp;
            else break;
        }
        return best ?? new TimingPoint { BeatLength = 500, Uninherited = true, SampleSet = 1, Volume = 100 };
    }

    public TimingPoint PointAt(double time)
    {
        TimingPoint? best = null;
        foreach (var tp in TimingPoints)
        {
            if (best == null || tp.Time <= time) best = tp;
            else break;
        }
        return best ?? UninheritedAt(time);
    }

    public double SliderVelocityAt(double time)
    {
        var u = UninheritedAt(time);
        var p = PointAt(time);
        return !p.Uninherited && p.Time >= u.Time && p.BeatLength < 0 ? Math.Clamp(-100 / p.BeatLength, 0.1, 10) : 1;
    }

    void Finish()
    {
        // Stable sorts (OrderBy): points sharing a timestamp keep file order, so a green line placed after the
        // red line at the same time still sets the slider velocity.
        TimingPoints = TimingPoints.OrderBy(t => t.Time).ToList();
        Objects = Objects.OrderBy(o => o.Time).ToList();

        // stable's numbering/colouring: the colour counter gains 1 + skip on every new combo,
        // the first object included (so the first combo uses colour 2); spinners don't take a colour but force a new
        // combo on the next object, as does the end of a break. Combo-colour skips are beatmap skinning, so
        // "ignore beatmap skins" drops them.
        // TODO: that last mapping (stable's setting is unknown) is inferred; verify on a map with skips.
        bool ignoreSkips = Config.GetBool("IgnoreBeatmapSkins", false);
        int combo = 0, colour = 0, nextBreak = 0;
        bool force = false;
        var breaks = Breaks.OrderBy(b => b.End).ToList();
        for (int i = 0; i < Objects.Count; i++)
        {
            var o = Objects[i];
            bool newCombo = o.NewCombo;
            for (; nextBreak < breaks.Count && breaks[nextBreak].End < o.Time; nextBreak++) newCombo = true;
            int skip = ignoreSkips ? 0 : o.ComboSkip;
            if (o.Type == ObjType.Spinner)
            {
                if (FormatVersion <= 8) force = true;
                else if (newCombo) { colour += skip; force = true; }
            }
            else if (force || newCombo || i == 0)
            {
                combo = 1;
                colour += skip + 1;
                force = false;
            }
            else combo++;
            o.ComboNumber = combo;
            o.ColourIndex = colour;

            if (o.Type == ObjType.Slider) BuildSlider(o);
        }
        ApplyStacking();
    }

    void BuildSlider(HitObject o)
    {
        var beat = UninheritedAt(o.Time).BeatLength;
        double sv = SliderVelocityAt(o.Time);
        double velocity = 100 * SliderMultiplier * sv / beat; // osu px per ms
        o.Cumulative = new float[o.Path.Length];
        for (int i = 1; i < o.Path.Length; i++)
            o.Cumulative[i] = o.Cumulative[i - 1] + Vector2.Distance(o.Path[i - 1], o.Path[i]);
        o.SpanDuration = Math.Max(1, o.Length / velocity);
        // stable (slider setup): velocity = scoring distance * tick rate * 1000 / (beat * float SV factor),
        // and the end is the truncated sum of every segment's duration over every span (reverse spans walked
        // backwards). A path a hair short of its declared length ends a millisecond early, like stable's does.
        var u = UninheritedAt(o.Time);
        var p = PointAt(o.Time);
        // The SV factor is a float clamp, but stable's 32-bit runtime divides by 100 at higher precision and never rounds
        // back to float (matched bit for bit against stable's in-memory velocity).
        double svFactor = !p.Uninherited && p.Time >= u.Time && p.BeatLength < 0 ? (double)Math.Clamp((float)-p.BeatLength, 10f, 1000f) / 100.0 : 1;
        double beatSv = u.BeatLength * svFactor, scoringDist = 100.0 * SliderMultiplier / SliderTickRate;
        o.StableVelocity = beatSv > 0 ? scoringDist * SliderTickRate * (1000.0 / beatSv) : scoringDist * SliderTickRate;
        double clock = o.Time;
        for (int span = 0; span < o.Slides; span++)
            for (int j = 1; j < o.Path.Length; j++)
                clock += o.SegmentMs(span % 2 == 1 ? o.Path.Length - j : j);
        o.EndTime = (int)clock;

        // Checkpoints, walked like stable: distance runs along the path segment by segment with a tick every tick
        // distance; a span's ticks stop once what's left of it is within 10ms of travel; the leftover carries into the
        // next span mirrored. Every span end is a checkpoint at (int)(start + (float)distance / velocity * 1000), and
        // the last one becomes the end check max(start + duration / 2, span end - 36). The list stays in this order
        // (not sorted): stable judges it in order, so an end check earlier than the last repeat waits for it.
        var tp = PointAt(o.Time);
        double tickFactor = tp.BeatLength < 0 ? (double)Math.Clamp((float)-tp.BeatLength, 10f, 1000f) / 100.0 : 1; // as velocity's
        double tickDist = Math.Min(FormatVersion < 8 ? scoringDist : scoringDist / tickFactor, o.Length);
        double pathLen = 0;
        for (int j = 1; j < o.Path.Length; j++) pathLen += Vector2.Distance(o.Path[j - 1], o.Path[j]);
        double tenMs = 0.01 * o.StableVelocity, travelled = 0, sinceTick = 0;
        // TODO: trick sliders with >100k checkpoints just lose their ticks
        bool ticksOn = tickDist > 0 && pathLen / tickDist * o.Slides < 100_000;
        int Ms(double d) => (int)(o.Time + (double)(float)d / o.StableVelocity * 1000.0);
        for (int span = 0; span < o.Slides; span++)
        {
            double remaining = pathLen;
            bool stop = !ticksOn;
            for (int j = 1; j < o.Path.Length; j++)
            {
                int k = span % 2 == 1 ? o.Path.Length - j : j;
                sinceTick += Vector2.Distance(o.Path[k - 1], o.Path[k]);
                while (sinceTick >= tickDist && !stop)
                {
                    travelled += tickDist;
                    sinceTick -= tickDist;
                    remaining -= tickDist;
                    if (stop = remaining <= tenMs) break;
                    o.Checkpoints.Add(new Checkpoint(Ms(travelled), CheckpointKind.Tick, span));
                }
            }
            travelled += sinceTick;
            if (span < o.Slides - 1) o.Checkpoints.Add(new Checkpoint(Ms(travelled), CheckpointKind.Repeat, span));
            else
            {
                int startMs = (int)o.Time;
                o.Checkpoints.Add(new Checkpoint(Math.Max(startMs + ((int)o.EndTime - startMs) / 2, Ms(travelled) - 36), CheckpointKind.End, span));
            }
            if (stop) sinceTick = 0;
            else { travelled -= tickDist - sinceTick; sinceTick = tickDist - sinceTick; }
        }
    }

    // stable's stacking for format v6+: objects within StackLeniency*preempt at (almost) the same spot
    // are pushed up-left by radius/10 per stack level.
    void ApplyStacking()
    {
        double threshold = Preempt * StackLeniency;
        const float dist = 3;
        for (int i = Objects.Count - 1; i > 0; i--)
        {
            var oi = Objects[i];
            if (oi.Stack != 0 || oi.Type == ObjType.Spinner) continue;
            int n = i;
            if (oi.Type == ObjType.Circle)
            {
                while (--n >= 0)
                {
                    var on = Objects[n];
                    if (on.Type == ObjType.Spinner) continue;
                    if (oi.Time - on.EndTime > threshold) break;
                    if (on.Type == ObjType.Slider && Vector2.Distance(on.EndPos, oi.Pos) < dist)
                    {
                        int offset = oi.Stack - on.Stack + 1;
                        for (int j = n + 1; j <= i; j++)
                            if (Vector2.Distance(on.EndPos, Objects[j].Pos) < dist) Objects[j].Stack -= offset;
                        break;
                    }
                    if (Vector2.Distance(on.Pos, oi.Pos) < dist)
                    {
                        on.Stack = oi.Stack + 1;
                        oi = on;
                    }
                }
            }
            else
            {
                while (--n >= 0)
                {
                    var on = Objects[n];
                    if (on.Type == ObjType.Spinner) continue;
                    if (oi.Time - on.Time > threshold) break;
                    if (Vector2.Distance(on.EndPos, oi.Pos) < dist)
                    {
                        on.Stack = oi.Stack + 1;
                        oi = on;
                    }
                }
            }
        }
    }

    // stable: the per-level offset is radius / 10 as a float, then multiplied by the stack height.
    public Vector2 StackOffset(HitObject o) => new(-(o.Stack * (Radius / 10f)));
}

static class SliderPath
{
    // stable's curve setup (slider class): the curve type and the map's format version pick the method.
    public static Vector2[] Build(char type, List<Vector2> pts, ref double length, int version)
    {
        List<Vector2> raw = type switch
        {
            'L' => pts,
            // perfect circle: exactly 3 points, not collinear; more points = Bezier, fewer/collinear = Linear
            'P' when pts.Count > 3 => Bezier(pts, version),
            'P' => pts.Count == 3 && Arc(pts) is { } arc ? arc : pts,
            'C' => Catmull(pts),
            _ => Bezier(pts, version),
        };
        return Fit(raw.Count > 0 ? raw : pts, ref length); // a curve with no control points ("C" alone) yields nothing
    }

    // Trim (or extend) the sampled curve to the declared pixel length.
    static Vector2[] Fit(List<Vector2> raw, ref double length)
    {
        var dedup = new List<Vector2> { raw[0] };
        for (int i = 1; i < raw.Count; i++)
            if (raw[i] != dedup[^1]) dedup.Add(raw[i]);
        // A path that never leaves its start: stable times the slider by walking the path's segments, so it ends at once.
        if (dedup.Count == 1) { length = 0; return [dedup[0]]; }

        double total = 0;
        for (int i = 1; i < dedup.Count; i++) total += Vector2.Distance(dedup[i - 1], dedup[i]);
        if (length <= 0) length = total;

        // stable (slider curve setup): drop end segments covered by the excess, then cut (or extend) the
        // last one to start + dir * (len - excess) in float, so the end point matches stable's to the float step
        // (stacking tests slider ends against < 3px, and some maps put the next object exactly 3px away).
        var outPts = dedup;
        double excess = total - length;
        while (outPts.Count > 1)
        {
            Vector2 a = outPts[^2], b = outPts[^1];
            float len = Vector2.Distance(a, b);
            if (len > excess + 0.0001)
            {
                outPts[^1] = a + XnaNormalize(b - a) * (len - (float)excess);
                break;
            }
            outPts.RemoveAt(outPts.Count - 1);
            excess -= len;
        }
        return outPts.ToArray();
    }

    // XNA's Vector2.Normalize multiplies by the reciprocal length; System.Numerics divides (can differ by a float step).
    static Vector2 XnaNormalize(Vector2 v)
    {
        float inv = 1f / MathF.Sqrt(v.X * v.X + v.Y * v.Y);
        return new Vector2(v.X * inv, v.Y * inv);
    }

    // Red anchors (a repeated point) split a Bezier into pieces. How depends on the format version, like stable:
    // v9+ splits at the first of the pair and makes 2-point pieces plain lines, pieces use the uniform sampler
    // below v10 and the adaptive one from v10; v7-8 split the same way, always adaptive; v6 and older end a piece
    // at the second point of the pair (both copies stay in it).
    static List<Vector2> Bezier(List<Vector2> pts, int version)
    {
        var result = new List<Vector2>();
        int start = 0;
        for (int m = 0; m < pts.Count; m++)
        {
            bool anchor = version > 6 ? m < pts.Count - 2 && pts[m] == pts[m + 1] : m > 0 && pts[m] == pts[m - 1];
            if (!anchor && m != pts.Count - 1) continue;
            var piece = pts.GetRange(start, m - start + 1);
            if (version > 8 && piece.Count == 2) result.AddRange(piece);
            else result.AddRange(version > 8 && version < 10 ? BezierUniform(piece) : BezierAdaptive(piece));
            if (anchor && version > 6) m++;
            start = m;
        }
        return result;
    }

    // stable's old sampler: 50 points per control point at t = i/n for i < n (the piece's last point isn't reached).
    static List<Vector2> BezierUniform(List<Vector2> ctrl)
    {
        int count = ctrl.Count, n = 50 * count;
        var tmp = new Vector2[count];
        var result = new List<Vector2>(n);
        for (int i = 0; i < n; i++)
        {
            ctrl.CopyTo(tmp);
            float t = i / (float)n;
            for (int k = 0; k < count; k++)
                for (int l = 0; l < count - k - 1; l++)
                    tmp[l] = tmp[l] + (tmp[l + 1] - tmp[l]) * t; // XNA's Lerp
            result.Add(tmp[0]);
        }
        return result;
    }

    // stable's adaptive approximator: subdivide (de Casteljau at t=0.5) until every second difference is within
    // 0.5px (squared 0.25), then emit each flat piece's smoothed midpoints.
    static List<Vector2> BezierAdaptive(List<Vector2> ctrl)
    {
        int n = ctrl.Count;
        var output = new List<Vector2>();
        if (n == 0) return output;
        var midpoints = new Vector2[n];
        var left = new Vector2[n * 2 - 1];
        var toFlatten = new Stack<Vector2[]>();
        var free = new Stack<Vector2[]>();
        toFlatten.Push(ctrl.ToArray());

        void Subdivide(Vector2[] p, Vector2[] l, Vector2[] r)
        {
            for (int i = 0; i < n; i++) midpoints[i] = p[i];
            for (int i = 0; i < n; i++)
            {
                l[i] = midpoints[0];
                r[n - i - 1] = midpoints[n - i - 1];
                for (int j = 0; j < n - i - 1; j++) midpoints[j] = (midpoints[j] + midpoints[j + 1]) * 0.5f;
            }
        }

        while (toFlatten.Count > 0)
        {
            var parent = toFlatten.Pop();
            bool flat = true;
            for (int i = 1; i < n - 1 && flat; i++)
                flat = (parent[i - 1] - 2f * parent[i] + parent[i + 1]).LengthSquared() <= 0.25f;
            if (flat)
            {
                Subdivide(parent, left, midpoints);
                for (int i = 0; i < n - 1; i++) left[n + i] = midpoints[i + 1];
                output.Add(parent[0]);
                for (int i = 1; i < n - 1; i++)
                {
                    int k = 2 * i;
                    output.Add(0.25f * (left[k - 1] + 2f * left[k] + left[k + 1]));
                }
                free.Push(parent);
                continue;
            }
            var right = free.Count > 0 ? free.Pop() : new Vector2[n];
            Subdivide(parent, left, right);
            for (int i = 0; i < n; i++) parent[i] = left[i];
            toFlatten.Push(right);
            toFlatten.Push(parent);
        }
        output.Add(ctrl[n - 1]);
        return output;
    }

    // stable's perfect circle: centre/radius in float, angles in double, (int)(arc length / 8) pieces ending exactly
    // on the third point. Collinear (exactly) = not an arc.
    static List<Vector2>? Arc(List<Vector2> p)
    {
        Vector2 a = p[0], b = p[1], c = p[2];
        if ((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y) == 0f) return null;
        float d = 2f * (a.X * (b.Y - c.Y) + b.X * (c.Y - a.Y) + c.X * (a.Y - b.Y));
        float aSq = a.LengthSquared(), bSq = b.LengthSquared(), cSq = c.LengthSquared();
        var centre = new Vector2(
            (aSq * (b.Y - c.Y) + bSq * (c.Y - a.Y) + cSq * (a.Y - b.Y)) / d,
            (aSq * (c.X - b.X) + bSq * (a.X - c.X) + cSq * (b.X - a.X)) / d);
        float r = Vector2.Distance(centre, a);
        double t0 = Math.Atan2(a.Y - centre.Y, a.X - centre.X), mid = Math.Atan2(b.Y - centre.Y, b.X - centre.X);
        double t1 = Math.Atan2(c.Y - centre.Y, c.X - centre.X);
        const double Tau = 6.2831854820251465; // (float)(2 pi), as stable has it
        while (mid < t0) mid += Tau;
        while (t1 < t0) t1 += Tau;
        if (mid > t1) t1 -= Tau;
        int pieces = (int)(Math.Abs((t1 - t0) * r) * 0.125);
        var outPts = new List<Vector2> { a };
        for (int j = 1; j < pieces; j++)
        {
            double f = (double)j / pieces, ang = t1 * f + t0 * (1 - f);
            outPts.Add(new Vector2((float)(Math.Cos(ang) * r), (float)(Math.Sin(ang) * r)) + centre);
        }
        outPts.Add(c);
        return outPts;
    }

    // stable: 50 pieces per point pair, XNA's CatmullRom (float, its operation order).
    static List<Vector2> Catmull(List<Vector2> p)
    {
        var result = new List<Vector2>();
        for (int i = 0; i < p.Count - 1; i++)
        {
            var v1 = i > 0 ? p[i - 1] : p[i];
            var v2 = p[i];
            var v3 = p[i + 1];
            var v4 = i + 2 < p.Count ? p[i + 2] : v3 + (v3 - v2);
            for (int s = 0; s <= 50; s++)
            {
                float t = s / 50f;
                result.Add(new Vector2(XnaCatmull(v1.X, v2.X, v3.X, v4.X, t), XnaCatmull(v1.Y, v2.Y, v3.Y, v4.Y, t)));
            }
        }
        return result;
    }

    static float XnaCatmull(float v1, float v2, float v3, float v4, float t)
    {
        float t2 = t * t, t3 = t * t2;
        return 0.5f * (2f * v2 + (-v1 + v3) * t + (2f * v1 - 5f * v2 + 4f * v3 - v4) * t2 + (-v1 + 3f * v2 - 3f * v3 + v4) * t3);
    }
}
