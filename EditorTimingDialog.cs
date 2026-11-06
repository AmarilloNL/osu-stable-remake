using System.Globalization;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// stable's Timing Setup Panel ("Timing and Control Points", F6): a Windows dialog (582x422). On the right the map's
// timing points as a list (All / Timing Points / Inherited Points; offset, BPM or velocity, meter, sample set, volume,
// kiai) with + and - to add and remove; on the left the selected point's settings - Timing (a timing point's BPM,
// offset and time signature, an inherited one's offset and slider velocity, or with several selected: moving them,
// their time signature and velocity), Audio (sample set, custom samples, volume) and Style (kiai, omit first bar line).
// Changes show at once; Cancel or Escape takes them all back. OK applies the options under "When applying timing
// changes...": objects scaled with their timing points (their place in beats kept) or snapped to the new timing,
// sliders lengthened or shortened to end on the beat snap divisor, and bookmarks and the preview point moved with the
// first timing point.
sealed partial class Editor
{
    sealed class TimingDialog : WinUi
    {
        public int ListTab, LeftTab, Scroll, Divisor = 3;
        public List<ETiming> Sel = [];
        public ETiming? Anchor; // (Shift+click selects from here)
        public bool Inherit = true, Scale, SnapObjects, Resnap, Bookmarks, WasModified;
        public string Before = ""; // the map at open: Cancel puts it back, OK makes the dialog's changes one undo state
        public List<(ETiming P, double Time, double Beat)> Reds = []; // the timing points as they were (scaling objects)
        public double? FirstOffset; // (none: the map had no timing points)
        public string EditText = ""; // a number box being typed into (Focus)
        public Action<double>? CommitTo; // (where it goes, bound to the points selected when it was clicked)
        public double FocusValue;
        public bool Commit; // (Enter, Tab or a click anywhere: the typed number goes in first)
        public double MoveBy;
    }
    TimingDialog? timingDlg;
    const float TdW = 582, TdH = 422;
    static readonly string[] TimingListTabs = ["All", "Timing Points", "Inherited Points"];
    static readonly string[] TimingLeftTabs = ["Timing", "Audio", "Style"];
    static readonly int[] SnapDivisors = [1, 2, 3, 4, 5, 6, 7, 8, 9, 12, 16]; // (the domain box under "When applying timing changes...")
    const float TdRowH = 17, TdHeadH = 24;

    void OpenTimingDialog()
    {
        var d = new TimingDialog { Before = Snapshot(), WasModified = modified };
        d.Reds = [.. timing.Where(p => p.Red).Select(p => (p, p.Time, p.Beat))];
        d.FirstOffset = timing.Any(p => p.Red) ? timing.Where(p => p.Red).Min(p => p.Time) : null;
        d.Divisor = Math.Max(0, Array.IndexOf(SnapDivisors, divisor));
        if (d.Divisor < 0) d.Divisor = 3;
        if (timing.Where(p => p.Time <= time + 1).OrderBy(p => p.Time).LastOrDefault() is { } at) d.Sel.Add(at);
        timingDlg = d;
        TimingScrollTo(d, d.Sel.FirstOrDefault());
    }

    List<ETiming> TimingList(TimingDialog d) => [.. timing.Where(p => d.ListTab == 0 || (d.ListTab == 1) == p.Red).OrderBy(p => p.Time)];
    int TimingRows => (int)((245 - TdHeadH) / TdRowH);
    void TimingScrollTo(TimingDialog d, ETiming? p)
    {
        int i = p == null ? -1 : TimingList(d).IndexOf(p);
        if (i >= 0 && (i < d.Scroll || i >= d.Scroll + TimingRows)) d.Scroll = Math.Max(0, i - TimingRows / 2);
    }

