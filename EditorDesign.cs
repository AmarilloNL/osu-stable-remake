using System.Globalization;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// Design (F2): stable's storyboard view. The storyboard as it stands at the editor's time, its layers toggled on the
// left. The difficulty's own sprites (its [Events]) can be picked, added from the Sprite Library (I) and keyframed:
// with a transformation picked (Move, Scale, Fade, Rotate), dragging the sprite sets its value at the current time,
// making a keyframe there (W A S D nudge it); Tweening joins the keyframes around the current time into one
// transformation, eased In or Out; the timeline shows the picked sprite's transformations. The .osb's sprites show but
// aren't edited here, nor are sprites using loops, triggers or variables.
sealed partial class Editor
{
    sealed class SbCmd
    {
        public string Type = "M";
        public int Easing, Start, End;
        public double[] From = [], To = [];
        public string? Raw; // the line as written, while it's unchanged
    }
    sealed class SbSprite
    {
        public int Line, Count; // its header line in [Events] and how many lines it takes (with its commands)
        public string[] Head = []; // Sprite / Animation, layer, origin, "file", x, y (and an animation's frames)
        public string? RawHead;
        public bool Editable = true, Osb; // Osb: in the .osb, not the difficulty
        public List<SbCmd> Cmds = [];
        public string Layer => Head.Length > 1 ? Head[1] : "";
        public string Origin => Head.Length > 2 ? Head[2] : "TopLeft";
        public string File => Head.Length > 3 ? Head[3].Trim().Trim('"').Replace('\\', '/') : ""; // (as the storyboard keys its images)
        public double X, Y;
    }

    static readonly string[] SbLayerNames = ["Background", "Fail", "Pass", "Foreground", "Overlay"];
    static int Dims(string type) => type switch { "M" or "V" => 2, "C" => 3, "MX" or "MY" or "S" or "F" or "R" => 1, _ => 0 };
    static string SbNum(double v) => ((float)v).ToString(CultureInfo.InvariantCulture); // (stable keeps them as floats: the shortest exact float)

    List<SbSprite> sbSprites = [];
    Section? sbParsed;
    int sbParsedCount = -1;
    SbSprite? sbSel, sbHover;
    string sbTool = "M";
    readonly bool[] sbShow = [true, true, true, true, true];
    bool sbLanesAlt, sbLibrary, sbHitObjects;
    float sbPanelAlpha; // the panel's backing and second column: shown while the mouse is near the left edge, like stable's
    SbSprite? sbCycleTo; // (picked on letting go without a drag; dropped whenever the sprites are parsed again)
    (string Type, double Offset, bool Undo)? sbLaneDrag;
    Storyboard? sbView;
    bool sbDirty = true;
    double sbBuiltAt = double.NegativeInfinity;
    (Vector2 Mouse, double[] Value, bool Undo)? sbDrag;
    int sbLibraryScroll;
    List<string> sbImages = [];

    Section EventsSection => sections.First(s => s.Name == "Events");

    // ---- the map's .osb (its lines as they stand: kept in the undo states, written back on save when changed)
    const string OsbMark = "\n\0osb\n";
    List<string>? osbLines, sbParsedOsb;
    string? osbPath;
    bool osbModified, osbUnreadable;
    int sbParsedOsbCount = -1;
    // the .osb's versions the undo states refer to (each kept once; an undo state holds its number)
    readonly List<string> osbStates = [];
    int osbState = -1; // osbLines' version in osbStates, -1 while it has changes not in there yet

