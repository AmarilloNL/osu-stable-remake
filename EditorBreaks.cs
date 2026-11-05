using System.Globalization;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// Breaks, kept the way stable's editor keeps them. Each edge of a break either follows the objects around it (200ms
// after the one before, a preempt before the next) or was put elsewhere and stays there (up to 5s from them). After
// every change they're fitted again: one squeezed under 50ms is lengthened, one whose objects are under 1.2s apart
// goes (50ms right after moving objects in time), so do ones too short or two in a gap, and every gap of 5s or more
// gets one. The same happens when a map is opened (not counted as a change), so a map saved here has stable's breaks.
sealed partial class Editor
{
    sealed class EBreak(int start, int end, string? raw = null)
    {
        public int Start = start, End = end;
        public bool OwnStart, OwnEnd; // (an edge not where the objects put it)
        public readonly string? Raw = raw; // the line as read
        public int Length => End - Start;
    }

    // the objects in whole ms, in time order
    readonly record struct Times(int[] St, int[] En, int MaxLen)
    {
        public int Count => St.Length;

        // the first object at or after t
        public int Index(int t, out bool found)
        {
            int i = Array.BinarySearch(St, t);
            found = i >= 0;
            if (i < 0) return ~i;
            while (i > 0 && St[i - 1] == t) i--;
            return i;
        }

        // of the objects before index i going on at t, the one ending last (after t); -1 if none
        public int LatestEnd(int t, int i)
        {
            int best = -1, end = t;
            for (int j = i - 1; j >= 0 && St[j] >= t - MaxLen; j--)
                if (St[j] <= t && t <= En[j] && En[j] > end) { end = En[j]; best = j; }
            return best;
        }

        // the objects around a break (stable's: the one after its middle, and the latest end before that)
        public bool Around(EBreak b, out int prev, out int next)
        {
            int i = Index(b.Length / 2 + b.Start, out _), p = i - 1;
            int k = LatestEnd(p >= 0 ? Math.Min(En[p], b.Start) : b.Start, i);
            if (k >= 0) p = k;
            prev = p >= 0 ? En[p] : -1;
            next = i < Count ? St[i] : -1;
            return prev >= 0 && next >= 0;
        }
    }

    Dictionary<(int, int), (bool, bool)> breakFlags = []; // each break's own edges, as of the last fitting
    int breakSig; // the objects' times then
    bool breakDragFit; // (the next fitting is after a drag in time)
    bool mouseWasDown;

    Times ObjTimes()
    {
        var sorted = objs.OrderBy(o => o.Time).ToArray();
        var st = new int[sorted.Length];
        var en = new int[sorted.Length];
        int max = 0;
        for (int i = 0; i < sorted.Length; i++) { st[i] = (int)sorted[i].Time; en[i] = (int)sorted[i].End; max = Math.Max(max, en[i] - st[i]); }
        return new(st, en, max);
    }

    int ObjSig()
    {
        var h = new HashCode();
        h.Add((int)Preempt);
        foreach (var o in objs) { h.Add((int)o.Time); h.Add((int)o.End); }
        return h.ToHashCode();
    }