    // true while the dialog is open (it has all the input)
    bool UpdateTimingDialog(Vector2 mouse)
    {
        var d = timingDlg!;
        if (d.UpdateFrame(mouse, TdW, TdH)) { TimingCancel(); return true; }
        if (d.Drag != null) return true;
        if (Input.IsKeyPressed(KeyboardKey.Escape))
        {
            if (d.Open >= 0) d.Open = -1;
            else if (d.Focus >= 0) d.Focus = -1;
            else TimingCancel();
            return true;
        }
        if (Input.IsKeyPressed(KeyboardKey.F6) && d.Focus < 0) { TimingOk(); return true; }
        if (d.Focus >= 0)
        {
            WinUi.Type(ref d.EditText);
            if (Input.IsKeyPressed(KeyboardKey.Enter) || Input.IsKeyPressed(KeyboardKey.Tab)) d.Commit = true;
        }
        else if (Input.IsKeyPressed(KeyboardKey.Enter)) { TimingOk(); return true; }
        if (Input.IsMouseButtonPressed(MouseButton.Left) && d.Focus >= 0) d.Commit = true; // (a click anywhere: the typed number goes in first)
        if (Input.IsKeyPressed(KeyboardKey.Delete) && d.Focus < 0 && d.Sel.Count > 0) { TimingRemove(d); return true; }
        float wheel = Input.GetMouseWheelMove();
        if (wheel != 0 && CheckCollisionPointRec(mouse, d.R(251, 28, 323, 245)))
            d.Scroll = Math.Clamp(d.Scroll - Math.Sign(wheel) * 3, 0, Math.Max(0, TimingList(d).Count - TimingRows));
        TimingUi(d, true);
        return true;
    }

    void DrawTimingDialog()
    {
        var d = timingDlg!;
        if (float.IsNaN(d.Pos.X)) return;
        d.DrawFrame("Timing and Control Points", TdW, TdH);
        TimingUi(d, false);
    }

    void TimingCancel()
    {
        var d = timingDlg!;
        timingDlg = null;
        string now = Snapshot();
        if (now == d.Before) return;
        Load(now, d.Before);
        modified = d.WasModified;
    }

    void TimingOk()
    {
        var d = timingDlg!;
        if (!timing.Any(p => p.Red)) { prompt = new("At least one timing point is required.", null, _ => { }, "OK", ""); return; }
        timingDlg = null;
        bool scale = d.Scale && d.Reds.Any(r => timing.Contains(r.P) && (r.P.Time != r.Time || r.P.Beat != r.Beat));
        double shift = d.FirstOffset is { } first ? timing.Where(p => p.Red).Min(p => p.Time) - first : 0; // (stable's: the first timing point's move)
        bool options = scale || d.SnapObjects || d.Resnap || d.Bookmarks && shift != 0;
        if (!options && Snapshot() == d.Before) return;
        // the whole dialog: one undo state
        undo.Add(d.Before);
        if (undo.Count > 100) undo.RemoveAt(0);
        redo.Clear();
        if (!options) return;
        Change(() =>
        {
            if (scale)
            {
                // each object keeps its place in beats from the timing point it was under
                var was = d.Reds.OrderBy(r => r.Time).ToList();
                foreach (var o in objs)
                {
                    var r = was.LastOrDefault(x => x.Time <= o.Time + 1);
                    if (r.P == null) r = was[0];
                    if (!timing.Contains(r.P)) continue;
                    double f = r.P.Beat / r.Beat, len = o.End - o.Time;
                    o.Time = Math.Round(r.P.Time + (o.Time - r.Time) * f);
                    o.End = o.Time + (o.Spinner || (o.Type & 128) != 0 ? Math.Round(len * f) : len);
                    o.Raw = null;
                }
            }
            else if (d.SnapObjects)
            {
                int div = SnapDivisors[d.Divisor];
                double SnapTo(double t) { var red = RedAt(t); double step = red.Beat / div; return Math.Round(red.Time + Math.Round((t - red.Time) / step) * step); }
                foreach (var o in objs)
                {
                    double t = SnapTo(o.Time), len = o.End - o.Time;
                    double end = o.Spinner || (o.Type & 128) != 0 ? SnapTo(o.End) : t + len;
                    if (t == o.Time && end == o.End) continue;
                    o.Time = t; o.End = Math.Max(t, end); o.Raw = null;
                }
            }
            if (d.Resnap)
            {
                // each slider's length made to end its span on the beat snap divisor under the new timing
                int div = SnapDivisors[d.Divisor];
                var sorted = timing.OrderBy(p => p.Time).ToList();
                foreach (var o in objs.Where(o => o.Slider && o.Path.Length > 1))
                {
                    double span = (SliderEnd(o, sorted) - o.Time) / o.Slides;
                    var red = RedAt(o.Time + span);
                    double step = red.Beat / div, to = Math.Round(red.Time + Math.Round((o.Time + span - red.Time) / step) * step);
                    if (span <= 0 || to <= o.Time || Math.Abs(to - (o.Time + span)) <= 1) continue;
                    o.Length *= (to - o.Time) / span;
                    o.Raw = null;
                }
            }
            if (d.Bookmarks && shift != 0)
            {
                var marks = Bookmarks();
                if (marks.Count > 0) SetKv("Editor", "Bookmarks", string.Join(",", marks.Select(m => (int)(m + shift))));
                if (double.TryParse(Kv("General", "PreviewTime"), NumberStyles.Float, Inv, out double preview) && preview >= 0)
                    SetKv("General", "PreviewTime", ((int)(preview + shift)).ToString(Inv));
            }
        }, undoable: false);
    }

