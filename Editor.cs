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
// the wheel steps through the beat snap (Ctrl changes the divisor, Alt the distance spacing, Ctrl+Shift zooms);
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
        public int Stack; // (stable's stacking, as the editor shows it)
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
    static Skin skin => Game.Skin; // (the default one with EditorDefaultSkin)
    bool modified;

    // ---- editing state
    int tab; // 0 compose, 1 timing, 2 song setup
    int tool = 1; // 0 select, 1 circle, 2 slider, 3 spinner (mania: 0 select, 1 note, 2 hold)
    int mode; // the map's (General: Mode)
    int divisor = 4;
    static readonly int[] Divisors = [1, 2, 3, 4, 6, 8, 12, 16];
    internal bool TakesAltWheel => tab == 0; // (Compose: Alt+wheel changes the distance spacing, not the volume)
    int gridSize = 32;
    bool gridSnap = true;
    float zoom = 1; // stable's timeline zoom ([Editor] TimelineZoom, 0.1 - 8): the timeline spans 6000 / zoom ms
    bool zoomChanged; // (written back on save only then, keeping an unchanged save as it was)
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
    string? exitPrompt; double exitAt;
    double loadedAt = Game.NowMs;

    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    // stable's editor: a native menu strip (24px at 100% scaling) above the game area, which is the rest of the window
    // (stable takes the strip's height off the game's and makes the window taller); everything else is drawn and
    // clicked in the game area's coordinates
    static float Strip
    {
        get
        {
            // (the DPI asked of the window only when its size changes: everything here measures in U, many times a frame)
            if (stripFor != Game.H) { stripFor = Game.H; strip = MathF.Round(24 * Math.Max(1, GetWindowScaleDPI().Y)); }
            return strip;
        }
    }
    static float strip;
    static int stripFor = -1;
    static float EH => Game.H - Strip;
    static Vector2 RawMouse => Input.GetMousePosition();
    static Vector2 EMouse => RawMouse - new Vector2(0, Strip);
    static float U => EH / 480f;
    // a tooltip at the cursor, placed on the screen (kept inside it) from within the game area's drawing
    static void Tip(string text)
    {
        Rlgl.PopMatrix();
        Options.DrawScreenTooltip(text, RawMouse);
        Rlgl.PushMatrix();
        Rlgl.Translatef(0, Strip, 0);
    }
    static KeyboardKey Bound(string key, KeyboardKey def) => Config.Key(key, def); // stable's editor bindings (Options)
    float FieldScale => fieldAsPlay ? U : 0.9f * U;
    Vector2 FieldOrigin => fieldAsPlay ? new((Game.W - 512 * U) / 2, (EH - 384 * U) / 2 + 8 * U) : new((Game.W - 512 * FieldScale) / 2, 78 * U);
    bool fieldAsPlay; // (Design's Hit Objects: the playfield where play has it, over the storyboard)
    Vector2 ToScreen(double x, double y) => FieldOrigin + (new Vector2((float)x, (float)y) + stackShift) * FieldScale;
    Vector2 stackShift; // (while an object is drawn: its stack's offset)
    Vector2 ToField(Vector2 s) => (s - FieldOrigin) / FieldScale;

    public Editor(string file, SongSelect back)
    {
        path = file;
        this.back = back;
        info = Beatmap.Load(file);
        samples = new BeatmapSamples(info.Folder);
        Read(File.ReadAllLines(file));
        LoadOsb();
        if (timing.Count == 0) tab = 1; // (stable's: an untimed song opens in Timing)
        mode = int.TryParse(Kv("General", "Mode"), out int m) ? m : 0;
        music = Track.Load(Path.Combine(info.Folder, info.AudioFilename), tempo: true);
        if (music != null) { music.Volume = Config.MusicVolume; length = Math.Max(1, music.LengthMs); }
        else length = Math.Max(1, objs.Count > 0 ? objs[^1].End + 2000 : 60000);
        if (Kv("Editor", "BeatDivisor") is { } bd && int.TryParse(bd, out int d) && Divisors.Contains(d)) divisor = d;
        if (Kv("Editor", "GridSize") is { } gs && int.TryParse(gs, out int g) && g is 4 or 8 or 16 or 32) gridSize = g;
        if (float.TryParse(Kv("Editor", "TimelineZoom"), NumberStyles.Float, Inv, out float tz)) zoom = Math.Clamp(tz, 0.1f, 8);
        time = objs.Count > 0 ? objs[0].Time : 0;
        music?.Seek(time);
        Rebuild();
        if (KeepBreaks()) info = ReloadInfo(); // (stable's breaks for the map, not a change)
    }

    void Read(string[] lines)
    {
        drag = null; pointDrag = null; timeDrag = null; endDrag = null; boxFrom = null; holdDraft = null; mDrag = null; mBox = null; breakDrag = null;
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

    // (missing fields: the map's default sample set and custom set 1, like the game reads them)
    ETiming? ParseTiming(string line)
    {
        var f = line.Split(',');
        if (f.Length < 2) return null;
        try
        {
            return new ETiming
            {
                Time = double.Parse(f[0], Inv), Beat = double.Parse(f[1], Inv),
                Meter = f.Length > 2 && int.Parse(f[2], Inv) is > 0 and var mt ? mt : 4, Set = f.Length > 3 ? int.Parse(f[3], Inv) : Kv("General", "SampleSet") switch { "Soft" => 2, "Drum" => 3, _ => 1 },
                Index = f.Length > 4 ? int.Parse(f[4], Inv) : 1, Volume = f.Length > 5 ? int.Parse(f[5], Inv) : 100,
                Red = f.Length <= 6 || f[6].Trim() == "1", Effects = f.Length > 7 ? int.Parse(f[7], Inv) : 0, Raw = line,
            };
        }
        catch (Exception e) when (e is FormatException or OverflowException or IndexOutOfRangeException) { return null; } // (dropped, like stable)
    }

    static string Num(double v) => v.ToString("G15", Inv); // (stable writes doubles to 15 significant digits)

    string WriteTiming(ETiming t) =>
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

    void Save() => TrySave();
    bool TrySave()
    {
        SetKv("Editor", "BeatDivisor", divisor.ToString(Inv));
        SetKv("Editor", "GridSize", gridSize.ToString(Inv));
        if (zoomChanged) SetKv("Editor", "TimelineZoom", zoom.ToString("G7", Inv));
        try
        {
            File.WriteAllLines(path, Write(), new UTF8Encoding(false));
            // the .osb, if the Design tab changed it (one undone back to nothing keeps just its [Events])
            if (osbModified && !osbUnreadable && (osbLines != null || osbPath != null && File.Exists(osbPath)))
                File.WriteAllLines(osbPath ??= NewOsbPath(), osbLines ?? ["[Events]"], new UTF8Encoding(false));
            osbModified = false;
            modified = false;
            Notification.Show("Saved!", 1500);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { Notification.Show($"Couldn't save: {e.Message}", 3000); return false; }
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
        var sorted = timing.OrderBy(p => p.Time).ToList();
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
                o.End = SliderEnd(o, sorted);
            }
            if (o.Spinner) { force = true; continue; }
            if (o.NewCombo || force || combo == 0) { combo = 1; colour += 1 + ((o.Type >> 4) & 7); force = false; }
            else combo++;
            o.Combo = combo;
            o.Colour = colour;
        }
        foreach (var o in objs) o.Stack = 0;
        if (mode == 0 && stacking) ApplyStacking();
    }

    // View → Stacking (the options' too): stable's stacking as in a play - objects at (almost) one spot within
    // StackLeniency x preempt pushed up-left by a tenth of the radius per level
    bool stacking = Config.GetBool("EditorStacking", true), snakingSliders = Config.GetBool("EditorSnakingSliders", true);
    static Vector2 PosOf(EObj o) => new((float)o.X, (float)o.Y);
    static Vector2 EndPosOf(EObj o) => o.Slider && o.Path.Length > 0 ? (o.Slides % 2 == 1 ? o.Path[^1] : o.Path[0]) : PosOf(o);
    Vector2 StackShift(EObj o) { float off = o.Stack * (Radius * 1.00041f / 10f); return new(-off, -off); }
    Vector2 StackedPos(EObj o) => PosOf(o) + StackShift(o);
    // the object (head) under the cursor: of those it's over, the nearest where it's drawn (in a stack, the circle
    // clicked on), the earliest (drawn on top) when level
    EObj? ObjectAt(Vector2 fieldPos) => Visible().Where(o => !o.Spinner && Vector2.Distance(StackedPos(o), fieldPos) <= Radius)
        .OrderBy(o => Vector2.Distance(StackedPos(o), fieldPos)).ThenBy(o => o.Time).FirstOrDefault();
    void ApplyStacking()
    {
        float threshold = (float)Preempt * (float)(double.TryParse(Kv("General", "StackLeniency"), NumberStyles.Float, Inv, out double sl) ? sl : 0.7);
        const float dist = 3;
        for (int i = objs.Count - 1; i > 0; i--)
        {
            var oi = objs[i];
            if (oi.Stack != 0 || oi.Spinner) continue;
            int n = i;
            if (oi.Circle)
            {
                while (--n >= 0)
                {
                    var on = objs[n];
                    if (on.Spinner) continue;
                    if ((float)(oi.Time - on.End) > threshold) break;
                    if (on.Slider && Vector2.Distance(EndPosOf(on), PosOf(oi)) < dist)
                    {
                        int offset = oi.Stack - on.Stack + 1;
                        for (int j = n + 1; j <= i; j++)
                            if (Vector2.Distance(EndPosOf(on), PosOf(objs[j])) < dist) objs[j].Stack -= offset;
                        break;
                    }
                    if (Vector2.Distance(PosOf(on), PosOf(oi)) < dist) { on.Stack = oi.Stack + 1; oi = on; }
                }
            }
            else
            {
                while (--n >= 0)
                {
                    var on = objs[n];
                    if (on.Spinner) continue;
                    if ((float)(oi.Time - on.Time) > threshold) break;
                    if (Vector2.Distance(EndPosOf(on), PosOf(oi)) < dist) { on.Stack = oi.Stack + 1; oi = on; }
                }
            }
        }
    }

    // a slider's end as the game has it (stable's): its segments' times added up over every span, in whole ms
    double SliderEnd(EObj o, List<ETiming> sorted)
    {
        var red = RedAt(o.Time);
        var p = sorted.LastOrDefault(q => q.Time <= o.Time) ?? sorted.FirstOrDefault();
        double sv = p is { Red: false } && p.Time >= red.Time && p.Beat < 0 ? Math.Clamp((float)-p.Beat, 10f, 1000f) / 100.0 : 1;
        double tick = double.TryParse(Kv("Difficulty", "SliderTickRate"), NumberStyles.Float, Inv, out double tr) ? tr : 1;
        double beat = red.Beat * sv, dist = 100.0 * SliderMultiplier / tick, velocity = beat > 0 ? dist * tick * (1000.0 / beat) : dist * tick;
        double clock = o.Time;
        for (int span = 0; span < o.Slides; span++)
            for (int j = 1; j < o.Path.Length; j++)
            {
                int k = span % 2 == 1 ? o.Path.Length - j : j;
                clock += 1000.0 * SliderPath.Len(o.Path[k - 1], o.Path[k]) / velocity;
            }
        return (int)clock;
    }

    // A change: the state before it goes on the undo stack.
    void Change(Action edit, bool undoable = true)
    {
        string before = Snapshot();
        if (undoable)
        {
            undo.Add(before);
            if (undo.Count > 100) undo.RemoveAt(0);
            redo.Clear();
        }
        edit();
        Rebuild();
        KeepBreaks();
        modified = true;
        tapUndoAt = double.NegativeInfinity;
        // info (timing, breaks, colours, files: the editor takes no objects from it) reloaded only when something
        // before the hit objects changed - a big map's reload takes tens of ms
        var lines = Write();
        int head = before.IndexOf("\n[HitObjects]", StringComparison.Ordinal);
        if (head < 0 || !before.AsSpan(0, head).SequenceEqual(string.Join("\n", lines.TakeWhile(l => l != "[HitObjects]"))))
            info = ReloadInfo(lines);
    }

    void Restore(List<string> from, List<string> to)
    {
        if (from.Count == 0) return;
        string now = Snapshot();
        to.Add(now);
        string state = from[^1];
        from.RemoveAt(from.Count - 1);
        Load(now, state);
    }

    void Load(string now, string state)
    {
        int osb = state.IndexOf(OsbMark, StringComparison.Ordinal); // (which .osb, after the .osu's lines)
        RestoreOsb(osb < 0 ? "-1" : state[(osb + OsbMark.Length)..]);
        Read((osb < 0 ? state : state[..osb]).Split('\n'));
        mode = int.TryParse(Kv("General", "Mode"), out int m) ? m : 0; // (an undone mode or difficulty change)
        tool = Math.Min(tool, ToolNames.Length - 1);
        tapUndoAt = double.NegativeInfinity;
        int h0 = now.IndexOf("\n[HitObjects]", StringComparison.Ordinal), h1 = state.IndexOf("\n[HitObjects]", StringComparison.Ordinal);
        if (h0 < 0 || h0 != h1 || string.CompareOrdinal(now, 0, state, 0, h0) != 0) info = ReloadInfo(); // (as in Change)
        selected.Clear();
        modified = true;
        Rebuild();
        NoteBreaks();
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

    double Preempt => (int)Beatmap.Range(Diff("ApproachRate") ?? Diff("OverallDifficulty") ?? 5, 1800, 1200, 450); // (whole ms, like stable's)
    double? Diff(string key) => float.TryParse(Kv("Difficulty", key), NumberStyles.Float, Inv, out float v) ? Math.Clamp(v, 0f, 10f) : null; // (read as the game reads it)
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
        var raw = RawMouse; // (the menu strip and the exit prompt: the screen's; the rest: the game area's)
        var mouse = EMouse;
        float u = U;
        KeepBreaksAfterDrag();
        // the clock (also under a window or the menu: the song plays on, and Use Current Time takes where it is)
        if (music != null && playing)
        {
            if (music.Stopped) { playing = false; time = length; }
            else time = music.PositionMs;
        }
        else if (music == null && playing) time = Math.Min(length, time + dtMs * rate);
        if (exitPrompt != null || xform != null || prompt != null || bmsWindow != null || poly != null || setupDlg != null || timingDlg != null) lastSoundTime = double.NaN; // (the clock stops under a window: its hitsounds aren't all played after)
        if (exitPrompt != null) { UpdateExitPrompt(raw); return; }
        if (xform != null) { UpdateXform(mouse); return; }
        if (prompt != null) { UpdatePrompt(mouse); return; }
        if (setupDlg != null) { UpdateSetupDialog(mouse); return; }
        if (timingDlg != null) { UpdateTimingDialog(mouse); return; }
        if (bmsWindow != null) { UpdateBmsImport(mouse); return; }
        if (poly != null) { UpdatePoly(mouse); return; }
        if (UpdateMenu(raw)) return;
        if (raw.Y < Strip && (Input.IsMouseButtonPressed(MouseButton.Left) || Input.IsMouseButtonPressed(MouseButton.Right))) return; // (the strip's empty part takes its clicks)
        // (the AiMod and Sample import windows: the editor below keeps its keys while they have the mouse)
        // (not during a drag begun in the editor: that keeps the mouse until it's let go)
        bool press = Input.IsMouseButtonPressed(MouseButton.Left);
        if (press) windowPress = false;
        bool editorDrag = Input.IsMouseButtonDown(MouseButton.Left) && !press && !windowPress;
        if (!editorDrag && (volumeWindow != null && UpdateVolumeWindow(mouse) || sampleImport != null && UpdateSampleImport(mouse) || aimod != null && UpdateAiMod(mouse)))
        {
            windowPress |= press;
            Input.MouseTaken = true;
            mouse = EMouse;
        }

        PlayHitsounds();

        if (Input.IsKeyPressed(KeyboardKey.Escape))
        {
            if (tab == 3 && sbColourPick) sbColourPick = false;
            else if (tab == 3 && sbLibrary) sbLibrary = false;
            else if (tab == 3 && sbSel != null) sbSel = null;
            else if (sliderDraft != null) sliderDraft = null;
            else if (selected.Count > 0) selected.Clear();
            else AskExit();
            return;
        }
        if (Input.IsKeyPressed(KeyboardKey.F1)) SetTab(0);
        if (Input.IsKeyPressed(KeyboardKey.F2)) SetTab(3);
        if (Input.IsKeyPressed(KeyboardKey.F3)) SetTab(1);
        if (Input.IsKeyPressed(KeyboardKey.F4)) SetTab(2);
        if (Input.IsKeyPressed(KeyboardKey.F6)) OpenTimingDialog(); // (stable's Timing Setup Panel)
        if (Input.IsKeyPressed(KeyboardKey.F5)) { TestPlay(); return; }
        if (Input.CtrlHeld && !Input.ShiftHeld && Input.IsKeyPressed(KeyboardKey.S)) Save();
        if (Input.CtrlHeld && Input.IsKeyPressed(KeyboardKey.L)) { RevertPrompt(full: Input.ShiftHeld); return; }
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
                // Ctrl+B adds one here, Ctrl+Shift+B removes the nearest (stable's bookmark buttons)
                if (Input.ShiftHeld) RemoveBookmark(); else AddBookmark();
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
            // stable's: Ctrl doubles / halves the beat snap divisor, Alt changes the distance spacing (0.1, Shift 0.01), Shift
            // steps 4 snaps; (Ctrl+Shift zooms the timeline: stable's wheel doesn't)
            if (Input.CtrlHeld && Input.ShiftHeld) Zoom(wheel > 0 ? 0.1f : -0.1f);
            else if (Input.CtrlHeld) divisor = wheel > 0 ? Math.Min(16, divisor * 2) : Math.Max(1, divisor / 2);
            else if (Input.AltHeld) // (outside Compose the volume takes it)
            {
                if (TakesAltWheel)
                    SetKv("Editor", "DistanceSpacing", Math.Clamp(Math.Round(DistanceSpacing + (wheel > 0 ? 1 : -1) * (Input.ShiftHeld ? 0.01 : 0.1), 2), 0.1, 6).ToString(Inv));
            }
            else for (int k = 0; k < (Input.ShiftHeld ? 4 : 1); k++) Seek(StepTime(time, wheel > 0 ? -1 : 1));
        }

        // the top tabs, the bottom bar
        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            for (int i = 0; i < 4; i++) if (CheckCollisionPointRec(mouse, TabRect(i))) { SetTab(TabOrder[i]); return; }
            if (UpdateBottomBar(mouse)) return;
            if (CheckCollisionPointRec(mouse, TimeTextRect())) { JumpPrompt(); return; }
            for (int i = 0; i < 2; i++) if (CheckCollisionPointRec(mouse, ZoomRect(i))) { Zoom(i == 0 ? 0.1f : -0.1f); zoomClickAt[i] = Game.NowMs; return; }
        }
        if (UpdateSnapPanel(mouse)) return;

        switch (tab)
        {
            case 0: UpdateCompose(mouse, u); break;
            case 1: Metronome(); UpdateTiming(mouse, u); break;
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
    // stable's compose toolbox: its tools as osu!ui's pictures down the left from 140 (60 apart; mania's hold where the
    // slider is), the toggles down the right edge, Sampleset and Additions as dropdowns above the tools
    string[] ToolNames => mode == 3 ? ["Select", "Note", "Hold"] : ["Select", "Circle", "Slider", "Spinner"];
    string[] ToolSprites => mode == 3 ? ["editor-draw-select", "editor-draw-normal", "editor-draw-hold"] : ["editor-draw-select", "editor-draw-normal", "editor-draw-slider", "editor-draw-spinner"];
    static readonly string[] ToolTips =
    [
        "Select and modify existing notes.\nCtrl+Click: Multi-select, Add point on slider\nRight-click: Remove note/point",
        "Left-click: Place note\nRight-click: New combo/Remove note (paused).",
        "Left-click: Start slider, Add point on current slider\nRight-click: Remove point, End current slider\nLeft-click twice on same spot to start a new curve section on bezier sliders.",
        "Left-click: Start spinner at current position\nRight-click: End spinner at current position",
    ];
    const string HoldTip = "Left-click: Place note\nClick on end: toggle active";
    static readonly (string Sprite, float Y, string Tip)[] Toggles =
    [
        ("editor-draw-newcombo", 80, "Force the beginning of a new combo.\nShortcut: 'Q', right-click in compose mode"),
        ("editor-sound-whistle", 140, "Add a whistle sound addition to the selection.\nShortcut: 'W'"),
        ("editor-sound-finish", 180, "Add a finish (cymbal) sound addition to the selection.\nShortcut: 'E'"),
        ("editor-sound-clap", 220, "Add a clap sound addition to the selection.\nShortcut: 'R'"),
        ("editor-draw-beatsnap", 260, "Circles will be snapped to the grid by default.\nShortcut: Hold 'Shift' to temporarily toggle, or hit 'T'\nHold 'Ctrl' to temporarily disable angle snapping."),
        ("editor-draw-distsnap", 320, "Distance between consecutive circles will be snapped based on their rhythmical difference.\nShortcut: Hold 'Alt' to temporarily toggle, or hit 'Y'\n'Alt'+Mousewheel adjusts snapping multiplier.\nSnapping multiplier can also be changed by holding 'Alt' and dragging the slidebar at the top-right."),
        ("editor-draw-lock", 380, "You can still select hit objects and apply sound additions, but not change their time or position.\nToggle with 'L'."),
    ];
    Rectangle ToolRect(int i) => Picture(ToolSprites[i], 0, 140 + i * 60);
    Rectangle ToggleRect(int i) => Picture(Toggles[i].Sprite, Game.W / U - 50, Toggles[i].Y);
    // stable's combo colour choice: one new combo selected, New combo shows an arrow on its left; that strip opens a
    // column of the map's colours (from the one it would have, a box each 20 units down, 70 from the right edge), and
    // picking one sets the object's colour skip
    bool comboList;
    EObj? ComboPickable => selected.Count == 1 && selected.First() is { NewCombo: true, Spinner: false } o ? o : null;
    Rectangle ComboExpandRect() { var r = ToggleRect(0); return new Rectangle(r.X, r.Y, 7.5f * U, r.Height); }
    static Rectangle ComboItemRect(int i) { float u = U; return new Rectangle(Game.W - 70 * u, (80 + 20 * i) * u, 20 * u, 20 * u); }
    int ComboItems(EObj o) => Math.Min(8, objs.FirstOrDefault(x => !x.Spinner) == o ? Colours.Length : Colours.Length - 1); // (the skip has 3 bits)
    Rectangle Picture(string name, float x, float y)
    {
        float u = U, sd = u / 1.6f;
        var s = skin.DefaultTex(name);
        return new Rectangle(x * u, y * u, (s?.Width ?? 82) * sd, (s?.Height ?? 86) * sd);
    }
    static Rectangle SetBox(bool addition) { float u = U; return new Rectangle(0, (addition ? 119 : 94) * u, 52 * u, 12 * u); }
    static Rectangle SetRow(bool addition, int i) { var b = SetBox(addition); return new Rectangle(b.X, b.Y + b.Height * (i + 1), b.Width, b.Height); }
    static readonly string[] SetNames = ["Auto", "Normal", "Soft", "Drum"];
    int setDrop = -1; // the open sample set dropdown (0 Sampleset, 1 Additions)
    bool noteLock; // stable's note lock: objects keep their time and place
    // (stable: the chosen tool and the toggles that are on at 0.9, the rest at 0.4, over 100ms)
    readonly (bool On, double At)[] toolLit = new (bool, double)[4], toggleLit = new (bool, double)[7];
    static float Lit(ref (bool On, double At) s, bool on)
    {
        double now = Game.NowMs;
        if (on != s.On) s = (on, now);
        float k = (float)Math.Clamp((now - s.At) / 100, 0, 1);
        return on ? 1 - 0.1f * k : 0.9f - 0.5f * k;
    }
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

    void SetSet(bool addition, int to)
    {
        if (selected.Count == 0) { setsNext = addition ? (setsNext.Normal, to) : (to, setsNext.Addition); return; }
        Change(() => { foreach (var o in selected) { var (n, a) = SetsOf(o); SetSets(o, addition ? n : to, addition ? to : a); } });
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
        if (Pressed(Bound("keyNoteLockToggle", KeyboardKey.L)) && !Input.CtrlHeld) noteLock = !noteLock;
        if (Pressed(Bound("keyGridChange", KeyboardKey.G)) && !Input.CtrlHeld) gridSize = gridSize == 32 ? 4 : gridSize * 2;
        // Nudge Left / Right (J / K): the selection a beat snap earlier / later
        int nudge = Input.CtrlHeld ? 0 : Pressed(Bound("keyNudgeLeft", KeyboardKey.J)) ? -1 : Pressed(Bound("keyNudgeRight", KeyboardKey.K)) ? 1 : 0;
        if (nudge != 0 && selected.Count > 0 && !noteLock) Nudge(nudge);
        if (Input.IsKeyPressed(KeyboardKey.Delete) && selected.Count > 0) DeleteSel();
        // stable's Edit menu keys (see the menu below)
        if (Input.CtrlHeld && selected.Count > 0 && !noteLock) // (note lock: no time or place changes)
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
            // an open sample set dropdown takes the click (a row sets it; anywhere else just closes it)
            if (setDrop >= 0)
            {
                bool addition = setDrop == 1;
                setDrop = -1;
                for (int i = 0; i < SetNames.Length; i++) if (CheckCollisionPointRec(mouse, SetRow(addition, i))) { SetSet(addition, i); return; }
                if (CheckCollisionPointRec(mouse, SetBox(false)) || CheckCollisionPointRec(mouse, SetBox(true))) return;
            }
            else for (int s = 0; s < 2; s++) if (CheckCollisionPointRec(mouse, SetBox(s == 1))) { setDrop = s; return; }
            if (comboList)
            {
                comboList = false; // (any click closes it)
                if (ComboPickable is { } co)
                    for (int i = 0; i < ComboItems(co); i++)
                        if (CheckCollisionPointRec(mouse, ComboItemRect(i))) { int skip = i; Change(() => { co.Type = co.Type & ~0x70 | (skip & 7) << 4; co.Raw = null; }); break; }
                return;
            }
            if (ComboPickable != null && CheckCollisionPointRec(mouse, ComboExpandRect())) { comboList = true; return; }
            for (int i = 0; i < ToolNames.Length; i++) if (CheckCollisionPointRec(mouse, ToolRect(i))) { tool = i; sliderDraft = null; return; }
            for (int i = 0; i < Toggles.Length; i++)
                if (CheckCollisionPointRec(mouse, ToggleRect(i)))
                {
                    if (i == 0) ToggleCombo();
                    else if (i < 4) ToggleSound(1 << i);
                    else if (i == 4) gridSnap = !gridSnap;
                    else if (i == 5) distSnap = !distSnap;
                    else noteLock = !noteLock;
                    return;
                }
        }

        // the timeline: objects drag in time (snapped), empty space scrubs
        var tl = TimelineRect();
        if (click && CheckCollisionPointRec(mouse, tl))
        {
            // a spinner's end: dragged to a new (snapped) end
            if (!noteLock && objs.LastOrDefault(o => o.Spinner && Math.Abs(TimelineX(o.End) - mouse.X) < 6 * u) is { } sp)
            {
                undo.Add(Snapshot()); redo.Clear();
                endDrag = sp;
                breakDragFit = true;
                return;
            }
            var hit = objs.LastOrDefault(o => Math.Abs(TimelineX(o.Time) - mouse.X) < 10 * u);
            if (hit != null)
            {
                if (!selected.Contains(hit)) { if (!Input.CtrlHeld) selected.Clear(); selected.Add(hit); }
                if (noteLock) return;
                undo.Add(Snapshot()); redo.Clear();
                timeDrag = (TimeAtX(mouse.X), selected.ToDictionary(o => o, o => o.Time));
                breakDragFit = true;
            }
            else if (!PickBreakEdge(mouse.X)) { scrubFrom = mouse.X; scrubTime = time; }
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
        if (breakDrag != null)
        {
            if (!held) DropBreakEdge(mouse.X);
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
            && ObjectAt(fieldPos) is { } gone)
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
                // the slider: a click for the head, one for each point (twice on one spot: a red point, where one curve
                // ends and the next begins); a right click ends it at the cursor
                if (click && inField)
                {
                    var p = sliderDraft == null ? PlaceAt(fieldPos, Snap(time)) : GridSnap(fieldPos);
                    if (sliderDraft == null) { sliderDraft = [p]; sliderDraftTime = Snap(time); }
                    else sliderDraft.Add(p);
                }
                if (Input.IsMouseButtonPressed(MouseButton.Right) && sliderDraft != null)
                {
                    // (stable's: the slider ends where it's drawn to, at the cursor)
                    if (inField && GridSnap(fieldPos) is var end && end != sliderDraft[^1]) sliderDraft.Add(end);
                    FinishSlider();
                }
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
    bool followPoints = Config.GetBool("EditorFollowPoints", true); // (the options' too)

    void DrawFollowPoints(float fs)
    {
        double pre = Preempt, lead = 800 * Math.Min(1, pre / 450), fade = 400 * Math.Min(1, pre / 450);
        int frames = skin.FrameCount("followpoint");
        float sd = EH / 768f * fs / (0.8f * U); // (the editor's playfield is a little bigger than a play's)
        for (int i = 1; i < objs.Count; i++)
        {
            EObj a = objs[i - 1], b = objs[i];
            if (b.Combo == 1 || a.Spinner || b.Spinner || a.End - lead > time || b.Time + fade < time) continue;
            Vector2 from = EndPosOf(a) + StackShift(a), to = StackedPos(b); // (where they're drawn, stacked)
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

    // File > Open Difficulty: the set's other difficulties (their names from the file names; one opens for editing once
    // the changes here are saved or reverted)
    string[] OtherDifficultyFiles()
    {
        try { return [.. Directory.EnumerateFiles(info.Folder, "*.osu").Where(f => !string.Equals(Path.GetFullPath(f), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)).Order()]; }
        catch (IOException) { return []; }
    }
    static string DifficultyName(string f) => Path.GetFileNameWithoutExtension(f) is var n && n.LastIndexOf('[') is var a and >= 0 && n.LastIndexOf(']') > a ? n[(a + 1)..n.LastIndexOf(']')] : n;
    MenuItem[] OtherDifficulties() => [.. OtherDifficultyFiles().Select(f => new MenuItem(DifficultyName(f), "", () => OpenOther(f)))];

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

    // stable's Jump to time (a click on the time): milliseconds, mm:ss or mm:ss:ms, or an osu! timestamp "mm:ss:ms (1,2,3) -"
    // that goes to the first object after it and selects those combo numbers (mania's: time|column pairs)
    void JumpPrompt() => prompt = new("Jump to time: [mm:ss:ms] (combonumbers,...)", FormatTime(time), text => { if (!JumpTo(text.Trim())) Toast.Show("Invalid format!"); });
    bool JumpTo(string s)
    {
        if (int.TryParse(s, out int ms)) { Seek(ms); return true; }
        try
        {
            if (s.IndexOf('(') <= 0)
            {
                var p = s.Replace("-", "").Trim().Split(':');
                if (p.Length is not (2 or 3)) return false;
                Seek(int.Parse(p[0]) * 60000 + int.Parse(p[1]) * 1000 + (p.Length == 3 ? int.Parse(p[2]) : 0));
                return true;
            }
            int at = int.Parse(s[..2]) * 60000 + int.Parse(s.Substring(3, 2)) * 1000 + int.Parse(s.Substring(6, 3)) - 20;
            int first = objs.FindIndex(o => o.Time > at);
            if (first < 0 || s.IndexOf(')') < 0) { Seek(at + 20); return true; }
            Seek(objs[first].Time);
            selected.Clear();
            SetTab(0);
            var items = s[11..s.IndexOf(')')].Split(',');
            if (items.Length > 0 && items[0].IndexOf('|') > 0)
            {
                foreach (var item in items)
                {
                    var tc = item.Split('|');
                    int.TryParse(tc[0], out int t); int.TryParse(tc[1], out int c);
                    for (int j = first; j < objs.Count; j++) if (objs[j].Time == t && MColumn(objs[j], MKeys) == c) selected.Add(objs[j]);
                }
            }
            else
            {
                var combos = items.Select(int.Parse).ToList();
                if (combos.Count == 1) selected.Add(objs[first]);
                else
                    for (int k = first; k < objs.Count && combos.Count > 0; k++)
                        if (objs[k].Combo == combos[0]) { selected.Add(objs[k]); combos.RemoveAt(0); }
            }
            return true;
        }
        catch (Exception e) when (e is FormatException or OverflowException or ArgumentOutOfRangeException or IndexOutOfRangeException) { return false; }
    }

    void MoveAllPrompt() => prompt = new("Enter the amount of milliseconds to move all notes by:", "", text =>
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, Inv, out int ms) || ms == 0) return;
        Change(() => { foreach (var o in objs) { o.Time += ms; o.End += ms; o.Raw = null; } });
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
    // File → Revert to Saved (stable's: asked first when there are changes); Full reads the map's images again too
    void RevertPrompt(bool full)
    {
        if (!modified && !osbModified) RevertToSaved(full);
        else prompt = new("Revert file to last saved state?", null, _ => RevertToSaved(full), "Yes", "No");
    }

    void RevertToSaved(bool full = false)
    {
        if (full)
        {
            if (bg is { Id: not 0 } old) UnloadTexture(old);
            bg = null;
            sbView?.Unload();
            sbView = null;
        }
        Read(File.ReadAllLines(path));
        LoadOsb();
        mode = int.TryParse(Kv("General", "Mode"), out int m) ? m : 0;
        undo.Clear(); redo.Clear(); selected.Clear();
        modified = false;
        Rebuild();
        breakFlags = []; breakDragFit = false; breakDrag = null;
        KeepBreaks(); // (as when it's opened)
        info = ReloadInfo();
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
    int menuOpen = -1, menuSub = -1, menuSub2 = -1; // (menuSub2: a submenu's own submenu, like Open Difficulty's)
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
            new("Open Difficulty...", Sub: [new("For Editing", Sub: OtherDifficulties() is { Length: > 0 } diffs ? diffs : null), new("For Reference", Sub: mode == 3 ? ReferenceItems() : null)]), Sep,
            new("Save", "Ctrl+S", Save), new("Create a new Difficulty...", "", () => OpenSetup(true)), Sep, new("Test Beatmap", "F5", TestPlay), new("Open AiMod", "Ctrl+Shift+A", OpenAiMod), Sep,
            new("Upload Beatmap..."), new("Extract Map Package", "", () => Notification.Show("Your beatmap is already extracted!", 3000)), new("Export Package", "", ExportPackage), new("Import from...", Sub: [new("bms/bme", "", OpenBmsImport)]), Sep,
            new("Open Song Folder", "", () => System.Diagnostics.Process.Start("explorer.exe", info.Folder)),
            new("Open .osu in Notepad", "", () => System.Diagnostics.Process.Start("notepad.exe", path)),
            new("Open .osb in Notepad", "", Directory.EnumerateFiles(info.Folder, "*.osb").FirstOrDefault() is { } osb ? () => System.Diagnostics.Process.Start("notepad.exe", osb) : null), Sep,
            new("Revert to Saved", "Ctrl+L", () => RevertPrompt(false)), new("Revert to Saved (Full)", "Ctrl+Shift+L", () => RevertPrompt(true)), Sep, new("Exit...", "Esc", AskExit)],
        1 => [new("Undo", "Ctrl+Z", undo.Count > 0 ? () => Restore(undo, redo) : null), new("Redo", "Ctrl+Y", redo.Count > 0 ? () => Restore(redo, undo) : null), Sep,
            new("Cut", "Ctrl+X", Sel(() => CopySel(cut: true))), new("Copy", "Ctrl+C", Sel(() => CopySel(cut: false))), new("Paste", "Ctrl+V", clipboard.Count > 0 ? PasteClip : null),
            new("Delete", "Del", Sel(DeleteSel)), Sep, new("Select All", "Ctrl+A", SelectAll), new("Clone", "Ctrl+D", Sel(() => Change(CloneSelection))), Sep,
            new("Reverse Selection", "Ctrl+G", Sel(() => Change(ReverseSelection))), new("Flip Horizontally", "Ctrl+H", Sel(FlipH)), new("Flip Vertically", "Ctrl+J", Sel(FlipV)),
            new("Rotate 90° Clockwise", "Ctrl+>", NotMania(Sel(() => Rotate90(clockwise: true)))), new("Rotate 90° Anticlockwise", "Ctrl+<", NotMania(Sel(() => Rotate90(clockwise: false)))),
            new("Rotate by...", "Ctrl+Shift+R", NotMania(Sel(() => OpenXform(scale: false)))), new("Scale by...", "Ctrl+Shift+S", NotMania(Sel(() => OpenXform(scale: true)))), Sep,
            new("Reset selected objects' samples", "", Sel(() => ResetSamples(selected))), new("Reset all samples", "", () => ResetSamples(objs)),
            new("Reset combo colours", "", ResetComboColours), new("Reset breaks", "", ResetBreaks), Sep,
            new("Nudge Backward", "J", Sel(() => Nudge(-1))), new("Nudge Forward", "K", Sel(() => Nudge(1)))],
        2 => [new("Compose", "F1", () => SetTab(0), tab == 0), new("Design", "F2", () => SetTab(3), tab == 3), new("Timing", "F3", () => SetTab(1), tab == 1), Sep,
            new("Song Setup...", "F4", () => SetTab(2), setupDlg != null), new("Timing Setup...", "F6", OpenTimingDialog), Sep,
            new("Volume", "", () => volumeWindow ??= new VolumeWindow()),
            new("Grid Level", Sub: [.. new[] { ("1 (Most sparse)", 32), ("2", 16), ("3", 8), ("4 (Most precise)", 4) }.Select(g => new MenuItem(g.Item1, "", () => gridSize = g.Item2, gridSize == g.Item2))]),
            new("Show Video/Storyboard", "", () => Config.SetOwn("EditorVideo", (showVideo = !showVideo) ? "1" : "0"), showVideo), new("Dim Background", "", () => dimBg = !dimBg, dimBg), new("Show Sample Name", "", () => showSampleName = !showSampleName, showSampleName), Sep, new("Hit animations", "", () => Config.SetOwn("EditorHitAnimations", (hitAnimations = !hitAnimations) ? "1" : "0"), hitAnimations), new("Follow points", "", () => Config.SetOwn("EditorFollowPoints", (followPoints = !followPoints) ? "1" : "0"), followPoints),
            new("Snaking sliders", "", () => Config.SetOwn("EditorSnakingSliders", (snakingSliders = !snakingSliders) ? "1" : "0"), snakingSliders),
            new("Stacking", "", () => { Config.SetOwn("EditorStacking", (stacking = !stacking) ? "1" : "0"); Rebuild(); }, stacking)],
        3 => [new("Snap Divisor", Sub: [.. new[] { (1, "Full Beats"), (2, "Half Beats"), (4, "Quarter Beats"), (8, "Eighth Beats"), (3, "Triplets*"), (6, "Double Triplets*") }
                .Select(d => new MenuItem($"1/{d.Item1} {d.Item2}", "", () => divisor = d.Item1, divisor == d.Item1))]),
            new("Audio Rate", Sub: [.. Rates.Select(r => new MenuItem($"{r * 100:0}%", "", () => { rate = r; music?.SetTempo(rate, false); }, rate == r))]),
            new("Grid Snapping", "T", () => gridSnap = !gridSnap, gridSnap), Sep,
            new("Create Polygon Circles...", "Ctrl+Shift+D", NotMania(OpenPoly)), new("Convert slider to stream...", "Ctrl+Shift+F", NotMania(AnySliderSelected ? SliderToStream : null)),
            new("Enable live mapping mode", "Ctrl+Tab", ToggleLiveMapping, liveMapping), new("Sample Import", "Ctrl+Shift+I", mode == 3 ? OpenSampleImport : null), new("Auto Volume Balance", "", mode == 3 ? AutoVolumeBalance : null)],
        4 => [new("Move all elements in time...", "", tab == 3 ? MoveAllElementsPrompt : null)], // (stable's: in Design only)
        5 => [new("Time Signature", Sub: [new("4/4 (Common Time)", "", () => Change(() => RedAt(time).Meter = 4), RedAt(time).Meter == 4), new("3/4 (Waltz/Triple)", "", () => Change(() => RedAt(time).Meter = 3), RedAt(time).Meter == 3)]),
            new("Metronome Clicks", "", () => metronome = !metronome, metronome), Sep,
            new("Add Timing Section", "Ctrl+P", () => TimingAction(0)), new("Add Inheriting Section", "Ctrl+Shift+P", () => TimingAction(1)),
            new("Reset Current Section", "", ResetCurrentSectionPrompt), new("Delete Timing Section", "Ctrl+I", DeleteTimingHere), new("Resnap Current Section", "", ResnapSection), Sep,
            new("Resnap All Notes", "", ResnapAll), new("Move all notes in time...", "", objs.Count > 0 ? MoveAllPrompt : null), new("Recalculate Slider Lengths", "", RecalcSliders),
            new("Delete All Timing Sections", "", timing.Count > 0 ? () => Change(() => { timing.Clear(); timingSel = -1; }) : null), Sep,
            new("Set Current Position as Preview Point", "", () => Change(() => SetKv("General", "PreviewTime", ((int)Math.Round(time)).ToString())))],
        6 => [new("This Beatmap's Information Page"), new("This Beatmap's Thread")],
        _ => [new("Show in-game help", "", () => { }), new("View FAQ")], // (stable's in-game help does nothing)
    };

    // the menu titles' text size: smaller on a narrow screen (4:3), so they end before the tabs
    // (the strip's sizes are Windows' (Segoe UI 9pt, 24px tall at 100%), in screen pixels)
    static float MenuPx => Strip / 24;
    static float MenuFont() => 12 * MenuPx;
    Rectangle MenuTitleRect(int i)
    {
        float m = MenuPx, s = MenuFont(), x = 2 * m;
        for (int k = 0; k < i; k++) x += Game.TextWidth(MenuTitles[k], s) + 14 * m;
        return new Rectangle(x, 2 * m, Game.TextWidth(MenuTitles[i], s) + 14 * m, Strip - 4 * m);
    }

    // the dropdown's rows: separators short, items 22px
    List<(MenuItem Item, Rectangle Rect)> MenuRows(MenuItem[] items, float x, float y)
    {
        float m = MenuPx, s = MenuFont(), w = 160 * m;
        foreach (var it in items) w = Math.Max(w, Game.TextWidth(it.Label, s) + Game.TextWidth(it.Keys, s) + 80 * m);
        var rows = new List<(MenuItem, Rectangle)>();
        foreach (var it in items)
        {
            float h = (it.Separator ? 9 : 22) * m;
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
            if (click && title >= 0) { menuOpen = title; menuSub = menuSub2 = -1; return true; }
            return false;
        }
        if (Input.IsKeyPressed(KeyboardKey.Escape)) { menuOpen = -1; return true; }
        if (title >= 0 && title != menuOpen) { menuOpen = title; menuSub = menuSub2 = -1; }
        var rows = MenuRows(MenuItems(menuOpen), MenuTitleRect(menuOpen).X, Strip - 2 * MenuPx);
        int hover = rows.FindIndex(r => CheckCollisionPointRec(mouse, r.Rect));
        if (hover >= 0 && rows[hover].Item.Sub != null) { if (menuSub != hover) menuSub2 = -1; menuSub = hover; }
        List<(MenuItem Item, Rectangle Rect)> sub = menuSub >= 0 && menuSub < rows.Count && rows[menuSub].Item.Sub is { } s ? MenuRows(s, rows[menuSub].Rect.X + rows[menuSub].Rect.Width, rows[menuSub].Rect.Y) : [];
        int hover2 = sub.FindIndex(r => CheckCollisionPointRec(mouse, r.Rect));
        if (hover2 >= 0 && sub[hover2].Item.Sub != null) menuSub2 = hover2;
        var sub2 = SubRows(sub, menuSub2);
        if (!click) return true;
        var hit = sub2.FirstOrDefault(r => CheckCollisionPointRec(mouse, r.Rect)).Item ?? sub.FirstOrDefault(r => CheckCollisionPointRec(mouse, r.Rect)).Item ?? (hover >= 0 ? rows[hover].Item : null);
        if (title == menuOpen && hit == null) { menuOpen = -1; return true; }
        if (hit?.Sub != null) return true;
        menuOpen = -1;
        hit?.Run?.Invoke();
        return true;
    }

    void DrawMenu(float u)
    {
        // the strip: Windows' light menu bar, a title lit under the cursor or while open
        var mouse = RawMouse;
        float m = MenuPx, fs = MenuFont();
        DrawRectangle(0, 0, Game.W, (int)Strip, new Color(240, 240, 240, 255));
        for (int i = 0; i < MenuTitles.Length; i++)
        {
            var r = MenuTitleRect(i);
            if (menuOpen == i || menuOpen < 0 && CheckCollisionPointRec(mouse, r))
            {
                DrawRectangleRec(r, menuOpen == i ? new Color(204, 232, 255, 255) : new Color(229, 243, 255, 255));
                DrawRectangleLinesEx(r, 1, new Color(153, 209, 255, 255));
            }
            Game.Text(MenuTitles[i], r.X + 7 * m, r.Y + (r.Height - fs) / 2 - 1 * m, fs, Color.Black);
        }
        if (menuOpen < 0) return;
        void Panel(List<(MenuItem Item, Rectangle Rect)> rows)
        {
            if (rows.Count == 0) return;
            var a = rows[0].Rect; var z = rows[^1].Rect;
            var box = new Rectangle(a.X, a.Y, a.Width, z.Y + z.Height - a.Y);
            DrawRectangleRec(box, new Color(242, 242, 242, 255));
            DrawRectangleLinesEx(box, 1, new Color(204, 204, 204, 255));
            foreach (var (it, r) in rows)
            {
                if (it.Separator) { DrawRectangle((int)(r.X + 30 * m), (int)(r.Y + r.Height / 2), (int)(r.Width - 32 * m), 1, new Color(215, 215, 215, 255)); continue; }
                if (it.Enabled && CheckCollisionPointRec(mouse, r))
                {
                    var hl = new Rectangle(r.X + 2 * m, r.Y, r.Width - 4 * m, r.Height);
                    DrawRectangleRec(hl, new Color(204, 232, 255, 255));
                    DrawRectangleLinesEx(hl, 1, new Color(153, 209, 255, 255));
                }
                var c = it.Enabled ? Color.Black : new Color(109, 109, 109, 255);
                if (it.Checked) Game.Text("\u2713", r.X + 10 * m, r.Y + (r.Height - fs) / 2 - 1 * m, fs, c);
                Game.Text(it.Label, r.X + 34 * m, r.Y + (r.Height - fs) / 2 - 1 * m, fs, c);
                string keys = it.Sub != null ? ">" : it.Keys;
                Game.Text(keys, r.X + r.Width - 12 * m - Game.TextWidth(keys, fs), r.Y + (r.Height - fs) / 2 - 1 * m, fs, c);
            }
        }
        var rows = MenuRows(MenuItems(menuOpen), MenuTitleRect(menuOpen).X, Strip - 2 * MenuPx);
        Panel(rows);
        if (menuSub >= 0 && menuSub < rows.Count && rows[menuSub].Item.Sub is { } s)
        {
            var sub = MenuRows(s, rows[menuSub].Rect.X + rows[menuSub].Rect.Width, rows[menuSub].Rect.Y);
            Panel(sub);
            Panel(SubRows(sub, menuSub2));
        }
    }
    List<(MenuItem Item, Rectangle Rect)> SubRows(List<(MenuItem Item, Rectangle Rect)> rows, int at) =>
        at >= 0 && at < rows.Count && rows[at].Item.Sub is { } s ? MenuRows(s, rows[at].Rect.X + rows[at].Rect.Width, rows[at].Rect.Y) : [];

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
    // stable's: a bookmark at the snapped time (if there isn't one); the nearest within 2 seconds removed; all removed
    // after asking (undoable)
    void AddBookmark()
    {
        var marks = Bookmarks();
        int at = (int)Math.Round(Snap(time));
        if (!marks.Contains(at)) Change(() => SetBookmarks([.. marks.Append(at).Order()]));
    }
    void RemoveBookmark()
    {
        var marks = Bookmarks();
        int near = marks.Where(b => Math.Abs(b - time) < 2000).OrderBy(b => Math.Abs(b - time)).DefaultIfEmpty(-1).First();
        if (near >= 0) Change(() => SetBookmarks([.. marks.Where(b => b != near)]));
    }
    void ResetBookmarksPrompt() => prompt = new("Remove all bookmarks?", null, _ => { if (Bookmarks().Count > 0) Change(() => SetBookmarks([])); }, "OK", "Cancel");
    void SeekBookmark(int dir)
    {
        var marks = Bookmarks();
        int to = dir < 0 ? marks.LastOrDefault(b => b < time - 1, -1) : marks.FirstOrDefault(b => b > time + 1, -1);
        if (to >= 0) Seek(to);
    }

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
        if (pts.Count < 2 || pts.All(p => p == pts[0])) return; // (no length to it)
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
                    if (Vector2.Distance(o.Anchors[i] + StackShift(o), fieldPos) <= 6) return (o, i); // (where it's drawn, stacked)
        return null;
    }

    (EObj Slider, int Index)? pointDrag;

    void UpdateSelect(Vector2 mouse, Vector2 fieldPos, bool click, bool held, bool inField)
    {
        // a slider's point being dragged (on the grid with grid snap); the slider refits as it goes
        if (pointDrag is { } pd)
        {
            var p = GridSnap(fieldPos - StackShift(pd.Slider));
            if (held && pd.Slider.Anchors[pd.Index] != p) { pd.Slider.Anchors[pd.Index] = p; FitLength(pd.Slider); modified = true; Rebuild(); }
            if (!held) pointDrag = null;
            return;
        }
        if (click && inField && !noteLock && PointAt(fieldPos) is { } pt)
        {
            var a = pt.Slider.Anchors;
            Vector2 before = pt.Index == 0 ? new((float)pt.Slider.X, (float)pt.Slider.Y) : a[pt.Index - 1];
            // (Ctrl: an inner point not already red is doubled, making it red - stable's)
            if (Input.CtrlHeld && pt.Index < a.Count - 1 && a[pt.Index] != before && a[pt.Index] != a[pt.Index + 1])
            {
                Change(() => { a.Insert(pt.Index, a[pt.Index]); FitLength(pt.Slider, retype: true); });
                return;
            }
            undo.Add(Snapshot()); redo.Clear();
            pointDrag = pt;
            return;
        }
        // Ctrl+click with only a slider selected, not on another object (stable's): a new point there, in the part of
        // its outline nearest, dragged on
        if (click && inField && !noteLock && Input.CtrlHeld && selected.Count == 1 && selected.First() is { Slider: true } body
            && Vector2.Distance(StackedPos(body), fieldPos) > Radius
            && !Visible().Any(o => o != body && !o.Spinner && Vector2.Distance(StackedPos(o), fieldPos) <= Radius))
        {
            undo.Add(Snapshot()); redo.Clear();
            var pts = new List<Vector2> { new((float)body.X, (float)body.Y) };
            pts.AddRange(body.Anchors);
            var at = fieldPos - StackShift(body); // (the slider as it's drawn, stacked)
            int seg = Enumerable.Range(0, pts.Count - 1).MinBy(i => SegmentDistance(at, pts[i], pts[i + 1]));
            body.Anchors.Insert(seg, GridSnap(at));
            FitLength(body, retype: true);
            modified = true;
            Rebuild();
            pointDrag = (body, seg);
            return;
        }
        if (click && inField)
        {
            // the topmost visible object under the cursor (its head)
            var hit = ObjectAt(fieldPos);
            if (hit != null)
            {
                if (!selected.Contains(hit)) { if (!Input.CtrlHeld) selected.Clear(); selected.Add(hit); }
                else if (Input.CtrlHeld) { selected.Remove(hit); return; }
                if (noteLock) return;
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
                var snapped = delta == Vector2.Zero ? Vector2.Zero // (a click that doesn't move leaves it where it is)
                    : GridSnap(new Vector2((float)anchor.Value.X, (float)anchor.Value.Y) + delta) - new Vector2((float)anchor.Value.X, (float)anchor.Value.Y);
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
                foreach (var o in Visible()) if (CheckCollisionPointRec(StackedPos(o), r)) selected.Add(o);
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

    // the Timing menu's Add Timing Section / Add Inheriting Section (Ctrl+P / Ctrl+Shift+P)
    void TimingAction(int i)
    {
        switch (i)
        {
            case 0: Change(() => { var red = RedAt(time); timing.Add(new ETiming { Time = Math.Round(time), Beat = red.Beat, Meter = red.Meter, Set = red.Set, Volume = red.Volume }); timingSel = timing.Count - 1; }); break;
            case 1: Change(() => { var red = RedAt(time); timing.Add(new ETiming { Time = Snap(time), Beat = -100, Red = false, Set = red.Set, Volume = red.Volume }); timingSel = timing.Count - 1; }); break;
        }
    }

    void UpdateTiming(Vector2 mouse, float u)
    {
        bool click = Input.IsMouseButtonPressed(MouseButton.Left);
        if (Input.IsKeyPressed(KeyboardKey.T) && !Input.CtrlHeld || click && CheckCollisionPointRec(mouse, TapButton())) { TimingTap(); return; } // (stable's Tap Here!)
        if (click && CheckCollisionPointRec(mouse, MetronomeReset())) { ResetCurrentSection(); return; }
        if (!click) return;
        if (TimingFlags(TimelineRect()).LastOrDefault(x => CheckCollisionPointRec(mouse, x.Flag)) is { Point: { } flagged }) { Seek(flagged.Time); return; }
        UpdateTimingView(mouse);
    }

    Beatmap ReloadInfo(string[]? lines = null)
    {
        try
        {
            var tmp = Beatmap.Load(path, lines: lines ?? Write());
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

    // a tab change (stable's: Compose and Design only once the song has a timing point)
    void SetTab(int t)
    {
        if (t == 2) { OpenSetup(); return; } // (stable's Song Setup: a dialog over the tab you're on)
        if (t is 0 or 3 && timing.Count == 0)
        {
            Notification.Show("Please time the song first!  This is imperative to creating a good beatmap.\nRead the guide on the site if you don't understand timing,\nor ask in the Beatmap Help forum.", 5000);
            return;
        }
        tab = t;
        breakDrag = null;
    }

    // ---- drawing
    // stable's tabs: osu!ui's picture at the game area's top left (compose, design, timing, song setup, 100 units each)
    // with its highlight sliding to the open one over 300ms
    Rectangle TabRect(int i) { float u = U; return new Rectangle(i * 100 * u, 0, 100 * u, 20.6f * u); }
    float tabFrom, tabTo; double tabAt = double.NegativeInfinity;
    static readonly int[] TabOrder = [0, 3, 1, 2]; // Compose, Design, Timing, Song Setup (tab numbers)
    Rectangle TimelineRect() { float u = U; return new Rectangle(20 * u, 25 * u, Game.W - 220 * u, 35 * u); } // (stable's: the snap panel or metronome on its right)
    float PxPerMs() => TimelineRect().Width / (int)(6000 / (tab == 1 ? zoom * 0.4f : zoom)); // (stable: Timing shows 2.5x as much)
    void Zoom(float by) { zoom = Math.Clamp(zoom + by, 0.1f, 8); zoomChanged = true; }

    // stable's kiai tag (osu!ui's) under the timeline's left end while the time is in kiai: it slides in from 40 units
    // left and fades in (to 0.9) over 160ms, with a lit copy pulsing down from 0.4 each beat; out, both fade over 100ms
    // as it slides back
    bool kiaiOn;
    double kiaiAt = double.NegativeInfinity;
    void DrawKiaiTag(float u)
    {
        ETiming? at = null;
        foreach (var p in timing) if (p.Time <= time && (at == null || p.Time > at.Time || p.Time == at.Time && !p.Red)) at = p;
        bool kiai = at != null && (at.Effects & 1) != 0;
        if (kiai != kiaiOn) { kiaiOn = kiai; kiaiAt = Game.NowMs; }
        double t = Game.NowMs - kiaiAt;
        if (skin.DefaultTex("editor-kiai") is not { } tag || !kiaiOn && t >= 160) return;
        float slide = (float)Easing.Apply(1, Math.Min(1, t / 160)), x = (kiaiOn ? -40 + 40 * slide : -40 * slide) * u;
        float fade = (float)Math.Min(1, t / (kiaiOn ? 160 : 100)), a = kiaiOn ? 0.9f * (float)Easing.Apply(1, fade) : 0.9f * (1 - fade);
        var r = new Rectangle(x, 66 * u, tag.Width * u / 1.6f, tag.Height * u / 1.6f);
        var src = new Rectangle(0, 0, tag.Tex.Width, tag.Tex.Height);
        DrawTexturePro(tag.Tex, src, r, Vector2.Zero, 0, ColorAlpha(Color.White, a));
        var red = RedAt(time);
        float beat = (float)(((time - red.Time) % red.Beat + red.Beat) % red.Beat / red.Beat);
        float lit = kiaiOn ? (t < 160 ? 0.4f * (float)Math.Min(1, t / 100) : Math.Max(0, 0.4f - beat * 0.6f)) : 0.4f * (1 - fade);
        BeginBlendMode(BlendMode.Additive);
        DrawTexturePro(tag.Tex, src, r, Vector2.Zero, 0, ColorAlpha(Color.White, lit));
        EndBlendMode();
    }
    // stable's zoom buttons left of the timeline (osu!ui's plus and minus, centred at 10,37 and 10,53; a click springs
    // them from 1.1x), 0.1 a click
    Rectangle ZoomRect(int i) { float u = U, sd = u / 1.6f * (Game.NowMs - zoomClickAt[i] < 200 ? 1.1f - 0.1f * (float)((Game.NowMs - zoomClickAt[i]) / 200) : 1); var s = skin.DefaultTex(i == 0 ? "editor-timeline-plus" : "editor-timeline-minus"); float w = (s?.Width ?? 26) * sd, h = (s?.Height ?? 24) * sd; return new Rectangle(10 * u - w / 2, (i == 0 ? 37 : 53) * u - h / 2, w, h); }
    readonly double[] zoomClickAt = [double.NegativeInfinity, double.NegativeInfinity];
    float TimelineX(double t) { var r = TimelineRect(); return r.X + r.Width / 2 + (float)((t - time) * PxPerMs()); }
    double TimeAtX(float x) { var r = TimelineRect(); return time + (x - r.X - r.Width / 2) / PxPerMs(); }

    public override void Draw()
    {
        int w = Game.W, h = (int)EH;
        float u = U;
        ClearBackground(Color.Black);
        Rlgl.PushMatrix();
        Rlgl.Translatef(0, Strip, 0);
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
            case 3: DrawDesign(u); break;
        }
        // (stable's dark gradient behind the top 69 units)
        if (skin.DefaultTex("editor-menu-bg-top") is { } top)
            DrawTexturePro(top.Tex, new Rectangle(0, 0, top.Tex.Width, top.Tex.Height), new Rectangle(0, 0, Game.W, top.Height * u / 1.6f), Vector2.Zero, 0, Color.White);
        DrawTimeline(u);
        DrawKiaiTag(u);
        if (tab == 1) { DrawTimingStatus(u); DrawMetronome(u); }
        var zm = EMouse;
        for (int i = 0; i < 2; i++)
            if (skin.DefaultTex(i == 0 ? "editor-timeline-plus" : "editor-timeline-minus") is { } zs)
            {
                var r = ZoomRect(i);
                DrawTexturePro(zs.Tex, new Rectangle(0, 0, zs.Tex.Width, zs.Tex.Height), r, Vector2.Zero, 0, Color.White);
                if (CheckCollisionPointRec(zm, r)) Tip(i == 0 ? "Increase timeline zoom" : "Decrease timeline zoom");
            }
        DrawTopBar(u);
        DrawBottomBar(u);
        if (aimod != null) DrawAiMod(u);
        if (sampleImport != null) DrawSampleImport();
        if (volumeWindow != null) DrawVolumeWindow();
        if (bmsWindow != null) DrawBmsImport();
        if (setupDlg != null) DrawSetupDialog();
        if (timingDlg != null) DrawTimingDialog();
        if (xform != null) DrawXform(u);
        if (prompt != null) DrawPrompt(u);
        if (poly != null) DrawPoly(u);
        Rlgl.PopMatrix();
        DrawMenu(u);
        if (exitPrompt != null)
        {
            Dialog.Backdrop(1);
            Dialog.Title(exitPrompt, 1);
            var mouse = RawMouse;
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
        sv.Draw(time, w, h, alpha, overlay: false, (int)Strip);
        sv.Draw(time, w, h, alpha, overlay: true, (int)Strip);
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
        if (skin.DefaultTex("topmenu-bg") is { } bar)
            DrawTexturePro(bar.Tex, new Rectangle(0, 0, bar.Tex.Width, bar.Tex.Height), new Rectangle(0, 0, bar.Width * u / 1.6f, bar.Height * u / 1.6f), Vector2.Zero, 0, Color.White);
        int open = Array.IndexOf(TabOrder, tab);
        if (open * 100 != tabTo) { tabFrom = TabX(); tabTo = open * 100; tabAt = Game.NowMs; }
        if (skin.DefaultTex("topmenu-selection") is { } sel)
        {
            BeginBlendMode(BlendMode.Additive); // (stable's: lit over the label)
            DrawTexturePro(sel.Tex, new Rectangle(0, 0, sel.Tex.Width, sel.Tex.Height), new Rectangle(TabX() * u, 0, sel.Width * u / 1.6f, sel.Height * u / 1.6f), Vector2.Zero, 0, Color.White);
            EndBlendMode();
        }
    }
    float TabX() => tabFrom + (tabTo - tabFrom) * (float)Easing.Apply(1, Math.Clamp((Game.NowMs - tabAt) / 300, 0, 1));

    void DrawTimeline(float u)
    {
        var r = TimelineRect();
        DrawRectangleRec(r, new Color(0, 0, 0, 180));
        BeginScissorMode((int)r.X, (int)(r.Y + Strip), (int)r.Width, (int)r.Height);
        // beat ticks: the bar's first beat white and tallest, then 1/2 red, 1/4 blue, 1/3 and 1/6 purple, 1/8 yellow, the rest grey
        double from = TimeAtX(r.X), to = TimeAtX(r.X + r.Width);
        // (the scale once a frame: it looks up the timing in effect, and the map can have thousands of objects)
        float px = PxPerMs(), mid = r.X + r.Width / 2;
        float X(double t) => mid + (float)((t - time) * px);
        var reds = timing.Where(p => p.Red && p.Beat > 0).OrderBy(p => p.Time).ToList();
        if (reds.Count == 0) reds.Add(new ETiming());
        for (int k = 0; k < reds.Count; k++)
        {
            var p = reds[k];
            double segEnd = k + 1 < reds.Count ? reds[k + 1].Time : to + p.Beat;
            double segStart = k == 0 ? Math.Min(p.Time, from) : p.Time;
            double step = p.Beat / divisor;
            if (step * px < 2) step *= Math.Ceiling(2 / (step * px));
            long n0 = (long)Math.Floor((Math.Max(segStart, from) - p.Time) / step), n1 = (long)Math.Ceiling((Math.Min(segEnd, to) - p.Time) / step);
            for (long n = n0; n <= n1; n++)
            {
                double t = p.Time + n * step;
                if (t < segStart || t >= segEnd) continue;
                long inBeat = ((n % divisor) + divisor) % divisor;
                long beatIndex = (long)Math.Floor(n / (double)divisor);
                var c = TickColour(inBeat, divisor);
                float hgt = inBeat == 0 ? (beatIndex % p.Meter == 0 ? 1 : 0.6f) : (divisor / Gcd((int)inBeat, divisor)) switch { 2 => 0.5f, 4 or 3 or 6 => 0.4f, 8 => 0.3f, _ => 0.25f };
                float x = X(t);
                DrawRectangle((int)x, (int)(r.Y + r.Height * (1 - hgt)), Math.Max(1, (int)u), (int)(r.Height * hgt), ColorAlpha(c, 0.8f));
            }
        }
        if (tab == 0) DrawBreakLabels(r, u);
        if (tab != 3) DrawTimelineObjects(r, u, from, to); // (Design: the picked sprite's transformations instead)
        if (tab == 3) DrawDesignLanes(r, u);
        if (tab == 1) DrawTimingFlags(r, u);
        EndScissorMode();
        if (tab == 3) DrawDesignLaneLabels(r, u);
        // the current time
        DrawRectangle((int)(r.X + r.Width / 2), (int)r.Y, Math.Max(2, (int)(2 * u)), (int)r.Height, new Color(255, 255, 255, 230));
        if (tab == 0) DrawSnapPanel(u);
    }

    // stable's objects on the timeline: the skin's hit circle in the object's colour under its overlay (both at 0.4)
    // and its combo number (0.32), on its start, its end and each repeat (with the reverse arrow, black on a bright
    // colour over stable's own circle); hitcircleselect over a selected one; anything with a length a band in its
    // colour (170, or solid when selected) between them. Each fades within 40 units of the timeline's ends; ones at
    // the same time step up 2 units each, under the one before; earlier objects are on top.
    void DrawTimelineObjects(Rectangle r, float u, double from, double to)
    {
        var colours = Colours;
        float rad = 16 * u - 1, cy = r.Y + 3 * u + r.Height / 2, left = r.X + rad, right = r.X + r.Width - rad;
        byte Fade(float x, float max = 255) => (byte)Math.Clamp(max * Math.Min(x - r.X, r.X + r.Width - x) / (40 * u), 0, max);
        int lo = objs.FindIndex(o => o.End >= from), hi = objs.FindLastIndex(o => o.Time <= to);
        if (lo < 0 || hi < lo) return;
        // the bands first, under every circle
        for (int i = lo; i <= hi; i++)
        {
            var o = objs[i];
            if (o.End <= o.Time) continue;
            float x0 = TimelineX(o.Time), x1 = TimelineX(o.End), mid = r.X + r.Width / 2;
            byte a = selected.Contains(o) ? (byte)255 : x0 > mid ? Fade(x0, 170) : x1 < mid ? Fade(x1, 170) : (byte)170;
            x0 = Math.Max(x0, left); x1 = Math.Min(x1, right);
            if (x0 > x1) continue;
            var c = o.Spinner ? Color.Gray : ObjColour(o, colours); // (spinners grey, like their circles)
            DrawRectangleRounded(new Rectangle(x0 - rad, cy - rad, x1 - x0 + rad * 2, rad * 2), 1, 16, c with { A = a });
        }
        var body = skin.Tex("hitcircle");
        var overlay = skin.Tex("hitcircleoverlay");
        var select = skin.Tex("hitcircleselect");
        var arrow = skin.Tex("reversearrow");
        bool defaultCircle = !skin.Own("hitcircle");
        float s = 0.4f * 0.625f * u;
        void Circle(EObj o, double t, int n, Color col, bool sel, bool number, bool repeat)
        {
            if (t < from || t > to) return;
            float x = TimelineX(t), y = (45 - 2 * n) * u;
            byte a = Fade(x);
            var c = new Vector2(x, y);
            if (body is { } b) b.Draw(c, s, col with { A = a });
            else DrawCircleV(c, 16 * u, col with { A = a });
            overlay?.Draw(c, s, new Color(255, 255, 255, (int)a));
            if (number) DrawComboNumber(o.Combo, new Vector2(x, (45 - 3 * n) * u), 0.32f * 0.625f * u, new Color(255, 255, 255, (int)a));
            if (repeat) arrow?.Draw(c, s, defaultCircle && col.R + col.G + col.B > 600 ? new Color(0, 0, 0, (int)a) : new Color(255, 255, 255, (int)a));
            if (sel) select?.Draw(c, s, new Color(255, 255, 255, (int)a));
        }
        // stacked indices (objects sharing a start time), then back to front
        Span<int> stack = hi - lo < 4096 ? stackalloc int[hi - lo + 1] : new int[hi - lo + 1];
        for (int i = lo; i <= hi; i++) stack[i - lo] = i > lo && objs[i - 1].Time == objs[i].Time ? stack[i - lo - 1] + 1 : 0;
        for (int i = hi; i >= lo; i--)
        {
            var o = objs[i];
            var col = o.Spinner ? Color.Gray : ObjColour(o, colours);
            bool sel = selected.Contains(o);
            int n = stack[i - lo];
            if (o.End != o.Time) Circle(o, o.End, n, col, sel, false, false);
            if (o.Slider)
                for (int k = o.Slides - 1; k >= 1; k--) Circle(o, (int)(o.Time + (o.End - o.Time) * k / o.Slides), n, col, false, false, true);
            Circle(o, o.Time, n, col, sel, !o.Spinner, false);
        }
    }

    // a combo number in the skin's hit circle font (HitCirclePrefix, overlapping by HitCircleOverlap)
    static void DrawComboNumber(int number, Vector2 centre, float scale, Color tint)
    {
        string text = number.ToString();
        float total = 0, overlap = skin.HitCircleOverlap * scale;
        foreach (char ch in text)
        {
            if (skin.Glyph(skin.HitCirclePrefix, ch) is not { } g) return;
            total += g.Width * scale;
        }
        float x = centre.X - (total - overlap * (text.Length - 1)) / 2;
        foreach (char ch in text)
        {
            var g = skin.Glyph(skin.HitCirclePrefix, ch)!.Value;
            g.Draw(new Vector2(x + g.Width * scale / 2, centre.Y), scale, tint);
            x += g.Width * scale - overlap;
        }
    }

    // stable's Timing tab: each timing point flagged along the timeline's bottom (osu!ui's tab, 37 units tall, from 4
    // left of it): "N bpm" white on a timing point, "N.Nx" light blue on an inherited one, fading within 40 units of
    // the ends; a click on one goes to it
    IEnumerable<(ETiming Point, Rectangle Flag)> TimingFlags(Rectangle r)
    {
        float u = U;
        var flag = skin.DefaultTex("editor-timeline-timinginfo");
        float w = (flag?.Width ?? 96) * u / 1.6f, h = (flag?.Height ?? 59) * u / 1.6f;
        foreach (var t in timing.OrderBy(t => t.Time))
        {
            float x = TimelineX(t.Time) - 4 * u;
            if (x < r.X - w || x > r.X + r.Width) continue;
            yield return (t, new Rectangle(x, r.Y + r.Height - 37 * u, w, h));
        }
    }
    void DrawTimingFlags(Rectangle r, float u)
    {
        if (skin.DefaultTex("editor-timeline-timinginfo") is not { } flag) return;
        foreach (var (t, f) in TimingFlags(r))
        {
            float a = Math.Clamp(Math.Min(f.X - r.X, r.Width - (f.X - r.X)) / (40 * u), 0, 1);
            DrawTexturePro(flag.Tex, new Rectangle(0, 0, flag.Tex.Width, flag.Tex.Height), f, Vector2.Zero, 0, t.Red ? ColorAlpha(Color.White, a) : new Color(180, 210, 254, (int)(255 * a)));
            string text = t.Red ? (t.Beat != 0 ? $"{60000 / t.Beat:0}bpm" : "-") : $"{(t.Beat < 0 ? Math.Round(-100 / t.Beat, 1) : 1.0).ToString(Inv)}x";
            float tx = f.X + 6 * u, ty = r.Y + r.Height - 27 * u, size = 14 * u;
            foreach (var (dx, dy) in new[] { (-1, 0), (1, 0), (0, -1), (0, 1) }) Game.Text(text, tx + dx * u * 0.6f, ty + dy * u * 0.6f, size, Color.Black);
            Game.Text(text, tx, ty, size, Color.White);
        }
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
            stackShift = StackShift(o);
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
                skin.Tex("spinner-circle")?.Draw(c, 0.5f * fs / U * EH / 768f, ColorAlpha(Color.White, alpha * 0.8f));
                continue;
            }
            if (o.Slider && o.Path.Length > 1)
            {
                // the body: a border-coloured tube with the combo colour inside (View → Snaking sliders: growing from the
                // head over the first third of the approach, as in a play; its end circles once it's all there)
                float snake = snakingSliders ? (float)Math.Clamp((time - (o.Time - pre)) / (pre / 3), 0, 1) : 1;
                float limit = float.MaxValue;
                if (snake < 1) { limit = 0; for (int i = 1; i < o.Path.Length; i++) limit += Vector2.Distance(o.Path[i - 1], o.Path[i]); limit *= snake; }
                float step = Math.Max(1, radius / 6);
                var border = ColorAlpha(Color.White, alpha);
                var inner = ColorAlpha(new Color(col.R / 3, col.G / 3, col.B / 3, 255), alpha);
                foreach (var pass in new[] { (radius, border), (radius * 0.86f, inner) })
                {
                    // along each segment of the path (a straight one is only its two ends)
                    float carry = 0, walked = 0;
                    for (int i = 1; i < o.Path.Length && walked < limit; i++)
                    {
                        Vector2 a = o.Path[i - 1], b = o.Path[i];
                        float d = Vector2.Distance(a, b), t = carry;
                        for (; t < d && walked + t <= limit; t += step) { var pt = Vector2.Lerp(a, b, t / d); DrawCircleV(ToScreen(pt.X, pt.Y), pass.Item1 * fs, pass.Item2); }
                        carry = t - d;
                        walked += d;
                    }
                    var end = snake < 1 ? PathAt(o.Path, snake) : o.Path[^1];
                    DrawCircleV(ToScreen(end.X, end.Y), pass.Item1 * fs, pass.Item2);
                }
                if (snake >= 1)
                {
                    var tail = o.Slides % 2 == 1 ? o.Path[^1] : o.Path[0];
                    DrawHitCircle(tail, 0, col, alpha, cs, selected.Contains(o), number: false);
                    if (o.Slides > 1) skin.Tex("reversearrow")?.Draw(ToScreen(o.Path[^1].X, o.Path[^1].Y), cs, ColorAlpha(Color.White, alpha));
                }
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
        stackShift = Vector2.Zero;
        // the selected (visible) sliders' points: joined by lines, a square on each (red where two meet: a segment's end)
        foreach (var o in vis)
        {
            if (!o.Slider || !selected.Contains(o)) continue;
            stackShift = StackShift(o); // (on the slider as it's drawn)
            var pts = new List<Vector2> { new((float)o.X, (float)o.Y) };
            pts.AddRange(o.Anchors);
            for (int i = 1; i < pts.Count; i++) DrawLineV(ToScreen(pts[i - 1].X, pts[i - 1].Y), ToScreen(pts[i].X, pts[i].Y), ColorAlpha(Color.White, 0.6f));
            for (int i = 0; i < pts.Count; i++)
            {
                bool red = i > 0 && pts[i] == pts[i - 1] || i + 1 < pts.Count && pts[i] == pts[i + 1];
                var s = ToScreen(pts[i].X, pts[i].Y);
                // (stable's: a soft black square under it; white at 150, the dragged one solid, red where two meet)
                bool dragged = pointDrag is { } pd && pd.Slider == o && pd.Index == i - 1;
                DrawRectangleV(s - new Vector2(4 * u), new Vector2(8 * u), new Color(0, 0, 0, 100));
                DrawRectangleV(s - new Vector2(3 * u), new Vector2(6 * u), red ? new Color(255, 40, 40, 255) : dragged ? Color.White : new Color(255, 255, 255, 150));
            }
        }
        stackShift = Vector2.Zero;
        // the slider being drawn
        if (sliderDraft != null)
        {
            var mouseField = GridSnap(ToField(EMouse));
            var pts = sliderDraft.Append(mouseField).ToList();
            for (int i = 1; i < pts.Count; i++) DrawLineEx(ToScreen(pts[i - 1].X, pts[i - 1].Y), ToScreen(pts[i].X, pts[i].Y), 2 * u, new Color(255, 200, 0, 255));
            foreach (var p in pts) DrawCircleV(ToScreen(p.X, p.Y), 3 * u, Color.White);
        }
        if (boxFrom is { } bf)
        {
            var a = ToScreen(bf.X, bf.Y); var b = EMouse;
            var box = new Rectangle(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
            DrawRectangleRec(box, new Color(255, 255, 255, 20)); // (stable's: a faint fill in a 180-alpha frame)
            DrawRectangleLinesEx(box, 1, new Color(255, 255, 255, 180));
        }
    }

    void DrawToolbox(float u)
    {
        // the toolbox and the toggles (stable's pictures; hovered, their tooltips)
        var mouse = EMouse;
        string? tip = null;
        void Picture(string name, Rectangle r, float a)
        {
            if (skin.DefaultTex(name) is { } s) DrawTexturePro(s.Tex, new Rectangle(0, 0, s.Tex.Width, s.Tex.Height), r, Vector2.Zero, 0, ColorAlpha(Color.White, a));
        }
        for (int i = 0; i < ToolNames.Length; i++)
        {
            var r = ToolRect(i);
            Picture(ToolSprites[i], r, Lit(ref toolLit[i], tool == i));
            if (CheckCollisionPointRec(mouse, r)) tip = mode == 3 && i == 2 ? HoldTip : ToolTips[i];
        }
        bool anySel = selected.Count > 0;
        bool[] on =
        [
            anySel ? selected.All(o => o.NewCombo) : newComboNext,
            anySel ? selected.All(o => (o.HitSound & 2) != 0) : (soundNext & 2) != 0,
            anySel ? selected.All(o => (o.HitSound & 4) != 0) : (soundNext & 4) != 0,
            anySel ? selected.All(o => (o.HitSound & 8) != 0) : (soundNext & 8) != 0,
            gridSnap, distSnap, noteLock,
        ];
        var pickable = ComboPickable;
        if (pickable == null) comboList = false;
        for (int i = 0; i < Toggles.Length; i++)
        {
            var r = ToggleRect(i);
            Picture(i == 0 && pickable != null ? "editor-draw-newcombo-expandable" : Toggles[i].Sprite, r, Lit(ref toggleLit[i], on[i]));
            if (i == 0 && pickable != null && CheckCollisionPointRec(mouse, ComboExpandRect())) Picture("editor-draw-newcombo-expand", r with { Width = ComboExpandRect().Width }, 1);
            if (CheckCollisionPointRec(mouse, r)) tip = Toggles[i].Tip;
        }
        // the colour column: black boxes (the current one teal, the hovered purple) each with a hit circle in its colour
        if (comboList && pickable != null)
        {
            var colours = Colours;
            int skip = (pickable.Type >> 4) & 7, from = ((pickable.Colour - skip) % colours.Length + colours.Length) % colours.Length;
            for (int i = 0; i < ComboItems(pickable); i++)
            {
                var r = ComboItemRect(i);
                DrawRectangleRec(r, CheckCollisionPointRec(mouse, r) ? new Color(165, 90, 234, 230) : i == skip ? new Color(0, 85, 85, 85) : new Color(0, 0, 0, 160));
                var c = new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2);
                float s = 5 / 32f * u / 1.6f;
                skin.Tex("hitcircle")?.Draw(c, s, colours[(from + i) % colours.Length]);
                skin.Tex("hitcircleoverlay")?.Draw(c, s, Color.White);
            }
        }
        // Sampleset and Additions: their names over dropdowns of Auto / Normal / Soft / Drum
        var (selN, selA) = anySel ? SetsOf(selected.First()) : setsNext;
        for (int s = 0; s < 2; s++)
        {
            bool addition = s == 1;
            Game.Text(addition ? "Additions" : "Sampleset", 2 * u, (addition ? 109 : 84) * u, 10 * u, Color.White);
            var box = SetBox(addition);
            DrawRectangleRec(box, new Color(0, 0, 0, 128));
            DrawRectangleLinesEx(box, 1, ColorAlpha(Color.White, 0.6f));
            Game.Text(SetNames[addition ? selA : selN], box.X + 2 * u, box.Y + 1 * u, 10 * u, Color.White);
            Options.DrawIcon(0xf078, box.X + box.Width - 5 * u, box.Y + box.Height / 2, 6 * u, Color.White);
        }
        if (setDrop >= 0)
        {
            bool addition = setDrop == 1;
            int current = addition ? selA : selN;
            for (int i = 0; i < SetNames.Length; i++)
            {
                var r = SetRow(addition, i);
                DrawRectangleRec(r, CheckCollisionPointRec(mouse, r) ? new Color(186, 17, 119, 255) : new Color(0, 0, 0, 230));
                Game.Text(SetNames[i], r.X + 2 * u, r.Y + 1 * u, 10 * u, i == current ? new Color(235, 117, 140, 255) : Color.White);
            }
        }
        Game.Text($"Grid {gridSize} (G)", Game.W - 92 * u, 438 * u, 10 * u, Color.LightGray);
        if (tip != null && setDrop < 0) Tip(tip);
        // the distance snap's ring for an object placed now
        if (distSnap && tool is 1 or 2 && sliderDraft == null && SnapRing(Snap(time)) is { } ring && ring.Radius > 0)
        {
            if (mode == 2)
                foreach (int side in new[] { -1, 1 }) DrawLineV(ToScreen(ring.From.X + side * ring.Radius, 0), ToScreen(ring.From.X + side * ring.Radius, 384), ColorAlpha(Color.White, 0.5f));
            else DrawCircleLinesV(ToScreen(ring.From.X, ring.From.Y), ring.Radius * FieldScale, ColorAlpha(Color.White, 0.5f));
        }
        Game.Text($"{selected.Count} selected", 4 * u, 380 * u, 10 * u, Color.LightGray);
        DrawSnapWarning(u);
        if (mode != 3) DrawPositionReadout(u);
    }

    // stable's readout under the snap slider (W-50,36): x and y of the cursor (white), of the selection's first object
    // (yellow) or of the slider point being dragged (salmon), with the spacing to the previous / next object as a
    // multiple of the slider velocity's travel (within 3 seconds; catch counts across only)
    void DrawPositionReadout(float u)
    {
        string Spacing(double t, Vector2 at, bool prev)
        {
            var o = prev ? objs.LastOrDefault(x => x.End < t) : objs.FirstOrDefault(x => x.Time > t + 1);
            if (o == null || o.Spinner) return "";
            double dt = prev ? t - o.End : o.Time - t;
            if (dt > 3000 || dt <= 0) return "";
            var other = prev ? EndPosOf(o) : PosOf(o);
            float d = mode == 2 ? Math.Abs(at.X - other.X) : Vector2.Distance(at, other);
            double perMs = 100 * SliderMultiplier * SvAt(prev ? t : o.Time) / RedAt(prev ? t : o.Time).Beat;
            return $"\n{(prev ? "Prev" : "Next")}: {(d / (perMs * dt)).ToString("f2", Inv)}x";
        }
        string text; Color colour;
        if (pointDrag is { } pd && pd.Index < pd.Slider.Anchors.Count)
        {
            var p = pd.Slider.Anchors[pd.Index];
            text = $"x:{p.X:0} y:{p.Y:0}" + Spacing(pd.Slider.Time, PosOf(pd.Slider), true) + Spacing(pd.Slider.End, EndPosOf(pd.Slider), false);
            colour = new Color(255, 160, 122, 255);
        }
        else if (selected.Count > 0)
        {
            var first = selected.MinBy(o => o.Time)!; var last = selected.MaxBy(o => o.End)!;
            text = $"x:{first.X:0} y:{first.Y:0}";
            if (!first.Spinner) text += (objs.IndexOf(first) > 0 ? Spacing(first.Time, PosOf(first), true) : "") + (objs.IndexOf(last) < objs.Count - 1 ? Spacing(last.End, EndPosOf(last), false) : "");
            colour = Color.Yellow;
        }
        else
        {
            var c = ToField(EMouse);
            if (gridSnap) c = GridSnap(c);
            text = $"x:{c.X:0} y:{c.Y:0}" + (tool is 1 or 2 && sliderDraft == null ? Spacing(time, c, true) + Spacing(time, c, false) : "");
            colour = Color.White;
        }
        float y = 36 * u;
        foreach (var line in text.Split('\n')) { Game.Text(line, Game.W - 50 * u, y, 10 * u, colour); y += 11 * u; }
    }

    // stable's word on an unusual beat snap divisor, over the playfield's top left (70,120, wrapped to 140 short of the
    // right edge): 1/12 and 1/16 in red until the divisor changes, 1/8 for 20 seconds, 1/3 and 1/6 for 6
    int warnedDivisor = -1;
    double warnedAt;
    void DrawSnapWarning(float u)
    {
        if (divisor != warnedDivisor) { warnedDivisor = divisor; warnedAt = Game.NowMs; }
        string? text = divisor switch
        {
            12 or 16 => $"1/{divisor} IS NOT RECOMMENDED FOR ANYONE TO USE EVER.",
            8 => "Using 1/8 snap interval is NOT RECOMMENDED unless you are a very experienced beatmapper.  If you find yourself using this, please consult someone about the timing of your map in the Beatmap Help forum or on chat!",
            3 => "Using 1/3 implies that your BPM can be divided into 3 and still land on beats.  In musical terms, this is compound timing.  If you are unsure, please ask before using this mode.",
            6 => "Using 1/6 implies that your BPM can be divided into 3 and still land on beats.  In musical terms, this is compound timing.  If you are unsure, please ask before using this mode.",
            _ => null,
        };
        if (text == null) return;
        double age = Game.NowMs - warnedAt, hold = divisor == 8 ? 20000 : 6000;
        bool red = divisor is 12 or 16;
        float a = red ? (float)Math.Min(1, age / 500) : age < hold ? 1 : 1 - (float)Math.Min(1, (age - hold) / 500);
        if (a <= 0) return;
        float size = 10 * u, width = Game.W - 210 * u, y = 120 * u;
        var colour = red ? ColorAlpha(Color.Red, a) : ColorAlpha(Color.White, a * 180 / 255f);
        string line = "";
        foreach (var word in text.Split(' '))
        {
            string next = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && Game.TextWidth(next, size) > width) { Game.Text(line, 70 * u, y, size, colour); y += size * 1.2f; line = word; }
            else line = next;
        }
        if (line.Length > 0) Game.Text(line, 70 * u, y, size, colour);
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
    // (ending before the snap panel, with a reference difficulty's stage beside it too)
    float MColW(int keys) => Math.Min(30, (Game.W / U - MLeft - 200) / (keys + (reference != null ? referenceKeys + 1 : 0)));
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
            if (hit != null && (tail || Input.ShiftHeld) && !noteLock)
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
                if (noteLock) { undo.RemoveAt(undo.Count - 1); return; }
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
        float refLeft = MLeft + colW * (keys + 1), refW = referenceKeys * colW;
        if (reference != null)
        {
            DrawRectangleRec(R(refLeft, MTop, refW + 1, h), new Color(50, 50, 50, 255));
            if (step > 1e-5)
                for (long n = (long)Math.Ceiling((MFrom - red.Time) / step), last = (long)Math.Floor((MTo - red.Time) / step); n <= last; n++)
                {
                    long inBeat = ((n % divisor) + divisor) % divisor, beat = (long)Math.Floor(n / (double)divisor);
                    bool bar = inBeat == 0 && ((beat % red.Meter) + red.Meter) % red.Meter == 0;
                    DrawRectangleRec(R(refLeft, MY(red.Time + n * step) - (bar ? 1 : 0), refW, bar ? 3 : 1), TickColour(inBeat, divisor));
                }
            for (int i = 0; i <= referenceKeys; i++) DrawRectangleRec(R(refLeft + colW * i, MTop, 1, h), Color.White);
        }
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
        // the reference's notes
        foreach (var o in reference ?? [])
        {
            if (o.End < MFrom) continue;
            if (o.Time > MTo) break;
            int c = MColumn(o, referenceKeys);
            var tint = Player.ManiaColumnKind(c, referenceKeys) switch { "2" => new Color(250, 170, 212, 255), "S" => new Color(247, 236, 0, 255), _ => Color.White };
            if ((o.Type & 128) != 0 && body is { } bd)
            {
                float y0 = Math.Min(MBottom, MY(o.Time)), y1 = Math.Max(MTop, MY(o.End));
                DrawTexturePro(bd.Tex, new Rectangle(0, 0, bd.Tex.Width, bd.Tex.Height), R(refLeft + colW * c, y1, bd.Width * 0.1875f / 1.6f * colW / 30, y0 - y1), Vector2.Zero, 0, tint);
                if (o.End < MTo) { var re = MNote(c, o.End, colW); Note(re with { X = re.X - MLeft + refLeft }, tint); }
            }
            if (o.Time >= MFrom) { var rn = MNote(c, o.Time, colW); Note(rn with { X = rn.X - MLeft + refLeft }, tint); }
        }
        // the note about to be placed, at the cursor
        var m = EMouse / u;
        if (tool > 0 && holdDraft == null && m.X > MLeft && m.X < MLeft + stageW && m.Y > MTop && m.Y < MBottom)
        {
            var r = MNote(0, time, colW);
            Note(new Rectangle(m.X - r.Width / 2, m.Y - r.Height, r.Width, r.Height), Color.White);
        }
        if (mBox is { } b)
        {
            var a = new Vector2(b.X, b.X > MStrip && b.X < MStrip + 60 ? b.Y : MY(b.Time)) * u;
            var e = EMouse;
            var box = new Rectangle(Math.Min(a.X, e.X), Math.Min(a.Y, e.Y), Math.Abs(e.X - a.X), Math.Abs(e.Y - a.Y));
            DrawRectangleRec(box, new Color(255, 255, 255, 20));
            DrawRectangleLinesEx(box, 1, new Color(255, 255, 255, 180));
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
        if (number) DrawComboNumber(combo, p, cs * 0.8f, ColorAlpha(Color.White, alpha));
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
        DrawTimingView(u);
    }

    // ---- the bottom bar: the time, the song's progress (click to seek), and stable's pictures: play / pause / stop /
    // test centred 245, 225, 205 and 175 units from the right at 470 (1.2x hovered, over 100ms), the Playback Rate scale
    // centred 77 from the right with its 25/50/75/100% (half seen, all hovered) and an arrow over the rate
    // stable's seek bar: (110,460), 20 units tall, to 260 from the right edge
    Rectangle ProgressRect() { float u = U; return new Rectangle(110 * u, 460 * u, Game.W - 370 * u, 20 * u); }
    Rectangle TimeTextRect() { float u = U; return new Rectangle(10 * u, 463 * u, Game.TextWidth(FormatTime(time), 14 * u), 14 * u); }
    static readonly string[] TransportSprites = ["editor-button-play", "editor-button-pause", "editor-button-stop", "editor-button-test"];
    static readonly float[] TransportX = [245, 225, 205, 175];
    static readonly string[] RateSprites = ["editor-rate-25", "editor-rate-50", "editor-rate-75", "editor-rate-100"];
    static readonly float[] RateX = [85, 62, 41, 18];
    Rectangle TransportRect(int i) => Centred(TransportSprites[i], Game.W / U - TransportX[i], 470);
    Rectangle RateRect(int i) => Centred(RateSprites[i], Game.W / U - RateX[i], 470);
    Rectangle Centred(string name, float x, float y, float scale = 1)
    {
        float u = U, sd = u / 1.6f * scale;
        var s = skin.DefaultTex(name);
        float w = (s?.Width ?? 30) * sd, h = (s?.Height ?? 30) * sd;
        return new Rectangle(x * u - w / 2, y * u - h / 2, w, h);
    }
    static readonly double[] Rates = [0.25, 0.5, 0.75, 1];
    // stable's bookmark buttons over the song's progress (add, remove, previous, next, reset; mania's after its stage),
    // fading in over 200ms while the cursor is down there
    static readonly string[] BookmarkSprites = ["editor-timeline-plus", "editor-timeline-minus", "editor-timeline-left", "editor-timeline-right", "editor-timeline-reset"];
    static readonly float[] BookmarkX = [150, 170, 190, 210, 242];
    static readonly string[] BookmarkTips = ["Add new bookmark at current location (Ctrl+B)", "Remove bookmark at current location (Ctrl+Shift+B)", "Seek to previous bookmark (Ctrl-LeftArrow)", "Seek to next bookmark (Ctrl-RightArrow)", "Reset all bookmarks"];
    Rectangle BookmarkRect(int i) => Centred(BookmarkSprites[i], BookmarkX[i] + (mode == 3 ? 25 + 30 * MKeys : 0), 450);
    Rectangle BookmarkArea() { float u = U; return new Rectangle(110 * u, 430 * u, Game.W - 440 * u, 40 * u); }
    (bool On, double At) bookmarkShown;
    float BookmarkAlpha => Hovered(ref bookmarkShown, CheckCollisionPointRec(EMouse, ProgressRect()) || bookmarkShown.On && CheckCollisionPointRec(EMouse, BookmarkArea()), 0, 1);
    readonly (bool On, double At)[] transportHover = new (bool, double)[4], rateHover = new (bool, double)[4];
    static float Hovered(ref (bool On, double At) s, bool on, float from, float to)
    {
        double now = Game.NowMs;
        if (on != s.On) s = (on, now);
        float k = (float)Math.Clamp((now - s.At) / 100, 0, 1);
        return on ? from + (to - from) * k : to + (from - to) * k;
    }

    bool UpdateBottomBar(Vector2 mouse)
    {
        if (CheckCollisionPointRec(mouse, ProgressRect())) { var r = ProgressRect(); Seek((mouse.X - r.X) / r.Width * length); return true; }
        if (BookmarkAlpha > 0.5f)
            for (int i = 0; i < BookmarkSprites.Length; i++)
                if (CheckCollisionPointRec(mouse, BookmarkRect(i)))
                {
                    if (i == 0) AddBookmark(); else if (i == 1) RemoveBookmark(); else if (i == 4) ResetBookmarksPrompt(); else SeekBookmark(i == 2 ? -1 : 1);
                    return true;
                }
        for (int i = 0; i < TransportSprites.Length; i++)
            if (CheckCollisionPointRec(mouse, TransportRect(i)))
            {
                if (i == 0) SetPlaying(true); else if (i == 1) SetPlaying(!playing); else if (i == 2) { SetPlaying(false); Seek(0); } else TestPlay();
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
        if (skin.DefaultTex("editor-menu-bg-bottom") is { } bg)
            DrawTexturePro(bg.Tex, new Rectangle(0, 0, bg.Tex.Width, bg.Tex.Height), new Rectangle(0, EH - bg.Height * u / 1.6f, Game.W, bg.Height * u / 1.6f), Vector2.Zero, 0, Color.White);
        else DrawRectangle(0, (int)(EH - 28 * u), Game.W, (int)(28 * u), new Color(0, 0, 0, 210));
        // stable's time (size 14 at 10,463) and, beside it, how far into the song ("intro" before it, "outro" after)
        Game.Text(FormatTime(time), 10 * u, 463 * u, 14 * u, Color.White);
        Game.Text(time < 0 ? "intro" : time > length ? "outro" : $"{time / length * 100:0.0}%", 70 * u, 465 * u, 12 * u, Color.White);
        // stable's seek bar: kiai sections (orange) and breaks (white) as bands, a line along it, the preview point
        // (yellow), bookmarks (blue, lower half), timing points (red / green, upper half), the position marker
        var r = ProgressRect();
        float X(double t) => r.X + r.Width * (float)(t / length);
        var points = timing.OrderBy(t => t.Time).ToList();
        for (int i = 0; i < points.Count; i++)
            if ((points[i].Effects & 1) != 0)
            {
                float x0 = X(points[i].Time), x1 = X(i + 1 < points.Count ? points[i + 1].Time : length);
                DrawRectangleRec(new Rectangle(x0, r.Y + r.Height * 0.36f, x1 - x0, r.Height * 0.28f), new Color(255, 146, 18, 140));
            }
        foreach (var b in ReadBreaks())
            DrawRectangleRec(new Rectangle(X(b.Start), r.Y + r.Height * 0.4f, X(b.End) - X(b.Start), r.Height * 0.2f), new Color(255, 255, 255, 140));
        DrawLineEx(new(r.X, r.Y + r.Height / 2), new(r.X + r.Width, r.Y + r.Height / 2), 1, new Color(255, 255, 255, 128));
        if (double.TryParse(Kv("General", "PreviewTime"), NumberStyles.Float, Inv, out double preview) && preview >= 0)
            DrawLineEx(new(X(preview), r.Y), new(X(preview), r.Y + r.Height), 1, new Color(255, 247, 17, 150));
        foreach (int b in Bookmarks()) DrawLineEx(new(X(b), r.Y + r.Height / 2), new(X(b), r.Y + r.Height), 1, new Color(58, 110, 170, 240));
        foreach (var t in points) DrawLineEx(new(X(t.Time), r.Y), new(X(t.Time), r.Y + r.Height / 2), 1, t.Red ? new Color(180, 20, 20, 150) : new Color(156, 255, 0, 100));
        if (skin.DefaultTex("seekbar-over") is { } marker)
        {
            float mw = marker.Width * u / 1.6f, mh = marker.Height * u / 1.6f, mx = X(Math.Clamp(time, 0, length));
            DrawTexturePro(marker.Tex, new Rectangle(0, 0, marker.Tex.Width, marker.Tex.Height), new Rectangle(mx - mw / 2, r.Y + r.Height / 2 - mh / 2, mw, mh), Vector2.Zero, 0, Color.White);
        }
        var mouse = EMouse;
        void Picture(string name, Rectangle at, float alpha = 1)
        {
            if (skin.DefaultTex(name) is { } s) DrawTexturePro(s.Tex, new Rectangle(0, 0, s.Tex.Width, s.Tex.Height), at, Vector2.Zero, 0, ColorAlpha(Color.White, alpha));
        }
        string? tip = null;
        for (int i = 0; i < TransportSprites.Length; i++)
        {
            bool over = CheckCollisionPointRec(mouse, TransportRect(i));
            Picture(TransportSprites[i], Centred(TransportSprites[i], Game.W / u - TransportX[i], 470, Hovered(ref transportHover[i], over, 1, 1.2f)));
            if (over && i == 3) tip = "Enter test mode, where you can play through from the current location.\nThis requires saving your beatmap.";
        }
        Picture("editor-rate-bg", Centred("editor-rate-bg", Game.W / u - 77, 470));
        float ba = BookmarkAlpha;
        for (int i = 0; i < BookmarkSprites.Length && ba > 0; i++)
        {
            Picture(BookmarkSprites[i], BookmarkRect(i), ba);
            if (CheckCollisionPointRec(mouse, BookmarkRect(i))) tip = BookmarkTips[i];
        }
        for (int i = 0; i < RateSprites.Length; i++) Picture(RateSprites[i], RateRect(i), Hovered(ref rateHover[i], CheckCollisionPointRec(mouse, RateRect(i)), 0.5f, 1));
        // (stable: the arrow 73 units from the right at 25%, 7 at 100%)
        Picture("editor-rate-arrow", Centred("editor-rate-arrow", Game.W / u - (73 - (float)(rate * 100 - 25) / 75 * 66), 473));
        if (tip != null) Tip(tip);
    }
}
