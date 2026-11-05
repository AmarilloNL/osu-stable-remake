using System.Globalization;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// Compose's top right, like stable's: "Beat Snap Divisor:" with its slider (1 to 16, the snaps stable allows) and the
// value - with Alt held, the distance spacing instead - and Insert Break Time, which puts a break between the two
// objects around the current time (from 200ms after the first to a preempt before the second, if that's 650ms or more
// and there's 1.2s of room).
sealed partial class Editor
{
    bool snapDragging;

    static Rectangle SnapSlider() { float u = U; return new Rectangle(Game.W - 190 * u, 46 * u, 140 * u, 10 * u); }
    static Rectangle BreakButton() { float u = U; return new Rectangle(Game.W - 190 * u, 61 * u, 130 * u, 17 * u); }

    // true when it took the mouse
    bool UpdateSnapPanel(Vector2 mouse)
    {
        if (tab != 0) { snapDragging = false; return false; }
        // a right-click on a break in the timeline takes it out - not one stable would put back (in 5s of room)
        if (Input.IsMouseButtonPressed(MouseButton.Right) && sliderDraft == null && CheckCollisionPointRec(mouse, TimelineRect())
            && info.Breaks.FirstOrDefault(b => TimeAtX(mouse.X) > b.Start && TimeAtX(mouse.X) < b.End) is { End: > 0 } hovered)
        {
            if (!AutoBreak(hovered)) Change(() => EventsSection.Lines.RemoveAll(l => IsBreakLine(l, hovered)));
            return true;
        }
        bool click = Input.IsMouseButtonPressed(MouseButton.Left);
        var s = SnapSlider();
        if (click && CheckCollisionPointRec(mouse, new Rectangle(s.X - 6 * U, s.Y - 4 * U, s.Width + 12 * U, s.Height + 8 * U))) snapDragging = true;
        if (snapDragging)
        {
            if (!Input.IsMouseButtonDown(MouseButton.Left)) { snapDragging = false; return true; }
            double f = Math.Clamp((mouse.X - s.X) / s.Width, 0, 1);
            if (Input.AltHeld) SetKv("Editor", "DistanceSpacing", Math.Round(0.1 + f * 5.9, 1).ToString(CultureInfo.InvariantCulture)); // (with Alt: the distance spacing's)
            else divisor = Divisors.MinBy(d => Math.Abs(d - (1 + f * 15)));
            return true;
        }
        if (click && CheckCollisionPointRec(mouse, BreakButton())) { InsertBreak(); return true; }
        return false;
    }

    void InsertBreak()
    {
        int t = (int)Math.Round(time), next = objs.FindIndex(o => o.Time >= t);
        if (next > 0 && objs[next].Time != t)
        {
            int from = (int)objs.Take(next).Max(o => o.End), to = (int)objs[next].Time;
            bool taken = info.Breaks.Any(b => b.Start < to && b.End > from); // (stable's check, and a break an object was put into)
            if (from < t && t < to && to - from >= 1200 && !taken)
            {
                int start = from + 200, end = to - (int)Preempt;
                if (end - start >= 650)
                {
                    Change(() =>
                    {
                        var ev = EventsSection;
                        // with the other breaks, in time order (the game reads them in file order): after the last one
                        // before it, else before the first, else under stable's "//Break Periods"
                        int after = ev.Lines.FindLastIndex(l => BreakStart(l) < start), first = ev.Lines.FindIndex(l => BreakStart(l) >= start);
                        int comment = ev.Lines.FindIndex(l => l.Trim() == "//Break Periods");
                        int at = after >= 0 ? after + 1 : first >= 0 ? first : comment >= 0 ? comment + 1 : ev.Lines.Count;
                        ev.Lines.Insert(at, $"2,{start},{end}");
                    });
                    return;
                }
            }
            if (taken) return;
        }
        Notification.Show("There isn't enough room to place a break here!  Make sure you are between two well-spaced beats.", 3000);
    }

    // a break line's start (either form; null for any other line)
    static double? BreakStart(string l)
    {
        var f = l.Split(',');
        return f.Length >= 3 && f[0].Trim() is "2" or "Break" && double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s) ? s : null;
    }
    static bool IsBreakLine(string l, (double Start, double End) b)
    {
        var f = l.Split(',');
        return f.Length >= 3 && f[0].Trim() is "2" or "Break" && double.TryParse(f[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double s)
            && double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double e) && s == b.Start && e == b.End;
    }
    // a break stable keeps in place: 650ms or longer, 200ms after the object before and a preempt before the next, in a
    // gap of 5s or more (stable fills those with breaks itself)
    bool AutoBreak((double Start, double End) b)
    {
        int next = objs.FindIndex(o => o.Time >= b.Start);
        if (next <= 0 || b.End - b.Start < 650) return false;
        double prevEnd = objs.Take(next).Max(o => o.End), nextStart = objs[next].Time;
        return b.Start - prevEnd >= 200 && nextStart - b.End >= (int)Preempt && nextStart - prevEnd >= 5000;
    }

    // stable's "Break" at each break's start on the timeline
    void DrawBreakLabels(Rectangle r, float u)
    {
        foreach (var b in info.Breaks)
        {
            float x = TimelineX(b.Start);
            if (x >= r.X - 60 * u && x <= r.X + r.Width) Game.Text("Break", x + 5 * u, r.Y + 12 * u, 14 * u, Color.White);
        }
    }

    void DrawSnapPanel(float u)
    {
        bool spacing = Input.AltHeld;
        float x = Game.W - 200 * u;
        Game.TextBold(spacing ? "Distance Spacing:" : "Beat Snap Divisor:", x, 20 * u, 12 * u, Color.White);
        Game.TextBold(spacing ? $"{DistanceSpacing.ToString("0.0#", CultureInfo.InvariantCulture)}x" : $"1/{divisor}", Game.W - 40 * u, 35 * u, 12 * u, Color.White);
        var s = SnapSlider();
        float f = spacing ? (float)Math.Clamp((DistanceSpacing - 0.1) / 5.9, 0, 1) : (divisor - 1) / 15f;
        DrawRectangle((int)s.X, (int)(s.Y + s.Height / 2 - u), (int)s.Width, (int)Math.Max(2, 2 * u), ColorAlpha(Color.White, 0.6f));
        DrawRectangle((int)s.X, (int)(s.Y + s.Height / 2 - u), (int)(s.Width * f), (int)Math.Max(2, 2 * u), new Color(255, 160, 40, 255));
        DrawCircleV(new(s.X + s.Width * f, s.Y + s.Height / 2), 4.5f * u, Color.White);
        var b = BreakButton();
        bool hover = CheckCollisionPointRec(Input.GetMousePosition(), b);
        DrawRectangleRounded(b, 0.25f, 6, hover ? new Color(140, 175, 255, 255) : new Color(112, 152, 252, 255));
        const string text = "Insert Break Time";
        Game.Text(text, b.X + b.Width / 2 - Game.TextWidth(text, 10 * u) / 2, b.Y + (b.Height - 10 * u) / 2, 10 * u, Color.White);
    }
}
