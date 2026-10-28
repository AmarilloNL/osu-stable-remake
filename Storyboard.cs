using System.Globalization;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace StableRemake;

// osu! storyboards: [Events] of the .osu plus the folder's .osb, in stable's 640x480 space (854x480 when the map
// is flagged widescreen). Sprites/animations with F/M/MX/MY/S/V/R/C/P commands, easings, loops, $variables,
// layers and sound samples.
// Trigger groups (T) replay their commands from the moment a hitsound/hit/pass/fail event fires them.
sealed class Storyboard
{
    enum Track { Fade, X, Y, Scale, VX, VY, Rotation, R, G, B }

    sealed class Cmd(double start, double end, int easing, float from, float to)
    {
        public readonly double Start = start, End = end;
        public readonly int Easing = easing;
        public readonly float From = from, To = to;
    }

    sealed class Element
    {
        public int Layer; // 0 background, 1 fail, 2 pass, 3 foreground, 4 overlay
        public Vector2 Origin; // 0..1 of the sprite size
        public string Path = "";
        public float X, Y;
        public int Frames = 1;
        public double FrameDelay;
        public bool LoopOnce;
        public readonly List<Cmd>?[] Tracks = new List<Cmd>?[10];
        public readonly List<(double Start, double End, char Kind)> Params = [];
        public double Start = double.MaxValue, End = double.MinValue;
        public Texture2D[] Textures = [];
        public List<Trigger>? Triggers;
    }

    sealed class Trigger(string name, double start, double end, int group)
    {
        public readonly string Name = name;
        public readonly double Start = start, End = end;
        public readonly int Group = group;
        public readonly List<(char Kind, string[] P)> Lines = [];
        public readonly List<object> Active = []; // commands added by the last firing, removed if it fires again
    }

    readonly List<Element> elements = [];
    readonly List<(double Time, int Layer, string Path, int Volume)> samples = [];
    readonly Dictionary<string, Texture2D> textures = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, SoundPool?> sounds = new(StringComparer.OrdinalIgnoreCase);
    readonly string folder;
    public readonly bool Widescreen;
    public bool Passing = true;
    int nextSample;

    public bool IsEmpty => elements.Count == 0 && samples.Count == 0;
    public bool UsesFile(string file) => elements.Any(e => e.Path.Equals(file, StringComparison.OrdinalIgnoreCase));

    Storyboard(string folder, bool widescreen)
    {
        this.folder = folder;
        Widescreen = widescreen;
    }

