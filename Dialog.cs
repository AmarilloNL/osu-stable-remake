using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// stable's dialog (exit prompt, mod select ...): the screen dimmed with black at 240/255, a size-24 title 2 units in,
// wrapped at the screen's width, content from 50 units under it, then 460x40-unit buttons 50 units apart, centred,
// built from button-left/middle/right tinted 20 darker than their colour (the full colour while hovered, a white
// flash on click), labelled "N. text" at 14/18 of their height. Buttons slide in from 40 units right/left
// (alternating) and fade in over 800ms, 60ms apart. Units: 640x480 (u = h / 480).
static class Dialog
{
    static float U => Game.H / 480f;
    const float TitleSize = 24.9f, LineH = 25.65f; // stable's 24, measured
    static readonly List<string> lines = [];

    public static void Backdrop(float alpha) => DrawRectangle(0, 0, Game.W, Game.H, new Color(0, 0, 0, (int)(240 * alpha)));

    // Draws the wrapped title; returns where the content starts (units).
    public static float Title(string text, float alpha)
    {
        float u = U, size = TitleSize * u, max = Game.W - 4 * u;
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
        return new Rectangle((Game.W / u / 2 - 230) * u, y * u, 460 * u, 40 * u);
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
        // stable: regular Aller (14 x height / 18) with its text shadow, centred
        float size = 14 * 40 / 18f * 1.028f * u, tx = r.X + r.Width / 2 - Game.RegularWidth(text, size) / 2, ty = r.Y + r.Height / 2 - size / 2 - 1.3f * u;
        Game.TextRegular(text, tx + 1, ty + 1, size, ColorAlpha(Color.Black, 0.5f * alpha));
        Game.TextRegular(text, tx, ty, size, ColorAlpha(Color.White, alpha));
        return hover;
    }
}

// stable's named colours are XNA's, which differ from raylib's (raylib's Red is 230,41,55, its DarkGray 80,80,80).
static class Xna
{
    public static readonly Color Red = new(255, 0, 0, 255), Gold = new(255, 215, 0, 255), Orange = new(255, 165, 0, 255),
        LightGray = new(211, 211, 211, 255), Gray = new(128, 128, 128, 255), DarkGray = new(169, 169, 169, 255),
        SkyBlue = new(135, 206, 235, 255);
}
