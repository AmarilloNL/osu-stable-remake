using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace StableRemake;

// The bottom-left back button: the skin's menu-back, else stable's default. That one is two back-button-layer sprites
// (bottom-right origins 22 and 60 units in, the left one over the right), the chevron icon at 9.17 units and "back"
// (size 15) between them, 21 units up. Hover slides them to 40 / 86 / 16.67 with an elastic ease over 600ms and
// darkens the left layer; the icon pulses 0.9 -> 1 on each beat (1.5 -> 1 while hovered).
sealed class BackButton
{
    static readonly Color Pink = new(238, 51, 153, 255), DarkPink = new(187, 17, 119, 255);
    bool hover;
    double changedAt = double.MinValue;

    // Hit area: the skin sprite, else the visible part of both layers.
    public static Rectangle Rect(int h)
    {
        float sd = h / 768f, u = h / 480f;
        if (Game.Skin.Tex("menu-back") is { } b) return new Rectangle(0, h - b.Height * sd, b.Width * sd, b.Height * sd);
        return new Rectangle(0, h - 36 * u, 60 * u, 36 * u);
    }

    // beat: time since the last beat and the beat length (ms), when music is playing.
    public void Draw(int h, Vector2 mouse, (double Since, double Length)? beat = null)
    {
        float sd = h / 768f, u = h / 480f;
        bool over = CheckCollisionPointRec(mouse, Rect(h));
        if (over != hover) { hover = over; changedAt = Game.NowMs; }
        if (Game.Skin.Tex("menu-back") is { } back)
        {
            var r = Rect(h);
            back.Draw(new Vector2(r.X + r.Width / 2, r.Y + r.Height / 2), sd, hover ? Color.White : ColorAlpha(Color.White, 0.85f));
            return;
        }
        if (Game.Skin.Tex("back-button-layer") is not { } layer) return;
        float k = (float)Easing.Apply(25, (Game.NowMs - changedAt) / 600);
        float Lerp(float idle, float hovered) => hover ? idle + (hovered - idle) * k : hovered + (idle - hovered) * k;
        float left = Lerp(22, 40), right = Lerp(60, 86), icon = Lerp(9.166666f, 16.666666f);
        float lw = layer.Width * sd, lh = layer.Height * sd;
        var leftColour = ColorLerp(hover ? Pink : DarkPink, hover ? DarkPink : Pink, Math.Clamp(k, 0, 1));
        DrawTexturePro(layer.Tex, new Rectangle(0, 0, layer.Tex.Width, layer.Tex.Height), new Rectangle(right * u - lw, h - lh, lw, lh), Vector2.Zero, 0, Pink);
        DrawTexturePro(layer.Tex, new Rectangle(0, 0, layer.Tex.Width, layer.Tex.Height), new Rectangle(left * u - lw, h - lh, lw, lh), Vector2.Zero, 0, leftColour);
        float textX = (right - left) / 2.4f + left, size = 16 * u, drop = Math.Max(1, u / 3), tx = textX * u - Game.TextWidth("back", size) / 2;
        Game.Text("back", tx + drop, h - 21 * u - size / 2 + drop, size, new Color(0, 0, 0, 128)); // text and icon cast a drop shadow
        Game.Text("back", tx, h - 21 * u - size / 2, size, Color.White);
        float scale = 1;
        if (beat is { } bt && bt.Length > 0)
            scale = (hover ? 1.5f : 0.9f) + (1 - (hover ? 1.5f : 0.9f)) * (float)Easing.Apply(25, (bt.Since + bt.Length / 4) / (3 * bt.Length));
        Options.DrawIcon(0xf137, icon * u + drop, h - 21 * u + drop, 15 * u * scale, new Color(0, 0, 0, 128));
        Options.DrawIcon(0xf137, icon * u, h - 21 * u, 15 * u * scale, Color.White);
    }
}