    // textures: false loads only what's needed for samples (nothing would be visible at 100% dim).
    public static Storyboard? Load(Beatmap map, bool textures)
    {
        var lines = new List<string>();
        bool widescreen = false;
        var vars = new List<(string Name, string Value)>();
        // The .osb first, then the difficulty's own events on top of it.
        foreach (var osb in Directory.EnumerateFiles(map.Folder, "*.osb"))
            ReadEvents(osb, lines, vars, ref widescreen);
        ReadEvents(map.File, lines, vars, ref widescreen);
        var sb = new Storyboard(map.Folder, widescreen);
        vars.Sort((a, b) => b.Name.Length.CompareTo(a.Name.Length)); // $ab before $a
        sb.Parse(lines, vars);
        if (sb.IsEmpty) return null;
        foreach (var path in sb.samples.Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase))
            sb.sounds[path] = SoundPool.Load(System.IO.Path.Combine(map.Folder, path)); // up front: no mid-play hitch
        if (textures) sb.LoadTextures();
        else { sb.elements.Clear(); sb.triggered.Clear(); }
        return sb;
    }

    static void ReadEvents(string path, List<string> into, List<(string, string)> vars, ref bool widescreen)
    {
        string section = "";
        foreach (var raw in File.ReadLines(path))
        {
            var t = raw.Trim();
            if (t.Length == 0 || t.StartsWith("//")) continue;
            if (t[0] == '[') { section = t; continue; }
            switch (section)
            {
                case "[Events]": into.Add(raw.TrimEnd()); break;
                case "[Variables]":
                    int eq = t.IndexOf('=');
                    if (t.StartsWith('$') && eq > 0) vars.Add((t[..eq], t[(eq + 1)..]));
                    break;
                case "[General]":
                    if (t.StartsWith("WidescreenStoryboard") && t.EndsWith('1')) widescreen = true;
                    break;
            }
        }
    }

    static int LayerOf(string s) => s switch
    {
        "Background" or "0" => 0, "Fail" or "1" => 1, "Pass" or "2" => 2, "Foreground" or "3" => 3, "Overlay" or "4" => 4, _ => 0,
    };

    static Vector2 OriginOf(string s) => s switch
    {
        "TopLeft" or "0" or "Custom" or "6" => new(0, 0),
        "Centre" or "1" => new(0.5f, 0.5f),
        "CentreLeft" or "2" => new(0, 0.5f),
        "TopRight" or "3" => new(1, 0),
        "BottomCentre" or "4" => new(0.5f, 1),
        "TopCentre" or "5" => new(0.5f, 0),
        "CentreRight" or "7" => new(1, 0.5f),
        "BottomLeft" or "8" => new(0, 1),
        "BottomRight" or "9" => new(1, 1),
        _ => new(0.5f, 0.5f),
    };

    static float F(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    void Parse(List<string> lines, List<(string Name, string Value)> vars)
    {
        Element? current = null;
        (double Start, int Count, List<(char Kind, string[] P)> Cmds)? loop = null;
        Trigger? trigger = null;

        void FlushLoop()
        {
            if (loop is not { } l || current == null) { loop = null; return; }
            var parsed = new List<(char, int, double, double, float[])>();
            foreach (var (kind, p) in l.Cmds) parsed.AddRange(Expand(kind, p));
            if (parsed.Count > 0)
            {
                double first = parsed.Min(c => c.Item3), last = parsed.Max(c => c.Item4);
                double length = last - first;
                for (int i = 0; i < Math.Max(1, l.Count); i++)
                    foreach (var (kind, easing, s, e, values) in parsed)
                    {
                        double offset = l.Start + i * length;
                        AddCmd(current, kind, easing, s + offset, e + offset, values);
                    }
            }
            loop = null;
        }

        foreach (var raw in lines)
        {
            string line = raw;
            if (line.Contains('$'))
                foreach (var (name, value) in vars) line = line.Replace(name, value);
            int depth = 0;
            while (depth < line.Length && line[depth] is ' ' or '_') depth++;
            var p = line[depth..].Split(',');
            if (depth == 0)
            {
                FlushLoop();
                trigger = null;
                current = null;
                switch (p[0])
                {
                    case "Sprite" or "4" when p.Length >= 6:
                        current = new Element { Layer = LayerOf(p[1]), Origin = OriginOf(p[2]), Path = Clean(p[3]), X = F(p[4]), Y = F(p[5]) };
                        elements.Add(current);
                        break;
                    case "Animation" or "6" when p.Length >= 8:
                        current = new Element
                        {
                            Layer = LayerOf(p[1]), Origin = OriginOf(p[2]), Path = Clean(p[3]), X = F(p[4]), Y = F(p[5]),
                            Frames = Math.Max(1, (int)D(p[6])), FrameDelay = D(p[7]), LoopOnce = p.Length > 8 && p[8] is "LoopOnce" or "1",
                        };
                        elements.Add(current);
                        break;
                    case "Sample" or "5" when p.Length >= 4:
                        samples.Add((D(p[1]), (int)D(p[2]), Clean(p[3]), p.Length > 4 ? (int)D(p[4]) : 100));
                        break;
                }
                continue;
            }
            if (current == null) continue;
            if (depth == 1)
            {
                FlushLoop();
                trigger = null;
                if (p[0] == "T" && p.Length >= 4)
                {
                    trigger = new Trigger(p[1], D(p[2]), D(p[3]), p.Length > 4 ? (int)D(p[4]) : 0);
                    (current.Triggers ??= []).Add(trigger);
                }
                else if (p[0] == "L" && p.Length >= 3) loop = (D(p[1]), (int)D(p[2]), []);
                else if (p[0].Length is 1 or 2)
                    foreach (var (kind, easing, s, e, values) in Expand(p[0][0] == 'M' && p[0].Length == 2 ? p[0][1] == 'X' ? 'x' : 'y' : p[0][0], p))
                        AddCmd(current, kind, easing, s, e, values);
            }
            else if (trigger != null) trigger.Lines.Add((KindOf(p[0]), p));
            else if (loop is { } l) l.Cmds.Add((KindOf(p[0]), p));
        }
        FlushLoop();
        samples.Sort((a, b) => a.Time.CompareTo(b.Time));
        foreach (var e in elements)
        {
            for (int i = 0; i < e.Tracks.Length; i++) e.Tracks[i] = e.Tracks[i]?.OrderBy(c => c.Start).ToList(); // stable: ties keep file order
            var ps = e.Params.OrderBy(x => x.Start).ToList();
            e.Params.Clear();
            e.Params.AddRange(ps);
        }
        elements.RemoveAll(e => e.Start > e.End && e.Triggers == null);
        triggered = elements.Where(e => e.Triggers != null).ToList();
        var byLayer = elements.OrderBy(e => e.Layer).ToList(); // draw order: layer, then file order
        elements.Clear();
        elements.AddRange(byLayer);
    }

    static char KindOf(string s) => s == "MX" ? 'x' : s == "MY" ? 'y' : s.Length > 0 ? s[0] : ' ';

    static string Clean(string path) => path.Trim().Trim('"').Replace('\\', '/');

    // A command line (kind already decoded: x/y for MX/MY) into one or more timed segments. Extra values chain
    // further segments of the same duration ("F,0,0,1000,0,1,0" fades in then out).
    static IEnumerable<(char Kind, int Easing, double Start, double End, float[] Values)> Expand(char kind, string[] p)
    {
        int arity = kind switch { 'M' or 'V' => 2, 'C' => 3, 'P' => 1, _ => 1 };
        if (p.Length < 4) yield break;
        int easing = (int)D(p[1]);
        double start = D(p[2]), end = p[3].Length == 0 ? start : D(p[3]);
        if (kind == 'P')
        {
            if (p.Length > 4) yield return ('P', 0, start, end, [p[4].Length > 0 ? p[4][0] : ' ']);
            yield break;
        }
        var values = p.Skip(4).Select(F).ToArray();
        if (values.Length < arity) yield break;
        int segments = Math.Max(1, values.Length / arity - 1);
        double duration = end - start;
        for (int i = 0; i < segments; i++)
        {
            var from = values.AsSpan(i * arity, arity);
            var to = values.Length >= (i + 2) * arity ? values.AsSpan((i + 1) * arity, arity) : from;
            yield return (kind, easing, start + i * duration, end + i * duration, [.. from, .. to]);
        }
    }

    static void AddCmd(Element e, char kind, int easing, double start, double end, float[] v, List<object>? created = null)
    {
        e.Start = Math.Min(e.Start, start);
        e.End = Math.Max(e.End, end);
        void Add(Track t, float from, float to)
        {
            var c = new Cmd(start, end, easing, from, to);
            (e.Tracks[(int)t] ??= []).Add(c);
            created?.Add(c);
        }
        int half = v.Length / 2;
        switch (kind)
        {
            case 'F': Add(Track.Fade, v[0], v[half]); break;
            case 'M': Add(Track.X, v[0], v[half]); Add(Track.Y, v[1], v[half + 1]); break;
            case 'x': Add(Track.X, v[0], v[half]); break;
            case 'y': Add(Track.Y, v[0], v[half]); break;
            case 'S': Add(Track.Scale, v[0], v[half]); break;
            case 'V': Add(Track.VX, v[0], v[half]); Add(Track.VY, v[1], v[half + 1]); break;
            case 'R': Add(Track.Rotation, v[0], v[half]); break;
            case 'C': Add(Track.R, v[0], v[half]); Add(Track.G, v[1], v[half + 1]); Add(Track.B, v[2], v[half + 2]); break;
            case 'P': e.Params.Add((start, end, (char)v[0])); created?.Add((start, end, (char)v[0])); break;
        }
    }

    List<Element> triggered = [];
    static readonly System.Text.RegularExpressions.Regex HitSoundName =
        new(@"^HitSound(All|Normal|Soft|Drum)?(All|Normal|Soft|Drum)?(Whistle|Finish|Clap)?(\d+)?$");
    static int SetOf(string s) => s switch { "Normal" => 1, "Soft" => 2, "Drum" => 3, _ => 0 };

    // A hitsound played: fires HitSound[set][additionSet][addition][index] triggers that match it.
    public void FireHitSound(double time, int set, int additionSet, int bits, int index) => Fire(time, name =>
    {
        var m = HitSoundName.Match(name);
        if (!m.Success) return false;
        if (m.Groups[1].Success && SetOf(m.Groups[1].Value) is int s1 && s1 != 0 && s1 != set) return false;
        if (m.Groups[2].Success && SetOf(m.Groups[2].Value) is int s2 && s2 != 0 && s2 != additionSet) return false;
        if (m.Groups[3].Success && (bits & (m.Groups[3].Value switch { "Whistle" => 2, "Finish" => 4, _ => 8 })) == 0) return false;
        return !m.Groups[4].Success || int.Parse(m.Groups[4].Value) == index;
    });

    // "Passing", "Failing" or "HitObjectHit".
    public void Fire(double time, string name) => Fire(time, n => n == name);

    void Fire(double time, Func<string, bool> matches)
    {
        foreach (var e in triggered)
            foreach (var trig in e.Triggers!)
            {
                if (time < trig.Start || time > trig.End || !matches(trig.Name)) continue;
                foreach (var other in e.Triggers!)
                    if (other.Group == trig.Group)
                    {
                        foreach (var o in other.Active)
                            if (o is Cmd c) foreach (var track in e.Tracks) track?.Remove(c);
                            else e.Params.Remove(((double, double, char))o);
                        other.Active.Clear();
                    }
                foreach (var (kind, p) in trig.Lines)
                    foreach (var (k, easing, s, end, values) in Expand(kind, p))
                        AddCmd(e, k, easing, s + time, end + time, values, trig.Active);
                for (int i = 0; i < e.Tracks.Length; i++) e.Tracks[i] = e.Tracks[i]?.OrderBy(c => c.Start).ToList();
                var ps = e.Params.OrderBy(x => x.Start).ToList();
                e.Params.Clear();
                e.Params.AddRange(ps);
            }
    }

    void LoadTextures()
    {
        foreach (var e in elements)
        {
            e.Textures = new Texture2D[e.Frames];
            for (int i = 0; i < e.Frames; i++)
            {
                string file = e.Frames == 1 ? e.Path : System.IO.Path.ChangeExtension(e.Path, null) + i + System.IO.Path.GetExtension(e.Path);
                if (!textures.TryGetValue(file, out var tex))
                {
                    var full = System.IO.Path.Combine(folder, file);
                    tex = File.Exists(full) ? LoadTexture(full) : default;
                    if (tex.Id != 0)
                    {
                        SetTextureFilter(tex, TextureFilter.Bilinear);
                        SetTextureWrap(tex, TextureWrap.Clamp);
                    }
                    textures[file] = tex;
                }
                e.Textures[i] = tex;
            }
        }
    }

    public void Unload()
    {
        foreach (var t in textures.Values) if (t.Id != 0) UnloadTexture(t);
        foreach (var s in sounds.Values) s?.Unload();
    }

    // Value of a track at t: before its first command, that command's start value; otherwise the last command
    // started by t, interpolated while running and held at its end value afterwards.
    static float Value(List<Cmd>? track, double t, float def)
    {
        if (track == null) return def;
        int lo = 0, hi = track.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (track[mid].Start <= t) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) return track[0].From;
        var c = track[found];
        if (t >= c.End) return c.To;
        double p = Easing.Apply(c.Easing, (t - c.Start) / (c.End - c.Start));
        return (float)(c.From + (c.To - c.From) * p);
    }

    bool Param(Element e, double t, char kind)
    {
        foreach (var (s, end, k) in e.Params)
        {
            if (s > t) break;
            if (k == kind && (t < end || s == end)) return true;
        }
        return false;
    }

    // Sound samples that came due between the previous frame and now.
    public void PlaySamples(double time)
    {
        while (nextSample < samples.Count && samples[nextSample].Time <= time)
        {
            var (t, layer, path, volume) = samples[nextSample++];
            if (time - t > 1000 || layer == 1 && Passing || layer == 2 && !Passing) continue; // skipped past it, or the other layer
            sounds.TryGetValue(path, out var pool);
            pool?.Play(BeatmapSamples.EffectVolume * volume / 100f);
        }
    }

    // Seeking (skip, retry) without replaying every sample in between.
    public void SeekSamples(double time) => nextSample = samples.FindIndex(s => s.Time > time) is int i and >= 0 ? i : samples.Count;

    // overlay: false draws background..foreground (under the hit objects), true draws the overlay layer.
    public void Draw(double t, int w, int h, float dim, bool overlay)
    {
        if (dim <= 0) return;
        float s = h / 480f;
        float ox = (w - 640 * s) / 2;
        if (!Widescreen) BeginScissorMode((int)ox, 0, (int)(640 * s), h); // 4:3 storyboards are cut to 4:3, like stable
        foreach (var e in elements)
        {
            if (t < e.Start || t > e.End || (e.Layer == 4) != overlay) continue;
            if (e.Layer == 1 && Passing || e.Layer == 2 && !Passing) continue;
            float alpha = Value(e.Tracks[(int)Track.Fade], t, 1) * dim;
            if (alpha <= 0) continue;
            int frame = 0;
            if (e.Frames > 1 && e.FrameDelay > 0)
            {
                int n = (int)((t - e.Start) / e.FrameDelay);
                frame = e.LoopOnce ? Math.Min(n, e.Frames - 1) : n % e.Frames;
            }
            var tex = e.Textures[frame];
            if (tex.Id == 0) continue;
            float scale = Value(e.Tracks[(int)Track.Scale], t, 1);
            float sx = scale * Value(e.Tracks[(int)Track.VX], t, 1), sy = scale * Value(e.Tracks[(int)Track.VY], t, 1);
            if (sx == 0 || sy == 0) continue;
            float x = Value(e.Tracks[(int)Track.X], t, e.X), y = Value(e.Tracks[(int)Track.Y], t, e.Y);
            float rot = Value(e.Tracks[(int)Track.Rotation], t, 0);
            var colour = new Color(
                (byte)Math.Clamp(Value(e.Tracks[(int)Track.R], t, 255), 0, 255),
                (byte)Math.Clamp(Value(e.Tracks[(int)Track.G], t, 255), 0, 255),
                (byte)Math.Clamp(Value(e.Tracks[(int)Track.B], t, 255), 0, 255),
                (byte)Math.Clamp(alpha * 255, 0, 255));
            bool flipH = Param(e, t, 'H') ^ sx < 0, flipV = Param(e, t, 'V') ^ sy < 0;
            float dw = tex.Width * Math.Abs(sx) * s, dh = tex.Height * Math.Abs(sy) * s;
            var src = new Rectangle(0, 0, flipH ? -tex.Width : tex.Width, flipV ? -tex.Height : tex.Height);
            var origin = new Vector2(flipH ? 1 - e.Origin.X : e.Origin.X, flipV ? 1 - e.Origin.Y : e.Origin.Y) * new Vector2(dw, dh);
            bool additive = Param(e, t, 'A');
            if (additive) BeginBlendMode(BlendMode.Additive);
            DrawTexturePro(tex, src, new Rectangle(ox + x * s, y * s, dw, dh), origin, rot * 180 / MathF.PI, colour);
            if (additive) EndBlendMode();
        }
        if (!Widescreen) EndScissorMode();
    }
}

