using System.Globalization;
using System.Numerics;
using System.Text;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// The beatmap editor, laid out like stable's: the timeline along the top with the beat snap divisor, the playfield
// (with its grid) in the middle, the toolbox on the left (Select / Circle / Slider / Spinner: 1-4), new combo and
// hitsound toggles on the right (Q, W / E / R), and the song's position, playback and rate along the bottom.
// F1 Compose, F3 Timing, F4 Song Setup, F5 test play, Ctrl+S save, Ctrl+Z / Ctrl+Y undo / redo, Ctrl+C / X / V,
// Ctrl+A, Delete; the wheel steps through the beat snap (Ctrl zooms the timeline, Alt changes the divisor);
// Z / X / C / V / B and Space for playback. The .osu file is kept as written, only the edited sections rewritten.
sealed class Editor : Screen
{
    // ---- the file: its sections as written, key-value sections editable, timing points and objects parsed
    sealed class Section(string name) { public string Name = name; public List<string> Lines = []; public int Blanks = 1; } // Blanks: the empty lines after it, as written

    sealed class EObj
    {
        public double X, Y, Time, End; // End: a slider's (computed) or spinner's end
        public int Type; // stable's bits: 1 circle, 2 slider, 8 spinner, 128 hold; 4 new combo, 16-64 colour skip
        public int HitSound;
        public char Curve = 'B';
        public List<Vector2> Anchors = []; // a slider's points after its head
        public int Slides = 1;
        public double Length;
        public string Edges = "", EdgeSets = "", Sample = "", Rest = ""; // kept as written (Rest: a spinner / hold's tail)
        public string? Raw; // the line as read, written back as long as nothing changed
        public Vector2[] Path = []; // (built for drawing)
        public int Combo, Colour;
        public bool Circle => (Type & 1) != 0;
        public bool Slider => (Type & 2) != 0;
        public bool Spinner => (Type & 8) != 0;
        public bool NewCombo { get => (Type & 4) != 0; set => Type = value ? Type | 4 : Type & ~4; }
        public EObj Clone() => (EObj)MemberwiseClone() is var c ? Fix(c) : this;
        EObj Fix(EObj c) { c.Anchors = [.. Anchors]; return c; }
    }

    sealed class ETiming
    {
        public double Time, Beat = 500;
        public int Meter = 4, Set = 1, Index = 0, Volume = 100, Effects;
        public bool Red = true;
        public string? Raw; // the line as read: written back while its values are unchanged
        public double Bpm => 60000 / Beat;
        public double Sv => Beat < 0 ? -100 / Beat : 1;
    }

    readonly string path;
    readonly SongSelect back;
    string header = "osu file format v14";
    readonly List<Section> sections = [];
    List<EObj> objs = [];
    List<ETiming> timing = [];
    int version = 14;
    Beatmap info; // the map as loaded (folder, audio, colours, defaults)
    Track? music;
    double length = 1;
    double time, rate = 1;
    double lastSoundTime = double.NaN;
    BeatmapSamples samples;
    readonly Skin skin = Game.Skin;
    bool modified;

    // ---- editing state
    int tab; // 0 compose, 1 timing, 2 song setup
    int tool = 1; // 0 select, 1 circle, 2 slider, 3 spinner
    int divisor = 4;
    static readonly int[] Divisors = [1, 2, 3, 4, 6, 8, 12, 16];
    int gridSize = 32;
    bool gridSnap = true;
    float zoom = 1; // timeline: 1 = a beat 50 units wide
    readonly HashSet<EObj> selected = [];
    bool newComboNext; int soundNext; // for the next placed object (nothing selected)
    readonly List<string> undo = [], redo = [];
    List<EObj> clipboard = [];
    List<Vector2>? sliderDraft; double sliderDraftTime;
    (Vector2 From, Dictionary<EObj, (double X, double Y)> Start)? drag; // moving the selection on the playfield
    (double From, Dictionary<EObj, double> Start)? timeDrag; // ... or in time on the timeline
    EObj? endDrag; // a spinner's end, on the timeline
    Vector2? boxFrom; // drag-selecting
    double scrubFrom = double.NaN, scrubTime;
    int timingSel = -1;
    int field = -1; // the Song Setup text field being typed into
    string? exitPrompt; double exitAt;
    double loadedAt = Game.NowMs;

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    static float U => Game.H / 480f;
    static KeyboardKey Bound(string key, KeyboardKey def) => Config.Key(key, def); // stable's editor bindings (Options)
    float FieldScale => 0.9f * U;
    Vector2 FieldOrigin => new((Game.W - 512 * FieldScale) / 2, 78 * U);
    Vector2 ToScreen(double x, double y) => FieldOrigin + new Vector2((float)x, (float)y) * FieldScale;
    Vector2 ToField(Vector2 s) => (s - FieldOrigin) / FieldScale;

    public Editor(string file, SongSelect back)
    {
        path = file;
        this.back = back;
        info = Beatmap.Load(file);
        samples = new BeatmapSamples(info.Folder);
        Read(File.ReadAllLines(file));
        music = Track.Load(Path.Combine(info.Folder, info.AudioFilename), tempo: true);
        if (music != null) { music.Volume = Config.MusicVolume; length = Math.Max(1, music.LengthMs); }
        else length = Math.Max(1, objs.Count > 0 ? objs[^1].End + 2000 : 60000);
        if (Kv("Editor", "BeatDivisor") is { } bd && int.TryParse(bd, out int d) && Divisors.Contains(d)) divisor = d;
        if (Kv("Editor", "GridSize") is { } gs && int.TryParse(gs, out int g) && g is 4 or 8 or 16 or 32) gridSize = g;
        time = objs.Count > 0 ? objs[0].Time : 0;
        music?.Seek(time);
        Rebuild();
    }

    void Read(string[] lines)
    {
        sections.Clear(); objs = []; timing = [];
        Section? cur = null;
        foreach (var raw in lines)
        {
            string line = raw.TrimEnd();
            if (cur == null && line.StartsWith("osu file format v")) { header = line.Trim(); int.TryParse(line.Trim()["osu file format v".Length..], out version); continue; }
            if (line.StartsWith('[') && line.EndsWith(']')) { cur = new Section(line[1..^1]); sections.Add(cur); continue; }
            if (cur == null) continue;
            if (cur.Name == "TimingPoints" && line.Trim().Length > 0 && !line.StartsWith("//")) { if (ParseTiming(line) is { } t) timing.Add(t); cur.Blanks = 0; continue; }
            if (cur.Name == "HitObjects" && line.Trim().Length > 0) { if (ParseObject(line) is { } o) objs.Add(o); cur.Blanks = 0; continue; }
            if (line.Trim().Length == 0 && cur.Name is "TimingPoints" or "HitObjects") { cur.Blanks++; continue; }
            cur.Lines.Add(line);
        }
        foreach (var name in new[] { "General", "Editor", "Metadata", "Difficulty", "Events", "TimingPoints", "HitObjects" })
            if (sections.All(s => s.Name != name)) sections.Add(new Section(name));
        // (trailing blank lines are written back as one)
        foreach (var s in sections)
        {
            if (s.Name is "TimingPoints" or "HitObjects") continue;
            s.Blanks = 0;
            while (s.Lines.Count > 0 && s.Lines[^1].Trim().Length == 0) { s.Lines.RemoveAt(s.Lines.Count - 1); s.Blanks++; }
        }
    }

    static ETiming? ParseTiming(string line)
    {
        var f = line.Split(',');
        if (f.Length < 2) return null;
        try
        {
            return new ETiming
            {
                Time = double.Parse(f[0], Inv), Beat = double.Parse(f[1], Inv),
                Meter = f.Length > 2 ? int.Parse(f[2], Inv) : 4, Set = f.Length > 3 ? int.Parse(f[3], Inv) : 1,
                Index = f.Length > 4 ? int.Parse(f[4], Inv) : 0, Volume = f.Length > 5 ? int.Parse(f[5], Inv) : 100,
                Red = f.Length <= 6 || f[6].Trim() == "1", Effects = f.Length > 7 ? int.Parse(f[7], Inv) : 0, Raw = line,
            };
        }
        catch (FormatException) { return null; }
    }

    static string Num(double v) => v.ToString("R", Inv);

    static string WriteTiming(ETiming t) =>
        t.Raw != null && ParseTiming(t.Raw) is { } p && (p.Time, p.Beat, p.Meter, p.Set, p.Index, p.Volume, p.Red, p.Effects) == (t.Time, t.Beat, t.Meter, t.Set, t.Index, t.Volume, t.Red, t.Effects)
            ? t.Raw : $"{Num(t.Time)},{Num(t.Beat)},{t.Meter},{t.Set},{t.Index},{t.Volume},{(t.Red ? 1 : 0)},{t.Effects}";

