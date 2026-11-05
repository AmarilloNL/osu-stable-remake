using System.Globalization;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// Compose's top right, like stable's: "Beat Snap Divisor:" with its slider (1 to 16, the snaps stable allows) and the
// value - with Alt held, the distance spacing instead - and Insert Break Time (EditorBreaks.cs).
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
            && ReadBreaks().FirstOrDefault(b => TimeAtX(mouse.X) > b.Start && TimeAtX(mouse.X) < b.End) is { } hovered)
        {
            if (!BreakFits(hovered, ObjTimes(), 5000)) Change(() => EventsSection.Lines.RemoveAll(l => BreakTimes(l) == (hovered.Start, hovered.End)));
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
