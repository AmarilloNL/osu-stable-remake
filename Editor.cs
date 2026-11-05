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
// Ctrl+A, Delete; the Edit menu's Ctrl+H / J (flip), Ctrl+< / > (rotate 90°), Ctrl+G (reverse), Ctrl+D (clone);
// Ctrl+Shift+R / S rotate / scale by; Ctrl+B / Ctrl+Shift+B add / remove a bookmark, Ctrl+Left / Right seek to them;
// Ctrl+Tab live mapping; a selected slider's points drag, a
// Ctrl+click on its body adds one and a right click on one removes it;
// the wheel steps through the beat snap (Ctrl zooms the timeline, Alt changes the divisor);
// Z / X / C / V / B and Space for playback. The .osu file is kept as written, only the edited sections rewritten.
// Like stable's, a taiko map's circles are red (blue with a whistle or clap) and its sliders yellow, a catch map's
// distance snap only measures across, and a mania map gets its own compose view (Select / Note / Hold).
sealed partial class Editor : Screen
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
        public string Edges = "", EdgeSets = "", Sample = ""; // kept as written
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

    string path; // (a new difficulty moves it)
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
    int tool = 1; // 0 select, 1 circle, 2 slider, 3 spinner (mania: 0 select, 1 note, 2 hold)
    int mode; // the map's (General: Mode)
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
    float FieldScale => fieldAsPlay ? U : 0.9f * U;
    Vector2 FieldOrigin => fieldAsPlay ? new((Game.W - 512 * U) / 2, (Game.H - 384 * U) / 2 + 8 * U) : new((Game.W - 512 * FieldScale) / 2, 78 * U);
    bool fieldAsPlay; // (Design's Hit Objects: the playfield where play has it, over the storyboard)
    Vector2 ToScreen(double x, double y) => FieldOrigin + new Vector2((float)x, (float)y) * FieldScale;
    Vector2 ToField(Vector2 s) => (s - FieldOrigin) / FieldScale;

    public Editor(string file, SongSelect back)
    {
        path = file;
        this.back = back;
        info = Beatmap.Load(file);
        samples = new BeatmapSamples(info.Folder);
        Read(File.ReadAllLines(file));
        LoadOsb();
        mode = int.TryParse(Kv("General", "Mode"), out int m) ? m : 0;
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
            // (lines are kept as written, trailing spaces too: only parsed trimmed)
            if (cur.Name == "TimingPoints" && line.Trim().Length > 0 && !line.StartsWith("//")) { if (ParseTiming(line) is { } t) { t.Raw = raw; timing.Add(t); } cur.Blanks = 0; continue; }
            if (cur.Name == "HitObjects" && line.Trim().Length > 0) { if (ParseObject(line) is { } o) { o.Raw = raw; objs.Add(o); } cur.Blanks = 0; continue; }
            if (line.Trim().Length == 0 && cur.Name is "TimingPoints" or "HitObjects") { cur.Blanks++; continue; }
            cur.Lines.Add(raw);
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
                Meter = f.Length > 2 && int.Parse(f[2], Inv) is > 0 and var mt ? mt : 4, Set = f.Length > 3 ? int.Parse(f[3], Inv) : 1,
                Index = f.Length > 4 ? int.Parse(f[4], Inv) : 0, Volume = f.Length > 5 ? int.Parse(f[5], Inv) : 100,
                Red = f.Length <= 6 || f[6].Trim() == "1", Effects = f.Length > 7 ? int.Parse(f[7], Inv) : 0, Raw = line,
            };
        }
        catch (Exception e) when (e is FormatException or OverflowException or IndexOutOfRangeException) { return null; } // (dropped, like stable)
    }

    static string Num(double v) => v.ToString("G15", Inv); // (stable writes doubles to 15 significant digits)

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
            else if ((o.Type & 128) != 0 && f.Length > 5) { int c = f[5].IndexOf(':'); o.End = double.Parse(c < 0 ? f[5] : f[5][..c], Inv); o.Sample = c < 0 ? "" : f[5][(c + 1)..]; }
            else o.Sample = f.Length > 5 ? f[5] : "";
            return o;
        }
        catch (Exception e) when (e is FormatException or OverflowException or IndexOutOfRangeException) { return null; } // (dropped, like stable)
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
        if ((o.Type & 128) != 0) return $"{head},{Num(Math.Round(o.End))}:{sample}";
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
            // the .osb, if the Design tab changed it (one undone back to nothing keeps just its [Events])
            if (osbModified && !osbUnreadable && (osbLines != null || osbPath != null && File.Exists(osbPath)))
                File.WriteAllLines(osbPath ??= NewOsbPath(), osbLines ?? ["[Events]"], new UTF8Encoding(false));
            osbModified = false;
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
        undo.Add(Snapshot());
        if (undo.Count > 100) undo.RemoveAt(0);
        redo.Clear();
        edit();
        modified = true;
        Rebuild();
    }

    void Restore(List<string> from, List<string> to)
    {
        if (from.Count == 0) return;
        to.Add(Snapshot());
        string state = from[^1];
        from.RemoveAt(from.Count - 1);
        int osb = state.IndexOf(OsbMark, StringComparison.Ordinal); // (which .osb, after the .osu's lines)
        RestoreOsb(osb < 0 ? "-1" : state[(osb + OsbMark.Length)..]);
        Read((osb < 0 ? state : state[..osb]).Split('\n'));
        mode = int.TryParse(Kv("General", "Mode"), out int m) ? m : 0; // (an undone mode or difficulty change)
        tool = Math.Min(tool, ToolNames.Length - 1);
        info = ReloadInfo();
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
        video?.Dispose();
        video = null;
        sbView?.Unload();
        samples.Unload();
        Game.FadeTo(back);
    }

    public override void Update(double dtMs)
    {
        var mouse = Input.GetMousePosition();
        float u = U;
        if (exitPrompt != null) { UpdateExitPrompt(mouse); return; }
        if (xform != null) { UpdateXform(mouse); return; }
        if (prompt != null) { UpdatePrompt(mouse); return; }
        if (poly != null) { UpdatePoly(mouse); return; }
        if (field < 0 && UpdateMenu(mouse)) return;
        // (the AiMod and Sample import windows: the editor below keeps its keys while they have the mouse)
        // (not during a drag begun in the editor: that keeps the mouse until it's let go)
        bool press = Input.IsMouseButtonPressed(MouseButton.Left);
        if (press) windowPress = false;
        bool editorDrag = Input.IsMouseButtonDown(MouseButton.Left) && !press && !windowPress;
        if (!editorDrag && (sampleImport != null && UpdateSampleImport(mouse) || aimod != null && UpdateAiMod(mouse)))
        {
            windowPress |= press;
            Input.MouseTaken = true;
            mouse = Input.GetMousePosition();
        }

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
            if (tab == 2 && setupColourPick >= 0) setupColourPick = -1;
            else if (tab == 3 && sbColourPick) sbColourPick = false;
            else if (tab == 3 && sbLibrary) sbLibrary = false;
            else if (tab == 3 && sbSel != null) sbSel = null;
            else if (sliderDraft != null) sliderDraft = null;
            else if (selected.Count > 0) selected.Clear();
            else AskExit();
            return;
        }
        if (Input.IsKeyPressed(KeyboardKey.F1)) tab = 0;
        if (Input.IsKeyPressed(KeyboardKey.F2)) tab = 3;
        if (Input.IsKeyPressed(KeyboardKey.F3)) tab = 1;
        if (Input.IsKeyPressed(KeyboardKey.F4)) tab = 2;
        if (Input.IsKeyPressed(KeyboardKey.F6)) tab = 1; // (stable's Timing Setup: this editor's Timing tab)
        if (Input.IsKeyPressed(KeyboardKey.F5)) { TestPlay(); return; }
        if (Input.CtrlHeld && !Input.ShiftHeld && Input.IsKeyPressed(KeyboardKey.S)) Save();
        if (Input.CtrlHeld && Input.IsKeyPressed(KeyboardKey.Z)) Restore(undo, redo);
        if (Input.CtrlHeld && Input.IsKeyPressed(KeyboardKey.Y)) Restore(redo, undo);
        if (Input.CtrlHeld && Input.ShiftHeld && Input.IsKeyPressed(KeyboardKey.A)) OpenAiMod();

        // stable's live mapping (Compose: Ctrl+Tab)
        if (Input.CtrlHeld && Input.IsKeyPressed(KeyboardKey.Tab)) ToggleLiveMapping();
        // the Timing menu's keys
        if (Input.CtrlHeld && Input.IsKeyPressed(KeyboardKey.P)) TimingAction(Input.ShiftHeld ? 1 : 0);
        if (Input.CtrlHeld && !Input.ShiftHeld && Input.IsKeyPressed(KeyboardKey.I)) DeleteTimingHere();
        if (Input.CtrlHeld && Input.ShiftHeld && Input.IsKeyPressed(KeyboardKey.I)) OpenSampleImport();
        live = Input.CtrlHeld ? [] : LiveMap(mouse);

        // playback (stable's bindings): Jump To First Note (Z), Play From Beginning (X), Audio Pause (C), Jump To End
        // (V); Space plays / pauses (the live mapping's keys come first)
        if (!Input.CtrlHeld)
        {
            if (Pressed(Bound("keyJumpToBegin", KeyboardKey.Z))) Seek(objs.Count > 0 ? objs[0].Time : 0);
            if (Pressed(Bound("keyPlayFromBegin", KeyboardKey.X))) { Seek(0); SetPlaying(true); }
            if (Pressed(Bound("keyAudioPause", KeyboardKey.C))) SetPlaying(!playing);
            if (Pressed(Bound("keyJumpToEnd", KeyboardKey.V))) Seek(objs.Count > 0 ? objs[^1].End : length);
        }
        if (Pressed(KeyboardKey.Space)) SetPlaying(!playing);
        bool leftKey = Input.IsKeyPressedRepeat(KeyboardKey.Left) || Input.IsKeyPressed(KeyboardKey.Left);
        bool rightKey = Input.IsKeyPressedRepeat(KeyboardKey.Right) || Input.IsKeyPressed(KeyboardKey.Right);
        if (Input.CtrlHeld)
        {
            // the bookmarks: Ctrl+Left / Right seek to the previous / next one
            var marks = Bookmarks();
            if (leftKey && marks.LastOrDefault(b => b < time - 1, -1) is var prev and >= 0) Seek(prev);
            if (rightKey && marks.FirstOrDefault(b => b > time + 1, -1) is var next and >= 0) Seek(next);
            if (Input.IsKeyPressed(KeyboardKey.B))
            {
                // Ctrl+B adds one here, Ctrl+Shift+B removes the one here (within half a beat snap)
                double near = RedAt(time).Beat / divisor / 2;
                List<int> to = Input.ShiftHeld ? [.. marks.Where(b => Math.Abs(b - time) > near)] : [.. marks.Union([(int)Math.Round(time)]).Order()];
                if (!to.SequenceEqual(marks)) Change(() => SetBookmarks(to)); // (undoable)
            }
        }
        else
        {
            if (leftKey) Seek(StepTime(time, -1));
            if (rightKey) Seek(StepTime(time, 1));
        }
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
            for (int i = 0; i < 4; i++) if (CheckCollisionPointRec(mouse, TabRect(i))) { tab = TabOrder[i]; return; }
            if (UpdateBottomBar(mouse)) return;
        }
        else if (Input.IsMouseButtonDown(MouseButton.Left) && !double.IsNaN(scrubFrom)) { }

        switch (tab)
        {
            case 0: UpdateCompose(mouse, u); break;
            case 1: Metronome(); UpdateTiming(mouse, u); break;
            case 2: UpdateSetup(mouse, u); break;
            case 3: UpdateDesign(mouse, u); break;
        }
    }

    // Live mapping, while the song plays in Compose: the taiko keys put a circle at the cursor (in the playfield) on
    // the beat snap (the rims with a clap); in a mania map the column keys put a note in their column, a hold when let
    // go over 300ms later. Gives the keys it took.
    bool liveMapping;
    KeyboardKey[] live = []; // this frame's live mapping keys (kept from the editor's own keys)
    bool Pressed(KeyboardKey k) => Input.IsKeyPressed(k) && !live.Contains(k);
    readonly EObj?[] liveHeld = new EObj?[18];

    void ToggleLiveMapping()
    {
        liveMapping = !liveMapping;
        if (liveMapping) tool = 1;
        Notification.Show(liveMapping ? "Live mapping enabled! Use your taiko/mania keybinds to map while the music is playing!" : "Live mapping disabled.", 3000);
    }

    KeyboardKey[] LiveMap(Vector2 mouse)
    {
        if (!liveMapping || !playing || tab != 0) return [];
        if (mode == 3)
        {
            int keys = MKeys;
            var bind = Player.ManiaKeys(keys);
            for (int c = 0; c < keys && c < liveHeld.Length; c++)
            {
                if (Input.IsKeyPressed(bind[c]))
                {
                    int col = c;
                    var n = new EObj { X = MColumnX(col, keys), Y = 192, Time = Snap(time), Type = 1, HitSound = soundNext };
                    n.End = n.Time;
                    if (objs.Any(o => MColumn(o, keys) == col && Math.Abs(o.Time - n.Time) < 1)) continue; // (one there already: kept)
                    Change(() => { objs.Add(n); selected.Clear(); });
                    liveHeld[c] = n;
                }
                else if (!Input.IsKeyDown(bind[c]) && liveHeld[c] is { } held)
                {
                    double end = Snap(time);
                    if (end - held.Time > 300) { held.Type = (held.Type & ~1) | 128; held.End = end; held.Raw = null; modified = true; Rebuild(); }
                    liveHeld[c] = null;
                }
            }
            return bind;
        }
        var field = ToField(mouse);
        if (field.X < 0 || field.X > 512 || field.Y < 0 || field.Y > 384) return [];
        KeyboardKey[] drum = [Config.Key("keyTaikoInnerLeft", KeyboardKey.X), Config.Key("keyTaikoInnerRight", KeyboardKey.C),
            Config.Key("keyTaikoOuterLeft", KeyboardKey.Z), Config.Key("keyTaikoOuterRight", KeyboardKey.V)];
        for (int i = 0; i < drum.Length; i++)
            if (Input.IsKeyPressed(drum[i]))
            {
                bool rim = i >= 2;
                Change(() =>
                {
                    var p = GridSnap(field);
                    var o = new EObj { X = p.X, Y = p.Y, Time = Snap(time), Type = 1, HitSound = rim ? 8 : 0 };
                    o.End = o.Time;
                    objs.Add(o);
                    selected.Clear();
                });
            }
        return drum;
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
    string[] ToolNames => mode == 3 ? ["Select", "Note", "Hold"] : ["Select", "Circle", "Slider", "Spinner"];
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
        if (prev == null || mode is 1 or 3) return null;
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
            p = mode == 2 ? new(ring.From.X + (dir.X < 0 ? -1 : 1) * ring.Radius, p.Y) : ring.From + Vector2.Normalize(dir) * ring.Radius;
            p = new Vector2(MathF.Round(p.X), MathF.Round(p.Y));
        }
        return p;
    }

    void UpdateCompose(Vector2 mouse, float u)
    {
        string[] toolKeys = ["keySelectTool", "keyNormalTool", "keySliderTool", "keySpinnerTool"];
        for (int k = 0; k < ToolNames.Length; k++) if (Pressed(Bound(toolKeys[k], KeyboardKey.One + k)) && !Input.CtrlHeld) { tool = k; sliderDraft = null; }
        if (Pressed(Bound("keyNewComboToggle", KeyboardKey.Q))) ToggleCombo();
        if (Pressed(Bound("keyWhistleToggle", KeyboardKey.W))) ToggleSound(2);
        if (Pressed(Bound("keyFinishToggle", KeyboardKey.E))) ToggleSound(4);
        if (Pressed(Bound("keyClapToggle", KeyboardKey.R)) && !Input.CtrlHeld) ToggleSound(8);
        if (Pressed(Bound("keyGridSnapToggle", KeyboardKey.T))) gridSnap = !gridSnap;
        if (Pressed(Bound("keyDistSnapToggle", KeyboardKey.Y)) && !Input.CtrlHeld) distSnap = !distSnap;
        if (Pressed(Bound("keyGridChange", KeyboardKey.G)) && !Input.CtrlHeld) gridSize = gridSize == 32 ? 4 : gridSize * 2;
        // Nudge Left / Right (J / K): the selection a beat snap earlier / later
        int nudge = Input.CtrlHeld ? 0 : Pressed(Bound("keyNudgeLeft", KeyboardKey.J)) ? -1 : Pressed(Bound("keyNudgeRight", KeyboardKey.K)) ? 1 : 0;
        if (nudge != 0 && selected.Count > 0) Nudge(nudge);
        if (Input.IsKeyPressed(KeyboardKey.Delete) && selected.Count > 0) DeleteSel();
        // stable's Edit menu keys (see the menu below)
        if (Input.CtrlHeld && selected.Count > 0)
        {
            if (Input.IsKeyPressed(KeyboardKey.H)) FlipH();
            if (Input.IsKeyPressed(KeyboardKey.J)) FlipV();
            if (mode != 3 && Input.IsKeyPressed(KeyboardKey.Period)) Rotate90(clockwise: true);
            if (mode != 3 && Input.IsKeyPressed(KeyboardKey.Comma)) Rotate90(clockwise: false);
            if (mode != 3 && Input.ShiftHeld && (Input.IsKeyPressed(KeyboardKey.R) || Input.IsKeyPressed(KeyboardKey.S))) { OpenXform(scale: Input.IsKeyPressed(KeyboardKey.S)); return; }
            if (Input.IsKeyPressed(KeyboardKey.G)) Change(ReverseSelection);
            if (mode != 3 && Input.ShiftHeld && Input.IsKeyPressed(KeyboardKey.F)) SliderToStream();
            if (Input.IsKeyPressed(KeyboardKey.D) && !Input.ShiftHeld) Change(CloneSelection);
        }
        if (Input.CtrlHeld && !Input.ShiftHeld && Input.IsKeyPressed(KeyboardKey.A)) SelectAll();
        if (mode != 3 && Input.CtrlHeld && Input.ShiftHeld && Input.IsKeyPressed(KeyboardKey.D)) { OpenPoly(); return; }
        if (Input.CtrlHeld && (Input.IsKeyPressed(KeyboardKey.C) || Input.IsKeyPressed(KeyboardKey.X)) && selected.Count > 0) CopySel(cut: Input.IsKeyPressed(KeyboardKey.X));
        if (Input.CtrlHeld && Input.IsKeyPressed(KeyboardKey.V) && clipboard.Count > 0) PasteClip();

        bool click = Input.IsMouseButtonPressed(MouseButton.Left), held = Input.IsMouseButtonDown(MouseButton.Left);
        if (click)
        {
            for (int i = 0; i < ToolNames.Length; i++) if (CheckCollisionPointRec(mouse, ToolRect(i))) { tool = i; sliderDraft = null; return; }
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
                undo.Add(Snapshot()); redo.Clear();
                endDrag = sp;
                return;
            }
            var hit = objs.LastOrDefault(o => Math.Abs(TimelineX(o.Time) - mouse.X) < 10 * u);
            if (hit != null)
            {
                if (!selected.Contains(hit)) { if (!Input.CtrlHeld) selected.Clear(); selected.Add(hit); }
                undo.Add(Snapshot()); redo.Clear();
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

        if (mode == 3) { UpdateMania(mouse, click, held); return; }

        // the playfield
        var fieldPos = ToField(mouse);
        bool inField = fieldPos.X >= -20 && fieldPos.X <= 532 && fieldPos.Y >= -20 && fieldPos.Y <= 404 && mouse.Y > tl.Y + tl.Height;
        // stable: a right click on a selected slider's point removes it (keeping two), else deletes the object under the
        // cursor (not while drawing a slider)
        if (Input.IsMouseButtonPressed(MouseButton.Right) && sliderDraft == null && pointDrag == null && inField && PointAt(fieldPos) is { } rp)
        {
            if (rp.Slider.Anchors.Count >= 2) Change(() => { rp.Slider.Anchors.RemoveAt(rp.Index); FitLength(rp.Slider, retype: true); });
            return;
        }
        if (Input.IsMouseButtonPressed(MouseButton.Right) && sliderDraft == null && pointDrag == null && inField
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

    // ---- stable's Rotate by... (-180 to 180 degrees, clockwise or anticlockwise) and Scale by... (0.1x up to what
    // still fits the playfield, on X and / or Y), about the playfield's centre or the selection's: previewed as it
    // changes, kept with OK (Enter), undone with Cancel (Escape)
    sealed class Xform
    {
        public bool Scale, Anticlockwise, SelectionCentre, X = true, Y = true, Dragging, WasModified;
        public double Value;
        public Dictionary<EObj, (double X, double Y, List<Vector2> Anchors, double Length, string? Raw)> Start = [];
        public List<string> Redo = []; // (given back on Cancel)
    }
    Xform? xform;

    void OpenXform(bool scale)
    {
        undo.Add(Snapshot());
        xform = new Xform { Scale = scale, Value = scale ? 1 : 0, WasModified = modified, Redo = [.. redo] };
        redo.Clear();
        foreach (var o in selected) xform.Start[o] = (o.X, o.Y, [.. o.Anchors], o.Length, o.Raw);
    }

    Vector2 XformCentre(Xform x) => !x.SelectionCentre ? new(256, 192)
        : new((float)x.Start.Values.Average(s => s.X), (float)x.Start.Values.Average(s => s.Y));

    // the largest scale that keeps the selection (heads, points) in the playfield
    double XformMaxScale(Xform x)
    {
        var c = XformCentre(x);
        double max = 2;
        foreach (var s in x.Start.Values)
            foreach (var p in s.Anchors.Prepend(new Vector2((float)s.X, (float)s.Y)))
            {
                if (x.X && Math.Abs(p.X - c.X) > 0.5) max = Math.Min(max, ((p.X > c.X ? 512 : 0) - c.X) / (p.X - c.X));
                if (x.Y && Math.Abs(p.Y - c.Y) > 0.5) max = Math.Min(max, ((p.Y > c.Y ? 384 : 0) - c.Y) / (p.Y - c.Y));
            }
        return Math.Max(0.1, max);
    }

    void ApplyXform(Xform x)
    {
        var c = XformCentre(x);
        double a = (x.Anticlockwise ? -x.Value : x.Value) * Math.PI / 180;
        float cos = (float)Math.Cos(a), sin = (float)Math.Sin(a);
        Vector2 F(Vector2 p)
        {
            var d = p - c;
            return x.Scale ? c + new Vector2(x.X ? d.X * (float)x.Value : d.X, x.Y ? d.Y * (float)x.Value : d.Y) : c + new Vector2(d.X * cos - d.Y * sin, d.X * sin + d.Y * cos);
        }
        foreach (var (o, s) in x.Start)
        {
            var p = F(new((float)s.X, (float)s.Y));
            o.X = Math.Round(p.X); o.Y = Math.Round(p.Y);
            o.Anchors = [.. s.Anchors.Select(F)];
            o.Length = s.Length;
            if (x.Scale && o.Slider) FitLength(o); // (stable refits a scaled slider)
            o.Raw = null;
        }
        modified = true;
        Rebuild();
    }

    Rectangle XformPanel() { float u = U; return new Rectangle(Game.W / 2f - 160 * u, 150 * u, 320 * u, 170 * u); }
    Rectangle XformBar() { var p = XformPanel(); float u = U; return new Rectangle(p.X + 20 * u, p.Y + 48 * u, p.Width - 40 * u, 14 * u); }
    Rectangle XformToggle(int row, int i) { var p = XformPanel(); float u = U; return new Rectangle(p.X + 20 * u + i * 142 * u, p.Y + (76 + row * 28) * u, 136 * u, 22 * u); }
    Rectangle XformButton(int i) { var p = XformPanel(); float u = U; return new Rectangle(p.X + 20 * u + i * 142 * u, p.Y + p.Height - 30 * u, 136 * u, 22 * u); }
    (double Min, double Max) XformRange(Xform x) => x.Scale ? (0.1, XformMaxScale(x)) : (-180, 180);

    void UpdateXform(Vector2 mouse)
    {
        var x = xform!;
        var (min, max) = XformRange(x);
        double before = x.Value;
        bool ok = Input.IsKeyPressed(KeyboardKey.Enter) || Input.IsKeyPressed(KeyboardKey.KpEnter), cancel = Input.IsKeyPressed(KeyboardKey.Escape);
        if (Input.IsKeyPressed(KeyboardKey.Left) || Input.IsKeyPressedRepeat(KeyboardKey.Left)) x.Value -= x.Scale ? 0.01 : 1;
        if (Input.IsKeyPressed(KeyboardKey.Right) || Input.IsKeyPressedRepeat(KeyboardKey.Right)) x.Value += x.Scale ? 0.01 : 1;
        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            var bar = XformBar();
            if (CheckCollisionPointRec(mouse, new Rectangle(bar.X - 8, bar.Y - 8, bar.Width + 16, bar.Height + 16))) x.Dragging = true;
            for (int i = 0; i < 2; i++)
            {
                if (CheckCollisionPointRec(mouse, XformToggle(0, i)))
                {
                    if (x.Scale) { if (i == 0) x.X = !x.X; else x.Y = !x.Y; if (!x.X && !x.Y) { if (i == 0) x.Y = true; else x.X = true; } }
                    else x.Anticlockwise = i == 1;
                    before = double.NaN;
                }
                if (CheckCollisionPointRec(mouse, XformToggle(1, i))) { x.SelectionCentre = i == 1; before = double.NaN; }
                if (CheckCollisionPointRec(mouse, XformButton(i))) { if (i == 0) ok = true; else cancel = true; }
            }
        }
        if (x.Dragging)
        {
            if (!Input.IsMouseButtonDown(MouseButton.Left)) x.Dragging = false;
            else
            {
                var bar = XformBar();
                double f = Math.Clamp((mouse.X - bar.X) / bar.Width, 0, 1);
                x.Value = x.Scale ? Math.Round(min + f * (max - min), 3) : Math.Round(min + f * (max - min));
            }
        }
        (min, max) = XformRange(x);
        x.Value = Math.Clamp(x.Value, min, max);
        if (x.Value != before) ApplyXform(x);
        if (cancel)
        {
            // (back exactly as it was)
            foreach (var (o, st) in x.Start) { o.X = st.X; o.Y = st.Y; o.Anchors = [.. st.Anchors]; o.Length = st.Length; o.Raw = st.Raw; }
            undo.RemoveAt(undo.Count - 1);
            redo.AddRange(x.Redo);
            modified = x.WasModified;
            Rebuild();
        }
        if (ok || cancel) xform = null;
    }

    void DrawXform(float u)
    {
        var x = xform!;
        var p = XformPanel();
        DrawRectangleRounded(p, 0.06f, 8, new Color(20, 20, 30, 235));
        DrawRectangleRoundedLinesEx(p, 0.06f, 8, 1, ColorAlpha(Color.White, 0.4f));
        string title = x.Scale ? "Scale by" : "Rotate by", label = x.Scale ? "Scale the selected objects’ spacing by:" : "Enter an angle to rotate by:";
        Game.TextBold(title, p.X + 12 * u, p.Y + 6 * u, 14 * u, Color.White);
        Game.Text(label, p.X + 20 * u, p.Y + 26 * u, 11 * u, Color.LightGray);
        string value = x.Scale ? $"{x.Value.ToString("0.000", Inv)}×" : $"{x.Value:0}°";
        Game.TextBold(value, p.X + p.Width - 20 * u - Game.TextWidth(value, 12 * u), p.Y + 26 * u, 12 * u, Color.White);
        var bar = XformBar();
        var (min, max) = XformRange(x);
        DrawRectangle((int)bar.X, (int)(bar.Y + bar.Height / 2 - u), (int)bar.Width, (int)(2 * u), Color.Gray);
        DrawCircleV(new(bar.X + bar.Width * (float)((x.Value - min) / Math.Max(1e-9, max - min)), bar.Y + bar.Height / 2), 6 * u, new Color(255, 160, 40, 255));
        string[] row0 = x.Scale ? ["X-axis", "Y-axis"] : ["Clockwise", "Anti-Clockwise"];
        bool[] on0 = x.Scale ? [x.X, x.Y] : [!x.Anticlockwise, x.Anticlockwise];
        for (int i = 0; i < 2; i++)
        {
            DrawButton(XformToggle(0, i), row0[i], on0[i]);
            DrawButton(XformToggle(1, i), i == 0 ? "Playfield Centre" : "Selection Centre", x.SelectionCentre == (i == 1));
            DrawButton(XformButton(i), i == 0 ? "OK" : "Cancel", false);
        }
    }

    // ---- the Edit menu's actions (and their keys)
    void Nudge(int dir) => Change(() =>
    {
        double step = RedAt(selected.Min(o => o.Time)).Beat / divisor;
        foreach (var o in selected) { o.Time = Math.Round(o.Time + dir * step); o.End = Math.Round(o.End + dir * step); o.Raw = null; }
    });
    void DeleteSel() => Change(() => { objs.RemoveAll(selected.Contains); selected.Clear(); });
    void SelectAll() { selected.Clear(); selected.UnionWith(objs); tool = 0; }
    void CopySel(bool cut)
    {
        clipboard = selected.OrderBy(o => o.Time).Select(o => o.Clone()).ToList();
        if (cut) DeleteSel();
    }
    // pasted at the current (snapped) time, keeping their spacing
    void PasteClip() => Change(() =>
    {
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
    // Flip Horizontally / Vertically across the playfield (mania: the columns the other way / the selection reversed in
    // time), Rotate 90° about its centre
    void FlipH()
    {
        int keys = MKeys;
        Change(() => { if (mode == 3) foreach (var o in selected) { o.X = MColumnX(keys - 1 - MColumn(o, keys), keys); o.Raw = null; } else Transform(p => new(512 - p.X, p.Y)); });
    }
    void FlipV() => Change(() => { if (mode == 3) ReverseSelection(); else Transform(p => new(p.X, 384 - p.Y)); });
    void Rotate90(bool clockwise) => Change(() => Transform(clockwise ? p => new(256 - (p.Y - 192), 192 + (p.X - 256)) : p => new(256 + (p.Y - 192), 192 - (p.X - 256))));
    // Reset ... samples: back to the timing point's (no sample set, addition set, index, volume or file of their own)
    void ResetSamples(IEnumerable<EObj> list) => Change(() =>
    {
        foreach (var o in list.ToList())
        {
            o.Sample = o.Sample.Length > 0 ? "0:0:0:0:" : "";
            if (o.Slider && o.EdgeSets.Length > 0) o.EdgeSets = string.Join('|', Enumerable.Repeat("0:0", o.Slides + 1));
            o.Raw = null;
        }
    });
    void ResetComboColours() => Change(() => { foreach (var o in objs) if ((o.Type & 0x70) != 0) { o.Type &= ~0x70; o.Raw = null; } });
    void ResetBreaks() => Change(() => { if (sections.FirstOrDefault(s => s.Name == "Events") is { } ev) ev.Lines.RemoveAll(l => l.StartsWith("2,") || l.StartsWith("Break,")); });
    // Resnap All Notes: every start (and a spinner's or hold's end) onto the beat snap (Current Section: the timing
    // section now playing's)
    void ResnapAll() => Resnap(objs);
    void ResnapSection()
    {
        var red = RedAt(time);
        double next = timing.Where(p => p.Red && p.Time > red.Time).Select(p => p.Time).DefaultIfEmpty(double.MaxValue).Min();
        Resnap(objs.Where(o => o.Time >= red.Time && o.Time < next).ToList());
    }
    void Resnap(List<EObj> list) => Change(() =>
    {
        foreach (var o in list)
        {
            double t = Snap(o.Time), end = o.Spinner || (o.Type & 128) != 0 ? Snap(o.End) : o.End + (t - o.Time);
            if (t == o.Time && end == o.End) continue;
            o.Time = t; o.End = end; o.Raw = null;
        }
    });
    // stable's Convert Slider to Stream (its dialog's defaults): a circle on every beat snap tick of the slider's time,
    // where its ball is then (back and forth through the repeats); the circles on its ends and repeats keep their
    // hitsounds, the first its new combo
    bool AnySliderSelected => selected.Any(o => o.Slider && o.Path.Length > 1);
    void SliderToStream()
    {
        var sliders = selected.Where(o => o.Slider && o.Path.Length > 1).ToList();
        if (sliders.Count == 0) return;
        Change(() =>
        {
            selected.Clear();
            foreach (var s in sliders)
            {
                objs.Remove(s);
                double step = RedAt(s.Time).Beat / divisor, span = (s.End - s.Time) / s.Slides;
                var edges = s.Edges.Split('|', StringSplitOptions.RemoveEmptyEntries).Select(e => int.TryParse(e, out int v) ? v : 0).ToArray();
                for (int k = 0; s.Time + k * step <= s.End + 1; k++)
                {
                    double t = s.Time + k * step, into = Math.Min(s.Slides, (t - s.Time) / span);
                    int sp = Math.Min(s.Slides - 1, (int)into);
                    double f = into - sp;
                    if (sp % 2 == 1) f = 1 - f;
                    var p = PathAt(s.Path, Math.Clamp(f, 0, 1));
                    double edgeAt = Math.Round(into);
                    int edge = Math.Abs(into - edgeAt) * span < 1 ? (int)edgeAt : -1;
                    var c = new EObj { X = Math.Round(p.X), Y = Math.Round(p.Y), Time = Math.Round(t), Type = 1 | (k == 0 ? s.Type & 0x74 : 0), Sample = s.Sample,
                        HitSound = edge < 0 ? 0 : edge < edges.Length ? edges[edge] : s.HitSound };
                    c.End = c.Time;
                    objs.Add(c);
                    selected.Add(c);
                }
            }
        });
    }
    bool dimBg = true;

    // stable's follow points (View > Follow points), as in a play: every 32 units from 48 in between a combo's
    // objects, each fading in from 800ms (x preempt / 450 under 450) before its time, sliding in, and out after it
    bool followPoints = true;

    void DrawFollowPoints(float fs)
    {
        double pre = Preempt, lead = 800 * Math.Min(1, pre / 450), fade = 400 * Math.Min(1, pre / 450);
        int frames = skin.FrameCount("followpoint");
        float sd = Game.H / 768f * fs / (0.8f * U); // (the editor's playfield is a little bigger than a play's)
        for (int i = 1; i < objs.Count; i++)
        {
            EObj a = objs[i - 1], b = objs[i];
            if (b.Combo == 1 || a.Spinner || b.Spinner || a.End - lead > time || b.Time + fade < time) continue;
            Vector2 from = a.Slider && a.Path.Length > 0 ? (a.Slides % 2 == 1 ? a.Path[^1] : a.Path[0]) : new((float)a.X, (float)a.Y), to = new((float)b.X, (float)b.Y);
            int dist = (int)Vector2.Distance(from, to);
            float rot = MathF.Atan2(to.Y - from.Y, to.X - from.X) * 180 / MathF.PI;
            for (int d = 48; d < dist - 32; d += 32)
            {
                float f = (float)d / dist;
                double at = a.End + (b.Time - a.End) * f, age = time - (at - lead);
                if (age < 0 || time > at + fade) continue;
                float t = (float)Math.Clamp(age / fade, 0, 1), e = 1 - (1 - t) * (1 - t);
                float alpha = t * (float)Math.Clamp(1 - (time - at) / fade, 0, 1);
                var p = Vector2.Lerp(from + (to - from) * (f - 0.1f), from + (to - from) * f, e);
                var s = skin.Frame("followpoint", Math.Max(0, Math.Min(frames - 1, (int)(age / (1000.0 / Math.Max(1, frames))))));
                if (s is { } sp) sp.Draw(ToScreen(p.X, p.Y), sd * (1.5f - 0.5f * e), ColorAlpha(Color.White, alpha), rot);
                else DrawCircleV(ToScreen(p.X, p.Y), 2.5f * fs, ColorAlpha(Color.White, alpha * 0.8f));
            }
        }
    }

    // File > Open Difficulty: the set's other difficulties (once the changes here are saved or reverted)
    MenuItem[] OtherDifficulties()
    {
        try
        {
            return [.. Directory.EnumerateFiles(info.Folder, "*.osu").Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)).Order()
                .Select(f => new MenuItem(Path.GetFileNameWithoutExtension(f) is var n && n.LastIndexOf('[') is var a and >= 0 && n.LastIndexOf(']') > a ? n[(a + 1)..n.LastIndexOf(']')] : n, "", () => OpenOther(f)))];
        }
        catch (IOException) { return []; }
    }

    void OpenOther(string file)
    {
        if (modified) { Notification.Show("Save (Ctrl+S) or revert the changes first.", 3000); return; }
        music?.Dispose();
        music = null;
        video?.Dispose();
        video = null;
        sbView?.Unload();
        samples.Unload();
        Game.FadeTo(() => new Editor(file, back));
    }

    // File > Export Package: the map's folder as an .osz in osu!'s Exports folder, which opens (like stable)
    void ExportPackage()
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(info.Folder)) + ".osz", exports = Path.Combine(Config.OsuDir, "Exports");
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
        try
        {
            Directory.CreateDirectory(exports);
            string file = Path.Combine(exports, name);
            File.Delete(file);
            System.IO.Compression.ZipFile.CreateFromDirectory(info.Folder, file);
            Notification.Show($"{name} has been exported.", 3000);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{exports}\"") { UseShellExecute = true });
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Notification.Show($"Couldn't export {name}: {e.Message}", 3000); }
    }

    // a small prompt: a line to type (Text not null) or a question; Enter / the first button takes it, Escape / the
    // second doesn't
    sealed class Prompt(string title, string? text, Action<string> ok, string yes = "OK", string no = "Cancel", Action? cancel = null)
    {
        public string Title = title, Yes = yes, No = no;
        public string? Text = text;
        public Action<string> Ok = ok;
        public Action? Cancel = cancel;
    }
    Prompt? prompt;

    void MoveAllPrompt() => prompt = new("Enter the amount of milliseconds to move all notes by:", "", text =>
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, Inv, out int ms) || ms == 0) return;
        Change(() => { foreach (var o in objs) { o.Time += ms; o.End += ms; o.Raw = null; } });
    });

    // File > Create a new Difficulty...: its name, then this map as that difficulty's file (Artist - Title (Creator)
    // [Name].osu), stable's question whether to start it empty, saved
    void NewDifficultyPrompt() => prompt = new("Enter a name for the new difficulty:", "", name =>
    {
        name = name.Trim();
        if (name.Length == 0) return;
        string file = $"{Kv("Metadata", "Artist")} - {Kv("Metadata", "Title")} ({Kv("Metadata", "Creator")}) [{name}].osu";
        foreach (char c in Path.GetInvalidFileNameChars()) file = file.Replace(c.ToString(), "");
        string full = Path.Combine(info.Folder, file);
        if (File.Exists(full)) { Notification.Show("A difficulty with that name already exists.", 3000); return; }
        path = full;
        SetKv("Metadata", "Version", name);
        if (Kv("Metadata", "BeatmapID") != null) SetKv("Metadata", "BeatmapID", "0");
        undo.Clear(); redo.Clear();
        prompt = new("Would you like to clear all objects and start fresh?", null, _ => { objs.Clear(); selected.Clear(); Rebuild(); Save(); }, "Yes", "No", Save);
    });

    void UpdatePrompt(Vector2 mouse)
    {
        var p = prompt!;
        if (p.Text != null)
        {
            for (int c; (c = Input.GetCharPressed()) != 0;) if (c >= 32) p.Text += char.ConvertFromUtf32(c);
            if ((Input.IsKeyPressed(KeyboardKey.Backspace) || Input.IsKeyPressedRepeat(KeyboardKey.Backspace)) && p.Text.Length > 0) p.Text = p.Text[..^1];
        }
        bool click = Input.IsMouseButtonPressed(MouseButton.Left);
        if (Input.IsKeyPressed(KeyboardKey.Escape) || click && CheckCollisionPointRec(mouse, XformButton(1))) { prompt = null; p.Cancel?.Invoke(); return; }
        if (Input.IsKeyPressed(KeyboardKey.Enter) || Input.IsKeyPressed(KeyboardKey.KpEnter) || click && CheckCollisionPointRec(mouse, XformButton(0))) { prompt = null; p.Ok(p.Text ?? ""); }
    }

    void DrawPrompt(float u)
    {
        var p = prompt!;
        var panel = XformPanel();
        DrawRectangleRounded(panel, 0.06f, 8, new Color(20, 20, 30, 235));
        DrawRectangleRoundedLinesEx(panel, 0.06f, 8, 1, ColorAlpha(Color.White, 0.4f));
        Game.Text(p.Title, panel.X + 16 * u, panel.Y + 20 * u, 11 * u, Color.LightGray);
        if (p.Text != null)
        {
            var box = new Rectangle(panel.X + 16 * u, panel.Y + 50 * u, panel.Width - 32 * u, 22 * u);
            DrawRectangleRec(box, new Color(70, 70, 90, 255));
            Game.Text(p.Text + ((int)(Game.NowMs / 500) % 2 == 0 ? "|" : ""), box.X + 4 * u, box.Y + 4 * u, 12 * u, Color.White);
        }
        for (int i = 0; i < 2; i++) DrawButton(XformButton(i), i == 0 ? p.Yes : p.No, false);
    }

    // ---- stable's Polygonal Circle Creation (Ctrl+Shift+D): Points circles round the playfield's centre, Repeat
    // times, a beat snap tick apart from now; the sides Distance snap times a tick's slider travel long, the first at
    // the Offset angle (the distance kept to what fits); new combo and hitsounds as toggled
    sealed class Poly { public double Spacing = 1, Angle, Points = 4, Repeat = 1; public int Dragging = -1; }
    Poly? poly;
    double lastPolySpacing = 1;
    static readonly (string Label, double Min, double Max, double Step)[] PolyRows = [("Distance snap", 0.1, 10, 0.1), ("Offset angle", -180, 180, 1), ("Points", 3, 32, 1), ("Repeat", 1, 10, 1)];

    void OpenPoly() => poly = new Poly { Spacing = lastPolySpacing };

    double PolyTick() => RedAt(time).Beat / divisor;
    double PolyRadius(Poly p, double spacing) => spacing * 100 * SliderMultiplier * SvAt(time) / RedAt(time).Beat * PolyTick() / (2 * Math.Sin(Math.PI / (int)p.Points));
    double PolyMaxSpacing(Poly p) => Math.Max(0.1, Math.Floor(190 / PolyRadius(p, 1) * 10) / 10);

    double[] PolyValues(Poly p) => [p.Spacing, p.Angle, p.Points, p.Repeat];
    void SetPolyValue(Poly p, int i, double v)
    {
        var (_, min, max, step) = PolyRows[i];
        v = Math.Clamp(Math.Round(v / step) * step, min, max);
        if (i == 0) p.Spacing = v; else if (i == 1) p.Angle = v; else if (i == 2) p.Points = v; else p.Repeat = v;
        p.Spacing = Math.Min(p.Spacing, PolyMaxSpacing(p));
    }

    Rectangle PolyBar(int i) { var p = XformPanel(); float u = U; return new Rectangle(p.X + 110 * u, p.Y + (34 + i * 24) * u, p.Width - 170 * u, 12 * u); }

    void UpdatePoly(Vector2 mouse)
    {
        var p = poly!;
        bool ok = Input.IsKeyPressed(KeyboardKey.Enter) || Input.IsKeyPressed(KeyboardKey.KpEnter), cancel = Input.IsKeyPressed(KeyboardKey.Escape);
        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            for (int i = 0; i < PolyRows.Length; i++) { var b = PolyBar(i); if (CheckCollisionPointRec(mouse, new Rectangle(b.X - 8, b.Y - 6, b.Width + 16, b.Height + 12))) p.Dragging = i; }
            if (CheckCollisionPointRec(mouse, XformButton(0))) ok = true;
            if (CheckCollisionPointRec(mouse, XformButton(1))) cancel = true;
        }
        if (p.Dragging >= 0)
        {
            if (!Input.IsMouseButtonDown(MouseButton.Left)) p.Dragging = -1;
            else { var b = PolyBar(p.Dragging); var r = PolyRows[p.Dragging]; SetPolyValue(p, p.Dragging, r.Min + Math.Clamp((mouse.X - b.X) / b.Width, 0, 1) * (r.Max - r.Min)); }
        }
        if (cancel) { poly = null; return; }
        if (!ok) return;
        poly = null;
        lastPolySpacing = p.Spacing;
        int n = (int)p.Points;
        double radius = PolyRadius(p, p.Spacing), tick = PolyTick(), t = Snap(time), a = p.Angle * Math.PI / 180;
        Change(() =>
        {
            selected.Clear();
            for (int i = 1; i <= n * (int)p.Repeat; i++)
            {
                float ang = (float)Math.PI * 2f / n * i;
                var o = new EObj { X = Math.Round(radius * Math.Cos(ang + a) + 256), Y = Math.Round(radius * Math.Sin(ang + a) + 192), Time = Math.Round(t),
                    Type = 1 | (i == 1 && newComboNext ? 4 : 0), HitSound = soundNext };
                o.End = o.Time;
                if (setsNext != (0, 0)) SetSets(o, setsNext.Normal, setsNext.Addition);
                objs.Add(o);
                selected.Add(o);
                t = Snap(t + tick);
            }
            newComboNext = false;
        });
    }

    void DrawPoly(float u)
    {
        var p = poly!;
        var panel = XformPanel();
        DrawRectangleRounded(panel, 0.06f, 8, new Color(20, 20, 30, 235));
        DrawRectangleRoundedLinesEx(panel, 0.06f, 8, 1, ColorAlpha(Color.White, 0.4f));
        Game.TextBold("Polygonal Circle Creation", panel.X + 12 * u, panel.Y + 6 * u, 14 * u, Color.White);
        var values = PolyValues(p);
        for (int i = 0; i < PolyRows.Length; i++)
        {
            var (label, min, max, _) = PolyRows[i];
            var b = PolyBar(i);
            Game.Text(label, panel.X + 16 * u, b.Y - 1 * u, 11 * u, Color.LightGray);
            DrawRectangle((int)b.X, (int)(b.Y + b.Height / 2 - u), (int)b.Width, (int)(2 * u), Color.Gray);
            DrawCircleV(new(b.X + b.Width * (float)((values[i] - min) / (max - min)), b.Y + b.Height / 2), 6 * u, new Color(255, 160, 40, 255));
            string v = i == 0 ? $"{values[i].ToString("0.0#", Inv)}×" : i == 1 ? $"{values[i]:0}°" : $"{values[i]:0}";
            Game.TextBold(v, b.X + b.Width + 10 * u, b.Y - 1 * u, 11 * u, Color.White);
        }
        Game.Text("Try smaller distance snap if you need more points", panel.X + 16 * u, panel.Y + 125 * u, 10 * u, Color.Gray);
        for (int i = 0; i < 2; i++) DrawButton(XformButton(i), i == 0 ? "OK" : "Cancel", false);
    }

    void RecalcSliders() => Change(() => { foreach (var o in objs) if (o.Slider) FitLength(o); });
    void RevertToSaved()
    {
        Read(File.ReadAllLines(path));
        LoadOsb();
        mode = int.TryParse(Kv("General", "Mode"), out int m) ? m : 0;
        undo.Clear(); redo.Clear(); selected.Clear();
        modified = false;
        info = ReloadInfo();
        Rebuild();
    }
    // the latest timing point at or before now
    void DeleteTimingHere()
    {
        if (timing.Where(p => p.Time <= time + 1).OrderBy(p => p.Time).LastOrDefault() is { } p) Change(() => { timing.Remove(p); timingSel = -1; });
    }

    // ---- stable's menu bar: File, Edit, View, Compose, Design, Timing, Web, Help, each a dropdown (what this editor
    // can't do greyed out, the keys shown); Snap Divisor, Audio Rate and Time Signature open to the side
    sealed record MenuItem(string Label, string Keys = "", Action? Run = null, bool Checked = false, MenuItem[]? Sub = null)
    {
        public bool Separator => Label == "-";
        public bool Enabled => Run != null || Sub != null;
    }
    static readonly MenuItem Sep = new("-");
    static readonly string[] MenuTitles = ["File", "Edit", "View", "Compose", "Design", "Timing", "Web", "Help"];
    int menuOpen = -1, menuSub = -1;
    Action? Sel(Action a) => selected.Count > 0 ? a : null;
    Action? NotMania(Action? a) => mode == 3 ? null : a;

    // Compose → Auto Volume Balance (mania): notes struck together share out 4/3 of their timing point's volume as their
    // own sample volume; stable leaves the last group as it was
    void AutoVolumeBalance() => Change(() =>
    {
        var points = timing.OrderBy(p => p.Time).ToList();
        for (int i = 0, first = 0; i < objs.Count; i++)
        {
            if (objs[i].Time == objs[first].Time) continue;
            int volume = (points.LastOrDefault(p => p.Time <= objs[first].Time) ?? points.FirstOrDefault())?.Volume ?? 100;
            for (int j = first; j < i; j++)
            {
                SetHitSample(objs[j], null, volume * 4 / 3 / Math.Max(1, i - first), null);
            }
            first = i;
        }
    });

    MenuItem[] MenuItems(int i) => i switch
    {
        0 => [new("Clear All Notes", "", objs.Count > 0 ? () => Change(() => { objs.Clear(); selected.Clear(); }) : null),
            new("Open Difficulty...", Sub: OtherDifficulties() is { Length: > 0 } diffs ? diffs : null), Sep,
            new("Save", "Ctrl+S", Save), new("Create a new Difficulty...", "", NewDifficultyPrompt), Sep, new("Test Beatmap", "F5", TestPlay), new("Open AiMod", "Ctrl+Shift+A", OpenAiMod), Sep,
            new("Upload Beatmap..."), new("Extract Map Package", "", () => Notification.Show("Your beatmap is already extracted!", 3000)), new("Export Package", "", ExportPackage), new("Import from..."), Sep,
            new("Open Song Folder", "", () => System.Diagnostics.Process.Start("explorer.exe", info.Folder)),
            new("Open .osu in Notepad", "", () => System.Diagnostics.Process.Start("notepad.exe", path)),
            new("Open .osb in Notepad", "", Directory.EnumerateFiles(info.Folder, "*.osb").FirstOrDefault() is { } osb ? () => System.Diagnostics.Process.Start("notepad.exe", osb) : null), Sep,
            new("Revert to Saved", "", modified ? RevertToSaved : null), Sep, new("Exit...", "Esc", AskExit)],
        1 => [new("Undo", "Ctrl+Z", undo.Count > 0 ? () => Restore(undo, redo) : null), new("Redo", "Ctrl+Y", redo.Count > 0 ? () => Restore(redo, undo) : null), Sep,
            new("Cut", "Ctrl+X", Sel(() => CopySel(cut: true))), new("Copy", "Ctrl+C", Sel(() => CopySel(cut: false))), new("Paste", "Ctrl+V", clipboard.Count > 0 ? PasteClip : null),
            new("Delete", "Del", Sel(DeleteSel)), Sep, new("Select All", "Ctrl+A", SelectAll), new("Clone", "Ctrl+D", Sel(() => Change(CloneSelection))), Sep,
            new("Reverse Selection", "Ctrl+G", Sel(() => Change(ReverseSelection))), new("Flip Horizontally", "Ctrl+H", Sel(FlipH)), new("Flip Vertically", "Ctrl+J", Sel(FlipV)),
            new("Rotate 90° Clockwise", "Ctrl+>", NotMania(Sel(() => Rotate90(clockwise: true)))), new("Rotate 90° Anticlockwise", "Ctrl+<", NotMania(Sel(() => Rotate90(clockwise: false)))),
            new("Rotate by...", "Ctrl+Shift+R", NotMania(Sel(() => OpenXform(scale: false)))), new("Scale by...", "Ctrl+Shift+S", NotMania(Sel(() => OpenXform(scale: true)))), Sep,
            new("Reset selected objects' samples", "", Sel(() => ResetSamples(selected))), new("Reset all samples", "", () => ResetSamples(objs)),
            new("Reset combo colours", "", ResetComboColours), new("Reset breaks", "", ResetBreaks), Sep,
            new("Nudge Backward", "J", Sel(() => Nudge(-1))), new("Nudge Forward", "K", Sel(() => Nudge(1)))],
        2 => [new("Compose", "F1", () => tab = 0, tab == 0), new("Design", "F2", () => tab = 3, tab == 3), new("Timing", "F3", () => tab = 1, tab == 1), Sep,
            new("Song Setup...", "F4", () => tab = 2, tab == 2), new("Timing Setup...", "F6", () => tab = 1), Sep,
            new("Show Video/Storyboard", "", () => Config.SetOwn("EditorVideo", (showVideo = !showVideo) ? "1" : "0"), showVideo), new("Dim Background", "", () => dimBg = !dimBg, dimBg), new("Show Sample Name", "", () => showSampleName = !showSampleName, showSampleName), Sep, new("Hit animations", "", () => Config.SetOwn("EditorHitAnimations", (hitAnimations = !hitAnimations) ? "1" : "0"), hitAnimations), new("Follow points", "", () => followPoints = !followPoints, followPoints)],
        3 => [new("Snap Divisor", Sub: [.. new[] { (1, "Full Beats"), (2, "Half Beats"), (4, "Quarter Beats"), (8, "Eighth Beats"), (3, "Triplets*"), (6, "Double Triplets*") }
                .Select(d => new MenuItem($"1/{d.Item1} {d.Item2}", "", () => divisor = d.Item1, divisor == d.Item1))]),
            new("Audio Rate", Sub: [.. Rates.Select(r => new MenuItem($"{r * 100:0}%", "", () => { rate = r; music?.SetTempo(rate, false); }, rate == r))]),
            new("Grid Snapping", "T", () => gridSnap = !gridSnap, gridSnap), Sep,
            new("Create Polygon Circles...", "Ctrl+Shift+D", NotMania(OpenPoly)), new("Convert slider to stream...", "Ctrl+Shift+F", NotMania(AnySliderSelected ? SliderToStream : null)),
            new("Enable live mapping mode", "Ctrl+Tab", ToggleLiveMapping, liveMapping), new("Sample Import", "Ctrl+Shift+I", mode == 3 ? OpenSampleImport : null), new("Auto Volume Balance", "", mode == 3 ? AutoVolumeBalance : null)],
        4 => [new("Move all elements in time...")],
        5 => [new("Time Signature", Sub: [new("4/4 (Common Time)", "", () => Change(() => RedAt(time).Meter = 4), RedAt(time).Meter == 4), new("3/4 (Waltz/Triple)", "", () => Change(() => RedAt(time).Meter = 3), RedAt(time).Meter == 3)]),
            new("Metronome Clicks", "", () => metronome = !metronome, metronome), Sep,
            new("Add Timing Section", "Ctrl+P", () => TimingAction(0)), new("Add Inheriting Section", "Ctrl+Shift+P", () => TimingAction(1)),
            new("Reset Current Section"), new("Delete Timing Section", "Ctrl+I", DeleteTimingHere), new("Resnap Current Section", "", ResnapSection), Sep,
            new("Resnap All Notes", "", ResnapAll), new("Move all notes in time...", "", objs.Count > 0 ? MoveAllPrompt : null), new("Recalculate Slider Lengths", "", RecalcSliders),
            new("Delete All Timing Sections", "", timing.Count > 0 ? () => Change(() => { timing.Clear(); timingSel = -1; }) : null), Sep,
            new("Set Current Position as Preview Point", "", () => Change(() => SetKv("General", "PreviewTime", ((int)Math.Round(time)).ToString())))],
        6 => [new("This Beatmap's Information Page"), new("This Beatmap's Thread")],
        _ => [new("Show in-game help"), new("View FAQ")],
    };

    Rectangle MenuTitleRect(int i)
    {
        float u = U, x = 4 * u;
        for (int k = 0; k < i; k++) x += Game.TextWidth(MenuTitles[k], 12 * u) + 12 * u;
        return new Rectangle(x - 4 * u, 0, Game.TextWidth(MenuTitles[i], 12 * u) + 8 * u, 17 * u);
    }

    // the dropdown's rows: separators short, items 16 units
    List<(MenuItem Item, Rectangle Rect)> MenuRows(MenuItem[] items, float x, float y)
    {
        float u = U, w = 200 * u;
        foreach (var it in items) w = Math.Max(w, Game.TextWidth(it.Label, 11 * u) + Game.TextWidth(it.Keys, 11 * u) + 48 * u);
        var rows = new List<(MenuItem, Rectangle)>();
        foreach (var it in items)
        {
            float h = (it.Separator ? 7 : 16) * u;
            rows.Add((it, new Rectangle(x, y, w, h)));
            y += h;
        }
        return rows;
    }

    // true while the menu has the input
    bool UpdateMenu(Vector2 mouse)
    {
        bool click = Input.IsMouseButtonPressed(MouseButton.Left);
        int title = Enumerable.Range(0, MenuTitles.Length).FirstOrDefault(i => CheckCollisionPointRec(mouse, MenuTitleRect(i)), -1);
        if (menuOpen < 0)
        {
            if (click && title >= 0) { menuOpen = title; menuSub = -1; return true; }
            return false;
        }
        if (Input.IsKeyPressed(KeyboardKey.Escape)) { menuOpen = -1; return true; }
        if (title >= 0 && title != menuOpen) { menuOpen = title; menuSub = -1; }
        var rows = MenuRows(MenuItems(menuOpen), MenuTitleRect(menuOpen).X, 17 * U);
        int hover = rows.FindIndex(r => CheckCollisionPointRec(mouse, r.Rect));
        if (hover >= 0 && rows[hover].Item.Sub != null) menuSub = hover;
        List<(MenuItem Item, Rectangle Rect)> sub = menuSub >= 0 && menuSub < rows.Count && rows[menuSub].Item.Sub is { } s ? MenuRows(s, rows[menuSub].Rect.X + rows[menuSub].Rect.Width, rows[menuSub].Rect.Y) : [];
        if (!click) return true;
        var hit = sub.FirstOrDefault(r => CheckCollisionPointRec(mouse, r.Rect)).Item ?? (hover >= 0 ? rows[hover].Item : null);
        if (title == menuOpen && hit == null) { menuOpen = -1; return true; }
        if (hit?.Sub != null) return true;
        menuOpen = -1;
        hit?.Run?.Invoke();
        return true;
    }

    void DrawMenu(float u)
    {
        for (int i = 0; i < MenuTitles.Length; i++)
        {
            var r = MenuTitleRect(i);
            if (menuOpen == i) DrawRectangleRec(r, new Color(255, 160, 40, 200));
            Game.Text(MenuTitles[i], r.X + 4 * u, 1 * u, 12 * u, Color.White);
        }
        if (menuOpen < 0) return;
        var mouse = Input.GetMousePosition();
        void Panel(List<(MenuItem Item, Rectangle Rect)> rows)
        {
            if (rows.Count == 0) return;
            var a = rows[0].Rect; var z = rows[^1].Rect;
            var box = new Rectangle(a.X, a.Y, a.Width, z.Y + z.Height - a.Y);
            DrawRectangleRec(box, new Color(25, 25, 30, 240));
            DrawRectangleLinesEx(box, 1, ColorAlpha(Color.White, 0.3f));
            foreach (var (it, r) in rows)
            {
                if (it.Separator) { DrawRectangle((int)(r.X + 6 * u), (int)(r.Y + r.Height / 2), (int)(r.Width - 12 * u), 1, ColorAlpha(Color.White, 0.25f)); continue; }
                if (it.Enabled && CheckCollisionPointRec(mouse, r)) DrawRectangleRec(r, new Color(255, 160, 40, 110));
                var c = it.Enabled ? Color.White : Color.Gray;
                if (it.Checked) DrawCircleV(new(r.X + 9 * u, r.Y + r.Height / 2), 2.5f * u, c);
                Game.Text(it.Label, r.X + 18 * u, r.Y + 2 * u, 11 * u, c);
                string keys = it.Sub != null ? ">" : it.Keys;
                Game.Text(keys, r.X + r.Width - 8 * u - Game.TextWidth(keys, 11 * u), r.Y + 2 * u, 11 * u, ColorAlpha(c, 0.7f));
            }
        }
        var rows = MenuRows(MenuItems(menuOpen), MenuTitleRect(menuOpen).X, 17 * u);
        Panel(rows);
        if (menuSub >= 0 && menuSub < rows.Count && rows[menuSub].Item.Sub is { } s) Panel(MenuRows(s, rows[menuSub].Rect.X + rows[menuSub].Rect.Width, rows[menuSub].Rect.Y));
    }

    // the selection's positions (slider points too) through f
    void Transform(Func<Vector2, Vector2> f)
    {
        foreach (var o in selected)
        {
            var p = f(new((float)o.X, (float)o.Y));
            o.X = Math.Round(p.X); o.Y = Math.Round(p.Y);
            for (int i = 0; i < o.Anchors.Count; i++) o.Anchors[i] = f(o.Anchors[i]);
            o.Raw = null;
        }
    }

    // stable's Reverse Selection: each object moved to where its mirror in time was (start = first start + last end -
    // its end), the new combos staying in their places, sliders run the other way (from where they end)
    void ReverseSelection()
    {
        var list = selected.OrderBy(o => o.Time).ToList();
        double from = list.Min(o => o.Time), to = list.Max(o => o.End);
        var combos = list.Select(o => o.NewCombo).ToList();
        for (int i = 0; i < list.Count; i++)
        {
            var o = list[i];
            o.NewCombo = combos[list.Count - 1 - i];
            double d = o.End - o.Time;
            o.Time = from + to - o.End; o.End = o.Time + d;
            if (o.Slider && o.Anchors.Count > 0 && o.Path.Length > 1)
            {
                var pts = new List<Vector2> { new((float)o.X, (float)o.Y) };
                pts.AddRange(o.Anchors);
                pts.Reverse();
                pts[0] = o.Path[^1];
                o.X = Math.Round(pts[0].X); o.Y = Math.Round(pts[0].Y);
                o.Anchors = pts.Skip(1).ToList();
                if (o.Edges.Length > 0) o.Edges = string.Join('|', o.Edges.Split('|').Reverse());
                if (o.EdgeSets.Length > 0) o.EdgeSets = string.Join('|', o.EdgeSets.Split('|').Reverse());
            }
            o.Raw = null;
        }
    }

    // stable's Clone: a copy of the selection a beat (snapped) after its end, selected
    void CloneSelection()
    {
        var list = selected.OrderBy(o => o.Time).ToList();
        double from = list[0].Time, at = Snap(list.Max(o => o.End) + RedAt(time).Beat);
        selected.Clear();
        foreach (var o in list)
        {
            var c = o.Clone();
            c.Raw = null; c.Time = at + (o.Time - from); c.End = c.Time + (o.End - o.Time);
            objs.Add(c);
            selected.Add(c);
        }
    }

    // [Editor] Bookmarks: the times, comma-separated (the line left out with none, like stable)
    List<int> Bookmarks() => [.. (Kv("Editor", "Bookmarks") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(s => int.TryParse(s, out int t) ? t : -1).Where(t => t >= 0).Order()];
    void SetBookmarks(List<int> marks)
    {
        if (sections.FirstOrDefault(x => x.Name == "Editor") is not { } ed) return;
        int i = ed.Lines.FindIndex(l => l.Split(':')[0].Trim() == "Bookmarks");
        if (marks.Count == 0) { if (i >= 0) { ed.Lines.RemoveAt(i); modified = true; } return; }
        if (i < 0) ed.Lines.Insert(0, "Bookmarks: "); // (stable writes it first)
        SetKv("Editor", "Bookmarks", string.Join(',', marks));
    }

    void FinishSlider()
    {
        var pts = sliderDraft!;
        sliderDraft = null;
        if (pts.Count < 2) return;
        Change(() =>
        {
            var o = new EObj { X = pts[0].X, Y = pts[0].Y, Time = sliderDraftTime, Type = 2 | (newComboNext ? 4 : 0), HitSound = soundNext, Anchors = pts.Skip(1).ToList() };
            FitLength(o, retype: true);
            objs.Add(o);
            newComboNext = false;
            selected.Clear();
        });
    }

    // A slider's length after its points change, like stable's: the longest the beat snap's ticks fit into its path
    // (at least one); its curve from its point count when that changed (two: linear, three: perfect circle, more: bezier).
    void FitLength(EObj o, bool retype = false)
    {
        var pts = new List<Vector2> { new((float)o.X, (float)o.Y) };
        pts.AddRange(o.Anchors);
        if (retype && o.Curve != 'C') o.Curve = pts.Count == 2 ? 'L' : pts.Count == 3 ? 'P' : 'B';
        double drawn = 0;
        SliderPath.Build(o.Curve, pts, ref drawn, version); // (a length of 0 comes back as the whole path's)
        double perTick = 100 * SliderMultiplier * SvAt(o.Time) / divisor;
        o.Length = Math.Max(1, Math.Floor(drawn / perTick)) * perTick;
        o.Raw = null;
    }

    // a selected (visible) slider's point (after its head) under the cursor
    (EObj Slider, int Index)? PointAt(Vector2 fieldPos)
    {
        foreach (var o in Visible())
            if (o.Slider && selected.Contains(o))
                for (int i = o.Anchors.Count - 1; i >= 0; i--)
                    if (Vector2.Distance(o.Anchors[i], fieldPos) <= 6) return (o, i);
        return null;
    }

    (EObj Slider, int Index)? pointDrag;

    void UpdateSelect(Vector2 mouse, Vector2 fieldPos, bool click, bool held, bool inField)
    {
        // a slider's point being dragged (on the grid with grid snap); the slider refits as it goes
        if (pointDrag is { } pd)
        {
            var p = GridSnap(fieldPos);
            if (held && pd.Slider.Anchors[pd.Index] != p) { pd.Slider.Anchors[pd.Index] = p; FitLength(pd.Slider); modified = true; Rebuild(); }
            if (!held) pointDrag = null;
            return;
        }
        if (click && inField && PointAt(fieldPos) is { } pt)
        {
            undo.Add(Snapshot()); redo.Clear();
            pointDrag = pt;
            return;
        }
        // Ctrl+click on a selected slider's body: a new point there, in the part of its outline nearest, dragged on
        if (click && inField && Input.CtrlHeld
            && selected.FirstOrDefault(o => o.Slider && Vector2.Distance(new((float)o.X, (float)o.Y), fieldPos) > Radius && Enumerable.Range(1, o.Path.Length - 1).Any(i => SegmentDistance(fieldPos, o.Path[i - 1], o.Path[i]) <= Radius)) is { } body)
        {
            undo.Add(Snapshot()); redo.Clear();
            var pts = new List<Vector2> { new((float)body.X, (float)body.Y) };
            pts.AddRange(body.Anchors);
            int seg = Enumerable.Range(0, pts.Count - 1).MinBy(i => SegmentDistance(fieldPos, pts[i], pts[i + 1]));
            body.Anchors.Insert(seg, GridSnap(fieldPos));
            FitLength(body, retype: true);
            modified = true;
            Rebuild();
            pointDrag = (body, seg);
            return;
        }
        if (click && inField)
        {
            // the topmost visible object under the cursor (its head)
            var hit = Visible().Reverse().FirstOrDefault(o => !o.Spinner && Vector2.Distance(new((float)o.X, (float)o.Y), fieldPos) <= Radius);
            if (hit != null)
            {
                if (!selected.Contains(hit)) { if (!Input.CtrlHeld) selected.Clear(); selected.Add(hit); }
                else if (Input.CtrlHeld) { selected.Remove(hit); return; }
                undo.Add(Snapshot()); redo.Clear();
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

    static float SegmentDistance(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        float t = ab.LengthSquared() < 1e-6f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
        return Vector2.Distance(p, a + ab * t);
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

    // stable's metronome (Timing tab, while the song plays): each beat, the bar's first metronomehigh, the rest
    // metronomelow at 0.8 (in 4/4 the third at 0.6)
    bool metronome = true;
    long metronomeBeat = long.MinValue;

    void Metronome()
    {
        if (!playing || !metronome) { metronomeBeat = long.MinValue; return; }
        var red = RedAt(time);
        long beat = (long)Math.Floor((time - red.Time) / red.Beat);
        if (beat == metronomeBeat) return;
        bool next = metronomeBeat == beat - 1 || metronomeBeat > beat; // (not when it first starts mid-beat)
        metronomeBeat = beat;
        if (!next) return;
        long inBar = ((beat % red.Meter) + red.Meter) % red.Meter;
        float vol = BeatmapSamples.EffectVolume * (inBar == 0 ? 1 : red.Meter == 4 && inBar == 2 ? 0.6f : 0.8f);
        (inBar == 0 ? skin.Sound("metronomehigh") : skin.Sound("metronomelow"))?.Play(vol);
    }

    // the Timing tab's buttons (also the Timing menu's and Ctrl+P / Ctrl+Shift+P)
    void TimingAction(int i)
    {
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
    }

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
            TimingAction(i);
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
    // (mania, like stable's: Circle Size is the Key Count, 1 to 9 in whole steps, and there's no Approach Rate)
    (string Key, string Label, double Min, double Max, double Step)[] Sliders => mode != 3 ? AllSliders
        : [.. AllSliders.Where(s => s.Key != "ApproachRate").Select(s => s.Key == "CircleSize" ? ("CircleSize", "Key Count", 1.0, 9.0, 1.0) : s)];
    static readonly (string Key, string Label, double Min, double Max, double Step)[] AllSliders =
    [
        ("HPDrainRate", "HP Drain Rate", 0, 10, 0.1), ("CircleSize", "Circle Size", 0, 10, 0.1), ("ApproachRate", "Approach Rate", 0, 10, 0.1),
        ("OverallDifficulty", "Overall Difficulty", 0, 10, 0.1), ("SliderMultiplier", "Slider Velocity", 0.4, 3.6, 0.01), ("SliderTickRate", "Slider Tick Rate", 0.5, 8, 0.5),
    ];
    string fieldText = "";

    Rectangle FieldRect(int i) { float u = U; return new Rectangle(220 * u, (96 + i * 26) * u, Game.W - 440 * u, 22 * u); }
    Rectangle SliderRect(int i) { float u = U; return new Rectangle(220 * u, (320 + i * 22) * u, Game.W - 440 * u, 18 * u); }
    int sliderDragging = -1;

    // the map's mode (stable's Advanced tab: Allowed Modes)
    static readonly string[] ModeNames = ["osu!", "Taiko", "Catch the Beat", "osu!mania"];
    static Rectangle ModeRect() { float u = U; return new Rectangle(Game.W - 200 * u - 150 * u, 80 * u, 146 * u, 14 * u); }

    void UpdateSetup(Vector2 mouse, float u)
    {
        if (UpdateSetupExtras(mouse)) return;
        if (Input.IsMouseButtonPressed(MouseButton.Left) && CheckCollisionPointRec(mouse, ModeRect()))
        {
            undo.Add(Snapshot()); redo.Clear();
            mode = (mode + 1) % 4;
            SetKv("General", "Mode", mode.ToString());
            tool = Math.Min(tool, ToolNames.Length - 1);
            selected.Clear();
            info = ReloadInfo();
            Rebuild();
            return;
        }
        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            for (int i = 0; i < Fields.Length; i++)
                if (CheckCollisionPointRec(mouse, FieldRect(i))) { field = i; fieldText = Kv(Fields[i].Section, Fields[i].Key) ?? ""; return; }
            for (int i = 0; i < Sliders.Length; i++)
                if (CheckCollisionPointRec(mouse, SliderRect(i))) { sliderDragging = i; undo.Add(Snapshot()); redo.Clear(); }
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
        if ((Kv(f.Section, f.Key) ?? "") != value) { undo.Add(Snapshot()); redo.Clear(); SetKv(f.Section, f.Key, value); }
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
    Rectangle TabRect(int i) { float u = U; return new Rectangle(Game.W - (4 - i) * 92 * u, 0, 90 * u, 16 * u); }
    static readonly int[] TabOrder = [0, 3, 1, 2]; // Compose, Design, Timing, Song Setup (tab numbers)
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
            DrawTexturePro(tex, new Rectangle(0, 0, tex.Width, tex.Height), new Rectangle((w - tex.Width * s) / 2, (h - tex.Height * s) / 2, tex.Width * s, tex.Height * s), Vector2.Zero, 0, ColorAlpha(Color.White, dimBg && tab != 3 ? 0.3f : 1));
        }
        if (showVideo && tab == 0) DrawStoryboard(w, h);
        switch (tab)
        {
            case 0: DrawCompose(u); break;
            case 1: DrawTiming(u); break;
            case 2: DrawSetup(u); break;
            case 3: DrawDesign(u); break;
        }
        DrawTimeline(u);
        DrawTopBar(u);
        DrawBottomBar(u);
        if (aimod != null) DrawAiMod(u);
        if (sampleImport != null) DrawSampleImport();
        DrawMenu(u);
        if (xform != null) DrawXform(u);
        if (prompt != null) DrawPrompt(u);
        if (poly != null) DrawPoly(u);
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
        Input.MouseTaken = false; // (the cursor shows over the AiMod / Sample import windows too)
        SongSelect.DrawCursor();
    }

    // View → Show Video/Storyboard (stable's EditorVideo, on by default): the map's video and storyboard behind the
    // playfield, at the editor's time and dim
    bool showVideo = Config.GetBool("EditorVideo", true), storyboardLoaded;
    bool hitAnimations = Config.GetBool("EditorHitAnimations", false); // View → Hit animations
    bool showSampleName = true; // View → Show Sample Name (the mania view's)
    static string SampleName(EObj o)
    {
        var f = o.Sample.Split(':');
        if (f.Length > 4 && f[4].Length > 0) return f[4].LastIndexOf('.') > 0 ? f[4][..f[4].LastIndexOf('.')] : f[4];
        return string.Join('|', new[] { (2, "W"), (4, "F"), (8, "C") }.Where(s => (o.HitSound & s.Item1) != 0).Select(s => s.Item2));
    }
    Video? video;
    static readonly bool[] AllLayers = [true, true, true, true, true];
    // the map's video (the storyboard itself is the Design tab's view, as its lines stand)
    void EnsureStoryboard()
    {
        if (storyboardLoaded) return;
        storyboardLoaded = true;
        var videoPath = Path.Combine(info.Folder, info.VideoFile);
        if (info.VideoFile.Length > 0 && File.Exists(videoPath)) video = new Video(videoPath);
    }
    void DrawStoryboard(int w, int h)
    {
        EnsureStoryboard();
        float alpha = dimBg ? 0.3f : 1;
        if (video != null && time >= info.VideoOffset && !video.Failed && video.Frame(time - info.VideoOffset) is { } vt)
        {
            float s = Math.Min(w / (float)vt.Width, h / (float)vt.Height);
            DrawRectangle(0, 0, w, h, Color.Black); // (it replaces the background, like in play)
            DrawTexturePro(vt, new Rectangle(0, 0, vt.Width, vt.Height), new Rectangle((w - vt.Width * s) / 2, (h - vt.Height * s) / 2, vt.Width * s, vt.Height * s), Vector2.Zero, 0, ColorAlpha(Color.White, alpha));
        }
        SbRefresh();
        if (sbView is not { } sv) return;
        var layers = sv.ShowLayer;
        sv.ShowLayer = AllLayers; // (all of it, the pass or fail layer as in play)
        sv.BothStates = false;
        sv.Draw(time, w, h, alpha, overlay: false);
        sv.Draw(time, w, h, alpha, overlay: true);
        sv.ShowLayer = layers;
        sv.BothStates = true;
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
        // (after the menu, cut short before the tabs)
        var last = MenuTitleRect(MenuTitles.Length - 1);
        float nx = last.X + last.Width + 14 * u;
        BeginScissorMode((int)nx, 0, (int)Math.Max(0, TabRect(0).X - nx - 8 * u), (int)(17 * u));
        Game.Text(name, nx, 1 * u, 12 * u, Color.LightGray);
        EndScissorMode();
        string[] tabs = ["Compose", "Design", "Timing", "Song Setup"];
        for (int i = 0; i < 4; i++)
        {
            var r = TabRect(i);
            DrawRectangleRec(r, tab == TabOrder[i] ? new Color(255, 160, 40, 255) : new Color(60, 60, 60, 255));
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
                var c = TickColour(inBeat, divisor);
                float hgt = inBeat == 0 ? (beatIndex % p.Meter == 0 ? 1 : 0.6f) : (divisor / Gcd((int)inBeat, divisor)) switch { 2 => 0.5f, 4 or 3 or 6 => 0.4f, 8 => 0.3f, _ => 0.25f };
                float x = TimelineX(t);
                DrawRectangle((int)x, (int)(r.Y + r.Height * (1 - hgt)), Math.Max(1, (int)u), (int)(r.Height * hgt), ColorAlpha(c, 0.8f));
            }
        }
        // the objects
        var colours = Colours;
        foreach (var o in tab == 3 ? [] : objs) // (Design: the picked sprite's transformations instead)
        {
            float x0 = TimelineX(o.Time), x1 = TimelineX(o.End);
            if (x1 < r.X - 20 * u || x0 > r.X + r.Width + 20 * u) continue;
            float cy = r.Y + r.Height / 2, rad = 9 * u;
            var col = o.Spinner ? Color.Gray : ObjColour(o, colours);
            if (o.Slider || o.Spinner) DrawRectangleRounded(new Rectangle(x0 - rad, cy - rad, x1 - x0 + rad * 2, rad * 2), 1, 8, ColorAlpha(col, 0.7f));
            DrawCircleV(new(x0, cy), rad, col);
            DrawCircleLinesV(new(x0, cy), rad, selected.Contains(o) ? new Color(255, 200, 0, 255) : Color.White);
            if (selected.Contains(o)) DrawCircleLinesV(new(x0, cy), rad + 2 * u, new Color(255, 200, 0, 255));
            if (!o.Spinner) Game.Text(o.Combo.ToString(), x0 - Game.TextWidth(o.Combo.ToString(), 11 * u) / 2, cy - 6 * u, 11 * u, Color.White);
        }
        if (tab == 3) DrawDesignLanes(r, u);
        EndScissorMode();
        if (tab == 3) DrawDesignLaneLabels(r, u);
        // the current time
        DrawRectangle((int)(r.X + r.Width / 2), (int)r.Y, Math.Max(2, (int)(2 * u)), (int)r.Height, new Color(255, 255, 255, 230));
        // the beat snap divisor
        string snap = $"1/{divisor}";
        Game.TextBold("Beat snap", r.X + r.Width + 6 * u, r.Y, 11 * u, Color.White);
        Game.TextBold(snap, r.X + r.Width + 6 * u, r.Y + 14 * u, 20 * u, Color.White);
        Game.Text("Alt+wheel", r.X + r.Width + 6 * u, r.Y + 34 * u, 9 * u, Color.Gray);
    }

    static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);

    // the beat snap's colours: a beat white, then 1/2 red, 1/4 blue, 1/3 and 1/6 purple, 1/8 yellow, the rest grey
    static Color TickColour(long inBeat, int divisor) => inBeat == 0 ? Color.White : (divisor / Gcd((int)inBeat, divisor)) switch
    {
        2 => new Color(255, 70, 70, 255), 4 => new Color(80, 130, 255, 255), 3 or 6 => new Color(190, 90, 255, 255), 8 => new Color(255, 220, 60, 255), _ => Color.Gray,
    };

    // stable colours a taiko map's objects by what they'll be: a don red, a kat (whistle or clap) blue, a drumroll yellow
    Color ObjColour(EObj o, Color[] colours) => mode != 1 ? colours[o.Colour % colours.Length]
        : o.Slider ? new Color(252, 184, 6, 255) : (o.HitSound & 10) != 0 ? new Color(67, 142, 172, 255) : new Color(235, 69, 44, 255);

    void DrawCompose(float u)
    {
        if (mode == 3) DrawMania(u); else DrawPlayfield(u);
        DrawToolbox(u);
    }

    void DrawPlayfield(float u, bool objectsOnly = false)
    {
        var o0 = FieldOrigin;
        float fs = FieldScale;
        if (!objectsOnly) DrawRectangleLinesEx(new Rectangle(o0.X, o0.Y, 512 * fs, 384 * fs), 1, ColorAlpha(Color.White, 0.3f));
        if (gridSnap && !objectsOnly)
            for (int gx = 0; gx <= 512; gx += gridSize) DrawLineV(ToScreen(gx, 0), ToScreen(gx, 384), ColorAlpha(Color.White, gx == 256 ? 0.25f : 0.08f));
        if (gridSnap && !objectsOnly)
            for (int gy = 0; gy <= 384; gy += gridSize) DrawLineV(ToScreen(0, gy), ToScreen(512, gy), ColorAlpha(Color.White, gy == 192 ? 0.25f : 0.08f));

        var colours = Colours;
        float radius = Radius, cs = radius / 64 * fs;
        if (followPoints) DrawFollowPoints(fs);
        double pre = Preempt;
        var vis = Visible().ToList();
        for (int k = vis.Count - 1; k >= 0; k--)
        {
            var o = vis[k];
            // past its end it fades over 800ms, or (Hit animations) like a hit in play: out over 240ms, growing to 1.4x
            float alpha = time < o.Time - pre + 400 ? (float)((time - (o.Time - pre)) / 400) : time > o.End ? 1 - (float)((time - o.End) / (hitAnimations ? 240 : 800)) : 1;
            float hitScale = hitAnimations && time > o.End ? 1 + 0.4f * (float)Math.Min(1, (time - o.End) / 240) : 1;
            alpha = Math.Clamp(alpha, 0, 1);
            var col = ObjColour(o, colours);
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
                    // along each segment of the path (a straight one is only its two ends)
                    float carry = 0;
                    for (int i = 1; i < o.Path.Length; i++)
                    {
                        Vector2 a = o.Path[i - 1], b = o.Path[i];
                        float d = Vector2.Distance(a, b), t = carry;
                        for (; t < d; t += step) { var pt = Vector2.Lerp(a, b, t / d); DrawCircleV(ToScreen(pt.X, pt.Y), pass.Item1 * fs, pass.Item2); }
                        carry = t - d;
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
            DrawHitCircle(new((float)o.X, (float)o.Y), o.Combo, col, alpha, cs * hitScale, selected.Contains(o), number: true);
            if (time < o.Time)
            {
                float a = (float)((o.Time - time) / pre);
                skin.Tex("approachcircle")?.Draw(ToScreen(o.X, o.Y), cs * (1 + 3 * a), ColorAlpha(col, alpha));
            }
            else if (o.Circle && !hitAnimations) // (a circle's approach circle stays, opening to 1.1x over 100ms)
                skin.Tex("approachcircle")?.Draw(ToScreen(o.X, o.Y), cs * (1 + 0.1f * (float)Math.Min(1, (time - o.Time) / 100)), ColorAlpha(col, alpha));
        }
        // the selected (visible) sliders' points: joined by lines, a square on each (red where two meet: a segment's end)
        foreach (var o in vis)
        {
            if (!o.Slider || !selected.Contains(o)) continue;
            var pts = new List<Vector2> { new((float)o.X, (float)o.Y) };
            pts.AddRange(o.Anchors);
            for (int i = 1; i < pts.Count; i++) DrawLineV(ToScreen(pts[i - 1].X, pts[i - 1].Y), ToScreen(pts[i].X, pts[i].Y), ColorAlpha(Color.White, 0.6f));
            for (int i = 0; i < pts.Count; i++)
            {
                bool red = i > 0 && pts[i] == pts[i - 1] || i + 1 < pts.Count && pts[i] == pts[i + 1];
                var s = ToScreen(pts[i].X, pts[i].Y);
                DrawRectangleV(s - new Vector2(3 * u), new Vector2(6 * u), red ? Color.Red : Color.White);
                DrawRectangleLinesEx(new Rectangle(s.X - 3 * u, s.Y - 3 * u, 6 * u, 6 * u), 1, Color.Black);
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
    }

    void DrawToolbox(float u)
    {
        // the toolbox and the toggles
        for (int i = 0; i < ToolNames.Length; i++) DrawButton(ToolRect(i), $"{i + 1}. {ToolNames[i]}", tool == i);
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
        {
            if (mode == 2)
                foreach (int side in new[] { -1, 1 }) DrawLineV(ToScreen(ring.From.X + side * ring.Radius, 0), ToScreen(ring.From.X + side * ring.Radius, 384), ColorAlpha(Color.White, 0.5f));
            else DrawCircleLinesV(ToScreen(ring.From.X, ring.From.Y), ring.Radius * FieldScale, ColorAlpha(Color.White, 0.5f));
        }
        Game.Text($"{selected.Count} selected", 4 * u, 230 * u, 10 * u, Color.LightGray);
    }

    // ---- mania: stable's own compose view. The map's columns as a stage (30 wide, from x 160) scrolling through the
    // beat snap's lines, a twelfth of the timeline's span below the judgement line (now) and a third above; beside it
    // the whole song's note density in a hundred slices (yellow, pink past 0.6 notes a column per half beat) under a
    // marker of what the stage shows. Notes are stable's default mania-note1 at 0.1875 tinted by column (white, pink,
    // the middle yellow; selected sky blue), holds with the note1L body between their ends.
    const float MTop = 66, MBottom = 458, MLeft = 160, MStrip = 80;
    EObj? holdDraft; double holdFrom; // the hold being drawn: its other end follows the cursor
    (double Time, int Column, Dictionary<EObj, (double Time, double End, int Column)> Start)? mDrag;
    (float X, float Y, double Time)? mBox;
    int MKeys => Math.Clamp((int)Math.Round(double.TryParse(Kv("Difficulty", "CircleSize"), NumberStyles.Float, Inv, out double cs) ? cs : 4), 1, 18);
    static float MColW(int keys) => Math.Min(30, (Game.W / U - MLeft - 70) / keys);
    double MFrom => time - 6000 / zoom / 12;
    double MTo => time + 6000 / zoom / 3;
    float MY(double t) => MBottom - (float)((t - MFrom) / (MTo - MFrom)) * (MBottom - MTop);
    double MTime(float y) => MFrom + (MBottom - y) / (MBottom - MTop) * (MTo - MFrom);
    static int MColumn(EObj o, int keys) => Math.Clamp((int)(o.X * keys / 512), 0, keys - 1);
    static double MColumnX(int c, int keys) => (int)((c + 0.5) * 512 / keys);

    // a note's sprite (bottom on its time) in 480-units
    Rectangle MNote(int c, double t, float colW)
    {
        var n = skin.DefaultTex("mania-note1");
        float w = (n?.Width ?? 256) * 0.1875f / 1.6f * colW / 30, h = (n?.Height ?? 82) * 0.1875f / 1.6f;
        return new Rectangle(MLeft + colW * c, MY(t) - h, w, h);
    }

    // what's under the cursor: a note (its head or body), or a hold's tail
    (EObj? Obj, bool Tail) MHit(float mx, float my, int keys, float colW)
    {
        var at = new Vector2(mx, my);
        for (int i = objs.Count - 1; i >= 0; i--)
        {
            var o = objs[i];
            if (o.End < MFrom - 1000 || o.Time > MTo + 1000) continue;
            int c = MColumn(o, keys);
            bool hold = (o.Type & 128) != 0;
            if (hold && CheckCollisionPointRec(at, MNote(c, o.End, colW))) return (o, true);
            if (CheckCollisionPointRec(at, MNote(c, o.Time, colW))) return (o, false);
            if (hold && mx >= MLeft + colW * c && mx < MLeft + colW * (c + 1) && my <= MY(o.Time) && my >= MY(o.End)) return (o, false);
        }
        return (null, false);
    }

    void UpdateMania(Vector2 mouse, bool click, bool held)
    {
        float u = U, mx = mouse.X / u, my = mouse.Y / u;
        int keys = MKeys;
        float colW = MColW(keys);
        int col = mx > MLeft ? (int)((mx - MLeft) / colW) : -1;
        bool inStage = col >= 0 && col < keys && my > MTop && my < MBottom;

        // a hold being drawn follows the cursor; let go, both ends snap (and with no length left it's a note)
        if (holdDraft is { } hd)
        {
            double t = MTime(my);
            hd.Time = Math.Min(holdFrom, t); hd.End = Math.Max(holdFrom, t); hd.Raw = null; modified = true;
            if (!held)
            {
                hd.Time = Snap(hd.Time); hd.End = Snap(hd.End);
                if (hd.End <= hd.Time) { hd.Type = (hd.Type & ~128) | 1; hd.End = hd.Time; }
                holdDraft = null;
                Rebuild();
            }
            return;
        }
        // the selection dragged: in time (snapped) and across the columns
        if (mDrag is { } d)
        {
            if (!held) { mDrag = null; Rebuild(); return; }
            double first = d.Start.Values.Min(s => s.Time);
            double shift = Snap(first + MTime(my) - d.Time) - first;
            int lo = d.Start.Values.Min(s => s.Column), hi = d.Start.Values.Max(s => s.Column);
            int dc = Math.Clamp((mx > MLeft ? (int)((mx - MLeft) / colW) : -1) - d.Column, -lo, keys - 1 - hi);
            foreach (var (o, s) in d.Start)
            {
                double nx = MColumnX(s.Column + dc, keys);
                if (Math.Abs(o.Time - (s.Time + shift)) < 0.5 && o.X == nx) continue;
                o.Time = s.Time + shift; o.End = s.End + shift; o.X = nx; o.Raw = null; modified = true;
            }
            return;
        }
        // a box: in the stage, the columns and times it covers; in the density strip, the part of the song
        if (mBox is { } b)
        {
            if (held) return;
            bool strip = b.X > MStrip && b.X < MStrip + 60 && mx > MStrip && mx < MStrip + 60;
            double t0 = strip ? (MBottom - b.Y) / (MBottom - MTop) * length : b.Time, t1 = strip ? (MBottom - my) / (MBottom - MTop) * length : MTime(my);
            int c0 = (int)Math.Floor((Math.Min(b.X, mx) - MLeft) / colW), c1 = (int)Math.Floor((Math.Max(b.X, mx) - MLeft) / colW);
            foreach (var o in objs)
                if (o.Time >= Math.Min(t0, t1) && o.Time <= Math.Max(t0, t1) && (strip || MColumn(o, keys) >= c0 && MColumn(o, keys) <= c1)) selected.Add(o);
            mBox = null;
            return;
        }

        var (hit, tail) = inStage ? MHit(mx, my, keys, colW) : (null, false);
        // a right click deletes the note under the cursor (the selection, if it's in it)
        if (Input.IsMouseButtonPressed(MouseButton.Right) && hit != null)
        {
            Change(() => { if (selected.Contains(hit)) { objs.RemoveAll(selected.Contains); selected.Clear(); } else objs.Remove(hit); });
            return;
        }
        if (!click) return;
        if (!inStage && !(mx > MStrip && mx < MStrip + 60 && my > MTop && my < MBottom)) return;
        undo.Add(Snapshot()); redo.Clear();
        if (tool == 0)
        {
            if (hit != null && (tail || Input.ShiftHeld))
            {
                // a hold's tail drags its end; Shift on a note makes it a hold from there
                if (!tail) { hit.Type = (hit.Type & ~1) | 128; hit.End = hit.Time; hit.Raw = null; }
                holdDraft = hit; holdFrom = hit.Time;
                return;
            }
            if (hit != null)
            {
                if (!selected.Contains(hit)) { if (!Input.CtrlHeld) selected.Clear(); selected.Add(hit); }
                else if (Input.CtrlHeld) { selected.Remove(hit); undo.RemoveAt(undo.Count - 1); return; }
                mDrag = (MTime(my), col, selected.ToDictionary(o => o, o => (o.Time, o.End, MColumn(o, keys))));
                return;
            }
            undo.RemoveAt(undo.Count - 1);
            if (!Input.CtrlHeld) selected.Clear();
            mBox = (mx, my, MTime(my));
            return;
        }
        if (!inStage) { undo.RemoveAt(undo.Count - 1); return; }
        var n = new EObj { X = MColumnX(col, keys), Y = 192, Time = Snap(MTime(my)), Type = tool == 2 ? 128 : 1, HitSound = soundNext };
        n.End = n.Time;
        if (setsNext != (0, 0)) SetSets(n, setsNext.Normal, setsNext.Addition);
        objs.RemoveAll(o => MColumn(o, keys) == col && Math.Abs(o.Time - n.Time) < 1);
        objs.Add(n);
        selected.Clear();
        modified = true;
        if (tool == 2) { holdDraft = n; holdFrom = n.Time; }
        else Rebuild();
    }

    // stable's "main" BPM: the one the song spends longest in
    double MainBpm()
    {
        var reds = timing.Where(p => p.Red && p.Beat > 0).OrderBy(p => p.Time).ToList();
        return reds.Count == 0 ? 120 : reds.Select((p, i) => (p.Bpm, Len: (i + 1 < reds.Count ? reds[i + 1].Time : length) - p.Time)).MaxBy(x => x.Len).Bpm;
    }

    void DrawMania(float u)
    {
        int keys = MKeys;
        float colW = MColW(keys), h = MBottom - MTop, stageW = keys * colW;
        Rectangle R(float x, float y, float w, float hh) => new(x * u, y * u, w * u, hh * u);

        // the density strip
        DrawRectangleRec(R(MStrip, MTop, 60, h), ColorAlpha(Color.Black, 0.95f));
        var counts = new int[100];
        double slice = Math.Ceiling(length / 100);
        foreach (var o in objs)
        {
            int a = (int)(o.Time / slice), z = (int)(o.End / slice);
            if (a < 0 || a >= 100) continue;
            for (int k = a; k <= z && k < 100; k++) counts[k]++;
        }
        int dense = Math.Max(1, (int)(slice / (60000 / MainBpm() / 2) * keys * 0.6));
        for (int k = 0; k < 100; k++)
            if (counts[k] > 0)
                DrawRectangleRec(R(MStrip, MBottom - h / 100 * (k + 1) + 1, 60f / dense * Math.Min(dense, counts[k]), h / 100 - 1), counts[k] > dense ? new Color(250, 170, 212, 255) : new Color(247, 236, 0, 255));
        float y7 = Math.Min(MBottom, MBottom - (float)(MFrom / length) * h), y8 = Math.Max(MTop, MBottom - (float)(MTo / length) * h);
        DrawRectangleRec(R(MStrip - 2, y8, 62, y7 - y8), ColorAlpha(Color.White, 0.5f));

        // the stage, the beat snap's lines (the current timing section's; a bar's first 3 tall), the columns
        DrawRectangleRec(R(MLeft - 1, MTop, stageW + 1, h), Color.Black);
        var red = RedAt(time);
        double step = red.Beat / divisor;
        if (step > 1e-5)
            for (long n = (long)Math.Ceiling((MFrom - red.Time) / step), last = (long)Math.Floor((MTo - red.Time) / step); n <= last; n++)
            {
                long inBeat = ((n % divisor) + divisor) % divisor, beat = (long)Math.Floor(n / (double)divisor);
                bool bar = inBeat == 0 && ((beat % red.Meter) + red.Meter) % red.Meter == 0;
                DrawRectangleRec(R(MLeft, MY(red.Time + n * step) - (bar ? 1 : 0), stageW, bar ? 3 : 1), TickColour(inBeat, divisor));
            }
        for (int i = 0; i <= keys; i++) DrawRectangleRec(R(MLeft + colW * i, MTop, 1, h), Color.White);
        foreach (float x in new[] { MStrip, MStrip + 60 }) DrawRectangleRec(R(x, MTop, 1, h), Color.White);
        DrawRectangleRec(R(MLeft, MBottom - h / 5, stageW, 4), new Color(173, 255, 47, 255));

        // the notes
        var note = skin.DefaultTex("mania-note1");
        var body = skin.DefaultTex("mania-note1L-0") ?? skin.DefaultTex("mania-note1L");
        void Note(Rectangle r, Color c) { if (note is { } s) DrawTexturePro(s.Tex, new Rectangle(0, 0, s.Tex.Width, s.Tex.Height), R(r.X, r.Y, r.Width, r.Height), Vector2.Zero, 0, c); }
        foreach (var o in objs)
        {
            if (o.End < MFrom || o.Time > MTo) continue;
            int c = MColumn(o, keys);
            var tint = selected.Contains(o) ? new Color(135, 206, 250, 255) : Player.ManiaColumnKind(c, keys) switch { "2" => new Color(250, 170, 212, 255), "S" => new Color(247, 236, 0, 255), _ => Color.White };
            bool hold = (o.Type & 128) != 0;
            if (hold && body is { } bd)
            {
                float y0 = Math.Min(MBottom, MY(o.Time)), y1 = Math.Max(MTop, MY(o.End));
                DrawTexturePro(bd.Tex, new Rectangle(0, 0, bd.Tex.Width, bd.Tex.Height), R(MLeft + colW * c, y1, bd.Width * 0.1875f / 1.6f * colW / 30, y0 - y1), Vector2.Zero, 0, tint);
            }
            if (o.Time >= MFrom && o.Time < MTo) Note(MNote(c, o.Time, colW), tint);
            if (hold && o.End >= MFrom && o.End < MTo) Note(MNote(c, o.End, colW), tint);
            // View → Show Sample Name: the note's own sample file (without its extension), else its additions
            if (showSampleName && o.Time > MFrom && o.Time < MTo && SampleName(o) is { Length: > 0 } name)
                Game.Text(name, (MLeft + colW * c) * u, (MY(o.Time) - 10) * u, 8 * u, Color.White);
        }
        // the note about to be placed, at the cursor
        var m = Input.GetMousePosition() / u;
        if (tool > 0 && holdDraft == null && m.X > MLeft && m.X < MLeft + stageW && m.Y > MTop && m.Y < MBottom)
        {
            var r = MNote(0, time, colW);
            Note(new Rectangle(m.X - r.Width / 2, m.Y - r.Height, r.Width, r.Height), Color.White);
        }
        if (mBox is { } b)
        {
            var a = new Vector2(b.X, b.X > MStrip && b.X < MStrip + 60 ? b.Y : MY(b.Time)) * u;
            var e = Input.GetMousePosition();
            DrawRectangleLinesEx(new Rectangle(Math.Min(a.X, e.X), Math.Min(a.Y, e.Y), Math.Abs(e.X - a.X), Math.Abs(e.Y - a.Y)), 1, Color.White);
        }
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
        var mr = ModeRect();
        DrawRectangleRec(mr, new Color(40, 40, 40, 255));
        Game.Text($"Mode: {ModeNames[Math.Clamp(mode, 0, 3)]} (click to change)", mr.X + 4 * u, mr.Y + 1 * u, 11 * u, Color.White);
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
        DrawSetupExtras(u);
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
        foreach (int b in Bookmarks()) DrawRectangle((int)(r.X + r.Width * b / length), (int)(r.Y + r.Height / 2), Math.Max(1, (int)u), (int)(r.Height / 2), new Color(60, 140, 255, 255));
        float x = r.X + r.Width * (float)(time / length);
        DrawRectangle((int)x - (int)u, (int)r.Y, (int)(3 * u), (int)r.Height, Color.White);
        string[] t3 = ["Play", playing ? "Pause" : "Paused", "Stop"];
        for (int i = 0; i < 3; i++) DrawButton(TransportRect(i), t3[i][..1] == "P" && i == 1 ? "||" : i == 0 ? " >" : " []", i == 1 && !playing);
        for (int i = 0; i < 4; i++) DrawButton(RateRect(i), $"{Rates[i] * 100:0}%", rate == Rates[i]);
    }
}