    static EObj? ParseObject(string line)
    {
        var f = line.Split(',');
        if (f.Length < 5) return null;
        try
        {
            var o = new EObj { X = double.Parse(f[0], Inv), Y = double.Parse(f[1], Inv), Time = double.Parse(f[2], Inv), Type = int.Parse(f[3], Inv), HitSound = int.Parse(f[4], Inv), Raw = line };
            o.End = o.Time;
            if (o.Slider && f.Length > 7)
            {
                var c = f[5].Split('|');
                o.Curve = c[0].Length > 0 ? c[0][0] : 'B';
                for (int i = 1; i < c.Length; i++) { var xy = c[i].Split(':'); o.Anchors.Add(new(float.Parse(xy[0], Inv), float.Parse(xy[1], Inv))); }
                o.Slides = Math.Max(1, int.Parse(f[6], Inv));
                o.Length = double.Parse(f[7], Inv);
                o.Edges = f.Length > 8 ? f[8] : ""; o.EdgeSets = f.Length > 9 ? f[9] : ""; o.Sample = f.Length > 10 ? f[10] : "";
            }
            else if (o.Spinner && f.Length > 5) { o.End = double.Parse(f[5], Inv); o.Sample = f.Length > 6 ? f[6] : ""; }
            else if ((o.Type & 128) != 0 && f.Length > 5) { o.Rest = f[5]; o.End = double.Parse(f[5].Split(':')[0], Inv); }
            else o.Sample = f.Length > 5 ? f[5] : "";
            return o;
        }
        catch (FormatException) { return null; }
    }

    static string WriteObject(EObj o)
    {
        if (o.Raw != null) return o.Raw;
        string head = $"{Num(Math.Round(o.X))},{Num(Math.Round(o.Y))},{Num(Math.Round(o.Time))},{o.Type},{o.HitSound}";
        string sample = o.Sample.Length > 0 ? o.Sample : "0:0:0:0:";
        if (o.Slider)
        {
            string pts = string.Join("|", o.Anchors.Select(a => $"{Num(Math.Round(a.X))}:{Num(Math.Round(a.Y))}"));
            string edges = o.Edges.Length > 0 ? o.Edges : string.Join("|", Enumerable.Repeat("0", o.Slides + 1));
            string sets = o.EdgeSets.Length > 0 ? o.EdgeSets : string.Join("|", Enumerable.Repeat("0:0", o.Slides + 1));
            return $"{head},{o.Curve}|{pts},{o.Slides},{Num(o.Length)},{edges},{sets},{sample}";
        }
        if (o.Spinner) return $"{head},{Num(Math.Round(o.End))},{sample}";
        if ((o.Type & 128) != 0) return $"{head},{o.Rest}";
        return $"{head},{sample}";
    }

    string? Kv(string section, string key)
    {
        var s = sections.FirstOrDefault(x => x.Name == section);
        var line = s?.Lines.FirstOrDefault(l => l.Split(':')[0].Trim() == key);
        return line == null ? null : line[(line.IndexOf(':') + 1)..].Trim();
    }

    void SetKv(string section, string key, string value)
    {
        var s = sections.First(x => x.Name == section);
        bool spaced = section is "General" or "Editor"; // stable writes "Key: value" there, "Key:value" in the others
        string line = spaced ? $"{key}: {value}" : $"{key}:{value}";
        int i = s.Lines.FindIndex(l => l.Split(':')[0].Trim() == key);
        if (i >= 0) { if (s.Lines[i] != line) { s.Lines[i] = line; modified = true; } }
        else { s.Lines.Add(line); modified = true; }
    }

    string[] Write()
    {
        var sb = new List<string> { header, "" };
        foreach (var s in sections)
        {
            sb.Add($"[{s.Name}]");
            if (s.Name == "TimingPoints") sb.AddRange(timing.OrderBy(t => t.Time).Select(WriteTiming));
            else if (s.Name == "HitObjects") sb.AddRange(objs.OrderBy(o => o.Time).Select(WriteObject));
            else sb.AddRange(s.Lines);
            for (int i = 0; i < s.Blanks; i++) sb.Add("");
        }
        return [.. sb];
    }

    void Save()
    {
        SetKv("Editor", "BeatDivisor", divisor.ToString(Inv));
        SetKv("Editor", "GridSize", gridSize.ToString(Inv));
        try
        {
            File.WriteAllLines(path, Write(), new UTF8Encoding(false));
            modified = false;
            Notification.Show("Saved!", 1500);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notification.Show($"Couldn't save: {e.Message}", 3000); }
    }

    // ---- derived data: slider paths and ends, combo numbers and colours
    double SliderMultiplier => double.TryParse(Kv("Difficulty", "SliderMultiplier"), NumberStyles.Float, Inv, out double v) ? v : 1.4;
    Color[] Colours => info.ComboColours.Count > 0 ? info.ComboColours.Select(c => new Color(c.R, c.G, c.B, (byte)255)).ToArray() : skin.ComboColours;

    ETiming RedAt(double t) => timing.Where(p => p.Red && p.Beat > 0).OrderBy(p => p.Time).LastOrDefault(p => p.Time <= t)
        ?? timing.Where(p => p.Red && p.Beat > 0).OrderBy(p => p.Time).FirstOrDefault() ?? new ETiming();
    double SvAt(double t)
    {
        var red = RedAt(t);
        var green = timing.Where(p => !p.Red && p.Time <= t && p.Time >= red.Time).OrderBy(p => p.Time).LastOrDefault();
        return green == null ? 1 : Math.Clamp(-100 / Math.Min(-10, green.Beat), 0.1, 10);
    }

    void Rebuild()
    {
        objs = [.. objs.OrderBy(o => o.Time)];
        int combo = 0, colour = 0;
        bool force = false;
        foreach (var o in objs)
        {
            if (o.Slider)
            {
                var pts = new List<Vector2> { new((float)o.X, (float)o.Y) };
                pts.AddRange(o.Anchors);
                double len = o.Length;
                o.Path = SliderPath.Build(o.Curve, pts, ref len, version);
                var red = RedAt(o.Time);
                double perBeat = 100 * SliderMultiplier * SvAt(o.Time);
                o.End = o.Time + o.Length / perBeat * red.Beat * o.Slides;
            }
            if (o.Spinner) { force = true; continue; }
            if (o.NewCombo || force || combo == 0) { combo = 1; colour += 1 + ((o.Type >> 4) & 7); force = false; }
            else combo++;
            o.Combo = combo;
            o.Colour = colour;
        }
    }

    // A change: the state before it goes on the undo stack.
    void Change(Action edit)
    {
        undo.Add(string.Join("\n", Write()));
        if (undo.Count > 100) undo.RemoveAt(0);
        redo.Clear();
        edit();
        modified = true;
        Rebuild();
    }

    void Restore(List<string> from, List<string> to)
    {
        if (from.Count == 0) return;
        to.Add(string.Join("\n", Write()));
        string state = from[^1];
        from.RemoveAt(from.Count - 1);
        Read(state.Split('\n'));
        selected.Clear();
        modified = true;
        Rebuild();
    }

    double Snap(double t)
    {
        var red = RedAt(t);
        double step = red.Beat / divisor;
        return Math.Round(red.Time + Math.Round((t - red.Time) / step) * step);
    }

    double StepTime(double t, int dir)
    {
        var red = RedAt(t + dir);
        double step = red.Beat / divisor;
        double snapped = red.Time + Math.Round((t - red.Time) / step) * step;
        return Math.Clamp(Math.Abs(snapped - t) > 1 && Math.Sign(snapped - t) == dir ? snapped : snapped + dir * step, 0, length);
    }

    Vector2 GridSnap(Vector2 p) => gridSnap ? new(MathF.Round(p.X / gridSize) * gridSize, MathF.Round(p.Y / gridSize) * gridSize) : new(MathF.Round(p.X), MathF.Round(p.Y));

    double Preempt => Beatmap.Range(double.TryParse(Kv("Difficulty", "ApproachRate"), NumberStyles.Float, Inv, out double ar) ? ar : 5, 1800, 1200, 450);
    float Radius => (float)(64 * (1 - 0.7 * ((double.TryParse(Kv("Difficulty", "CircleSize"), NumberStyles.Float, Inv, out double cs) ? cs : 4) - 5) / 5) / 2);

    // ---- update
    bool Playing => music != null && !music.Paused && !music.Stopped && playing;
    bool playing;

    void Seek(double t)
    {
        time = Math.Clamp(t, 0, length);
        music?.Seek(time);
        lastSoundTime = double.NaN;
    }

    void SetPlaying(bool on)
    {
        playing = on;
        if (music == null) return;
        if (on) { music.Seek(time); music.Play(); }
        else music.Pause();
        lastSoundTime = double.NaN;
    }

    public override void CloseRequested() => AskExit();

    void AskExit()
    {
        if (!modified) { Leave(); return; }
        exitPrompt = "Save changes before leaving?";
        exitAt = Game.NowMs;
    }

    void Leave()
    {
        music?.Dispose();
        music = null;
        samples.Unload();
        Game.FadeTo(back);
    }

