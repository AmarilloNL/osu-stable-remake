using System.Globalization;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

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
        public double MaxEnd; // the latest end among this and the commands before it in its track (Sorted)
    }

    // a track in start order (ties keep file order, like stable's), with each command's running latest end
    static List<Cmd>? Sorted(List<Cmd>? track)
    {
        if (track == null) return null;
        var list = track.OrderBy(c => c.Start).ToList();
        double max = double.NegativeInfinity;
        foreach (var c in list) c.MaxEnd = max = Math.Max(max, c.End);
        return list;
    }

    sealed class Element
    {
        public int Layer; // 0 background, 1 fail, 2 pass, 3 foreground, 4 overlay
        public bool Animation;
        public string Always = ""; // P parameters of no length: stable sets them on the sprite for its whole life
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
    public int CountIn(params int[] layers) => elements.Count(e => layers.Contains(e.Layer));
    // The earliest event (sprite command or sample), capped at 0: stable starts the map early enough to show it.
    // the last event's end (stable holds the ranking screen back until 500ms before it)
    public double LatestEvent => Math.Max(elements.Count > 0 ? elements.Max(e => e.End) : double.NegativeInfinity, samples.Count > 0 ? samples.Max(s => s.Time) : double.NegativeInfinity);
    public double EarliestEvent => Math.Min(0, Math.Min(elements.Count > 0 ? elements.Min(e => e.Start) : 0, samples.Count > 0 ? samples.Min(s => s.Time) : 0));
    readonly HashSet<string> referenced = new(StringComparer.OrdinalIgnoreCase);
    // stable hides the map's background when a Background-layer sprite names the same file (slashes made alike, case kept)
    readonly HashSet<string> backgroundLayerFiles = new(StringComparer.Ordinal);
    public bool UsesFile(string file) => backgroundLayerFiles.Contains(Clean(file));
    public IEnumerable<string> ReferencedFiles => referenced;
    public int CountShorterThan(double ms) => elements.Count(e => e.End - e.Start < ms); // (AiMod's epilepsy check)

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
        // Like stable's: the difficulty's own events first, then the one .osb named after the map (other .osb files in the
        // folder are left alone); a layer draws them in that order
        ReadEvents(map.File, lines, vars, ref widescreen);
        string osb = System.IO.Path.Combine(map.Folder, OsbName(map.Artist, map.Title, map.Creator, map.File));
        if (File.Exists(osb)) ReadEvents(osb, lines, vars, ref widescreen);
        var sb = new Storyboard(map.Folder, widescreen);
        sb.formatVersion = map.FormatVersion;
        sb.Parse(lines, vars);
        if (sb.IsEmpty) return null;
        foreach (var path in sb.samples.Select(s => s.Path).Distinct(StringComparer.OrdinalIgnoreCase))
            sb.sounds[path] = SoundPool.Load(System.IO.Path.Combine(map.Folder, path)); // up front: no mid-play hitch
        if (textures) sb.LoadTextures();
        else { sb.elements.Clear(); sb.triggered.Clear(); }
        return sb;
    }

    // The editor's Design view: the .osb with the difficulty's [Events] as they stand in the editor (not as saved);
    // textures an earlier view already loaded are taken over, so an edit only loads what's new. No sounds.
    public static Storyboard? LoadLines(Beatmap map, IEnumerable<string> osuEvents, IEnumerable<string> osuVariables, IEnumerable<string>? osbLines, bool widescreenOsu, Storyboard? reuse)
    {
        var lines = new List<string>();
        bool widescreen = false;
        var vars = new List<(string Name, string Value)>();
        ReadEvents(new[] { "[Variables]" }.Concat(osuVariables).Append("[Events]").Concat(osuEvents), lines, vars, ref widescreen);
        if (osbLines != null) ReadEvents(osbLines, lines, vars, ref widescreen);
        widescreen |= widescreenOsu;
        var sb = new Storyboard(map.Folder, widescreen);
        sb.formatVersion = map.FormatVersion;
        sb.Parse(lines, vars);
        if (reuse != null) { foreach (var (k, v) in reuse.textures) sb.textures[k] = v; reuse.textures.Clear(); }
        sb.LoadTextures();
        return sb;
    }
    public bool[] ShowLayer = [true, true, true, true, true]; // (the Design view's layer toggles)
    public bool BothStates; // the Design view: the fail and pass layers both, not as the play goes
    public Texture2D? TextureOf(string path) => textures.TryGetValue(path, out var t) && t.Id != 0 ? t : null;

    // stable's .osb name: "Artist - Title (Creator).osb" (the .osu's own name without an artist, no "(...)" without a
    // creator), characters a file name can't have taken out
    static string OsbName(string artist, string title, string creator, string osuFile)
    {
        string name = (artist.Length > 0 ? artist + " - " + title : System.IO.Path.GetFileNameWithoutExtension(osuFile)) + (creator.Length > 0 ? " (" + creator + ")" : "") + ".osb";
        foreach (char c in System.IO.Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
        return name;
    }

    static void ReadEvents(string path, List<string> into, List<(string, string)> vars, ref bool widescreen) => ReadEvents(File.ReadLines(path), into, vars, ref widescreen);
    static void ReadEvents(IEnumerable<string> fileLines, List<string> into, List<(string, string)> vars, ref bool widescreen)
    {
        string section = "";
        foreach (var raw in fileLines)
        {
            var t = raw.Trim();
            // (in [Events] a blank or comment line is kept as an empty line: it ends the sprite's commands, like stable's)
            if (t.Length == 0 || t.StartsWith("//")) { if (section == "[Events]") into.Add(""); continue; }
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

    internal static Vector2 OriginOf(string s) => s switch
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

    // (NaN / infinity read as 0: a NaN time froze the play's clock)
    static float F(string s) => float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && float.IsFinite(v) ? v : 0;
    static double D(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && double.IsFinite(v) ? v : 0;

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
                // (a loop of no length runs once; a huge count stops at 100,000 runs: it built them all up front and ran
                // out of memory)
                int count = length <= 0 ? 1 : Math.Clamp(l.Count, 1, 100_000);
                for (int i = 0; i < count; i++)
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
            // stable reads a sprite's commands while the lines are indented: a blank or comment line ends them (commands
            // after it belong to no sprite)
            if (raw.Length == 0) { FlushLoop(); trigger = null; current = null; continue; }
            string line = raw;
            if (line.Contains('$'))
                foreach (var (name, value) in vars) line = line.Replace(name, value); // (in the order they were defined, like stable's)
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
                            Animation = true, Frames = Math.Max(1, (int)D(p[6])), FrameDelay = FrameDelayOf(D(p[7])), LoopOnce = p.Length > 8 && p[8] is "LoopOnce" or "1",
                        };
                        elements.Add(current);
                        break;
                    case "Sample" or "5" when p.Length >= 4:
                        samples.Add((D(p[1]), (int)D(p[2]), Clean(p[3]), p.Length > 4 ? Math.Clamp((int)D(p[4]), 0, 100) : 100)); // (stable: volume 0-100)
                        break;
                }
                continue;
            }
            if (current == null) continue;
            if (depth == 1)
            {
                FlushLoop();
                trigger = null;
                // (stable: "T,name" alone has no time window; its times are read only with both given, and an end of 0 is none)
                if (p[0] == "T" && p.Length >= 2 && p.Length != 3)
                {
                    trigger = new Trigger(p[1], p.Length > 3 ? D(p[2]) : 0, p.Length > 3 ? D(p[3]) : 0, p.Length > 4 ? (int)D(p[4]) : 0);
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
            for (int i = 0; i < e.Tracks.Length; i++) e.Tracks[i] = Sorted(e.Tracks[i]);
            var ps = e.Params.OrderBy(x => x.Start).ToList();
            e.Params.Clear();
            e.Params.AddRange(ps);
            e.Always = string.Concat(ps.Where(x => x.Start == x.End).Select(x => x.Kind).Distinct());
        }
        foreach (var e in elements) { referenced.Add(e.Path); if (e.Layer == 0) backgroundLayerFiles.Add(e.Path); } // before dropping sprites without commands: stable still hides a background they name
        elements.RemoveAll(e => e.Start > e.End && e.Triggers == null);
        triggered = elements.Where(e => e.Triggers != null).ToList();
        var byLayer = elements.OrderBy(e => e.Layer).ToList(); // draw order: layer, then file order
        elements.Clear();
        elements.AddRange(byLayer);
    }

    int formatVersion = 14;
    // stable: maps before format 6 had their frame delays in 60Hz frames of 1.186 (rounded at 0.015 a ms)
    double FrameDelayOf(double d) => formatVersion < 6 ? Math.Round(0.015 * d) * 1.186 * (1000 / 60.0) : d;

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
        new(@"^HitSound(All|Normal|Soft|Drum)?(All|Normal|Soft|Drum)?(Whistle|Finish|Clap)?([0-9]{1,9})?$"); // (plain digits that fit an int)
    static int SetOf(string s) => s switch { "Normal" => 1, "Soft" => 2, "Drum" => 3, _ => 0 };

    // A hitsound played: fires HitSound[set][additionSet][addition][index] triggers that match it.
    public void FireHitSound(double time, int set, int additionSet, int bits, int index) => Fire(time, name =>
    {
        var m = HitSoundName.Match(name);
        if (!m.Success) return false;
        // (stable: one set before an addition is the addition's set: "HitSoundSoftWhistle" is a whistle from the soft set)
        bool oneSet = m.Groups[1].Success && !m.Groups[2].Success && m.Groups[3].Success;
        var normal = oneSet ? null : m.Groups[1].Success ? m.Groups[1].Value : null;
        var addition = oneSet ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : null;
        if (normal != null && SetOf(normal) is int s1 && s1 != 0 && s1 != set) return false;
        if (addition != null && SetOf(addition) is int s2 && s2 != 0 && s2 != additionSet) return false;
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
                if (!matches(trig.Name)) continue;
                // stable: a firing first takes out what earlier firings added: Passing / Failing take out every Passing and
                // Failing trigger's (even outside their window), others their group's, or with no group (0) only their own
                void Clear(Trigger other)
                {
                    foreach (var o in other.Active)
                        if (o is Cmd c) foreach (var track in e.Tracks) track?.Remove(c);
                        else e.Params.Remove(((double, double, char))o);
                    other.Active.Clear();
                }
                bool passFail = trig.Name is "Passing" or "Failing";
                if (passFail) foreach (var other in e.Triggers!) if (other.Name is "Passing" or "Failing") Clear(other);
                if (trig.End != 0 && (time < trig.Start || time > trig.End)) continue;
                if (!passFail) foreach (var other in e.Triggers!) if (trig.Group != 0 ? other.Group == trig.Group : other == trig) Clear(other);
                foreach (var (kind, p) in trig.Lines)
                    foreach (var (k, easing, s, end, values) in Expand(kind, p))
                        AddCmd(e, k, easing, s + time, end + time, values, trig.Active);
                for (int i = 0; i < e.Tracks.Length; i++) e.Tracks[i] = Sorted(e.Tracks[i]);
                var ps = e.Params.OrderBy(x => x.Start).ToList();
                e.Params.Clear();
                e.Params.AddRange(ps);
                e.Always = string.Concat(ps.Where(x => x.Start == x.End).Select(x => x.Kind).Distinct());
            }
    }

    void LoadTextures()
    {
        // stable's frame files: the number before every "." of the path ("a.png" -> "a0.png"), one frame too; a sprite's own path
        static string FileOf(Element e, int i) => e.Frames == 1 && !e.Animation ? e.Path : e.Path.Replace(".", i + ".");
        // an animation claiming over 10,000 frames runs over the ones that are there (up to the first missing one): a
        // count like 2^31 built that many names and hung
        foreach (var e in elements.Where(e => e.Frames > 10_000))
        {
            int n = 0;
            while (n < e.Frames && File.Exists(System.IO.Path.Combine(folder, FileOf(e, n)))) n++;
            e.Frames = Math.Max(2, n); // (two at least: a single frame's name has no number)
        }
        var files = elements.SelectMany(e => Enumerable.Range(0, e.Frames).Select(i => FileOf(e, i))).Distinct(StringComparer.OrdinalIgnoreCase).Where(f => !textures.ContainsKey(f)).ToArray();
        // Decoding was most of the time a play took to start on a big storyboard: it's done on all cores while the
        // images are uploaded here as they come (a few waiting at most, to keep memory down).
        using var decoded = new System.Collections.Concurrent.BlockingCollection<(int, Image)>(Environment.ProcessorCount);
        var decoding = Task.Run(() =>
        {
            try
            {
                Parallel.For(0, files.Length, k =>
                {
                    var full = System.IO.Path.Combine(folder, files[k]);
                    decoded.Add((k, File.Exists(full) ? LoadImage(full) : default));
                });
            }
            finally { decoded.CompleteAdding(); }
        });
        foreach (var (k, img) in decoded.GetConsumingEnumerable())
        {
            var tex = img.Width > 0 ? LoadTextureFromImage(img) : default;
            UnloadImage(img);
            if (tex.Id != 0)
            {
                SetTextureFilter(tex, TextureFilter.Bilinear);
                SetTextureWrap(tex, TextureWrap.Clamp);
            }
            textures[files[k]] = tex;
        }
        decoding.Wait(); // rethrows a failure
        foreach (var e in elements)
        {
            e.Textures = new Texture2D[e.Frames];
            for (int i = 0; i < e.Frames; i++) e.Textures[i] = textures[FileOf(e, i)];
        }
    }

    public void Unload()
    {
        foreach (var t in textures.Values) if (t.Id != 0) UnloadTexture(t);
        foreach (var s in sounds.Values) s?.Unload();
    }

    // Value of a track at t, like stable's: before its first command, that command's start value; otherwise the first
    // command (in start order) running at t, or with none running the last one started, held at its end value.
    static float Value(List<Cmd>? track, double t, float def)
    {
        if (track is not { Count: > 0 }) return def; // (a trigger's commands taken out can leave it empty)
        int lo = 0, hi = track.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (track[mid].Start <= t) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) return track[0].From;
        // the first running: the earliest command whose running latest end reaches t (it started by t: at or before found)
        int first = -1;
        lo = 0; hi = found;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (track[mid].MaxEnd >= t) { first = mid; hi = mid - 1; }
            else lo = mid + 1;
        }
        var c = track[first >= 0 ? first : found];
        if (t >= c.End) return c.To;
        double p = Easing.Apply(c.Easing, (t - c.Start) / (c.End - c.Start));
        return (float)(c.From + (c.To - c.From) * p);
    }

    bool Param(Element e, double t, char kind)
    {
        if (e.Always.Contains(kind)) return true;
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
    // (screenY: where its area starts on the screen, for the cut - the editor draws it under its menu strip)
    public void Draw(double t, int w, int h, float dim, bool overlay, int screenY = 0)
    {
        if (dim <= 0) return;
        float s = h / 480f;
        float ox = (w - 640 * s) / 2;
        if (!Widescreen) BeginScissorMode((int)ox, screenY, (int)(640 * s), h); // 4:3 storyboards are cut to 4:3, like stable
        foreach (var e in elements)
        {
            // (stable: a sprite with triggers stays, whatever its commands' times; its alpha is 0 until a fade sets it)
            if (e.Triggers == null && (t < e.Start || t > e.End) || (e.Layer == 4) != overlay || !ShowLayer[Math.Clamp(e.Layer, 0, 4)]) continue;
            if (!BothStates && (e.Layer == 1 && Passing || e.Layer == 2 && !Passing)) continue;
            // stable dims a storyboard by darkening its colours, not by fading it: opaque sprites stay opaque.
            var fades = e.Tracks[(int)Track.Fade];
            float alpha = e.Triggers != null && (fades is not { Count: > 0 } || t < fades[0].Start) ? 0 : Value(fades, t, 1);
            if (alpha <= 0) continue;
            int frame = 0;
            if (e.Frames > 1 && e.FrameDelay > 0)
            {
                int n = (int)((t - e.Start) / e.FrameDelay);
                frame = e.LoopOnce ? Math.Min(n, e.Frames - 1) : n % e.Frames;
            }
            var tex = e.Textures[frame];
            if (tex.Id == 0) continue;
            float scale = Math.Max(0, Value(e.Tracks[(int)Track.Scale], t, 1)); // (stable: a negative S is 0, not a flip; V can flip)
            float sx = scale * Value(e.Tracks[(int)Track.VX], t, 1), sy = scale * Value(e.Tracks[(int)Track.VY], t, 1);
            if (sx == 0 || sy == 0) continue;
            float x = Value(e.Tracks[(int)Track.X], t, e.X), y = Value(e.Tracks[(int)Track.Y], t, e.Y);
            float rot = Value(e.Tracks[(int)Track.Rotation], t, 0);
            var colour = new Color(
                (byte)Math.Clamp(Value(e.Tracks[(int)Track.R], t, 255) * dim, 0, 255),
                (byte)Math.Clamp(Value(e.Tracks[(int)Track.G], t, 255) * dim, 0, 255),
                (byte)Math.Clamp(Value(e.Tracks[(int)Track.B], t, 255) * dim, 0, 255),
                (byte)Math.Clamp(alpha * 255, 0, 255));
            bool flipH = Param(e, t, 'H') ^ sx < 0, flipV = Param(e, t, 'V') ^ sy < 0;
            float dw = tex.Width * Math.Abs(sx) * s, dh = tex.Height * Math.Abs(sy) * s;
            var src = new Rectangle(0, 0, flipH ? -tex.Width : tex.Width, flipV ? -tex.Height : tex.Height);
            // stable builds the quad from the origin with the scale's size and only mirrors the texture coordinates on a
            // flip (P H/V or a negative scale): the sprite stays on the same side of its origin.
            var origin = e.Origin * new Vector2(dw, dh);
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