    void LoadOsb()
    {
        osbModified = osbUnreadable = false;
        osbLines = null;
        osbStates.Clear();
        osbState = -1;
        try
        {
            var all = Directory.EnumerateFiles(info.Folder, "*.osb").ToList();
            osbPath = all.FirstOrDefault(f => string.Equals(Path.GetFileName(f), Path.GetFileName(NewOsbPath()), StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
            if (osbPath != null) osbLines = [.. File.ReadAllLines(osbPath)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { osbUnreadable = true; osbLines = null; } // (left alone: never written over)
        sbParsed = null;
    }
    // an edit of the .osb's lines
    void OsbChanged() { osbModified = true; osbState = -1; }
    bool OsbWritable() { if (osbUnreadable) Notification.Show("The map's .osb couldn't be read, so it isn't changed.", 3000); return !osbUnreadable; }
    // stable's name for a map's .osb
    string NewOsbPath()
    {
        string name = $"{Kv("Metadata", "Artist")} - {Kv("Metadata", "Title")} ({Kv("Metadata", "Creator")}).osb";
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
        return Path.Combine(info.Folder, name);
    }
    // an undo state: the .osu as it would be written, then which version of the .osb (-1: none)
    string Snapshot()
    {
        if (osbLines != null && osbState < 0) { osbStates.Add(string.Join("\n", osbLines)); osbState = osbStates.Count - 1; }
        return string.Join("\n", Write()) + OsbMark + (osbLines == null ? -1 : osbState).ToString(CultureInfo.InvariantCulture);
    }
    // an undo state's .osb back (changed for saving only if it differs from now)
    void RestoreOsb(string state)
    {
        int id = int.TryParse(state, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) ? n : -1;
        int now = osbLines == null ? -1 : osbState;
        osbLines = id < 0 || id >= osbStates.Count ? null : [.. osbStates[id].Split('\n')];
        osbState = osbLines == null ? -1 : id;
        if (id != now) osbModified = true;
    }
    // the .osb's [Events] lines (from, to); none without that section
    (int From, int To) OsbEvents()
    {
        if (osbLines == null) return (0, 0);
        int s = osbLines.FindIndex(l => l.Trim() == "[Events]");
        if (s < 0) return (osbLines.Count, osbLines.Count);
        int e = ++s;
        while (e < osbLines.Count && !osbLines[e].TrimStart().StartsWith('[')) e++;
        return (s, e);
    }

    // ---- the sprites (the .osb's, then the difficulty's), parsed from the lines as they stand
    void SbParse()
    {
        var ev = EventsSection;
        sbParsed = ev;
        sbParsedCount = ev.Lines.Count;
        sbParsedOsb = osbLines;
        sbParsedOsbCount = osbLines?.Count ?? -1;
        string? selKey = sbSel == null ? null : string.Join(',', sbSel.Head);
        int selLine = sbSel?.Line ?? -1;
        bool selOsb = sbSel?.Osb ?? false;
        sbSprites = [];
        sbCycleTo = null;
        if (osbLines != null) { var (from, to) = OsbEvents(); SbParseLines(osbLines, from, to, osb: true); }
        SbParseLines(ev.Lines, 0, ev.Lines.Count, osb: false);
        sbSprites = [.. sbSprites.OrderBy(s => Math.Max(0, Array.IndexOf(SbLayerNames, s.Layer)))]; // (drawn, and so picked, layer by layer)
        sbSel = sbSprites.FirstOrDefault(s => s.Osb == selOsb && s.Line == selLine && string.Join(',', s.Head) == selKey)
            ?? sbSprites.FirstOrDefault(s => s.Osb == selOsb && string.Join(',', s.Head) == selKey);
        sbDirty = true;
    }

    void SbParseLines(List<string> lines, int from, int to, bool osb)
    {
        SbSprite? cur = null;
        bool gap = false; // (a blank or comment line after a sprite: the game reads on past it, so one with commands after it isn't edited here)
        for (int i = from; i < to; i++)
        {
            string line = lines[i];
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith("//")) { gap |= cur != null; continue; }
            if (line.Length > 0 && line[0] is ' ' or '_')
            {
                if (cur == null) continue;
                if (gap) cur.Editable = false;
                cur.Count = i - cur.Line + 1;
                int depth = line.TakeWhile(c => c is ' ' or '_').Count();
                var f = line[depth..].Split(',');
                var cmd = new SbCmd { Type = f[0], Raw = line };
                cur.Cmds.Add(cmd);
                if (depth > 1 || f[0] is "L" or "T") { cmd.Type = ""; cur.Editable = false; continue; } // (loops and triggers: left alone)
                try
                {
                    cmd.Easing = int.Parse(f[1], CultureInfo.InvariantCulture);
                    cmd.Start = int.Parse(f[2], CultureInfo.InvariantCulture);
                    cmd.End = f[3].Length == 0 ? cmd.Start : int.Parse(f[3], CultureInfo.InvariantCulture);
                    int n = Dims(cmd.Type);
                    if (n == 0) continue; // (P: kept as written)
                    var v = f[4..].Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
                    if (v.Length == n) { cmd.From = v; cmd.To = [.. v]; }
                    else if (v.Length == 2 * n) { cmd.From = v[..n]; cmd.To = v[n..]; }
                    else cur.Editable = false; // (a chain of values)
                }
                catch (Exception e) when (e is FormatException or IndexOutOfRangeException or OverflowException) { cur.Editable = false; }
                continue;
            }
            cur = null;
            gap = false;
            if (!line.StartsWith("Sprite,") && !line.StartsWith("Animation,")) continue;
            cur = new SbSprite { Line = i, Count = 1, Head = line.Split(','), RawHead = line, Osb = osb };
            if (cur.Head.Length < 6 || !double.TryParse(cur.Head[4], NumberStyles.Float, CultureInfo.InvariantCulture, out cur.X)
                || !double.TryParse(cur.Head[5], NumberStyles.Float, CultureInfo.InvariantCulture, out cur.Y)) cur.Editable = false;
            sbSprites.Add(cur);
        }
    }

    List<string> LinesOf(SbSprite sp) => sp.Osb ? osbLines! : EventsSection.Lines;
    List<string> SpriteLines(SbSprite sp)
    {
        var lines = new List<string> { sp.RawHead ?? string.Join(',', sp.Head) };
        foreach (var c in sp.Cmds.Where(c => c.Type.Length > 0 && Dims(c.Type) > 0).OrderBy(c => c.Start).Concat(sp.Cmds.Where(c => c.Type.Length == 0 || Dims(c.Type) == 0)))
            lines.Add(c.Raw ?? $" {c.Type},{c.Easing},{c.Start},{(c.End == c.Start ? "" : c.End.ToString(CultureInfo.InvariantCulture))},"
                + string.Join(',', (c.From.SequenceEqual(c.To) ? c.From : c.From.Concat(c.To)).Select(SbNum)));
        return lines;
    }

    // a sprite's lines, back into its file's [Events]
    void SbWrite(SbSprite sp)
    {
        var lines = SpriteLines(sp);
        var into = LinesOf(sp);
        into.RemoveRange(sp.Line, sp.Count);
        into.InsertRange(sp.Line, lines);
        modified = true;
        if (sp.Osb) OsbChanged();
        SbParse();
    }

    static double[] SbDefault(SbSprite sp, string type) => type switch { "M" => [sp.X, sp.Y], "V" => [1, 1], "C" => [255, 255, 255], "R" => [0], _ => [1] };

    // a sprite's value of a transformation at a time: before the first, its start; while one runs, eased between its
    // values; after, the last one's end
    static double[] SbValue(SbSprite sp, string type, double t)
    {
        var list = sp.Cmds.Where(c => c.Type == type && c.From.Length > 0).OrderBy(c => c.Start).ToList();
        if (list.Count == 0) return SbDefault(sp, type);
        if (t < list[0].Start) return list[0].From;
        var c = list.Last(x => x.Start <= t);
        if (t >= c.End || c.End == c.Start) return c.To;
        double p = Easing.Apply(c.Easing, (t - c.Start) / (c.End - c.Start));
        return [.. c.From.Zip(c.To, (a, b) => a + (b - a) * p)];
    }

    // stable's keyframing: the transformation of this type that has a keyframe at time t, made if needed (splitting the
    // one running then, stretching an instant one, or bridging from the last); its value at t is set to `value`
    void SbKeyframe(SbSprite sp, string type, int t, double[] value)
    {
        var list = sp.Cmds.Where(c => c.Type == type).OrderBy(c => c.Start).ToList();
        SbCmd? key = null;
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var c = list[i];
            if (c.End == t || c.Start == t) { key = c; break; }
            // (stable stretches the last instant keyframe it comes to, even across an earlier transformation)
            if (c.Start == c.End) { if (c.End > t) c.Start = t; else c.End = t; c.Raw = null; key = c; break; }
            if (c.Start > t && i == 0) { key = new SbCmd { Type = type, Start = t, End = c.Start, From = [.. value], To = [.. c.From] }; sp.Cmds.Add(key); break; }
            if (i == list.Count - 1 && c.End < t) { key = new SbCmd { Type = type, Start = c.End, End = t, From = [.. c.To], To = [.. value] }; sp.Cmds.Add(key); break; }
            if (c.End > t && c.Start < t)
            {
                key = new SbCmd { Type = type, Start = t, End = c.End, From = [.. value], To = [.. c.To], Easing = c.Easing };
                c.End = t; c.Raw = null;
                sp.Cmds.Add(key);
                break;
            }
            if (c.Start > t && list[i - 1].End < t)
            {
                sp.Cmds.Add(new SbCmd { Type = type, Start = t, End = c.Start, From = [.. value], To = [.. c.From] });
                key = new SbCmd { Type = type, Start = list[i - 1].End, End = t, From = [.. list[i - 1].To], To = [.. value] };
                sp.Cmds.Add(key);
                break;
            }
        }
        if (key == null) { key = new SbCmd { Type = type, Start = t, End = t, From = [.. value], To = [.. value] }; sp.Cmds.Add(key); }
        // the value at t: the keyframe's side(s) there, and the neighbour meeting it
        key.Raw = null;
        if (key.Start == t) key.From = [.. value];
        if (key.End == t) key.To = [.. value];
        foreach (var n in sp.Cmds.Where(c => c != key && c.Type == type))
        {
            if (n.End == t) { n.To = [.. value]; n.Raw = null; }
            if (n.Start == t) { n.From = [.. value]; n.Raw = null; }
        }
    }

    // Tweening on: the keyframes before and after now become one transformation; off: the one running now splits into
    // its two ends
    void SbTween(bool on)
    {
        if (sbSel is not { Editable: true } sp) return;
        int t = (int)Math.Round(time);
        SbChange(sp, () =>
        {
            var list = sp.Cmds.Where(c => c.Type == sbTool).OrderBy(c => c.Start).ToList();
            if (on)
            {
                if (list.Any(c => c.Start < t && t < c.End)) return;
                var before = list.LastOrDefault(c => c.End <= t);
                var after = list.FirstOrDefault(c => c.Start >= t);
                if (before == null || after == null || before == after || before.End >= after.Start) return; // (keyframes already meeting: nothing to join)
                if (before.Start == before.End) sp.Cmds.Remove(before);
                if (after.Start == after.End) sp.Cmds.Remove(after);
                sp.Cmds.Add(new SbCmd { Type = sbTool, Start = before.End, End = after.Start, From = [.. before.To], To = [.. after.From] });
            }
            else
            {
                var c = list.FirstOrDefault(x => x.Start < t && t < x.End);
                if (c == null) return;
                sp.Cmds.Remove(c);
                // (an instant keyframe at each end, unless another transformation starts or ends there)
                if (!list.Any(x => x != c && (x.Start == c.End || x.End == c.End))) sp.Cmds.Add(new SbCmd { Type = sbTool, Start = c.End, End = c.End, From = [.. c.To], To = [.. c.To] });
                if (!list.Any(x => x != c && (x.End == c.Start || x.Start == c.Start))) sp.Cmds.Add(new SbCmd { Type = sbTool, Start = c.Start, End = c.Start, From = [.. c.From], To = [.. c.From] });
            }
        });
    }

    // stable's flips: a P (H or V) keyframed at the current time when ticked, the one there (or the nearest) taken out when
    // unticked
    static bool IsFlip(SbCmd c, string axis) => c.Type == "P" && c.Raw != null && c.Raw.TrimEnd().EndsWith("," + axis);
    void SbFlip(string axis)
    {
        if (sbSel is not { Editable: true } sp) return;
        int t = (int)Math.Round(time);
        SbChange(sp, () =>
        {
            var here = sp.Cmds.FirstOrDefault(c => IsFlip(c, axis) && c.Start <= t && t <= c.End);
            if (here != null) sp.Cmds.Remove(here);
            else sp.Cmds.Add(new SbCmd { Type = "P", Start = t, End = t, Raw = $" P,0,{t},,{axis}" });
        });
    }

    SbCmd? SbRunning(SbSprite sp) { int t = (int)Math.Round(time); return sp.Cmds.FirstOrDefault(c => c.Type == sbTool && c.Start <= t && t <= c.End); }

    // ---- Keyframe Control: + a keyframe now (Colour: picked from a palette), - the keyframe now taken out (the two
    // transformations meeting there joined), < > to the previous / next keyframe of the transformation
    void SbAddKeyframe()
    {
        if (sbSel is not { Editable: true } sp) return;
        if (sbTool == "C") { sbColourPick = true; return; }
        int t = (int)Math.Round(time);
        var value = SbValue(sp, sbTool, t);
        SbChange(sp, () => SbKeyframe(sp, sbTool, t, value));
    }
    void SbRemoveKeyframe()
    {
        if (sbSel is not { Editable: true } sp) return;
        int t = (int)Math.Round(time);
        if (!sp.Cmds.Any(c => c.Type == sbTool && (c.Start == t || c.End == t))) return; // (no keyframe here)
        SbChange(sp, () =>
        {
            var list = sp.Cmds.Where(c => c.Type == sbTool).OrderBy(c => c.Start).ToList();
            SbCmd? ending = null, starting = null;
            foreach (var c in list)
            {
                if (c.Start == t && c.End == t) { sp.Cmds.Remove(c); return; }
                if (c.Start == t) { starting = c; break; }
                if (c.End == t) ending = c;
            }
            if (ending != null && starting != null)
            {
                sp.Cmds.Remove(ending);
                sp.Cmds.Remove(starting);
                sp.Cmds.Add(new SbCmd { Type = sbTool, Start = ending.Start, End = starting.End, From = ending.From, To = starting.To, Easing = ending.Easing });
                return;
            }
            if (ending != null) { ending.End = ending.Start; ending.To = [.. ending.From]; ending.Raw = null; }
            if (starting != null) { starting.Start = starting.End; starting.From = [.. starting.To]; starting.Raw = null; }
        });
    }
    void SbJump(bool next)
    {
        if (sbSel == null) return;
        int t = (int)Math.Round(time);
        var keys = sbSel.Cmds.Where(c => c.Type == sbTool).SelectMany(c => new[] { c.Start, c.End }).ToList();
        var to = next ? keys.Where(k => k > t).DefaultIfEmpty(int.MinValue).Min() : keys.Where(k => k < t).DefaultIfEmpty(int.MinValue).Max();
        if (to != int.MinValue) Seek(to);
    }
    static Vector2 SbKeyButton(int i, float u) => new((250 + 15 * i) * u, 70 * u);
    Vector2 SbLaneButton(int i, float u) => new(TimelineRect().X + (5 + 15 * i) * u, 70 * u);

    // the Colour transformation's colours: Windows' 48 basic colours (stable asks with Windows' own colour dialog)
    bool sbColourPick;
    static readonly uint[] BasicColours =
    [
        0xFF8080, 0xFFFF80, 0x80FF80, 0x00FF80, 0x80FFFF, 0x0080FF, 0xFF80C0, 0xFF80FF,
        0xFF0000, 0xFFFF00, 0x80FF00, 0x00FF40, 0x00FFFF, 0x0080C0, 0x8080C0, 0xFF00FF,
        0x804040, 0xFF8040, 0x00FF00, 0x008080, 0x004080, 0x8080FF, 0x800040, 0xFF0080,
        0x800000, 0xFF8000, 0x008000, 0x008040, 0x0000FF, 0x0000A0, 0x800080, 0x8000FF,
        0x400000, 0x804000, 0x004000, 0x004040, 0x000080, 0x000040, 0x400040, 0x400080,
        0x000000, 0x808000, 0x808040, 0x808080, 0x408080, 0xC0C0C0, 0x400040, 0xFFFFFF,
    ];
    Rectangle ColourSwatch(int i) { float u = U; return new Rectangle(Game.W / 2f - 100 * u + (i % 8) * 25 * u, 180 * u + (i / 8) * 21 * u, 22 * u, 18 * u); }

    // an edit of one sprite: undoable, written back at once
    void SbChange(SbSprite sp, Action edit)
    {
        undo.Add(Snapshot());
        if (undo.Count > 100) undo.RemoveAt(0);
        redo.Clear();
        edit();
        SbWrite(sp);
    }

    // ---- where things are: the storyboard's 640 x 480 (854 wide for a widescreen one) fitted to the screen's height
    float SbScale => Game.H / 480f;
    Vector2 SbOrigin => new((Game.W - 640 * SbScale) / 2, 0);
    Vector2 SbToScreen(double x, double y) => SbOrigin + new Vector2((float)x, (float)y) * SbScale;
    Vector2 SbFromScreen(Vector2 p) => (p - SbOrigin) / SbScale;

    static Vector2 OriginOf(string origin) => Storyboard.OriginOf(origin.Trim());

    // a sprite's corners on screen now (null while it isn't showing)
    Vector2[]? SbCorners(SbSprite sp)
    {
        var cmds = sp.Cmds.Where(c => c.Type.Length > 0).ToList();
        if (cmds.Count == 0 || time < cmds.Min(c => c.Start) || time > cmds.Max(c => c.End)) return null;
        if (sbView?.TextureOf(sp.Head[0] == "Animation" ? Path.ChangeExtension(sp.File, null) + "0" + Path.GetExtension(sp.File) : sp.File) is not { } tex) return null;
        var pos = SbValue(sp, "M", time);
        double x = sp.Cmds.Any(c => c.Type == "MX") ? SbValue(sp, "MX", time)[0] : pos[0], y = sp.Cmds.Any(c => c.Type == "MY") ? SbValue(sp, "MY", time)[0] : pos[1];
        double s = SbValue(sp, "S", time)[0], r = SbValue(sp, "R", time)[0];
        var v = SbValue(sp, "V", time);
        var size = new Vector2(tex.Width * (float)(s * v[0]), tex.Height * (float)(s * v[1]));
        var o = OriginOf(sp.Origin) * size;
        float cos = MathF.Cos((float)r), sin = MathF.Sin((float)r);
        Vector2 Corner(float cx, float cy) { var d = new Vector2(cx, cy) * size - o; return SbToScreen(x + d.X * cos - d.Y * sin, y + d.X * sin + d.Y * cos); }
        return [Corner(0, 0), Corner(1, 0), Corner(1, 1), Corner(0, 1)];
    }

    static bool InQuad(Vector2 p, Vector2[] q)
    {
        bool inside = false;
        for (int i = 0, j = 3; i < 4; j = i++)
            if ((q[i].Y > p.Y) != (q[j].Y > p.Y) && p.X < (q[j].X - q[i].X) * (p.Y - q[i].Y) / (q[j].Y - q[i].Y) + q[i].X) inside = !inside;
        return inside;
    }

    // ---- the left panel (stable's: layer toggles, the transformation, tweening, easing, origin)
    static readonly (string Label, float Y)[] SbLayerRows = [("Background", 95), ("Failing", 110), ("Passing", 125), ("Foreground", 140), ("Overlay", 155)];
    static readonly (string Label, string Tool, float Y)[] SbToolRows = [("Move", "M", 215), ("Scale", "S", 230), ("Fade", "F", 245), ("Rotate", "R", 260), ("Colour", "C", 275)];
    Rectangle SbRow(float x, float y) { float u = U; return new Rectangle(x * u, y * u, 70 * u, 13 * u); }
    Rectangle SbLibraryButton() { float u = U; return new Rectangle(Game.W - 70 * u, 372 * u, 66 * u, 40 * u); }
    Rectangle SbLibraryPanel() { float u = U; return new Rectangle(Game.W / 2f - 200 * u, 90 * u, 400 * u, 300 * u); }

    // the storyboard as its lines stand (the Design view, Compose's background and AiMod's checks): parsed again after
    // any change to them, rebuilt at most every 150ms while a sprite is being dragged
    void SbRefresh()
    {
        var ev = EventsSection;
        if (!ReferenceEquals(ev, sbParsed) || ev.Lines.Count != sbParsedCount || !ReferenceEquals(osbLines, sbParsedOsb) || (osbLines?.Count ?? -1) != sbParsedOsbCount) SbParse();
        if (sbDirty && (sbDrag == null || Game.NowMs - sbBuiltAt > 150))
        {
            var variables = sections.FirstOrDefault(s => s.Name == "Variables")?.Lines ?? [];
            try { sbView = Storyboard.LoadLines(info, ev.Lines, variables, osbLines, Kv("General", "WidescreenStoryboard") == "1", sbView); }
            catch (IOException) { }
            if (sbView != null) { sbView.BothStates = true; Array.Copy(sbShow, sbView.ShowLayer, 5); }
            sbDirty = false;
            sbBuiltAt = Game.NowMs;
        }
    }

    void UpdateDesign(Vector2 mouse, float u)
    {
        var ev = EventsSection;
        SbRefresh();
        bool click = Input.IsMouseButtonPressed(MouseButton.Left);

        // the colour palette: a colour keyframe now for the picked sprite
        if (sbColourPick)
        {
            if (Input.IsKeyPressed(KeyboardKey.Escape) || sbSel is not { Editable: true } cs) { sbColourPick = false; return; }
            if (!click) return;
            sbColourPick = false;
            for (int i = 0; i < BasicColours.Length; i++)
                if (CheckCollisionPointRec(mouse, ColourSwatch(i)))
                {
                    uint c = BasicColours[i];
                    int t = (int)Math.Round(time);
                    SbChange(cs, () => SbKeyframe(cs, "C", t, [c >> 16 & 0xFF, c >> 8 & 0xFF, c & 0xFF]));
                }
            return;
        }

        if (sbLibrary)
        {
            var p = SbLibraryPanel();
            float wheel = Input.GetMouseWheelMove();
            if (wheel != 0) sbLibraryScroll = Math.Clamp(sbLibraryScroll - Math.Sign(wheel), 0, Math.Max(0, sbImages.Count - 16));
            if (Input.IsKeyPressed(KeyboardKey.Escape) || click && !CheckCollisionPointRec(mouse, p)) { sbLibrary = false; return; }
            if (!click) return;
            int row = (int)((mouse.Y - p.Y - 24 * u) / (17 * u)) + sbLibraryScroll;
            if (mouse.Y > p.Y + 24 * u && row >= 0 && row < sbImages.Count) SbAdd(sbImages[row]);
            return;
        }

        if (Input.IsKeyPressed(KeyboardKey.I) && !Input.CtrlHeld) { SbOpenLibrary(); return; }
        if (sbSel is { Editable: true } sel)
        {
            if (Input.IsKeyPressed(KeyboardKey.Delete))
            {
                undo.Add(Snapshot()); redo.Clear();
                LinesOf(sel).RemoveRange(sel.Line, sel.Count);
                modified = true;
                if (sel.Osb) OsbChanged();
                sbSel = null;
                SbParse();
                return;
            }
            // W A S D: a pixel's nudge, a keyframe at the current time
            int dx = Input.IsKeyPressed(KeyboardKey.A) ? -1 : Input.IsKeyPressed(KeyboardKey.D) ? 1 : 0, dy = Input.IsKeyPressed(KeyboardKey.W) ? -1 : Input.IsKeyPressed(KeyboardKey.S) ? 1 : 0;
            if ((dx != 0 || dy != 0) && !Input.CtrlHeld)
            {
                var at = SbValue(sel, "M", time);
                SbChange(sel, () => SbKeyframe(sel, "M", (int)Math.Round(time), [at[0] + dx, at[1] + dy]));
            }
            // Ctrl+1-4: the sprite to another layer
            for (int k = 0; k < 4; k++)
                if (Input.CtrlHeld && Input.IsKeyPressed(KeyboardKey.One + k) && sel.Layer != SbLayerNames[k]) SbChange(sel, () => { sel.Head[1] = SbLayerNames[k]; sel.RawHead = null; });
        }

        // the panel
        if (click && CheckCollisionPointRec(mouse, SbRow(2, 170))) { sbHitObjects = !sbHitObjects; return; }
        for (int i = 0; i < SbLayerRows.Length; i++)
            if (click && CheckCollisionPointRec(mouse, SbRow(2, SbLayerRows[i].Y))) { sbShow[i] = !sbShow[i]; if (sbView != null) Array.Copy(sbShow, sbView.ShowLayer, 5); return; }
        // the second column (stable's: in with the mouse at the left edge, out past the panel)
        float step = 0.08f * GetFrameTime() * 60;
        sbPanelAlpha = mouse.X < 40 * u ? Math.Min(1, sbPanelAlpha + step) : mouse.X > 160 * u ? Math.Max(0, sbPanelAlpha - step) : sbPanelAlpha is > 0.5f and < 1 ? Math.Min(1, sbPanelAlpha + step) : sbPanelAlpha;
        if (sbPanelAlpha > 0.5f)
        {
            if (click && CheckCollisionPointRec(mouse, SbRow(72, 215))) { sbTool = "V"; return; }
            if (click && CheckCollisionPointRec(mouse, SbRow(72, 230))) { SbFlip("H"); return; }
            if (click && CheckCollisionPointRec(mouse, SbRow(72, 245))) { SbFlip("V"); return; }
        }
        foreach (var (_, tool, y) in SbToolRows)
            if (click && CheckCollisionPointRec(mouse, SbRow(2, y))) { sbTool = tool; return; }
        if (click && CheckCollisionPointRec(mouse, SbRow(2, 305))) { SbTween(sbSel == null || SbRunning(sbSel) == null); return; }
        for (int e = 0; e < 2; e++)
            if (click && CheckCollisionPointRec(mouse, SbRow(2, 335 + e * 15)) && sbSel is { Editable: true } es && SbRunning(es) is { } run)
            {
                int easing = e == 0 ? 2 : 1;
                SbChange(es, () => { run.Easing = run.Easing == easing ? 0 : easing; run.Raw = null; });
                return;
            }
        if (click && CheckCollisionPointRec(mouse, SbRow(2, 440)) && sbSel is { Editable: true } ms) { SbMove(ms); return; }
        for (int o = 0; o < 2; o++)
            if (click && CheckCollisionPointRec(mouse, SbRow(2, 395 + o * 15)) && sbSel is { Editable: true } os && os.Origin != (o == 0 ? "TopLeft" : "Centre"))
            {
                SbChange(os, () => { os.Head[2] = o == 0 ? "TopLeft" : "Centre"; os.RawHead = null; });
                return;
            }
        if (click && CheckCollisionPointRec(mouse, SbLibraryButton())) { SbOpenLibrary(); return; }
        // the lanes' switches (the arrows before them) and Keyframe Control's buttons
        for (int i = 0; i < 2; i++)
            if (click && Vector2.Distance(mouse, SbLaneButton(i, u)) < 7 * u) { sbLanesAlt = i == 0; return; }
        for (int i = 0; i < 4; i++)
            if (click && Vector2.Distance(mouse, SbKeyButton(i, u)) < 7 * u)
            {
                if (i == 0) SbAddKeyframe(); else if (i == 1) SbRemoveKeyframe(); else SbJump(next: i == 3);
                return;
            }
        if (click && mouse.X < 160 * u && mouse.Y > 66 * u && mouse.Y < 462 * u) return; // (the panel's own space)

        // a lane of the timeline dragged: the picked sprite's transformations of that kind moved in time (snapped to the
        // beat unless Shift is held), as long as they all stay inside the song
        var tl = TimelineRect();
        if (sbLaneDrag is { } ld)
        {
            if (!Input.IsMouseButtonDown(MouseButton.Left) || sbSel is not { Editable: true } ls) { sbLaneDrag = null; return; }
            var ofType = ls.Cmds.Where(c => c.Type == ld.Type).ToList();
            if (ofType.Count == 0) { sbLaneDrag = null; return; }
            double want = TimeAtX(mouse.X) - ld.Offset;
            int delta = (int)Math.Round(Input.ShiftHeld ? want : Snap(want)) - ofType.Min(c => c.Start);
            if (delta != 0 && ofType.All(c => c.Start + delta >= 0 && c.Start + delta < length))
            {
                if (!ld.Undo) { undo.Add(Snapshot()); redo.Clear(); sbLaneDrag = ld with { Undo = true }; } // (once it moves)
                foreach (var c in ofType) { c.Start += delta; c.End += delta; c.Raw = null; }
                SbWrite(ls);
            }
            return;
        }
        if (click && CheckCollisionPointRec(mouse, tl) && sbSel is { Editable: true } lsel)
        {
            string[] lanes = sbLanesAlt ? ["S", "F", "R", "C"] : ["M", "S", "F", "R"];
            string type = lanes[Math.Clamp((int)((mouse.Y - tl.Y) / (tl.Height / 4)), 0, 3)];
            double at = TimeAtX(mouse.X);
            var hitCmd = lsel.Cmds.FirstOrDefault(c => c.Type == type && TimelineX(c.Start) - 3 * u <= mouse.X && mouse.X <= Math.Max(TimelineX(c.End), TimelineX(c.Start) + 3 * u) + 3 * u);
            if (hitCmd != null) sbLaneDrag = (type, at - lsel.Cmds.Where(c => c.Type == type).Min(c => c.Start), false);
            return;
        }

        // picking and dragging
        var sbMouse = SbFromScreen(mouse);
        bool held = Input.IsMouseButtonDown(MouseButton.Left);
        if (sbDrag is { } d)
        {
            if (!held || sbSel is not { Editable: true } ds)
            {
                if (!held && !d.Undo && sbCycleTo != null) sbSel = sbCycleTo; // (a click, not a drag)
                sbDrag = null; sbCycleTo = null; sbDirty = true;
                return;
            }
            var m = SbFromScreen(mouse);
            if (Input.ShiftHeld) { if (Math.Abs(m.X - d.Mouse.X) > Math.Abs(m.Y - d.Mouse.Y)) m.Y = d.Mouse.Y; else m.X = d.Mouse.X; }
            if (!d.Undo && m == d.Mouse || sbTool == "C") return; // (a click alone picks; only moving keyframes; Colour: from the palette)
            float up = d.Mouse.Y - m.Y; // (dragging up raises scale, fade and rotation)
            double[] value = sbTool switch
            {
                "M" => [Math.Round(d.Value[0] + m.X - d.Mouse.X), Math.Round(d.Value[1] + m.Y - d.Mouse.Y)],
                "S" => [Math.Clamp((d.Value[0] * 100 + up) / 100, 0.1, 5)],
                "V" => [Math.Clamp((d.Value[0] * 100 + d.Mouse.X - m.X) / 100, 0.1, 5), Math.Clamp((d.Value[1] * 100 + d.Mouse.Y - m.Y) / 100, 0.1, 5)],
                "F" => [Math.Clamp((d.Value[0] * 100 + up) / 100, 0, 1)],
                _ => [Math.Clamp((d.Value[0] * 50 + up) / 50, -50, 50)],
            };
            if (!d.Undo) { undo.Add(Snapshot()); if (undo.Count > 100) undo.RemoveAt(0); redo.Clear(); sbDrag = d with { Undo = true }; }
            SbKeyframe(ds, sbTool, (int)Math.Round(time), value);
            SbWrite(ds);
            return;
        }
        sbHover = sbSprites.Where(s => sbShow[Math.Max(0, Array.IndexOf(SbLayerNames, s.Layer))] && SbCorners(s) is { } q && InQuad(mouse, q)).LastOrDefault();
        if (!click || mouse.Y < 80 * u) return;
        // a click on the picked sprite again picks the one under it (stable cycles through them)
        var under = sbSprites.Where(s => sbShow[Math.Max(0, Array.IndexOf(SbLayerNames, s.Layer))] && SbCorners(s) is { } q && InQuad(mouse, q)).ToList();
        // (the picked sprite not showing now - before or after its keyframes: a drag anywhere keyframes it here)
        if (under.Count == 0 && sbSel is { Editable: true } hidden && SbCorners(hidden) == null) { sbDrag = (sbMouse, SbValue(hidden, sbTool, time), false); sbCycleTo = null; return; }
        if (under.Count == 0) { sbSel = null; return; }
        // stable: pressing on the picked sprite drags it; let go without moving, the one under it is picked instead
        int idx = sbSel == null ? -1 : under.IndexOf(sbSel);
        var below = idx > 0 ? under[idx - 1] : under[^1];
        if (idx >= 0 && sbSel!.Editable) { sbDrag = (sbMouse, SbValue(sbSel, sbTool, time), false); sbCycleTo = below; return; }
        sbSel = idx >= 0 ? below : under[^1];
        sbCycleTo = null;
        if (sbSel.Editable) sbDrag = (sbMouse, SbValue(sbSel, sbTool, time), false);
    }

    void SbOpenLibrary()
    {
        try
        {
            sbImages = [.. Directory.EnumerateFiles(info.Folder, "*", SearchOption.AllDirectories)
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg")
                .Select(f => Path.GetRelativePath(info.Folder, f).Replace('\\', '/')).Where(f => !f.Contains(',') && !f.Contains('"')).Order(StringComparer.OrdinalIgnoreCase)];
        }
        catch (IOException) { sbImages = []; }
        sbLibrary = true;
        sbLibraryScroll = 0;
    }

    // where a sprite of a layer goes in a file's events: after that layer's sprites (under stable's layer comment), else
    // at the end
    static int LayerInsertAt(List<string> lines, int from, int to, string layer)
    {
        int at = lines.FindIndex(from, to - from, l => l.Trim() == $"//Storyboard Layer {Math.Max(0, Array.IndexOf(SbLayerNames, layer))} ({layer})");
        if (at < 0) return to;
        at++;
        while (at < to && !lines[at].TrimStart().StartsWith("//")) at++;
        return at;
    }

    void SbInsert(List<string> lines, bool osb, IReadOnlyList<string> sprite, string layer)
    {
        var (from, to) = osb ? OsbEvents() : (0, lines.Count);
        int at = LayerInsertAt(lines, from, to, layer);
        lines.InsertRange(at, sprite);
        modified = true;
        if (osb) OsbChanged();
        SbParse();
        sbSel = sbSprites.FirstOrDefault(s => s.Osb == osb && s.Line == at);
    }

    // stable's new sprite: centred at the storyboard's middle on the foreground, with a keyframe at the current time;
    // in the .osb (made if the map has none) - "Diff Specific" moves it to the difficulty
    void SbAdd(string file)
    {
        sbLibrary = false;
        if (!OsbWritable()) return;
        undo.Add(Snapshot()); redo.Clear();
        int t = (int)Math.Round(time);
        string[] lines = [$"Sprite,Foreground,Centre,\"{file}\",320,240", sbTool switch
        {
            "S" => $" S,0,{t},,1", "F" => $" F,0,{t},,1", "R" => $" R,0,{t},,0", _ => $" M,0,{t},,320,240",
        }];
        if (osbLines != null && !osbLines.Any(l => l.Trim() == "[Events]")) osbLines.Add("[Events]"); // (an .osb without the section)
        osbLines ??= ["[Events]", "//Background and Video events", "//Storyboard Layer 0 (Background)", "//Storyboard Layer 1 (Fail)",
            "//Storyboard Layer 2 (Pass)", "//Storyboard Layer 3 (Foreground)", "//Storyboard Layer 4 (Overlay)", "//Storyboard Sound Samples"];
        SbInsert(osbLines, true, lines, "Foreground");
    }

    // Diff Specific: the picked sprite over to the difficulty's [Events], or back to the .osb
    void SbMove(SbSprite sp)
    {
        if (!sp.Osb && !OsbWritable()) return;
        undo.Add(Snapshot()); redo.Clear();
        var lines = SpriteLines(sp);
        LinesOf(sp).RemoveRange(sp.Line, sp.Count);
        osbModified |= sp.Osb;
        if (!sp.Osb) { osbLines ??= ["[Events]"]; if (!osbLines.Any(l => l.Trim() == "[Events]")) osbLines.Add("[Events]"); }
        SbInsert(sp.Osb ? EventsSection.Lines : osbLines!, !sp.Osb, lines, sp.Layer);
    }

    void DrawDesign(float u)
    {
        int w = Game.W, h = Game.H;
        sbView?.Draw(time, w, h, 1, overlay: false);
        if (sbHitObjects && mode != 3) { fieldAsPlay = true; DrawPlayfield(u, objectsOnly: true); fieldAsPlay = false; }
        sbView?.Draw(time, w, h, 1, overlay: true);
        var mouse = Input.GetMousePosition();
        // the picked and the hovered sprite
        void Outline(SbSprite sp, Color c) { if (SbCorners(sp) is { } q) for (int i = 0; i < 4; i++) DrawLineEx(q[i], q[(i + 1) % 4], 2, c); }
        if (sbHover != null && sbHover != sbSel) Outline(sbHover, ColorAlpha(Color.White, 0.5f));
        if (sbSel != null)
        {
            Outline(sbSel, sbSel.Editable ? Color.White : Color.Gray);
            if (SbCorners(sbSel) is { } q)
            {
                string label = $"{sbSel.File} ({sbSel.Layer})" + (sbSel.Editable ? "" : " - not editable here");
                Game.Text(label, q.Min(c => c.X), q.Min(c => c.Y) - 10 * u, 8 * u, Color.White);
            }
        }
        // the cursor's storyboard position and the time; the hit objects aren't shown here
        var sm = SbFromScreen(mouse);
        Game.Text(string.Format(CultureInfo.InvariantCulture, "x:{0:0} y:{1:0}", sm.X, sm.Y), w - 200 * u, 70 * u, 10 * u, Color.White);
        Game.Text(string.Format(CultureInfo.InvariantCulture, "{0:#,0}ms", time), w - 200 * u, 81 * u, 10 * u, Color.White);

        // the panel
        DrawRectangle(0, (int)(66 * u), (int)(160 * u), (int)(396 * u), new Color(0, 0, 0, (int)(130 * sbPanelAlpha)));
        void Header(string text, float y) => Game.TextBold(text, 0, y * u, 10 * u, Color.White);
        void Check(string text, float y, bool on, bool enabled = true, float x = 2, float alpha = 1)
        {
            var r = SbRow(x, y);
            // stable's checkbox (the options' pink circle) at 0.7 of its size
            var c = ColorAlpha(enabled ? Color.White : Color.Gray, alpha);
            var pink = ColorAlpha(enabled ? new Color(235, 117, 140, 255) : Color.Gray, alpha);
            var at = new Vector2(r.X + 5 * u, r.Y + 6.5f * u);
            if (on) DrawCircleV(at, 2.7f * u, pink);
            else DrawRing(at, 2.1f * u, 2.7f * u, 0, 360, 24, pink);
            Game.Text(text, r.X + 14 * u, r.Y, 9 * u, c);
        }
        Header("Layer Toggles", 80);
        for (int i = 0; i < SbLayerRows.Length; i++) Check(SbLayerRows[i].Label, SbLayerRows[i].Y, sbShow[i]);
        Check("Hit Objects", 170, sbHitObjects, mode != 3);
        Header("Transformation", 200);
        foreach (var (label, tool, y) in SbToolRows) Check(label, y, sbTool == tool);
        if (sbPanelAlpha > 0)
        {
            bool canFlip = sbSel is { Editable: true };
            int now = (int)Math.Round(time);
            Check("Vector Scale", 215, sbTool == "V", true, 72, sbPanelAlpha);
            Check("Horizontal Flip", 230, sbSel != null && sbSel.Cmds.Any(c => IsFlip(c, "H") && c.Start <= now && now <= c.End), canFlip, 72, sbPanelAlpha);
            Check("Vertical Flip", 245, sbSel != null && sbSel.Cmds.Any(c => IsFlip(c, "V") && c.Start <= now && now <= c.End), canFlip, 72, sbPanelAlpha);
        }
        var run = sbSel == null ? null : SbRunning(sbSel);
        bool can = sbSel is { Editable: true };
        Header("Tweening", 290);
        Check("Enabled", 305, run != null, can);
        Header("Easing", 320);
        Check("In", 335, run?.Easing == 2, can && run != null);
        Check("Out", 350, run?.Easing == 1, can && run != null);
        Header("Origin", 380);
        Check("TopLeft", 395, sbSel?.Origin == "TopLeft", can);
        Check("Centre", 410, sbSel?.Origin == "Centre", can);
        Header("Advanced", 425);
        Check("Diff Specific", 440, sbSel is { Osb: false }, can); // (a little higher than stable's: this bottom bar is taller)

        // the Sprite Library button
        var lb = SbLibraryButton();
        DrawRectangleRounded(lb, 0.2f, 6, new Color(154, 205, 50, 220));
        Game.Text("Sprite", lb.X + lb.Width / 2 - Game.TextWidth("Sprite", 10 * u) / 2, lb.Y + 8 * u, 10 * u, Color.White);
        Game.Text("Library", lb.X + lb.Width / 2 - Game.TextWidth("Library", 10 * u) / 2, lb.Y + 20 * u, 10 * u, Color.White);

        if (sbColourPick)
        {
            var first = ColourSwatch(0);
            var box = new Rectangle(first.X - 10 * u, first.Y - 26 * u, 8 * 25 * u + 17 * u, 6 * 21 * u + 34 * u);
            DrawRectangleRec(box, new Color(240, 240, 240, 255));
            DrawRectangleLinesEx(box, 1, new Color(160, 160, 160, 255));
            Game.Text("Basic colours:", box.X + 8 * u, box.Y + 6 * u, 10 * u, Color.Black);
            for (int i = 0; i < BasicColours.Length; i++)
            {
                var r = ColourSwatch(i);
                uint c = BasicColours[i];
                DrawRectangleRec(r, new Color((int)(c >> 16 & 0xFF), (int)(c >> 8 & 0xFF), (int)(c & 0xFF), 255));
                DrawRectangleLinesEx(r, 1, CheckCollisionPointRec(mouse, r) ? Color.Black : new Color(130, 130, 130, 255));
            }
        }

        if (sbLibrary)
        {
            var p = SbLibraryPanel();
            DrawRectangleRec(p, new Color(20, 20, 30, 240));
            DrawRectangleLinesEx(p, 1, ColorAlpha(Color.White, 0.4f));
            Game.TextBold("Sprite Library", p.X + 8 * u, p.Y + 5 * u, 12 * u, Color.White);
            if (sbImages.Count == 0) Game.Text("No images in this map's folder.", p.X + 8 * u, p.Y + 28 * u, 10 * u, Color.LightGray);
            for (int i = 0; i < 16 && i + sbLibraryScroll < sbImages.Count; i++)
            {
                var r = new Rectangle(p.X + 4 * u, p.Y + (24 + i * 17) * u, p.Width - 8 * u, 16 * u);
                if (CheckCollisionPointRec(mouse, r)) DrawRectangleRec(r, new Color(255, 160, 40, 120));
                Game.Text(sbImages[i + sbLibraryScroll], r.X + 4 * u, r.Y + 3 * u, 10 * u, Color.White);
            }
        }
    }

    void DrawDesignLaneLabels(Rectangle r, float u)
    {
        string[] labels = sbLanesAlt ? ["Scale", "Fade", "Rotation", "Colour"] : ["Movement", "Scale", "Fade", "Rotation"];
        float laneH = r.Height / 4;
        for (int i = 0; i < 4; i++) Game.Text(labels[i], r.X - 4 * u - Game.TextWidth(labels[i], 7 * u), r.Y + laneH * i + (laneH - 7 * u) / 2, 7 * u, Color.White);
        float sd = Game.H / 768f;
        for (int i = 0; i < 2; i++) // (stable's: the play button turned down and up)
            skin.DefaultTex("editor-button-play")?.Draw(SbLaneButton(i, u), 0.7f * sd, ColorAlpha(Color.White, sbLanesAlt == (i == 0) ? 0.4f : 1), i == 0 ? 90 : -90);
        string[] buttons = ["editor-timeline-plus", "editor-timeline-minus", "editor-timeline-left", "editor-timeline-right"];
        for (int i = 0; i < 4; i++) skin.DefaultTex(buttons[i])?.Draw(SbKeyButton(i, u), 0.8f * sd, Color.White);
        Game.Text("Keyframe Control", 160 * u, 66 * u, 10 * u, Color.White);
    }

    // the timeline in Design: the picked sprite's transformations in four lanes (Movement, Scale, Fade, Rotation, or
    // with the arrows Scale, Fade, Rotation, Colour)
    void DrawDesignLanes(Rectangle r, float u)
    {
        string[] lanes = sbLanesAlt ? ["S", "F", "R", "C"] : ["M", "S", "F", "R"];
        float laneH = r.Height / 4;
        for (int i = 1; i < 4; i++) DrawLineEx(new(r.X, r.Y + laneH * i), new(r.X + r.Width, r.Y + laneH * i), 1, Color.Gray);
        if (sbSel == null) return;
        Color[] colours = [new(255, 160, 40, 255), new(120, 200, 255, 255), new(200, 200, 200, 255), new(255, 100, 160, 255)];
        for (int i = 0; i < 4; i++)
            foreach (var c in sbSel.Cmds.Where(c => c.Type == lanes[i] || lanes[i] == "M" && c.Type is "MX" or "MY" || lanes[i] == "S" && c.Type == "V"))
            {
                float x0 = TimelineX(c.Start), x1 = Math.Max(x0 + 3 * u, TimelineX(c.End)), y = r.Y + laneH * i + 1;
                if (x1 < r.X || x0 > r.X + r.Width) continue;
                DrawRectangleRec(new Rectangle(x0, y, x1 - x0, laneH - 2), ColorAlpha(colours[i], c.Start == c.End ? 1 : 0.6f));
                DrawRectangleRec(new Rectangle(x0 - 1, y, 2, laneH - 2), Color.White);
                DrawRectangleRec(new Rectangle(x1 - 1, y, 2, laneH - 2), Color.White);
            }
    }
    // Design → Move all elements in time... (stable's): the storyboard's samples, sprites and animations (their
    // commands, a loop's start, a trigger's times - not what's in a loop or trigger, timed from those) and the video
    // moved by an amount of milliseconds, in the .osu and the .osb
    void MoveAllElementsPrompt() => prompt = new("Enter the number of milliseconds to shift by:", "0", text =>
    {
        if (!int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int ms) || ms == 0) return;
        Change(() =>
        {
            if (sections.FirstOrDefault(s => s.Name == "Events") is { } ev)
                for (int i = 0; i < ev.Lines.Count; i++) ev.Lines[i] = ShiftElementLine(ev.Lines[i], ms);
            if (osbLines != null)
            {
                var (from, to) = OsbEvents();
                for (int i = from; i < to; i++) osbLines[i] = ShiftElementLine(osbLines[i], ms);
                OsbChanged();
            }
        });
        sbParsed = null;
    }, "OK", "Cancel");

    static string ShiftElementLine(string line, int ms)
    {
        int depth = 0;
        while (depth < line.Length && line[depth] is ' ' or '_') depth++;
        var f = line[depth..].Split(',');
        int[] times = depth switch
        {
            0 => f[0].Trim() is "Sample" or "5" or "Video" or "1" ? [1] : [],
            1 => f[0].Trim() == "L" ? [1] : [2, 3], // (a command's start and end, a trigger's too)
            _ => [],
        };
        bool changed = false;
        foreach (int i in times)
            if (i < f.Length && int.TryParse(f[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out int t)) { f[i] = (t + ms).ToString(CultureInfo.InvariantCulture); changed = true; }
        return changed ? line[..depth] + string.Join(',', f) : line;
    }
}
