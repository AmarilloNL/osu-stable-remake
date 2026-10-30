using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace StableRemake;

// stable's dialog (exit prompt, mod select ...): the screen dimmed with black at 240/255, a size-24 title 2 units in,
// wrapped at the screen's width, content from 50 units under it, then 460x40-unit buttons 50 units apart, centred,
// built from button-left/middle/right tinted 20 darker than their colour (the full colour while hovered, a white
// flash on click), labelled "N. text" at 14/18 of their height. Buttons slide in from 40 units right/left
// (alternating) and fade in over 800ms, 60ms apart. Units: 640x480 (u = h / 480).
static class Dialog
{
    static float U => GetScreenHeight() / 480f;
    const float TitleSize = 24.9f, LineH = 25.65f; // stable's 24, measured
    static readonly List<string> lines = [];

    public static void Backdrop(float alpha) => DrawRectangle(0, 0, GetScreenWidth(), GetScreenHeight(), new Color(0, 0, 0, (int)(240 * alpha)));

    // Draws the wrapped title; returns where the content starts (units).
    public static float Title(string text, float alpha)
    {
        float u = U, size = TitleSize * u, max = GetScreenWidth() - 4 * u;
        lines.Clear();
        foreach (var paragraph in text.Split('\n'))
        {
            string line = "";
            foreach (var word in paragraph.Split(' '))
            {
                string next = line.Length == 0 ? word : line + " " + word;
                if (line.Length > 0 && Game.TextWidth(next, size) > max) { lines.Add(line); line = word; }
                else line = next;
            }
            lines.Add(line);
        }
        for (int i = 0; i < lines.Count; i++) Game.Text(lines[i], 5 * u, (2 + i * LineH) * u, size, ColorAlpha(Color.White, alpha)); // 2 units in, plus stable's text padding
        return 2 + lines.Count * LineH + 50;
    }

    public static Rectangle ButtonRect(float y)
    {
        float u = U;
        return new Rectangle((GetScreenWidth() / u / 2 - 230) * u, y * u, 460 * u, 40 * u);
    }

    // One button at y (units); age = ms since the dialog opened, flashAt = when it was clicked. Returns whether hovered.
    public static bool Button(int index, string label, Color colour, float y, double age, Vector2 mouse, double flashAge = double.MaxValue)
    {
        float u = U;
        double t = (age - index * 60) / 800;
        if (t <= 0) return false;
        float slide = (index % 2 == 0 ? 40 : -40) * (1 - (float)Easing.Apply(33, t)), alpha = (float)Easing.Apply(1, t);
        var r = ButtonRect(y);
        r.X += slide * u;
        bool hover = CheckCollisionPointRec(mouse, r);
        var tint = hover ? colour : new Color(Math.Max(0, colour.R - 20), Math.Max(0, colour.G - 20), Math.Max(0, colour.B - 20), 255);
        if (flashAge < 400) tint = ColorLerp(Color.White, tint, (float)(flashAge / 400));
        tint = ColorAlpha(tint, alpha);
        var skin = Game.Skin;
        if (skin.Tex("button-left") is { } left && skin.Tex("button-middle") is { } mid && skin.Tex("button-right") is { } right)
        {
            float k = r.Height / left.Height, cap = left.Width * k;
            DrawTexturePro(left.Tex, new Rectangle(0, 0, left.Tex.Width, left.Tex.Height), new Rectangle(r.X, r.Y, cap, r.Height), Vector2.Zero, 0, tint);
            DrawTexturePro(mid.Tex, new Rectangle(0, 0, mid.Tex.Width, mid.Tex.Height), new Rectangle(r.X + cap, r.Y, r.Width - 2 * cap, r.Height), Vector2.Zero, 0, tint);
            DrawTexturePro(right.Tex, new Rectangle(0, 0, right.Tex.Width, right.Tex.Height), new Rectangle(r.X + r.Width - cap, r.Y, cap, r.Height), Vector2.Zero, 0, tint);
        }
        else DrawRectangleRec(r, tint);
        string text = $"{index + 1}. {label}";
        float size = 14 * 40 / 18f * u; // stable's size, in bold (measured)
        Game.TextBold(text, r.X + r.Width / 2 - Game.BoldWidth(text, size) / 2, r.Y + r.Height / 2 - size / 2 - 1.3f * u, size, ColorAlpha(Color.White, alpha)); // bold
        return hover;
    }
}