    void TimingRemove(TimingDialog d)
    {
        var gone = d.Sel.ToList();
        Change(() => timing.RemoveAll(gone.Contains), undoable: false);
        d.Sel.Clear();
    }

    void TimingAdd(TimingDialog d)
    {
        var from = d.Sel.FirstOrDefault() ?? (d.Inherit ? timing.Where(p => p.Time <= time + 1).OrderBy(p => p.Time).LastOrDefault() : null);
        bool red = d.ListTab == 1 || d.ListTab == 0 && d.Sel.Count == 0;
        var p = new ETiming { Time = Math.Round(time), Red = red };
        if (from != null) { p.Meter = from.Meter; p.Set = from.Set; p.Index = from.Index; p.Volume = from.Volume; p.Effects = from.Effects; }
        p.Beat = red ? (timing.Any(t => t.Red) ? RedAt(time).Beat : 500) : from is { Red: false } ? from.Beat : -100;
        Change(() => timing.Add(p), undoable: false);
        d.Sel = [p];
        TimingScrollTo(d, p);
    }

    // a change to the given points (no undo state of its own: the dialog's OK makes one)
    void TimingEdit(List<ETiming> on, Action<ETiming> edit, Func<ETiming, bool>? only = null)
    {
        var points = on.Where(p => timing.Contains(p) && (only == null || only(p))).ToList();
        if (points.Count == 0) return;
        Change(() => { foreach (var p in points) { edit(p); p.Raw = null; } }, undoable: false);
    }