// osu!'s storyboard easings (ids 0-34).
static class Easing
{
    public static double Apply(int id, double t)
    {
        t = Math.Clamp(t, 0, 1);
        const double c1 = 1.70158, c2 = c1 * 1.525, c3 = c1 + 1;
        switch (id)
        {
            case 1: return 1 - (1 - t) * (1 - t); // Out (legacy)
            case 2: return t * t; // In (legacy)
            case 3: return t * t;
            case 4: return 1 - (1 - t) * (1 - t);
            case 5: return t < 0.5 ? 2 * t * t : 1 - Math.Pow(-2 * t + 2, 2) / 2;
            case 6: return t * t * t;
            case 7: return 1 - Math.Pow(1 - t, 3);
            case 8: return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
            case 9: return Math.Pow(t, 4);
            case 10: return 1 - Math.Pow(1 - t, 4);
            case 11: return t < 0.5 ? 8 * Math.Pow(t, 4) : 1 - Math.Pow(-2 * t + 2, 4) / 2;
            case 12: return Math.Pow(t, 5);
            case 13: return 1 - Math.Pow(1 - t, 5);
            case 14: return t < 0.5 ? 16 * Math.Pow(t, 5) : 1 - Math.Pow(-2 * t + 2, 5) / 2;
            case 15: return 1 - Math.Cos(t * Math.PI / 2);
            case 16: return Math.Sin(t * Math.PI / 2);
            case 17: return -(Math.Cos(Math.PI * t) - 1) / 2;
            case 18: return t == 0 ? 0 : Math.Pow(2, 10 * t - 10);
            case 19: return t == 1 ? 1 : 1 - Math.Pow(2, -10 * t);
            case 20: return t == 0 ? 0 : t == 1 ? 1 : t < 0.5 ? Math.Pow(2, 20 * t - 10) / 2 : (2 - Math.Pow(2, -20 * t + 10)) / 2;
            case 21: return 1 - Math.Sqrt(1 - t * t);
            case 22: return Math.Sqrt(1 - Math.Pow(t - 1, 2));
            case 23: return t < 0.5 ? (1 - Math.Sqrt(1 - Math.Pow(2 * t, 2))) / 2 : (Math.Sqrt(1 - Math.Pow(-2 * t + 2, 2)) + 1) / 2;
            case 24: return t == 0 ? 0 : t == 1 ? 1 : -Math.Pow(2, 10 * t - 10) * Math.Sin((t * 10 - 10.75) * (2 * Math.PI / 3));
            case 25: return ElasticOut(t, 1);
            case 26: return ElasticOut(t, 0.5);
            case 27: return ElasticOut(t, 0.25);
            case 28: return t == 0 ? 0 : t == 1 ? 1 : t < 0.5
                ? -(Math.Pow(2, 20 * t - 10) * Math.Sin((20 * t - 11.125) * (2 * Math.PI / 4.5))) / 2
                : Math.Pow(2, -20 * t + 10) * Math.Sin((20 * t - 11.125) * (2 * Math.PI / 4.5)) / 2 + 1;
            case 29: return c3 * t * t * t - c1 * t * t;
            case 30: return 1 + c3 * Math.Pow(t - 1, 3) + c1 * Math.Pow(t - 1, 2);
            case 31: return t < 0.5 ? Math.Pow(2 * t, 2) * ((c2 + 1) * 2 * t - c2) / 2 : (Math.Pow(2 * t - 2, 2) * ((c2 + 1) * (t * 2 - 2) + c2) + 2) / 2;
            case 32: return 1 - BounceOut(1 - t);
            case 33: return BounceOut(t);
            case 34: return t < 0.5 ? (1 - BounceOut(1 - 2 * t)) / 2 : (1 + BounceOut(2 * t - 1)) / 2;
            default: return t;
        }
    }

    static double ElasticOut(double t, double period) =>
        t == 0 ? 0 : t == 1 ? 1 : Math.Pow(2, -10 * t) * Math.Sin((t * 10 * period - 0.75) * (2 * Math.PI / 3)) + 1;

    static double BounceOut(double t)
    {
        const double n1 = 7.5625, d1 = 2.75;
        if (t < 1 / d1) return n1 * t * t;
        if (t < 2 / d1) return n1 * (t -= 1.5 / d1) * t + 0.75;
        if (t < 2.5 / d1) return n1 * (t -= 2.25 / d1) * t + 0.9375;
        return n1 * (t -= 2.625 / d1) * t + 0.984375;
    }
}
