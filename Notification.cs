using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// stable's centre-screen notification band (text and bar measured from a capture), drawn over
// every screen: 18.8-unit Aller Light across the screen centre on a bar 1.5 line heights per line that opens with
// BackOut over 300ms, gone after the duration with a 150ms fade. Plain notices are a black bar at 180/255 with
// the text outlined in black; warnings are (255,186,0) with the text outlined in (146,82,0) and hazard stripes
// (test-build-overlay at 1.5x) drifting 100 units along both edges. A new notice replaces the current one.
static class Notification
{
    static string[]? lines;
    static double from, duration;
    static bool warning;

    public static void Show(string text, double ms, bool isWarning = false)
    {
        lines = text.Split('\n');
        from = Game.NowMs;
        duration = ms;
        warning = isWarning;
    }

    public static void Draw()
    {
        if (lines == null) return;
        double age = Game.NowMs - from;
        if (age > duration + 150) { lines = null; return; }
        int w = GetScreenWidth(), h = GetScreenHeight();
        float u = h / 480f, sd = h / 768f;
        float outro = (float)Math.Clamp(1 - (age - duration) / 150, 0, 1);

        const float Pitch = 18.1f, LineH = 19.1f; // measured: the bar is 1.5 line heights per line
        float barH = lines.Length * LineH * 1.5f * u;
        float open = (float)Easing.Apply(30, age / 300), cy = h / 2f;
        var bar = warning ? new Color(255, 186, 0, 255) : Color.Black;
        DrawRectangleRec(new Rectangle(0, cy - barH * open / 2, w, barH * open), ColorAlpha(bar, 180 / 255f * outro));

        // Warning stripes along the bar's edges: the top one drifts left, the bottom one (rotated 180) right.
        if (warning && Game.Skin.Tex("test-build-overlay") is { } stripe)
        {
            float k = 1.5f * sd, drift = (float)(age / duration) * 100 * u;
            var tint = ColorAlpha(Color.White, (float)Math.Min(1, age / 300) * outro);
            float sw = stripe.Width * k, sh = stripe.Height * k;
            DrawTexturePro(stripe.Tex, new Rectangle(0, 0, stripe.Tex.Width, stripe.Tex.Height),
                new Rectangle(-drift, cy - barH / 2 - sh, sw, sh), Vector2.Zero, 0, tint);
            DrawTexturePro(stripe.Tex, new Rectangle(0, 0, -stripe.Tex.Width, -stripe.Tex.Height), // dark edge outward
                new Rectangle(-100 * u + drift, cy + barH / 2, sw, sh), Vector2.Zero, 0, tint);
        }

        float textAlpha = (float)Math.Min(1, age / 100) * outro, size = 18.8f * u, edge = Math.Max(1, u / 3);
        var outline = ColorAlpha(warning ? new Color(146, 82, 0, 255) : Color.Black, textAlpha);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            float lx = w / 2f - Game.LightWidth(line, size) / 2;
            float ly = cy + ((i - (lines.Length - 1) / 2f) * Pitch - 1) * u - size / 2; // 1 unit up: measured
            for (int dx = -1; dx <= 1; dx++)
                for (int dy = -1; dy <= 1; dy++)
                    if (dx != 0 || dy != 0) Game.TextLight(line, lx + dx * edge, ly + dy * edge, size, outline);
            Game.TextLight(line, lx, ly, size, ColorAlpha(Color.White, textAlpha));
        }
    }
}
