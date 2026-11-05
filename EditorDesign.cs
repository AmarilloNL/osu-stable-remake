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
        public string File => Head.Length > 3 ? Head[3].Trim('"') : "";
        public double X, Y;
    }

    static readonly string[] SbLayerNames = ["Background", "Fail", "Pass", "Foreground", "Overlay"];
    static int Dims(string type) => type switch { "M" or "V" => 2, "C" => 3, "MX" or "MY" or "S" or "F" or "R" => 1, _ => 0 };
    static string SbNum(double v) => Math.Round(v, 3).ToString(CultureInfo.InvariantCulture);

    List<SbSprite> sbSprites = [];
    Section? sbParsed;
    int sbParsedCount = -1;
    SbSprite? sbSel, sbHover;
    string sbTool = "M";
    readonly bool[] sbShow = [true, true, true, true, true];
    bool sbLanesAlt, sbLibrary;
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
    bool osbModified;
    int sbParsedOsbCount = -1;

    void LoadOsb()
    {
        osbModified = false;
        osbLines = null;
        try
        {
            var all = Directory.EnumerateFiles(info.Folder, "*.osb").ToList();
            osbPath = all.FirstOrDefault(f => string.Equals(Path.GetFileName(f), Path.GetFileName(NewOsbPath()), StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault();
            if (osbPath != null) osbLines = [.. File.ReadAllLines(osbPath)];
        }
        catch (IOException) { osbPath = null; osbLines = null; }
        sbParsed = null;
    }
    // stable's name for a map's .osb
    string NewOsbPath()
    {
        string name = $"{Kv("Metadata", "Artist")} - {Kv("Metadata", "Title")} ({Kv("Metadata", "Creator")}).osb";
        foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c.ToString(), "");
        return Path.Combine(info.Folder, name);
    }
    // an undo state: the .osu as it would be written, then the .osb's lines
    string Snapshot() => string.Join("\n", Write()) + (osbLines != null ? OsbMark + string.Join("\n", osbLines) : "");
    // the .osb's [Events] lines (from, to)
    (int From, int To) OsbEvents()
    {
        if (osbLines == null) return (0, 0);
        int s = osbLines.FindIndex(l => l.Trim() == "[Events]") + 1, e = s;
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
        for (int i = from; i < to; i++)
        {
            string line = lines[i];
            if (line.Length > 0 && line[0] is ' ' or '_')
            {
                if (cur == null) continue;
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
        osbModified |= sp.Osb;
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
                if (before == null || after == null || before == after) return;
                if (before.Start == before.End) sp.Cmds.Remove(before);
                if (after.Start == after.End) sp.Cmds.Remove(after);
                sp.Cmds.Add(new SbCmd { Type = sbTool, Start = before.End, End = after.Start, From = [.. before.To], To = [.. after.From] });
            }
            else
            {
                var c = list.FirstOrDefault(x => x.Start < t && t < x.End);
                if (c == null) return;
                sp.Cmds.Remove(c);
                if (!list.Any(x => x != c && x.End == c.End)) sp.Cmds.Add(new SbCmd { Type = sbTool, Start = c.End, End = c.End, From = [.. c.To], To = [.. c.To] });
                if (!list.Any(x => x != c && x.End == c.Start)) sp.Cmds.Add(new SbCmd { Type = sbTool, Start = c.Start, End = c.Start, From = [.. c.From], To = [.. c.From] });
            }
        });
    }

    SbCmd? SbRunning(SbSprite sp) { int t = (int)Math.Round(time); return sp.Cmds.FirstOrDefault(c => c.Type == sbTool && c.Start < t && t < c.End); }

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

    static Vector2 OriginOf(string origin) => origin switch
    {
        "TopLeft" => new(0, 0), "TopCentre" => new(0.5f, 0), "TopRight" => new(1, 0),
        "CentreLeft" => new(0, 0.5f), "CentreRight" => new(1, 0.5f),
        "BottomLeft" => new(0, 1), "BottomCentre" => new(0.5f, 1), "BottomRight" => new(1, 1),
        _ => new(0.5f, 0.5f),
    };

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
    static readonly (string Label, string Tool, float Y)[] SbToolRows = [("Move", "M", 215), ("Scale", "S", 230), ("Fade", "F", 245), ("Rotate", "R", 260)];
    Rectangle SbRow(float x, float y) { float u = U; return new Rectangle(x * u, y * u, 70 * u, 13 * u); }
    Rectangle SbLibraryButton() { float u = U; return new Rectangle(Game.W - 70 * u, 372 * u, 66 * u, 40 * u); }
    Rectangle SbLibraryPanel() { float u = U; return new Rectangle(Game.W / 2f - 200 * u, 90 * u, 400 * u, 300 * u); }

    void UpdateDesign(Vector2 mouse, float u)
    {
        var ev = EventsSection;
        if (!ReferenceEquals(ev, sbParsed) || ev.Lines.Count != sbParsedCount || !ReferenceEquals(osbLines, sbParsedOsb) || (osbLines?.Count ?? -1) != sbParsedOsbCount) SbParse();
        if (sbDirty && (sbDrag == null || Game.NowMs - sbBuiltAt > 150))
        {
            try { sbView = Storyboard.LoadLines(info, ev.Lines, osbLines, Kv("General", "WidescreenStoryboard") == "1", sbView); }
            catch (IOException) { }
            if (sbView != null) { sbView.BothStates = true; Array.Copy(sbShow, sbView.ShowLayer, 5); }
            sbDirty = false;
            sbBuiltAt = Game.NowMs;
        }
        bool click = Input.IsMouseButtonPressed(MouseButton.Left);

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
                osbModified |= sel.Osb;
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
                if (Input.CtrlHeld && Input.IsKeyPressed(KeyboardKey.One + k)) SbChange(sel, () => { sel.Head[1] = SbLayerNames[k]; sel.RawHead = null; });
        }

        // the panel
        for (int i = 0; i < SbLayerRows.Length; i++)
            if (click && CheckCollisionPointRec(mouse, SbRow(2, SbLayerRows[i].Y))) { sbShow[i] = !sbShow[i]; if (sbView != null) Array.Copy(sbShow, sbView.ShowLayer, 5); return; }
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
            if (click && CheckCollisionPointRec(mouse, SbRow(2, 395 + o * 15)) && sbSel is { Editable: true } os)
            {
                SbChange(os, () => { os.Head[2] = o == 0 ? "TopLeft" : "Centre"; os.RawHead = null; });
                return;
            }
        if (click && CheckCollisionPointRec(mouse, SbLibraryButton())) { SbOpenLibrary(); return; }
        if (click && mouse.X < 160 * u && mouse.Y > 66 * u && mouse.Y < 462 * u) return; // (the panel's own space)
        // the lanes' toggle (the arrows before them)
        if (click && CheckCollisionPointRec(mouse, new Rectangle(TimelineRect().X - 20 * u, 64 * u, 20 * u, 12 * u))) { sbLanesAlt = !sbLanesAlt; return; }

        // picking and dragging
        var sbMouse = SbFromScreen(mouse);
        bool held = Input.IsMouseButtonDown(MouseButton.Left);
        if (sbDrag is { } d)
        {
            if (!held || sbSel is not { Editable: true } ds) { sbDrag = null; sbDirty = true; return; }
            var m = SbFromScreen(mouse);
            if (Input.ShiftHeld) { if (Math.Abs(m.X - d.Mouse.X) > Math.Abs(m.Y - d.Mouse.Y)) m.Y = d.Mouse.Y; else m.X = d.Mouse.X; }
            float up = d.Mouse.Y - m.Y; // (dragging up raises scale, fade and rotation)
            double[] value = sbTool switch
            {
                "M" => [Math.Round(d.Value[0] + m.X - d.Mouse.X), Math.Round(d.Value[1] + m.Y - d.Mouse.Y)],
                "S" => [Math.Clamp((d.Value[0] * 100 + up) / 100, 0.1, 5)],
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
        if (under.Count == 0 && sbSel is { Editable: true } hidden && SbCorners(hidden) == null) { sbDrag = (sbMouse, SbValue(hidden, sbTool, time), false); return; }
        if (under.Count == 0) { sbSel = null; return; }
        int idx = sbSel == null ? -1 : under.IndexOf(sbSel);
        sbSel = idx > 0 ? under[idx - 1] : idx == 0 && under.Count > 1 ? under[^1] : under[^1];
        if (idx >= 0 && under.Count == 1) sbSel = under[0];
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
        osbModified |= osb;
        SbParse();
        sbSel = sbSprites.FirstOrDefault(s => s.Osb == osb && s.Line == at);
    }

    // stable's new sprite: centred at the storyboard's middle on the foreground, with a keyframe at the current time;
    // in the .osb (made if the map has none) - "Diff Specific" moves it to the difficulty
    void SbAdd(string file)
    {
        sbLibrary = false;
        undo.Add(Snapshot()); redo.Clear();
        int t = (int)Math.Round(time);
        string[] lines = [$"Sprite,Foreground,Centre,\"{file}\",320,240", sbTool switch
        {
            "S" => $" S,0,{t},,1", "F" => $" F,0,{t},,1", "R" => $" R,0,{t},,0", _ => $" M,0,{t},,320,240",
        }];
        osbLines ??= ["[Events]", "//Background and Video events", "//Storyboard Layer 0 (Background)", "//Storyboard Layer 1 (Fail)",
            "//Storyboard Layer 2 (Pass)", "//Storyboard Layer 3 (Foreground)", "//Storyboard Layer 4 (Overlay)", "//Storyboard Sound Samples"];
        SbInsert(osbLines, true, lines, "Foreground");
    }

    // Diff Specific: the picked sprite over to the difficulty's [Events], or back to the .osb
    void SbMove(SbSprite sp)
    {
        undo.Add(Snapshot()); redo.Clear();
        var lines = SpriteLines(sp);
        LinesOf(sp).RemoveRange(sp.Line, sp.Count);
        osbModified |= sp.Osb;
        if (!sp.Osb) osbLines ??= ["[Events]"];
        SbInsert(sp.Osb ? EventsSection.Lines : osbLines!, !sp.Osb, lines, sp.Layer);
    }

    void DrawDesign(float u)
    {
        int w = Game.W, h = Game.H;
        sbView?.Draw(time, w, h, 1, overlay: false);
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
        Game.Text(string.Format(CultureInfo.InvariantCulture, "x:{0:0} y:{1:0}\n{2:#,0}ms", sm.X, sm.Y, time), w - 200 * u, 70 * u, 10 * u, Color.White);

        // the panel
        DrawRectangle(0, (int)(66 * u), (int)(160 * u), (int)(396 * u), new Color(0, 0, 0, 130));
        void Header(string text, float y) => Game.TextBold(text, 0, y * u, 10 * u, Color.White);
        void Check(string text, float y, bool on, bool enabled = true)
        {
            var r = SbRow(2, y);
            var c = enabled ? Color.White : Color.Gray;
            DrawRectangleLinesEx(new Rectangle(r.X + 1 * u, r.Y + 2 * u, 9 * u, 9 * u), 1, c);
            if (on) DrawRectangleRec(new Rectangle(r.X + 3 * u, r.Y + 4 * u, 5 * u, 5 * u), new Color(255, 180, 60, 255));
            Game.Text(text, r.X + 14 * u, r.Y, 9 * u, c);
        }
        Header("Layer Toggles", 80);
        for (int i = 0; i < SbLayerRows.Length; i++) Check(SbLayerRows[i].Label, SbLayerRows[i].Y, sbShow[i]);
        Check("Hit Objects", 170, false, enabled: false);
        Header("Transformation", 200);
        foreach (var (label, tool, y) in SbToolRows) Check(label, y, sbTool == tool);
        Check("Colour", 275, false, enabled: false);
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
        Game.Text(sbLanesAlt ? "<" : ">", r.X - 16 * u, 64 * u, 9 * u, Color.White);
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
}