    // the dialog's controls, drawn (act false) or taking the mouse (act true) from one layout
    void TimingUi(TimingDialog d, bool act)
    {
        d.Begin(act, d.Open >= 0);
        float s = AiS, fs = WinUi.Fs;
        var sel = d.Sel.Where(timing.Contains).ToList();
        if (sel.Count != d.Sel.Count) d.Sel = sel;
        var one = sel.Count == 1 ? sel[0] : null;
        if (act && d.Commit)
        {
            var to = d.CommitTo;
            d.Commit = false; d.Focus = -1; d.CommitTo = null;
            if (to != null && double.TryParse(d.EditText, NumberStyles.Float, Inv, out double typed) && double.IsFinite(typed) && typed != d.FocusValue) to(typed);
            sel = d.Sel.Where(timing.Contains).ToList();
            one = sel.Count == 1 ? sel[0] : null;
        }

        // a number box that can be typed into (a click on its text): set gets what's typed, or a step from its arrows
        void Number(int id, double v, double min, double max, double step, string format, float x, float y, float w, float h, bool on, Action<double> set)
        {
            if (act && on && d.Hit(d.R(x, y, w - 16, h))) { d.Focus = id; d.EditText = v.ToString(format, Inv); d.FocusValue = v; d.CommitTo = t => set(Math.Clamp(t, min, max)); return; }
            if (!act && d.Focus == id)
            {
                var r = d.R(x, y, w, h);
                DrawRectangleRec(r, Color.White);
                DrawRectangleLinesEx(r, 1, WinUi.Blue);
                Game.Text(d.EditText + ((int)(Game.NowMs / 500) % 2 == 0 ? "|" : ""), r.X + 3 * s, r.Y + (r.Height - fs) / 2, fs, WinUi.Text);
                return;
            }
            double nv = v;
            if (d.Numeric(ref nv, min, max, step, format, x, y, w, h, on)) set(nv);
        }

        // ---- the list (right)
        d.Tabs(ref d.ListTab, TimingListTabs, 248, 4, 330, 250);
        var list = TimingList(d);
        d.Scroll = Math.Clamp(d.Scroll, 0, Math.Max(0, list.Count - TimingRows));
        float lx = 251, ly = 28;
        float[] cols = [81, 58, 44, 51, 45, 25]; // (the point's icon in the offset's column, like stable's list)
        string[] heads = ["Offset", "BPM", "Meter", "Sample", "Vol.", "Ki"];
        if (!act)
        {
            var box = d.R(lx, ly, 323, 245);
            DrawRectangleRec(box, Color.White);
            DrawRectangleLinesEx(box, 1, new Color(130, 135, 144, 255));
            var head = d.R(lx + 1, ly + 1, 321, TdHeadH);
            DrawRectangleRec(head, Color.White);
            float hx = lx + 1;
            for (int c = 0; c < cols.Length; c++)
            {
                var hr = d.R(hx, ly + 1, cols[c], TdHeadH);
                if (c == 0) Game.Text(heads[c], hr.X + hr.Width - 6 * s - Game.TextWidth(heads[c], fs), hr.Y + (hr.Height - fs) / 2, fs, WinUi.Text);
                else Game.Text(heads[c], hr.X + 6 * s, hr.Y + (hr.Height - fs) / 2, fs, WinUi.Text);
                hx += cols[c];
                DrawRectangle((int)d.R(hx, 0, 0, 0).X, (int)hr.Y, 1, (int)hr.Height, new Color(229, 229, 229, 255));
            }
            DrawRectangle((int)head.X, (int)(head.Y + head.Height), (int)head.Width, 1, new Color(229, 229, 229, 255));
        }
        for (int i = 0; i < TimingRows && i + d.Scroll < list.Count; i++)
        {
            var p = list[i + d.Scroll];
            var row = d.R(lx + 1, ly + 1 + TdHeadH + i * TdRowH, 321, TdRowH);
            if (act)
            {
                if (!d.Hit(row)) continue;
                if (Input.CtrlHeld) { if (!d.Sel.Remove(p)) d.Sel.Add(p); }
                else if (Input.ShiftHeld && d.Anchor != null && list.IndexOf(d.Anchor) is var a and >= 0)
                    d.Sel = [.. list.Skip(Math.Min(a, i + d.Scroll)).Take(Math.Abs(a - (i + d.Scroll)) + 1)];
                else { d.Sel = [p]; Seek(p.Time); }
                if (!Input.ShiftHeld) d.Anchor = p;
                d.Focus = -1;
                continue;
            }
            bool on = sel.Contains(p);
            DrawRectangleRec(row, on ? WinUi.Blue : (i + d.Scroll) % 2 == 1 ? new Color(247, 247, 247, 255) : Color.White);
            var fg = on ? Color.White : WinUi.Text;
            string set = p.Set switch { 1 => "N", 2 => "S", 3 => "D", _ => "?" } + (p.Index > 0 ? ":C" + p.Index : "");
            int at = (int)p.Time;
            string[] cells = [$"{at / 60000:00}:{at % 60000 / 1000:00}:{at % 1000:000}", p.Red ? p.Bpm.ToString("0.000", Inv) : "x" + p.Sv.ToString("0.00", Inv), p.Red ? p.Meter + "/4" : "", set, p.Volume + "%"];
            float cx = row.X;
            var icon = new Rectangle(cx + 4 * s, row.Y + 3.5f * s, 10 * s, 10 * s);
            DrawRectangleRec(icon, p.Red ? new Color(200, 40, 40, 255) : new Color(40, 160, 60, 255));
            DrawRectangleLinesEx(icon, 1, new Color(0, 0, 0, 90));
            for (int c = 0; c < cells.Length; c++)
            {
                float w = cols[c] * s;
                if (c == 0) Game.Text(cells[c], cx + w - 6 * s - Game.TextWidth(cells[c], fs), row.Y + (row.Height - fs) / 2, fs, fg);
                else Game.Text(cells[c], cx + 6 * s, row.Y + (row.Height - fs) / 2, fs, fg);
                cx += w;
            }
            if ((p.Effects & 1) != 0) WinUi.CheckMark(new Rectangle(cx + 4 * s, row.Y + 2 * s, 13 * s, 13 * s), fg);
        }
        if (!act && list.Count > TimingRows)
        {
            // (the list's scroll bar)
            var bar = d.R(lx + 323 - 17, ly + 1, 16, 243);
            DrawRectangleRec(bar, new Color(240, 240, 240, 255));
            float frac = TimingRows / (float)list.Count, top = d.Scroll / (float)list.Count;
            DrawRectangleRec(new Rectangle(bar.X + 3 * s, bar.Y + bar.Height * top, bar.Width - 6 * s, Math.Max(10 * s, bar.Height * frac)), new Color(192, 192, 192, 255));
        }
        d.Check(ref d.Inherit, 252, 282, "Inherit previous timing settings");
        // + and - (the tool strip)
        for (int k = 0; k < 2; k++)
        {
            var r = d.R(511 + k * 32, 279, 24, 24);
            bool on = k == 0 || sel.Count > 0;
            if (act) { if (on && d.Hit(r)) { if (k == 0) TimingAdd(d); else TimingRemove(d); } continue; }
            if (on && d.Hover(r)) { DrawRectangleRec(r, new Color(229, 241, 251, 255)); DrawRectangleLinesEx(r, 1, WinUi.Blue); Tip(k == 0 ? "Add Timing Section" : "Remove Timing Section"); }
            var c = new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2);
            var col = !on ? WinUi.Grey : k == 0 ? new Color(40, 150, 40, 255) : new Color(200, 40, 40, 255);
            DrawRectangleRec(new Rectangle(c.X - 7 * s, c.Y - 1.5f * s, 14 * s, 3 * s), col);
            if (k == 0) DrawRectangleRec(new Rectangle(c.X - 1.5f * s, c.Y - 7 * s, 3 * s, 14 * s), col);
        }