    static (int Start, int End)? BreakTimes(string l)
    {
        var f = l.Split(',');
        return f.Length >= 3 && f[0].Trim() is "2" or "Break" && double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s)
            && double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double e) ? ((int)s, (int)e) : null;
    }

    List<EBreak> ReadBreaks() => [.. EventsSection.Lines.Select(l => BreakTimes(l) is { } b ? new EBreak(b.Start, b.End, l) : null).OfType<EBreak>().OrderBy(b => b.Start)];

    // the breaks written back where they were (else under "//Break Periods"), unchanged ones as they were written
    void WriteBreaks(List<EBreak> list)
    {
        var ev = EventsSection.Lines;
        int at = ev.FindIndex(l => BreakTimes(l) != null);
        ev.RemoveAll(l => BreakTimes(l) != null);
        if (at < 0) at = ev.FindIndex(l => l.Trim() == "//Break Periods") is >= 0 and var c ? c + 1 : ev.Count;
        ev.InsertRange(at, list.OrderBy(b => b.Start).Select(b => b.Raw != null && BreakTimes(b.Raw) == (b.Start, b.End) ? b.Raw : $"2,{b.Start},{b.End}"));
        sbParsed = null; // (the storyboard's lines may have moved)
    }

    // a break stable leaves alone: 650ms or longer, 200ms after the object before and a preempt before the next, with
    // its objects at least `gap` apart (5s: one stable would put back, so it can't be taken out)
    bool BreakFits(EBreak b, Times t, int gap) =>
        b.Length >= 650 && t.Around(b, out int prev, out int next) && b.Start - prev >= 200 && next - b.End >= (int)Preempt && next - prev >= gap;

    static bool Taken(List<EBreak> list, int from, int to) => list.Any(b => b.Start <= from && b.End > from || b.End > from && b.End <= to);

    // the fitting: true if it changed the breaks
    bool KeepBreaks()
    {
        var t = ObjTimes();
        int pre = (int)Preempt;
        bool drag = breakDragFit;
        breakDragFit = false;
        var list = ReadBreaks();
        var before = list.Select(b => (b.Start, b.End)).ToList();
        foreach (var b in list)
            (b.OwnStart, b.OwnEnd) = breakFlags.TryGetValue((b.Start, b.End), out var f) ? f
                : t.Around(b, out int p, out int n) ? (b.Start != p + 200, b.End != n - pre) : (false, false);
        if (t.Count > 1)
        {
            list.RemoveAll(b => !Fit(b, t, pre, drag ? 50 : 1200));
            list = [.. list.OrderBy(b => b.Start).DistinctBy(b => (b.Start, b.End))]; // (two fitted into one place: written once)
            if (!drag)
            {
                list.RemoveAll(b => !BreakFits(b, t, 1200));
                for (int j = 1; j < list.Count; j++) // (two in one gap: the later goes)
                {
                    int i = t.Index(list[j].Start, out _);
                    if (list[j].Start <= list[j - 1].End || i == 0 || t.En[i - 1] <= list[j - 1].End) list.RemoveAt(j--);
                }
            }
            for (int i = 1; i < t.Count; i++)
            {
                int from = t.En[i - 1], to = t.St[i];
                if (from < 0 || to - from < 5000 || Taken(list, from, to)) continue;
                if (t.LatestEnd(from, i - 1) is >= 0 and var k) from = t.En[k];
                if (to - from >= 5000 && to - pre - from - 200 >= 650) list.Add(new EBreak(from + 200, to - pre));
            }
        }
        else list.Clear();
        breakSig = ObjSig();
        breakFlags = [];
        foreach (var b in list) breakFlags[(b.Start, b.End)] = (b.OwnStart, b.OwnEnd);
        if (list.Select(b => (b.Start, b.End)).Order().SequenceEqual(before)) return false;
        WriteBreaks(list);
        return true;
    }

    // a break fitted to its objects: false when it has to go
    static bool Fit(EBreak b, Times t, int pre, int gap)
    {
        if (!t.Around(b, out int prev, out int next) || next - prev < gap) return false;
        int s0 = prev + 200, e0 = next - pre;
        if (!b.OwnStart) b.Start = s0;
        if (!b.OwnEnd) b.End = e0;
        if (b.Start < s0) { b.OwnStart = false; b.Start = s0; }
        if (b.Length >= 0 && (b.End > e0 || s0 > b.End)) { b.OwnEnd = false; b.End = e0; }
        FitEdges(b, prev, next, pre);
        if (b.Length >= 50) return true;
        // under 50ms: lengthened, at its end first
        int need = 50 - b.Length, room = next - b.End - pre;
        if (room > 0)
        {
            if (room >= need) { b.OwnEnd = room != need; b.End += need; return true; }
            b.OwnEnd = false; b.End = e0; need -= room;
        }
        room = b.Start - prev - 200;
        if (room >= need) { b.OwnStart = room != need; b.Start -= need; return true; }
        FitEdges(b, prev, next, pre);
        return b.Start <= next && b.End >= prev;
    }

    // an own edge stays (at most 5s from its object) while there's room, else it follows the object again
    static void FitEdges(EBreak b, int prev, int next, int pre)
    {
        int n = b.Start - prev;
        if (!b.OwnStart || n <= 200) { b.OwnStart = false; b.Start = prev + 200; }
        else if (n > 5000) b.Start = prev + 5000;
        n = next - b.End;
        if (!b.OwnEnd || n <= pre) { b.OwnEnd = false; b.End = next - pre; }
        else if (n > 5000) b.End = next - 5000;
    }

    // the breaks' own edges as they stand (after an undo)
    void NoteBreaks()
    {
        breakDragFit = false;
        breakDrag = null;
        var t = ObjTimes();
        int pre = (int)Preempt;
        breakFlags = [];
        foreach (var b in ReadBreaks()) breakFlags[(b.Start, b.End)] = t.Around(b, out int p, out int n) ? (b.Start != p + 200, b.End != n - pre) : (false, false);
        breakSig = ObjSig();
    }

    // ---- a break's edge dragged on the timeline (stable's: grabbed within a few px inside the break, moved when let go)
    (EBreak B, bool End, int Limit, float FromX)? breakDrag;

    bool PickBreakEdge(float mx)
    {
        if (tab != 0) return false;
        double t = TimeAtX(mx);
        if (ReadBreaks().FirstOrDefault(x => t >= x.Start && t <= x.End) is not { } b) return false;
        bool end;
        if (TimeAtX(mx - 5 * U) <= b.Start) end = false;
        else if (TimeAtX(mx + 5 * U) >= b.End) end = true;
        else return false;
        // (an own edge goes at most 5s from the object next to it)
        var times = ObjTimes();
        int i = times.Index(b.Length / 2 + b.Start, out _);
        int limit = end ? (i < times.Count ? times.St[i] - 5000 : int.MinValue) : (i > 0 ? times.En[i - 1] + 5000 : int.MaxValue);
        breakDrag = (b, end, limit, mx);
        return true;
    }

    void DropBreakEdge(float mx)
    {
        if (breakDrag is not var (b, isEnd, limit, fromX)) return;
        breakDrag = null;
        if (Math.Abs(mx - fromX) < 2) return; // (a click, not a drag)
        int t = (int)Snap(TimeAtX(mx)), pre = (int)Preempt, s = b.Start, e = b.End;
        var times = ObjTimes();
        bool around = times.Around(b, out int prev, out int next);
        var (ownStart, ownEnd) = breakFlags.TryGetValue((b.Start, b.End), out var f) ? f : around ? (b.Start != prev + 200, b.End != next - pre) : (false, false);
        if (isEnd)
        {
            if (t == e || t > e && !ownEnd) return; // (an edge that follows its object can't go past it)
            if (around && t < e) { t = Math.Max(t, prev + 250); if (t < limit) return; }
            e = t < e ? t : Math.Min(t, next - pre);
        }
        else
        {
            if (t == s || t < s && !ownStart) return;
            if (around && t > s) { t = Math.Min(t, next - pre - 50); if (t > limit) return; }
            s = t > s ? t : Math.Max(t, prev + 200);
        }
        breakDragFit = true; // (fitted like after a drag: the break stays while there's 50ms for it)
        Change(() =>
        {
            var list = ReadBreaks();
            list.RemoveAll(x => x.Start == b.Start && x.End == b.End);
            if (around) list.Add(new EBreak(s, e)); // (one with no objects around it goes)
            WriteBreaks(list);
        });
    }

    // (each frame: once a change made with the mouse is let go, like stable's)
    void KeepBreaksAfterDrag()
    {
        bool down = Input.IsMouseButtonDown(MouseButton.Left) || Input.IsMouseButtonDown(MouseButton.Right);
        if (!down && !mouseWasDown && xform == null && poly == null) // (not while a transform is previewed: a cancel puts it back)
        {
            if (ObjSig() != breakSig) { if (KeepBreaks()) info = ReloadInfo(); }
            else breakDragFit = false; // (a press that changed nothing)
        }
        mouseWasDown = down;
    }

    // Insert Break Time: a break between the two objects around the current time (from 200ms after the first to a
    // preempt before the second, if that's 650ms or more and they're 1.2s apart)
    void InsertBreak()
    {
        var t = ObjTimes();
        int now = (int)Math.Round(time), i = t.Index(now, out bool found);
        if (!found && i > 0 && i < t.Count)
        {
            int from = t.En[i - 1], to = t.St[i];
            if (t.LatestEnd(from, i) is >= 0 and var k) from = t.En[k];
            if (from < now && to > now)
            {
                var list = ReadBreaks();
                bool taken = false;
                if (from >= 0 && to - from >= 1200 && !(taken = Taken(list, from, to)) && to - (int)Preempt - from - 200 >= 650)
                {
                    Change(() => { list.Add(new EBreak(from + 200, to - (int)Preempt)); WriteBreaks(list); });
                    return;
                }
                if (taken) return;
            }
        }
        Notification.Show("There isn't enough room to place a break here!  Make sure you are between two well-spaced beats.", 3000);
    }

    // stable's breaks on the timeline: a grey band through its middle from the object before to the one after (the
    // break itself red when under 50ms), "Break" at its start
    void DrawBreakLabels(Rectangle r, float u)
    {
        float cy = r.Y + r.Height / 2 + 3 * u, thick = Math.Max(1, r.Height / 8 - 1);
        var grey = new Color(100, 100, 100, 150);
        foreach (var b in info.Breaks)
        {
            int next = objs.FindIndex(o => o.Time >= b.Start);
            double before = next > 0 ? objs[next - 1].End : b.Start, after = next >= 0 ? objs[next].Time : b.End + Preempt;
            float x0 = TimelineX(before), x1 = TimelineX(b.Start), x2 = TimelineX(b.End), x3 = TimelineX(after);
            if (x3 < r.X || x0 > r.X + r.Width) continue;
            if (breakDrag is var (db, _, _, _) && db.Start == (int)b.Start && db.End == (int)b.End) // (where the dragged edge would go)
                DrawRectangle((int)TimelineX(Snap(TimeAtX(Input.GetMousePosition().X))) - 1, (int)r.Y, 2, (int)r.Height, Color.White);
            DrawLineEx(new(x0, cy), new(x1, cy), thick, grey);
            DrawLineEx(new(x1, cy), new(x2, cy), thick, b.End - b.Start < 50 ? new Color(255, 0, 0, 150) : new Color(100, 100, 100, 100));
            DrawLineEx(new(x2, cy), new(x3, cy), thick, grey);
            if (x1 >= r.X - 60 * u && x1 <= r.X + r.Width) Game.Text("Break", x1 + 5 * u, r.Y + 12 * u, 14 * u, Color.White);
        }
    }
}