    public override void Update(double dtMs)
    {
        var mouse = Input.GetMousePosition();
        float u = U;
        if (exitPrompt != null) { UpdateExitPrompt(mouse); return; }

        // the clock
        if (music != null && playing)
        {
            if (music.Stopped) { playing = false; time = length; }
            else time = music.PositionMs;
        }
        else if (music == null && playing) time = Math.Min(length, time + dtMs * rate);
        PlayHitsounds();

        if (field >= 0) { UpdateTextField(); return; }

        if (Input.IsKeyPressed(KeyboardKey.Escape))
        {
            if (sliderDraft != null) sliderDraft = null;
            else if (selected.Count > 0) selected.Clear();
            else AskExit();
            return;
        }
        if (Input.IsKeyPressed(KeyboardKey.F1)) tab = 0;
        if (Input.IsKeyPressed(KeyboardKey.F3)) tab = 1;
        if (Input.IsKeyPressed(KeyboardKey.F4)) tab = 2;
        if (Input.IsKeyPressed(KeyboardKey.F5)) { TestPlay(); return; }
        if (Input.CtrlHeld && Input.IsKeyPressed(KeyboardKey.S)) Save();
        if (Input.CtrlHeld && Input.IsKeyPressed(KeyboardKey.Z)) Restore(undo, redo);
        if (Input.CtrlHeld && Input.IsKeyPressed(KeyboardKey.Y)) Restore(redo, undo);

        // playback (stable's bindings): Jump To First Note (Z), Play From Beginning (X), Audio Pause (C), Jump To End
        // (V); Space plays / pauses
        if (!Input.CtrlHeld)
        {
            if (Input.IsKeyPressed(Bound("keyJumpToBegin", KeyboardKey.Z))) Seek(objs.Count > 0 ? objs[0].Time : 0);
            if (Input.IsKeyPressed(Bound("keyPlayFromBegin", KeyboardKey.X))) { Seek(0); SetPlaying(true); }
            if (Input.IsKeyPressed(Bound("keyAudioPause", KeyboardKey.C))) SetPlaying(!playing);
            if (Input.IsKeyPressed(Bound("keyJumpToEnd", KeyboardKey.V))) Seek(objs.Count > 0 ? objs[^1].End : length);
        }
        if (Input.IsKeyPressed(KeyboardKey.Space)) SetPlaying(!playing);
        if (Input.IsKeyPressedRepeat(KeyboardKey.Left) || Input.IsKeyPressed(KeyboardKey.Left)) Seek(StepTime(time, -1));
        if (Input.IsKeyPressedRepeat(KeyboardKey.Right) || Input.IsKeyPressed(KeyboardKey.Right)) Seek(StepTime(time, 1));
        float wheel = Input.GetMouseWheelMove();
        if (wheel != 0)
        {
            if (Input.CtrlHeld) zoom = Math.Clamp(zoom * (wheel > 0 ? 1.25f : 0.8f), 0.1f, 8);
            else if (Input.AltHeld) divisor = Divisors[Math.Clamp(Array.IndexOf(Divisors, divisor) + (wheel > 0 ? 1 : -1), 0, Divisors.Length - 1)];
            else Seek(StepTime(time, wheel > 0 ? -1 : 1));
        }

        // the top tabs, the bottom bar
        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            for (int i = 0; i < 3; i++) if (CheckCollisionPointRec(mouse, TabRect(i))) { tab = i; return; }
            if (UpdateBottomBar(mouse)) return;
        }
        else if (Input.IsMouseButtonDown(MouseButton.Left) && !double.IsNaN(scrubFrom)) { }