        // ---- the selected point's settings (left)
        d.Tabs(ref d.LeftTab, TimingLeftTabs, 4, 4, 238, 275);
        float px = 8, py = 26;
        bool any = sel.Count > 0;
        switch (d.LeftTab)
        {
            case 0 when sel.Count > 1:
            {
                d.Group(px + 6, py + 6, 218, 71, "General");
                d.Label(px + 12, py + 25, "Move selected offsets by:", true);
                Number(100, d.MoveBy, -300000, 300000, 1, "0", px + 15, py + 45, 120, 20, true, v => d.MoveBy = v);
                if (d.Button(px + 143, py + 43, 75, 23, "Move") && d.MoveBy != 0) { double by = d.MoveBy; TimingEdit(sel, p => p.Time += by); }
                Signature(px + 6, py + 83, 97, 22, 43, 64, sel.Where(p => p.Red).ToList());
                Velocity(px + 6, py + 186, 77, 22, 48, sel.Where(p => !p.Red).ToList());
                break;
            }
            case 0 when one is { Red: false }:
            {
                d.Group(px + 6, py + 6, 218, 129, "General");
                d.Label(px + 12, py + 42, "Offset", true);
                double t = one.Time;
                Number(101, t, -1e8, 1e8, 1, "0", px + 71, py + 40, 147, 20, true, v => TimingEdit(sel, p => p.Time = Math.Round(v)));
                if (d.Button(px + 12, py + 87, 206, 42, "Use Current Time")) { double now = Math.Round(time); TimingEdit(sel, p => p.Time = now); }
                d.Group(px + 6, py + 141, 218, 124, "Difficulty");
                d.Label(px + 12, py + 160, "Slider velocity multiplier:", true);
                Velocity(px + 6, py + 141, 0, 37, 63, [one], false);
                break;
            }
            case 0:
            {
                bool on = one != null;
                d.Group(px + 6, py + 6, 218, 129, "General");
                d.Label(px + 12, py + 25, "BPM", true, null, 0, on);
                d.Label(px + 12, py + 55, "Offset", true, null, 0, on);
                double bpm = one?.Bpm ?? 120, t = one?.Time ?? 0;
                Number(102, bpm, 15, 10000, 1, "0.000", px + 71, py + 23, 147, 20, on, v => TimingEdit(sel, p => p.Beat = 60000 / v));
                Number(103, t, -1e8, 1e8, 1, "0", px + 71, py + 53, 147, 20, on, v => TimingEdit(sel, p => p.Time = Math.Round(v)));
                if (d.Button(px + 12, py + 87, 206, 42, "Use Current Time", on)) { double now = Math.Round(time); TimingEdit(sel, p => p.Time = now); }
                Signature(px + 6, py + 141, 124, 31, 52, 73, one != null ? [one] : []);
                break;
            }
            case 1:
            {
                d.Group(px + 6, py + 6, 218, 117, "Sampleset");
                int set = one != null ? Math.Clamp(one.Set - 1, 0, 2) : sel.Count > 0 && sel.All(p => p.Set == sel[0].Set) ? Math.Clamp(sel[0].Set - 1, 0, 2) : -1;
                int was = set;
                d.ListBox(ref set, SampleSets, px + 14, py + 28, 103, 62, any);
                if (set != was && set >= 0) { int to = set + 1; TimingEdit(sel, p => p.Set = to); }
                int index = sel.Count > 0 && sel.All(p => p.Index == sel[0].Index) ? sel[0].Index : -1;
                int kind = index switch { 0 => 0, 1 => 1, >= 2 => 2, _ => -1 }, kindWas = kind;
                d.Radio(ref kind, 0, px + 123, py + 28, "Default", any);
                d.Radio(ref kind, 1, px + 123, py + 49, "Custom 1", any);
                d.Radio(ref kind, 2, px + 123, py + 70, " ", any);
                if (kind != kindWas) { int to = kind == 2 ? Math.Max(2, index) : kind; TimingEdit(sel, p => p.Index = to); }
                double custom = Math.Max(2, index);
                Number(104, custom, 2, 100, 1, "0", px + 139, py + 70, 54, 20, any && kind == 2, v => TimingEdit(sel, p => p.Index = (int)v));
                d.Link(px + 12, py + 99, "How to add custom sample overrides");
                d.Group(px + 6, py + 143, 218, 123, "Volume");
                int vol = sel.Count > 0 ? sel[0].Volume : 100, volWas = vol;
                d.Track(0, ref vol, 5, 100, px + 8, py + 163, 147, 5, false, any, 1);
                if (vol != volWas) { int to = vol; TimingEdit(sel, p => p.Volume = to); }
                double volume = vol;
                Number(105, volume, 5, 100, 1, "0", px + 162, py + 166, 43, 20, any, v => TimingEdit(sel, p => p.Volume = (int)v));
                d.Label(px + 205, py + 170, "%", false, null, 0, any);
                string[] sounds = ["Hit", "Whistle", "Finish", "Clap"];
                for (int i = 0; i < 4; i++)
                    if (d.Button(px + 15 + i / 2 * 105, py + 201 + i % 2 * 31, 96, 25, sounds[i], any) && sel.Count > 0)
                        samples.Play(Math.Max(1, sel[0].Set), "hit" + (i == 0 ? "normal" : sounds[i].ToLowerInvariant()), sel[0].Index, Math.Max(8, sel[0].Volume));
                break;
            }
            case 2:
            {
                d.Group(px + 6, py + 6, 218, 222, "Effects");
                bool kiai = sel.Count > 0 && (sel[0].Effects & 1) != 0, omit = sel.Count > 0 && (sel[0].Effects & 8) != 0;
                bool kiaiWas = kiai, omitWas = omit;
                d.Check(ref kiai, px + 15, py + 28, "Kiai Mode", any);
                if (kiai != kiaiWas) { bool on = kiai; TimingEdit(sel, p => p.Effects = on ? p.Effects | 1 : p.Effects & ~1); }
                d.Wrap(px + 12, py + 54, 206, "Fountains, flying stars, flashing beats. This is what you could call the \"epic chorus\" mode in osu!. It will emphasize a particular part of a song. Please don't overuse it.");
                d.Check(ref omit, px + 15, py + 141, "Omit first bar line", any);
                if (omit != omitWas) { bool on = omit; TimingEdit(sel, p => p.Effects = on ? p.Effects | 8 : p.Effects & ~8); }
                d.Wrap(px + 12, py + 168, 206, "Use this to remove the first bar line from this section in Taiko and Mania modes.");
                break;
            }
        }

        // a time signature group: 3/4, 4/4 or another (5-8) over the given timing points
        void Signature(float gx, float gy, float h, float y3, float y4, float yo, List<ETiming> reds)
        {
            bool on = reds.Count > 0;
            d.Group(gx, gy, 218, h, "Time Signature (metronome)");
            int meter = on && reds.All(p => p.Meter == reds[0].Meter) ? reds[0].Meter : -1;
            int pick = meter switch { 3 => 3, 4 => 4, -1 => -1, _ => 0 }, pickWas = pick;
            d.Radio(ref pick, 3, gx + 9, gy + y3, "3 / 4 (waltz)", on);
            d.Radio(ref pick, 4, gx + 9, gy + y4, "4 / 4 (common)", on);
            d.Radio(ref pick, 0, gx + 9, gy + yo, "Other:", on);
            double other = meter is >= 5 and <= 8 ? meter : 5;
            if (pick != pickWas) { int to = pick == 0 ? (int)other : pick; TimingEdit(reds, p => p.Meter = to, p => p.Red); }
            Number(106, other, 5, 8, 1, "0", gx + 73, gy + yo, 44, 20, on && pick == 0, v => TimingEdit(reds, p => p.Meter = (int)v, p => p.Red));
            d.Label(gx + 123, gy + yo + 2, "/ 4", false, null, 0, on);
        }
        // a slider velocity group (or just its choices, under a label): 0.75x, 1.0x, 1.5x or a custom one
        void Velocity(float gx, float gy, float h, float yr, float yc, List<ETiming> greens, bool group = true)
        {
            bool on = greens.Count > 0;
            if (group) d.Group(gx, gy, 218, h, "Slider velocity multiplier");
            double sv = on && greens.All(p => Math.Abs(p.Sv - greens[0].Sv) < 1e-9) ? greens[0].Sv : -1;
            int pick = sv < 0 ? -1 : Math.Abs(sv - 0.75) < 1e-9 ? 0 : Math.Abs(sv - 1) < 1e-9 ? 1 : Math.Abs(sv - 1.5) < 1e-9 ? 2 : 3, pickWas = pick;
            d.Radio(ref pick, 0, gx + 9, gy + yr, "0.75x", on);
            d.Radio(ref pick, 1, gx + 72, gy + yr, "1.0x", on);
            d.Radio(ref pick, 2, gx + 135, gy + yr, "1.5x", on);
            d.Radio(ref pick, 3, gx + 9, gy + yc, "Custom:", on);
            double custom = sv > 0 ? Math.Round(sv, 2) : 1;
            if (pick != pickWas && pick < 3) { double to = pick switch { 0 => 0.75, 1 => 1, _ => 1.5 }; TimingEdit(greens, p => p.Beat = -100 / to, p => !p.Red); }
            Number(107, custom, 0.1, 10, 0.1, "0.00", gx + 91, gy + yc - 1, 61, 20, on && pick == 3, v => TimingEdit(greens, p => p.Beat = -100 / Math.Round(v, 2), p => !p.Red));
        }