        switch (tab)
        {
            case 0: UpdateCompose(mouse, u); break;
            case 1: UpdateTiming(mouse, u); break;
            case 2: UpdateSetup(mouse, u); break;
        }
    }

    void PlayHitsounds()
    {
        if (!playing) { lastSoundTime = double.NaN; return; }
        if (!double.IsNaN(lastSoundTime))
            foreach (var o in objs)
            {
                if (o.Time > lastSoundTime && o.Time <= time) Hitsound(o.HitSound, o.Time);
                if (o.Slider || o.Spinner)
                    if (o.End > lastSoundTime && o.End <= time) Hitsound(o.HitSound, o.End);
            }
        lastSoundTime = time;
    }

    void Hitsound(int bits, double at)
    {
        var tp = info.PointAt(at + 5);
        int set = tp.SampleSet > 0 ? tp.SampleSet : 1, vol = Math.Max(8, tp.Volume);
        samples.Play(set, "hitnormal", tp.SampleIndex, (int)(vol * 0.8));
        if ((bits & 2) != 0) samples.Play(set, "hitwhistle", tp.SampleIndex, (int)(vol * 0.85));
        if ((bits & 4) != 0) samples.Play(set, "hitfinish", tp.SampleIndex, vol);
        if ((bits & 8) != 0) samples.Play(set, "hitclap", tp.SampleIndex, (int)(vol * 0.85));
    }

    // ---- compose
    static Rectangle ToolRect(int i) { float u = U; return new Rectangle(4 * u, (90 + i * 34) * u, 62 * u, 30 * u); }
    Rectangle ToggleRect(int i) { float u = U; return new Rectangle(Game.W - 92 * u, (90 + i * 34) * u, 88 * u, 30 * u); }
    static readonly string[] ToolNames = ["Select", "Circle", "Slider", "Spinner"];
    static readonly string[] ToggleNames = ["New combo", "Whistle", "Finish", "Clap", "Grid snap", "Distance snap", "Sampleset", "Additions"];
    static readonly string[] SetNames = ["Auto", "Normal", "Soft", "Drum"];
    bool distSnap;
    (int Normal, int Addition) setsNext; // for the next placed object (nothing selected)

    // an object's sample sets (its hitsample's first two fields; 0 = the timing point's)
    static (int, int) SetsOf(EObj o)
    {
        var p = o.Sample.Split(':');
        return (p.Length > 0 && int.TryParse(p[0], out int n) ? n : 0, p.Length > 1 && int.TryParse(p[1], out int a) ? a : 0);
    }

    static void SetSets(EObj o, int normal, int addition)
    {
        var p = (o.Sample.Length > 0 ? o.Sample : "0:0:0:0:").Split(':').ToList();
        while (p.Count < 5) p.Add(p.Count == 4 ? "" : "0");
        p[0] = normal.ToString(); p[1] = addition.ToString();
        o.Sample = string.Join(':', p);
        o.Raw = null;
    }

    void CycleSet(bool addition)
    {
        if (selected.Count == 0) { setsNext = addition ? (setsNext.Normal, (setsNext.Addition + 1) % 4) : ((setsNext.Normal + 1) % 4, setsNext.Addition); return; }
        var (n0, a0) = SetsOf(selected.First());
        int next = ((addition ? a0 : n0) + 1) % 4;
        Change(() => { foreach (var o in selected) { var (n, a) = SetsOf(o); SetSets(o, addition ? n : next, addition ? next : a); } });
    }

    // stable's distance snap: a new object goes the beat-snapped time's worth of slider travel (times the map's
    // distance spacing) from where the previous one ends, in the cursor's direction
    double DistanceSpacing => double.TryParse(Kv("Editor", "DistanceSpacing"), NumberStyles.Float, Inv, out double d) && d > 0 ? d : 1;
    (Vector2 From, float Radius)? SnapRing(double t)
    {
        var prev = objs.LastOrDefault(o => o.End < t - 1 && !o.Spinner);
        if (prev == null) return null;
        var from = prev.Slider && prev.Path.Length > 0 ? (prev.Slides % 2 == 1 ? prev.Path[^1] : prev.Path[0]) : new Vector2((float)prev.X, (float)prev.Y);
        double perMs = 100 * SliderMultiplier * SvAt(t) / RedAt(t).Beat;
        return (from, (float)(DistanceSpacing * perMs * (t - prev.End)));
    }

    Vector2 PlaceAt(Vector2 fieldPos, double t)
    {
        var p = GridSnap(fieldPos);
        if (distSnap && SnapRing(t) is { } ring && ring.Radius > 0)
        {
            var dir = fieldPos - ring.From;
            if (dir.LengthSquared() < 1e-6f) dir = new Vector2(1, 0);
            p = ring.From + Vector2.Normalize(dir) * ring.Radius;
            p = new Vector2(MathF.Round(p.X), MathF.Round(p.Y));
        }
        return p;
    }

    void UpdateCompose(Vector2 mouse, float u)
    {
        string[] toolKeys = ["keySelectTool", "keyNormalTool", "keySliderTool", "keySpinnerTool"];
        for (int k = 0; k < 4; k++) if (Input.IsKeyPressed(Bound(toolKeys[k], KeyboardKey.One + k)) && !Input.CtrlHeld) { tool = k; sliderDraft = null; }
        if (Input.IsKeyPressed(Bound("keyNewComboToggle", KeyboardKey.Q))) ToggleCombo();
        if (Input.IsKeyPressed(Bound("keyWhistleToggle", KeyboardKey.W))) ToggleSound(2);
        if (Input.IsKeyPressed(Bound("keyFinishToggle", KeyboardKey.E))) ToggleSound(4);
        if (Input.IsKeyPressed(Bound("keyClapToggle", KeyboardKey.R)) && !Input.CtrlHeld) ToggleSound(8);
        if (Input.IsKeyPressed(Bound("keyGridSnapToggle", KeyboardKey.T))) gridSnap = !gridSnap;
        if (Input.IsKeyPressed(Bound("keyDistSnapToggle", KeyboardKey.Y))) distSnap = !distSnap;
        if (Input.IsKeyPressed(Bound("keyGridChange", KeyboardKey.G))) gridSize = gridSize == 32 ? 4 : gridSize * 2;
        // Nudge Left / Right (J / K): the selection a beat snap earlier / later
        int nudge = Input.IsKeyPressed(Bound("keyNudgeLeft", KeyboardKey.J)) ? -1 : Input.IsKeyPressed(Bound("keyNudgeRight", KeyboardKey.K)) ? 1 : 0;
        if (nudge != 0 && selected.Count > 0)
            Change(() =>
            {
                double step = RedAt(selected.Min(o => o.Time)).Beat / divisor;
                foreach (var o in selected) { o.Time = Math.Round(o.Time + nudge * step); o.End = Math.Round(o.End + nudge * step); o.Raw = null; }
            });
        if (Input.IsKeyPressed(KeyboardKey.Delete) && selected.Count > 0) Change(() => { objs.RemoveAll(selected.Contains); selected.Clear(); });
        if (Input.CtrlHeld && Input.IsKeyPressed(KeyboardKey.A)) { selected.Clear(); selected.UnionWith(objs); tool = 0; }
        if (Input.CtrlHeld && (Input.IsKeyPressed(KeyboardKey.C) || Input.IsKeyPressed(KeyboardKey.X)) && selected.Count > 0)
        {
            clipboard = selected.OrderBy(o => o.Time).Select(o => o.Clone()).ToList();
            if (Input.IsKeyPressed(KeyboardKey.X)) Change(() => { objs.RemoveAll(selected.Contains); selected.Clear(); });
        }
        if (Input.CtrlHeld && Input.IsKeyPressed(KeyboardKey.V) && clipboard.Count > 0)
            Change(() =>
            {
                // pasted at the current (snapped) time, keeping their spacing
                double shift = Snap(time) - clipboard[0].Time;
                selected.Clear();
                foreach (var c in clipboard)
                {
                    var o = c.Clone();
                    o.Raw = null; o.Time += shift; o.End += shift;
                    objs.Add(o);
                    selected.Add(o);
                }
            });

        bool click = Input.IsMouseButtonPressed(MouseButton.Left), held = Input.IsMouseButtonDown(MouseButton.Left);
        if (click)
        {
            for (int i = 0; i < 4; i++) if (CheckCollisionPointRec(mouse, ToolRect(i))) { tool = i; sliderDraft = null; return; }
            for (int i = 0; i < ToggleNames.Length; i++)
                if (CheckCollisionPointRec(mouse, ToggleRect(i)))
                {
                    if (i == 0) ToggleCombo();
                    else if (i < 4) ToggleSound(1 << i);
                    else if (i == 4) gridSnap = !gridSnap;
                    else if (i == 5) distSnap = !distSnap;
                    else CycleSet(addition: i == 7);
                    return;
                }
        }

        // the timeline: objects drag in time (snapped), empty space scrubs
        var tl = TimelineRect();
        if (click && CheckCollisionPointRec(mouse, tl))
        {
            // a spinner's end: dragged to a new (snapped) end
            if (objs.LastOrDefault(o => o.Spinner && Math.Abs(TimelineX(o.End) - mouse.X) < 6 * u) is { } sp)
            {
                undo.Add(string.Join("\n", Write())); redo.Clear();
                endDrag = sp;
                return;
            }
            var hit = objs.LastOrDefault(o => Math.Abs(TimelineX(o.Time) - mouse.X) < 10 * u);
            if (hit != null)
            {
                if (!selected.Contains(hit)) { if (!Input.CtrlHeld) selected.Clear(); selected.Add(hit); }
                undo.Add(string.Join("\n", Write())); redo.Clear();
                timeDrag = (TimeAtX(mouse.X), selected.ToDictionary(o => o, o => o.Time));
            }
            else { scrubFrom = mouse.X; scrubTime = time; }
            return;
        }
        if (endDrag is { } ed)
        {
            if (held)
            {
                double end = Math.Max(ed.Time + RedAt(ed.Time).Beat / divisor, Snap(TimeAtX(mouse.X)));
                if (end != ed.End) { ed.End = end; ed.Raw = null; modified = true; }
            }
            else { endDrag = null; Rebuild(); }
            return;
        }
        if (timeDrag is { } td)
        {
            if (held)
            {
                double shift = Snap(td.Start.Keys.Min(o => td.Start[o]) + TimeAtX(mouse.X) - td.From) - td.Start.Keys.Min(o => td.Start[o]);
                foreach (var (o, t0) in td.Start)
                {
                    double d = o.End - o.Time;
                    if (Math.Abs(o.Time - (t0 + shift)) > 0.5) { o.Time = t0 + shift; o.End = o.Time + d; o.Raw = null; modified = true; }
                }
            }
            else { timeDrag = null; Rebuild(); }
            return;
        }
        if (!double.IsNaN(scrubFrom))
        {
            if (held) Seek(scrubTime - (mouse.X - scrubFrom) / PxPerMs());
            else scrubFrom = double.NaN;
            return;
        }

        // the playfield
        var fieldPos = ToField(mouse);
        bool inField = fieldPos.X >= -20 && fieldPos.X <= 532 && fieldPos.Y >= -20 && fieldPos.Y <= 404 && mouse.Y > tl.Y + tl.Height;
        // stable: a right click deletes the object under the cursor (not while drawing a slider)
        if (Input.IsMouseButtonPressed(MouseButton.Right) && sliderDraft == null && inField
            && Visible().Reverse().FirstOrDefault(o => !o.Spinner && Vector2.Distance(new((float)o.X, (float)o.Y), fieldPos) <= Radius) is { } gone)
        {
            Change(() => { objs.Remove(gone); selected.Remove(gone); });
            return;
        }
        switch (tool)
        {
            case 0: UpdateSelect(mouse, fieldPos, click, held, inField); break;
            case 1:
                if (click && inField)
                    Change(() =>
                    {
                        var p = PlaceAt(fieldPos, Snap(time));
                        var o = new EObj { X = p.X, Y = p.Y, Time = Snap(time), Type = 1 | (newComboNext ? 4 : 0), HitSound = soundNext };
                        o.End = o.Time;
                        if (setsNext != (0, 0)) SetSets(o, setsNext.Normal, setsNext.Addition);
                        objs.RemoveAll(x => Math.Abs(x.Time - o.Time) < 1 && !x.Spinner);
                        objs.Add(o);
                        selected.Clear();
                        newComboNext = false;
                    });
                break;
            case 2:
                // the slider: a click for the head, one for each point; a right click (or a click on the last point) ends it
                if (click && inField)
                {
                    var p = sliderDraft == null ? PlaceAt(fieldPos, Snap(time)) : GridSnap(fieldPos);
                    if (sliderDraft == null) { sliderDraft = [p]; sliderDraftTime = Snap(time); }
                    else if (Vector2.Distance(p, sliderDraft[^1]) < 4) FinishSlider();
                    else sliderDraft.Add(p);
                }
                if (Input.IsMouseButtonPressed(MouseButton.Right) && sliderDraft != null) FinishSlider();
                break;
            case 3:
                if (click && inField)
                    Change(() =>
                    {
                        // a spinner of a bar (4 beats) from here; drag its end on the timeline to change it
                        double t = Snap(time), beat = RedAt(t).Beat;
                        objs.Add(new EObj { X = 256, Y = 192, Time = t, End = t + beat * 4, Type = 8 | 4, HitSound = soundNext, Sample = "0:0:0:0:" });
                    });
                break;
        }
    }

    void FinishSlider()
    {
        var pts = sliderDraft!;
        sliderDraft = null;
        if (pts.Count < 2) return;
        Change(() =>
        {
            var o = new EObj { X = pts[0].X, Y = pts[0].Y, Time = sliderDraftTime, Type = 2 | (newComboNext ? 4 : 0), HitSound = soundNext, Curve = pts.Count == 2 ? 'L' : pts.Count == 3 ? 'P' : 'B', Anchors = pts.Skip(1).ToList() };
            // stable snaps the length down to the beat snap's ticks (at least one)
            double drawn = 0;
            var built = SliderPath.Build(o.Curve, pts, ref drawn, version);
            for (int i = 1; i < built.Length; i++) drawn += Vector2.Distance(built[i - 1], built[i]);
            double perTick = 100 * SliderMultiplier * SvAt(o.Time) / divisor;
            o.Length = Math.Max(1, Math.Floor(drawn / perTick)) * perTick;
            objs.Add(o);
            newComboNext = false;
            selected.Clear();
        });
    }

    void UpdateSelect(Vector2 mouse, Vector2 fieldPos, bool click, bool held, bool inField)
    {
        if (click && inField)
        {
            // the topmost visible object under the cursor (its head)
            var hit = Visible().Reverse().FirstOrDefault(o => !o.Spinner && Vector2.Distance(new((float)o.X, (float)o.Y), fieldPos) <= Radius);
            if (hit != null)
            {
                if (!selected.Contains(hit)) { if (!Input.CtrlHeld) selected.Clear(); selected.Add(hit); }
                else if (Input.CtrlHeld) { selected.Remove(hit); return; }
                undo.Add(string.Join("\n", Write())); redo.Clear();
                drag = (fieldPos, selected.ToDictionary(o => o, o => (o.X, o.Y)));
            }
            else { if (!Input.CtrlHeld) selected.Clear(); boxFrom = fieldPos; }
            return;
        }
        if (drag is { } d)
        {
            if (held)
            {
                var delta = fieldPos - d.From;
                var anchor = d.Start.First();
                var snapped = GridSnap(new Vector2((float)anchor.Value.X, (float)anchor.Value.Y) + delta) - new Vector2((float)anchor.Value.X, (float)anchor.Value.Y);
                foreach (var (o, p0) in d.Start)
                {
                    double nx = p0.X + snapped.X, ny = p0.Y + snapped.Y;
                    if (nx == o.X && ny == o.Y) continue;
                    var move = new Vector2((float)(nx - o.X), (float)(ny - o.Y));
                    for (int i = 0; i < o.Anchors.Count; i++) o.Anchors[i] += move;
                    o.X = nx; o.Y = ny; o.Raw = null; modified = true;
                }
                if (modified) Rebuild();
            }
            else drag = null;
            return;
        }
        if (boxFrom is { } bf)
        {
            if (!held)
            {
                var r = new Rectangle(Math.Min(bf.X, fieldPos.X), Math.Min(bf.Y, fieldPos.Y), Math.Abs(fieldPos.X - bf.X), Math.Abs(fieldPos.Y - bf.Y));
                foreach (var o in Visible()) if (CheckCollisionPointRec(new((float)o.X, (float)o.Y), r)) selected.Add(o);
                boxFrom = null;
            }
        }
    }

    void ToggleCombo()
    {
        if (selected.Count == 0) { newComboNext = !newComboNext; return; }
        bool on = !selected.All(o => o.NewCombo);
        Change(() => { foreach (var o in selected) { o.NewCombo = on; o.Raw = null; } });
    }

    void ToggleSound(int bit)
    {
        if (selected.Count == 0) { soundNext ^= bit; return; }
        bool on = !selected.All(o => (o.HitSound & bit) != 0);
        Change(() => { foreach (var o in selected) { o.HitSound = on ? o.HitSound | bit : o.HitSound & ~bit; o.Raw = null; } });
    }

    IEnumerable<EObj> Visible()
    {
        double pre = Preempt;
        return objs.Where(o => o.Time - pre <= time && time <= o.End + 800);
    }

    // ---- timing
    Rectangle TimingRow(int i) { float u = U; return new Rectangle(80 * u, (100 + i * 18) * u, Game.W - 300 * u, 17 * u); }
    static readonly string[] TimingButtons = ["Add timing point", "Add inherited point", "Delete", "Move to current time", "Kiai toggle", "Omit first bar line"];
    Rectangle TimingButton(int i) { float u = U; return new Rectangle(Game.W - 210 * u, (100 + i * 26) * u, 200 * u, 22 * u); }
    static readonly string[] Steppers = ["Offset", "BPM", "Slider velocity", "Meter", "Volume", "Sample set"];
    Rectangle StepRect(int i, int dir) { float u = U; return new Rectangle(Game.W - (dir < 0 ? 210 : 40) * u, (270 + i * 26) * u, 30 * u, 22 * u); }
    int timingScroll;

    void UpdateTiming(Vector2 mouse, float u)
    {
        var list = timing.OrderBy(t => t.Time).ToList();
        if (Input.GetMouseWheelMove() != 0 && mouse.X < Game.W - 220 * u) { }
        bool click = Input.IsMouseButtonPressed(MouseButton.Left);
        if (!click) return;
        for (int i = 0; i < 18 && i + timingScroll < list.Count; i++)
            if (CheckCollisionPointRec(mouse, TimingRow(i))) { timingSel = timing.IndexOf(list[i + timingScroll]); Seek(list[i + timingScroll].Time); return; }
        if (mouse.Y < 100 * u && mouse.X > 80 * u && mouse.X < Game.W - 220 * u) { timingScroll = Math.Max(0, timingScroll - 5); return; }
        if (mouse.Y > (100 + 18 * 18) * u && mouse.X < Game.W - 220 * u) { timingScroll = Math.Min(Math.Max(0, list.Count - 18), timingScroll + 5); return; }
        for (int i = 0; i < TimingButtons.Length; i++)
        {
            if (!CheckCollisionPointRec(mouse, TimingButton(i))) continue;
            var sel = timingSel >= 0 && timingSel < timing.Count ? timing[timingSel] : null;
            switch (i)
            {
                case 0: Change(() => { var red = RedAt(time); timing.Add(new ETiming { Time = Math.Round(time), Beat = red.Beat, Meter = red.Meter, Set = red.Set, Volume = red.Volume }); timingSel = timing.Count - 1; }); break;
                case 1: Change(() => { var red = RedAt(time); timing.Add(new ETiming { Time = Snap(time), Beat = -100, Red = false, Set = red.Set, Volume = red.Volume }); timingSel = timing.Count - 1; }); break;
                case 2: if (sel != null) Change(() => { timing.Remove(sel); timingSel = -1; }); break;
                case 3: if (sel != null) Change(() => sel.Time = Math.Round(time)); break;
                case 4: if (sel != null) Change(() => sel.Effects ^= 1); break;
                case 5: if (sel != null) Change(() => sel.Effects ^= 8); break;
            }
            return;
        }
        if (timingSel < 0 || timingSel >= timing.Count) return;
        var t = timing[timingSel];
        for (int i = 0; i < Steppers.Length; i++)
            for (int dir = -1; dir <= 1; dir += 2)
            {
                if (!CheckCollisionPointRec(mouse, StepRect(i, dir))) continue;
                double big = Input.ShiftHeld ? 10 : 1;
                Change(() =>
                {
                    switch (i)
                    {
                        case 0: t.Time += dir * big; break;
                        case 1: if (t.Red) t.Beat = 60000 / Math.Max(1, t.Bpm + dir * (Input.ShiftHeld ? 0.1 : 1)); break;
                        case 2: if (!t.Red) t.Beat = -100 / Math.Clamp(Math.Round((t.Sv + dir * (Input.ShiftHeld ? 0.01 : 0.05)) * 100) / 100, 0.1, 10); break;
                        case 3: if (t.Red) t.Meter = Math.Clamp(t.Meter + dir, 1, 16); break;
                        case 4: t.Volume = Math.Clamp(t.Volume + dir * 5, 5, 100); break;
                        case 5: t.Set = (t.Set + dir + 4) % 4; break;
                    }
                });
                return;
            }
    }

    // ---- song setup
    static readonly (string Section, string Key, string Label)[] Fields =
    [
        ("Metadata", "Artist", "Artist"), ("Metadata", "ArtistUnicode", "Romanised artist (unicode)"), ("Metadata", "Title", "Title"),
        ("Metadata", "TitleUnicode", "Title (unicode)"), ("Metadata", "Creator", "Creator"), ("Metadata", "Version", "Difficulty"),
        ("Metadata", "Source", "Source"), ("Metadata", "Tags", "Tags"),
    ];
    static readonly (string Key, string Label, double Min, double Max, double Step)[] Sliders =
    [
        ("HPDrainRate", "HP Drain Rate", 0, 10, 0.1), ("CircleSize", "Circle Size", 0, 10, 0.1), ("ApproachRate", "Approach Rate", 0, 10, 0.1),
        ("OverallDifficulty", "Overall Difficulty", 0, 10, 0.1), ("SliderMultiplier", "Slider Velocity", 0.4, 3.6, 0.01), ("SliderTickRate", "Slider Tick Rate", 0.5, 8, 0.5),
    ];
    string fieldText = "";

    Rectangle FieldRect(int i) { float u = U; return new Rectangle(220 * u, (96 + i * 26) * u, Game.W - 440 * u, 22 * u); }
    Rectangle SliderRect(int i) { float u = U; return new Rectangle(220 * u, (320 + i * 22) * u, Game.W - 440 * u, 18 * u); }
    int sliderDragging = -1;

    void UpdateSetup(Vector2 mouse, float u)
    {
        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            for (int i = 0; i < Fields.Length; i++)
                if (CheckCollisionPointRec(mouse, FieldRect(i))) { field = i; fieldText = Kv(Fields[i].Section, Fields[i].Key) ?? ""; return; }
            for (int i = 0; i < Sliders.Length; i++)
                if (CheckCollisionPointRec(mouse, SliderRect(i))) { sliderDragging = i; undo.Add(string.Join("\n", Write())); redo.Clear(); }
        }
        if (sliderDragging >= 0)
        {
            if (!Input.IsMouseButtonDown(MouseButton.Left)) { sliderDragging = -1; info = ReloadInfo(); Rebuild(); return; }
            var s = Sliders[sliderDragging];
            var r = SliderRect(sliderDragging);
            double f = Math.Clamp((mouse.X - r.X) / r.Width, 0, 1), v = s.Min + f * (s.Max - s.Min);
            v = Math.Round(v / s.Step) * s.Step;
            SetKv("Difficulty", s.Key, Num(Math.Round(v, 2)));
        }
    }

    void UpdateTextField()
    {
        for (int c; (c = Input.GetCharPressed()) != 0;) if (c >= 32) fieldText += char.ConvertFromUtf32(c);
        if ((Input.IsKeyPressed(KeyboardKey.Backspace) || Input.IsKeyPressedRepeat(KeyboardKey.Backspace)) && fieldText.Length > 0) fieldText = fieldText[..^1];
        bool done = Input.IsKeyPressed(KeyboardKey.Enter) || Input.IsKeyPressed(KeyboardKey.Tab) || Input.IsMouseButtonPressed(MouseButton.Left) && !CheckCollisionPointRec(Input.GetMousePosition(), FieldRect(field));
        if (Input.IsKeyPressed(KeyboardKey.Escape)) { field = -1; return; }
        if (!done) return;
        var f = Fields[field];
        string value = fieldText.Trim();
        if ((Kv(f.Section, f.Key) ?? "") != value) { undo.Add(string.Join("\n", Write())); redo.Clear(); SetKv(f.Section, f.Key, value); }
        field = Input.IsKeyPressed(KeyboardKey.Tab) && field + 1 < Fields.Length ? field + 1 : -1;
        if (field >= 0) fieldText = Kv(Fields[field].Section, Fields[field].Key) ?? "";
    }

    Beatmap ReloadInfo()
    {
        try
        {
            var tmp = Beatmap.Load(path, lines: Write());
            return tmp;
        }
        catch (Exception e) when (e is not OutOfMemoryException) { return info; }
    }

    // ---- test play: the map as it stands, from the current time; back here after it
    void TestPlay()
    {
        SetPlaying(false);
        Beatmap map;
        try { map = Beatmap.Load(path, lines: Write()); }
        catch (Exception e) when (e is not OutOfMemoryException) { Notification.Show($"Can't test: {e.Message}", 3000); return; }
        // stable tests from where the editor is (the objects before it left out)
        if (map.Objects.Any(o => o.Time >= time - 1)) map.TestFrom = map.Objects.RemoveAll(o => o.Time < time - 1) > 0;
        if (map.Objects.Count == 0) { Notification.Show("Place some objects first!", 2000); return; }
        map.PlayMode = map.Mode;
        Game.FadeTo(() => new Player(map, back, Mods.None) { ReturnTo = this });
    }

    public override void Entered()
    {
        // back from a test play: the music where it was
        music?.Seek(time);
    }

    // ---- exit prompt
    void UpdateExitPrompt(Vector2 mouse)
    {
        if (Input.IsKeyPressed(KeyboardKey.Escape)) { exitPrompt = null; return; }
        int pick = Input.IsKeyPressed(KeyboardKey.One) ? 0 : Input.IsKeyPressed(KeyboardKey.Two) ? 1 : Input.IsKeyPressed(KeyboardKey.Three) ? 2 : -1;
        if (Input.IsMouseButtonPressed(MouseButton.Left))
            for (int i = 0; i < 3; i++) if (CheckCollisionPointRec(mouse, Dialog.ButtonRect(PromptY + i * 50))) pick = i;
        if (pick == 0) { Save(); Leave(); }
        else if (pick == 1) Leave();
        else if (pick == 2) exitPrompt = null;
    }

    const float PromptY = 120;

    // ---- drawing
    Rectangle TabRect(int i) { float u = U; return new Rectangle(Game.W - (3 - i) * 92 * u, 0, 90 * u, 16 * u); }
    Rectangle TimelineRect() { float u = U; return new Rectangle(70 * u, 20 * u, Game.W - 150 * u, 44 * u); }
    float PxPerMs() => 50 * U * zoom / (float)RedAt(time).Beat;
    float TimelineX(double t) { var r = TimelineRect(); return r.X + r.Width / 2 + (float)((t - time) * PxPerMs()); }
    double TimeAtX(float x) { var r = TimelineRect(); return time + (x - r.X - r.Width / 2) / PxPerMs(); }

    public override void Draw()
    {
        int w = Game.W, h = Game.H;
        float u = U;
        ClearBackground(Color.Black);
        // the background, dimmed
        if (bg is null) LoadBg();
        if (bg is { } tex && tex.Id != 0)
        {
            float s = Math.Max(w / (float)tex.Width, h / (float)tex.Height);
            DrawTexturePro(tex, new Rectangle(0, 0, tex.Width, tex.Height), new Rectangle((w - tex.Width * s) / 2, (h - tex.Height * s) / 2, tex.Width * s, tex.Height * s), Vector2.Zero, 0, ColorAlpha(Color.White, 0.3f));
        }
        switch (tab)
        {
            case 0: DrawCompose(u); break;
            case 1: DrawTiming(u); break;
            case 2: DrawSetup(u); break;
        }
        DrawTimeline(u);
        DrawTopBar(u);
        DrawBottomBar(u);
        if (exitPrompt != null)
        {
            Dialog.Backdrop(1);
            Dialog.Title(exitPrompt, 1);
            var mouse = Input.GetMousePosition();
            double age = Game.NowMs - exitAt;
            Dialog.Button(0, "Yes", new Color(144, 200, 72, 255), PromptY, age, mouse);
            Dialog.Button(1, "No", new Color(240, 80, 80, 255), PromptY + 50, age, mouse);
            Dialog.Button(2, "Cancel", new Color(120, 120, 120, 255), PromptY + 100, age, mouse);
        }
        SongSelect.DrawCursor();
    }

    Texture2D? bg;
    void LoadBg()
    {
        var p = Path.Combine(info.Folder, info.Background);
        bg = info.Background.Length > 0 && File.Exists(p) ? LoadTexture(p) : new Texture2D();
        if (bg is { Id: not 0 } t) SetTextureFilter(t, TextureFilter.Bilinear);
    }

    void DrawTopBar(float u)
    {
        DrawRectangle(0, 0, Game.W, (int)(17 * u), new Color(0, 0, 0, 200));
        string name = $"{Kv("Metadata", "Artist")} - {Kv("Metadata", "Title")} [{Kv("Metadata", "Version")}]{(modified ? " *" : "")}";
        Game.Text(name, 4 * u, 1 * u, 12 * u, Color.White);
        string[] tabs = ["Compose", "Timing", "Song Setup"];
        for (int i = 0; i < 3; i++)
        {
            var r = TabRect(i);
            DrawRectangleRec(r, tab == i ? new Color(255, 160, 40, 255) : new Color(60, 60, 60, 255));
            float tw = Game.TextWidth(tabs[i], 12 * u);
            Game.Text(tabs[i], r.X + (r.Width - tw) / 2, r.Y + 1 * u, 12 * u, Color.White);
        }
    }

    void DrawTimeline(float u)
    {
        var r = TimelineRect();
        DrawRectangleRec(r, new Color(0, 0, 0, 180));
        BeginScissorMode((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
        // beat ticks: the bar's first beat white and tallest, then 1/2 red, 1/4 blue, 1/3 and 1/6 purple, 1/8 yellow, the rest grey
        double from = TimeAtX(r.X), to = TimeAtX(r.X + r.Width);
        var reds = timing.Where(p => p.Red && p.Beat > 0).OrderBy(p => p.Time).ToList();
        if (reds.Count == 0) reds.Add(new ETiming());
        for (int k = 0; k < reds.Count; k++)
        {
            var p = reds[k];
            double segEnd = k + 1 < reds.Count ? reds[k + 1].Time : to + p.Beat;
            double segStart = k == 0 ? Math.Min(p.Time, from) : p.Time;
            double step = p.Beat / divisor;
            if (step * PxPerMs() < 2) step *= Math.Ceiling(2 / (step * PxPerMs()));
            long n0 = (long)Math.Floor((Math.Max(segStart, from) - p.Time) / step), n1 = (long)Math.Ceiling((Math.Min(segEnd, to) - p.Time) / step);
            for (long n = n0; n <= n1; n++)
            {
                double t = p.Time + n * step;
                if (t < segStart || t >= segEnd) continue;
                long inBeat = ((n % divisor) + divisor) % divisor;
                long beatIndex = (long)Math.Floor(n / (double)divisor);
                Color c; float hgt;
                if (inBeat == 0) { c = Color.White; hgt = beatIndex % p.Meter == 0 ? 1 : 0.6f; }
                else
                {
                    int den = divisor / Gcd((int)inBeat, divisor);
                    (c, hgt) = den switch { 2 => (new Color(255, 70, 70, 255), 0.5f), 4 => (new Color(80, 130, 255, 255), 0.4f), 3 or 6 => (new Color(190, 90, 255, 255), 0.4f), 8 => (new Color(255, 220, 60, 255), 0.3f), _ => (Color.Gray, 0.25f) };
                }
                float x = TimelineX(t);
                DrawRectangle((int)x, (int)(r.Y + r.Height * (1 - hgt)), Math.Max(1, (int)u), (int)(r.Height * hgt), ColorAlpha(c, 0.8f));
            }
        }
        // the objects
        var colours = Colours;
        foreach (var o in objs)
        {
            float x0 = TimelineX(o.Time), x1 = TimelineX(o.End);
            if (x1 < r.X - 20 * u || x0 > r.X + r.Width + 20 * u) continue;
            float cy = r.Y + r.Height / 2, rad = 9 * u;
            var col = o.Spinner ? Color.Gray : colours[o.Colour % colours.Length];
            if (o.Slider || o.Spinner) DrawRectangleRounded(new Rectangle(x0 - rad, cy - rad, x1 - x0 + rad * 2, rad * 2), 1, 8, ColorAlpha(col, 0.7f));
            DrawCircleV(new(x0, cy), rad, col);
            DrawCircleLinesV(new(x0, cy), rad, selected.Contains(o) ? new Color(255, 200, 0, 255) : Color.White);
            if (selected.Contains(o)) DrawCircleLinesV(new(x0, cy), rad + 2 * u, new Color(255, 200, 0, 255));
            if (!o.Spinner) Game.Text(o.Combo.ToString(), x0 - Game.TextWidth(o.Combo.ToString(), 11 * u) / 2, cy - 6 * u, 11 * u, Color.White);
        }
        EndScissorMode();
        // the current time
        DrawRectangle((int)(r.X + r.Width / 2), (int)r.Y, Math.Max(2, (int)(2 * u)), (int)r.Height, new Color(255, 255, 255, 230));
        // the beat snap divisor
        string snap = $"1/{divisor}";
        Game.TextBold("Beat snap", r.X + r.Width + 6 * u, r.Y, 11 * u, Color.White);
        Game.TextBold(snap, r.X + r.Width + 6 * u, r.Y + 14 * u, 20 * u, Color.White);
        Game.Text("Alt+wheel", r.X + r.Width + 6 * u, r.Y + 34 * u, 9 * u, Color.Gray);
    }

    static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    void DrawCompose(float u)
    {
        var o0 = FieldOrigin;
        float fs = FieldScale;
        DrawRectangleLinesEx(new Rectangle(o0.X, o0.Y, 512 * fs, 384 * fs), 1, ColorAlpha(Color.White, 0.3f));
        if (gridSnap)
            for (int gx = 0; gx <= 512; gx += gridSize) DrawLineV(ToScreen(gx, 0), ToScreen(gx, 384), ColorAlpha(Color.White, gx == 256 ? 0.25f : 0.08f));
        if (gridSnap)
            for (int gy = 0; gy <= 384; gy += gridSize) DrawLineV(ToScreen(0, gy), ToScreen(512, gy), ColorAlpha(Color.White, gy == 192 ? 0.25f : 0.08f));

        var colours = Colours;
        float radius = Radius, cs = radius / 64 * fs;
        double pre = Preempt;
        var vis = Visible().ToList();
        for (int k = vis.Count - 1; k >= 0; k--)
        {
            var o = vis[k];
            float alpha = time < o.Time - pre + 400 ? (float)((time - (o.Time - pre)) / 400) : time > o.End ? 1 - (float)((time - o.End) / 800) : 1;
            alpha = Math.Clamp(alpha, 0, 1);
            var col = colours[o.Colour % colours.Length];
            if (o.Spinner)
            {
                var c = ToScreen(256, 192);
                float p = (float)Math.Clamp((time - o.Time) / Math.Max(1, o.End - o.Time), 0, 1);
                DrawCircleLinesV(c, 180 * fs * (1 - p), ColorAlpha(Color.White, alpha));
                skin.Tex("spinner-circle")?.Draw(c, 0.5f * fs / U * Game.H / 768f, ColorAlpha(Color.White, alpha * 0.8f));
                continue;
            }
            if (o.Slider && o.Path.Length > 1)
            {
                // the body: a border-coloured tube with the combo colour inside
                float step = Math.Max(1, radius / 6);
                var border = ColorAlpha(Color.White, alpha);
                var inner = ColorAlpha(new Color(col.R / 3, col.G / 3, col.B / 3, 255), alpha);
                foreach (var pass in new[] { (radius, border), (radius * 0.86f, inner) })
                {
                    Vector2? last = null;
                    foreach (var pt in o.Path)
                    {
                        if (last is { } l && Vector2.Distance(l, pt) < step) continue;
                        DrawCircleV(ToScreen(pt.X, pt.Y), pass.Item1 * fs, pass.Item2);
                        last = pt;
                    }
                    var end = o.Path[^1];
                    DrawCircleV(ToScreen(end.X, end.Y), pass.Item1 * fs, pass.Item2);
                }
                var tail = o.Slides % 2 == 1 ? o.Path[^1] : o.Path[0];
                DrawHitCircle(tail, 0, col, alpha, cs, selected.Contains(o), number: false);
                if (o.Slides > 1) skin.Tex("reversearrow")?.Draw(ToScreen(o.Path[^1].X, o.Path[^1].Y), cs, ColorAlpha(Color.White, alpha));
                // the ball while it runs
                if (time >= o.Time && time <= o.End)
                {
                    double span = (o.End - o.Time) / o.Slides, into = (time - o.Time) / span;
                    int sp = Math.Min(o.Slides - 1, (int)into);
                    double f = into - sp; if (sp % 2 == 1) f = 1 - f;
                    var ball = PathAt(o.Path, f);
                    skin.Tex("sliderb0")?.Draw(ToScreen(ball.X, ball.Y), cs, ColorAlpha(col, alpha));
                }
            }
            DrawHitCircle(new((float)o.X, (float)o.Y), o.Combo, col, alpha, cs, selected.Contains(o), number: true);
            if (time < o.Time)
            {
                float a = (float)((o.Time - time) / pre);
                skin.Tex("approachcircle")?.Draw(ToScreen(o.X, o.Y), cs * (1 + 3 * a), ColorAlpha(col, alpha));
            }
        }
        // the slider being drawn
        if (sliderDraft != null)
        {
            var mouseField = GridSnap(ToField(Input.GetMousePosition()));
            var pts = sliderDraft.Append(mouseField).ToList();
            for (int i = 1; i < pts.Count; i++) DrawLineEx(ToScreen(pts[i - 1].X, pts[i - 1].Y), ToScreen(pts[i].X, pts[i].Y), 2 * u, new Color(255, 200, 0, 255));
            foreach (var p in pts) DrawCircleV(ToScreen(p.X, p.Y), 3 * u, Color.White);
        }
        if (boxFrom is { } bf)
        {
            var a = ToScreen(bf.X, bf.Y); var b = Input.GetMousePosition();
            DrawRectangleLinesEx(new Rectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y)), 1, Color.White);
        }

        // the toolbox and the toggles
        for (int i = 0; i < 4; i++) DrawButton(ToolRect(i), $"{i + 1}. {ToolNames[i]}", tool == i);
        bool anySel = selected.Count > 0;
        bool[] on =
        [
            anySel ? selected.All(o => o.NewCombo) : newComboNext,
            anySel ? selected.All(o => (o.HitSound & 2) != 0) : (soundNext & 2) != 0,
            anySel ? selected.All(o => (o.HitSound & 4) != 0) : (soundNext & 4) != 0,
            anySel ? selected.All(o => (o.HitSound & 8) != 0) : (soundNext & 8) != 0,
            gridSnap, distSnap, false, false,
        ];
        var (selN, selA) = anySel ? SetsOf(selected.First()) : setsNext;
        string[] keys = ["Q", "W", "E", "R", "T", "Y"];
        for (int i = 0; i < ToggleNames.Length; i++)
            DrawButton(ToggleRect(i), i < 6 ? $"{ToggleNames[i]} ({keys[i]})" : $"{ToggleNames[i]}: {SetNames[i == 6 ? selN : selA]}", on[i]);
        Game.Text($"Grid {gridSize} (G)", Game.W - 92 * u, (90 + ToggleNames.Length * 34) * u, 10 * u, Color.LightGray);
        // the distance snap's ring for an object placed now
        if (distSnap && tool is 1 or 2 && sliderDraft == null && SnapRing(Snap(time)) is { } ring && ring.Radius > 0)
            DrawCircleLinesV(ToScreen(ring.From.X, ring.From.Y), ring.Radius * FieldScale, ColorAlpha(Color.White, 0.5f));
        Game.Text($"{selected.Count} selected", 4 * u, 230 * u, 10 * u, Color.LightGray);
    }

    static Vector2 PathAt(Vector2[] path, double f)
    {
        float total = 0;
        for (int i = 1; i < path.Length; i++) total += Vector2.Distance(path[i - 1], path[i]);
        float want = (float)(f * total), acc = 0;
        for (int i = 1; i < path.Length; i++)
        {
            float d = Vector2.Distance(path[i - 1], path[i]);
            if (acc + d >= want && d > 0) return Vector2.Lerp(path[i - 1], path[i], (want - acc) / d);
            acc += d;
        }
        return path[^1];
    }

    void DrawHitCircle(Vector2 at, int combo, Color col, float alpha, float cs, bool sel, bool number)
    {
        var p = ToScreen(at.X, at.Y);
        if (skin.Tex("hitcircle") is { } body) body.Draw(p, cs, ColorAlpha(col, alpha));
        else DrawCircleV(p, Radius * FieldScale, ColorAlpha(col, alpha));
        skin.Tex("hitcircleoverlay")?.Draw(p, cs, ColorAlpha(Color.White, alpha));
        if (number)
        {
            // the combo number from the skin's default-0..9, overlapping like stable's (HitCircleOverlap)
            string s = combo.ToString();
            var digits = s.Select(ch => skin.Tex($"default-{ch}")).ToList();
            if (digits.All(d => d != null))
            {
                float scale = cs * 0.8f, overlap = skin.HitCircleOverlap * scale;
                float total = digits.Sum(d => d!.Value.Width * scale) - overlap * (digits.Count - 1), x = p.X - total / 2;
                foreach (var d in digits) { d!.Value.Draw(new(x + d.Value.Width * scale / 2, p.Y), scale, ColorAlpha(Color.White, alpha)); x += d.Value.Width * scale - overlap; }
            }
        }
        if (sel) (skin.Tex("hitcircleselect") is { } hs ? (Action)(() => hs.Draw(p, cs, ColorAlpha(Color.White, alpha))) : () => DrawCircleLinesV(p, Radius * FieldScale + 2, new Color(255, 200, 0, 255)))();
    }

    static void DrawButton(Rectangle r, string text, bool on)
    {
        float u = U;
        DrawRectangleRounded(r, 0.2f, 6, on ? new Color(255, 160, 40, 230) : new Color(40, 40, 40, 200));
        DrawRectangleRoundedLinesEx(r, 0.2f, 6, 1, ColorAlpha(Color.White, 0.4f));
        float size = 11 * u;
        Game.Text(text, r.X + 4 * u, r.Y + (r.Height - size) / 2 - 1 * u, size, Color.White);
    }

    void DrawTiming(float u)
    {
        var list = timing.OrderBy(t => t.Time).ToList();
        DrawRectangle((int)(76 * u), (int)(80 * u), (int)(Game.W - 290 * u), (int)(360 * u), new Color(0, 0, 0, 200));
        Game.TextBold("Time          BPM / SV      Meter   Set     Vol   Kiai", 82 * u, 84 * u, 11 * u, Color.LightGray);
        string[] sets = ["Auto", "Normal", "Soft", "Drum"];
        for (int i = 0; i < 18 && i + timingScroll < list.Count; i++)
        {
            var t = list[i + timingScroll];
            var r = TimingRow(i);
            bool sel = timingSel >= 0 && timingSel < timing.Count && timing[timingSel] == t;
            bool current = t == RedAt(time) || !t.Red && t.Time <= time && list.LastOrDefault(x => !x.Red && x.Time <= time) == t;
            DrawRectangleRec(r, sel ? new Color(255, 160, 40, 160) : t.Red ? new Color(120, 30, 30, 140) : new Color(30, 110, 30, 140));
            if (current) DrawRectangleLinesEx(r, 1, Color.White);
            string row = $"{FormatTime(t.Time),-13} {(t.Red ? $"{t.Bpm:0.##} BPM" : $"{t.Sv:0.##}x"),-13} {(t.Red ? t.Meter.ToString() : ""),-7} {sets[Math.Clamp(t.Set, 0, 3)],-7} {t.Volume,-5} {((t.Effects & 1) != 0 ? "yes" : "")}";
            Game.Text(row, r.X + 6 * u, r.Y + 2 * u, 11 * u, Color.White);
        }
        if (list.Count > 18) Game.Text($"{timingScroll + 1}-{Math.Min(list.Count, timingScroll + 18)} of {list.Count} (click above / below to scroll)", 82 * u, 428 * u, 10 * u, Color.LightGray);
        for (int i = 0; i < TimingButtons.Length; i++) DrawButton(TimingButton(i), TimingButtons[i], false);
        if (timingSel >= 0 && timingSel < timing.Count)
        {
            var t = timing[timingSel];
            string[] values = [FormatTime(t.Time), t.Red ? $"{t.Bpm:0.###}" : "-", t.Red ? "-" : $"{t.Sv:0.##}x", t.Red ? t.Meter.ToString() : "-", $"{t.Volume}%", sets[Math.Clamp(t.Set, 0, 3)]];
            for (int i = 0; i < Steppers.Length; i++)
            {
                DrawButton(StepRect(i, -1), " -", false);
                DrawButton(StepRect(i, 1), " +", false);
                Game.Text($"{Steppers[i]}: {values[i]}", Game.W - 175 * u, (272 + i * 26) * u, 11 * u, Color.White);
            }
            Game.Text("Shift: bigger / finer steps", Game.W - 210 * u, 430 * u, 10 * u, Color.LightGray);
        }
    }

    void DrawSetup(float u)
    {
        DrawRectangle((int)(200 * u), (int)(80 * u), (int)(Game.W - 400 * u), (int)(360 * u), new Color(0, 0, 0, 200));
        for (int i = 0; i < Fields.Length; i++)
        {
            var r = FieldRect(i);
            Game.Text(Fields[i].Label, r.X - 4 * u - Game.TextWidth(Fields[i].Label, 11 * u) + 0, r.Y + 4 * u, 11 * u, Color.LightGray);
            DrawRectangleRec(r, field == i ? new Color(70, 70, 90, 255) : new Color(40, 40, 40, 255));
            string text = field == i ? fieldText + ((int)(Game.NowMs / 500) % 2 == 0 ? "|" : "") : Kv(Fields[i].Section, Fields[i].Key) ?? "";
            BeginScissorMode((int)r.X, (int)r.Y, (int)r.Width, (int)r.Height);
            float over = Math.Max(0, Game.TextWidth(text, 12 * u) - (r.Width - 8 * u)); // (typing past the end scrolls)
            Game.Text(text, r.X + 4 * u - (field == i ? over : 0), r.Y + 4 * u, 12 * u, Color.White);
            EndScissorMode();
        }
        for (int i = 0; i < Sliders.Length; i++)
        {
            var r = SliderRect(i);
            var s = Sliders[i];
            double v = double.TryParse(Kv("Difficulty", s.Key), NumberStyles.Float, Inv, out double d) ? d : s.Min;
            float f = (float)Math.Clamp((v - s.Min) / (s.Max - s.Min), 0, 1);
            Game.Text($"{s.Label}", r.X - 4 * u - Game.TextWidth(s.Label, 11 * u), r.Y + 2 * u, 11 * u, Color.LightGray);
            DrawRectangle((int)r.X, (int)(r.Y + r.Height / 2 - 1 * u), (int)r.Width, (int)(2 * u), Color.Gray);
            DrawCircleV(new(r.X + r.Width * f, r.Y + r.Height / 2), 6 * u, new Color(255, 160, 40, 255));
            Game.Text(v.ToString("0.##", Inv), r.X + r.Width + 6 * u, r.Y + 2 * u, 11 * u, Color.White);
        }
    }

    // ---- the bottom bar: the time, the song's progress (click to seek), play / pause / stop and the playback rate
    // (the right end kept clear for the frame counter)
    Rectangle ProgressRect() { float u = U; return new Rectangle(120 * u, Game.H - 22 * u, Game.W - 384 * u, 14 * u); }
    Rectangle TransportRect(int i) { float u = U; return new Rectangle(Game.W - 256 * u + i * 26 * u, Game.H - 24 * u, 24 * u, 18 * u); }
    Rectangle RateRect(int i) { float u = U; return new Rectangle(Game.W - 172 * u + i * 30 * u, Game.H - 24 * u, 28 * u, 18 * u); }
    static readonly double[] Rates = [0.25, 0.5, 0.75, 1];

    bool UpdateBottomBar(Vector2 mouse)
    {
        if (CheckCollisionPointRec(mouse, ProgressRect())) { var r = ProgressRect(); Seek((mouse.X - r.X) / r.Width * length); return true; }
        for (int i = 0; i < 3; i++)
            if (CheckCollisionPointRec(mouse, TransportRect(i)))
            {
                if (i == 0) SetPlaying(true); else if (i == 1) SetPlaying(!playing); else { SetPlaying(false); Seek(0); }
                return true;
            }
        for (int i = 0; i < 4; i++)
            if (CheckCollisionPointRec(mouse, RateRect(i)))
            {
                rate = Rates[i];
                music?.SetTempo(rate, false);
                return true;
            }
        return false;
    }

    static string FormatTime(double ms)
    {
        bool neg = ms < 0; ms = Math.Abs(ms);
        return $"{(neg ? "-" : "")}{(int)(ms / 60000):00}:{(int)(ms / 1000) % 60:00}:{(int)ms % 1000:000}";
    }

    void DrawBottomBar(float u)
    {
        DrawRectangle(0, (int)(Game.H - 28 * u), Game.W, (int)(28 * u), new Color(0, 0, 0, 210));
        Game.TextBold(FormatTime(time), 6 * u, Game.H - 24 * u, 16 * u, Color.White);
        var r = ProgressRect();
        DrawRectangle((int)r.X, (int)(r.Y + r.Height / 2 - 1 * u), (int)r.Width, (int)(2 * u), Color.Gray);
        // bookmarks of the timing: red and green ticks; kiai in orange
        foreach (var t in timing) DrawRectangle((int)(r.X + r.Width * t.Time / length), (int)r.Y, Math.Max(1, (int)u), (int)(r.Height / 2), t.Red ? Color.Red : Color.Green);
        float x = r.X + r.Width * (float)(time / length);
        DrawRectangle((int)x - (int)u, (int)r.Y, (int)(3 * u), (int)r.Height, Color.White);
        string[] t3 = ["Play", playing ? "Pause" : "Paused", "Stop"];
        for (int i = 0; i < 3; i++) DrawButton(TransportRect(i), t3[i][..1] == "P" && i == 1 ? "||" : i == 0 ? " >" : " []", i == 1 && !playing);
        for (int i = 0; i < 4; i++) DrawButton(RateRect(i), $"{Rates[i] * 100:0}%", rate == Rates[i]);
    }
}