        // ---- when applying timing changes, OK / Cancel
        d.Group(4, 303, 574, 74, "When applying timing changes...");
        bool scaleWas = d.Scale, snapWas = d.SnapObjects;
        d.Check(ref d.Scale, 12, 325, "Scale objects to new timing");
        d.Check(ref d.SnapObjects, 12, 346, "Snap objects to new timing");
        if (d.Scale && !scaleWas) d.SnapObjects = false;
        if (d.SnapObjects && !snapWas) d.Scale = false;
        d.Check(ref d.Resnap, 195, 325, "Resnap slider lengths");
        d.Check(ref d.Bookmarks, 195, 346, "Adjust bookmarks and audio preview");
        d.Label(344, 327, "Use beat snap divisor of");
        double div = d.Divisor;
        if (d.Numeric(ref div, 0, SnapDivisors.Length - 1, 1, "0", 482, 325, 48, 20)) d.Divisor = (int)div;
        if (!act)
        {
            // (a domain box: its text is the divisor)
            var r = d.R(483, 326, 30, 18);
            DrawRectangleRec(r, Color.White);
            Game.Text("1/" + SnapDivisors[d.Divisor], r.X + 2 * s, r.Y + (r.Height - fs) / 2, fs, WinUi.Text);
        }
        if (d.Button(4, 383, 397, 35, "OK", true, true)) { TimingOk(); return; }
        if (d.Button(407, 383, 171, 35, "Cancel")) { TimingCancel(); return; }
    }
}
