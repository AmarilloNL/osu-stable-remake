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

// stable's corner notifications: its own notification sprite (tinted by the toast's colour, drawn in three slices
// of its 56 logical px: top 13, middle 13 stretched to the text, bottom 30) at the right edge, the newest 28 units
// above the bottom, holding bold text (7 units, wrapped to 90% of the sprite's width). Each slides in 100 units from
// the right (800ms, elastic) and fades in over 200ms; it fades out over the last 500ms of 200ms + its duration. A new
// one pushes the others up by its height (800ms, elastic). Hovering one turns it white and holds it; clicking
// dismisses it (100ms). By default dark red, shown for 100ms per character; the same text again while it's still up
// is ignored.
static class Toast
{
    sealed class Item
    {
        public string[] Lines = [];
        public Color Colour;
        public double Shown, Duration, Dismissed = double.NaN, Hovered = double.NaN, Unhovered = double.NaN;
        public float Height, Rise, RiseFrom; // units
        public double RiseAt = double.MinValue;
        public string Text = "";
    }

    static readonly List<Item> items = [];
    const float Width = 244 * 0.625f, TextWidth = Width * 0.9f, Pad = (Width - TextWidth) / 2, LineH = 8.9f, Size = 8.7f; // text measured against stable at 1080p

    // Without "Show notification popups instantly during gameplay", stable holds them back during an active play
    // (and hides any showing), then lets them out one per 500ms.
    static readonly Queue<(string Text, Color? Colour, int Ms)> held = new();
    static double released;
    static bool Allowed => Config.GetBool("PopupDuringGameplay", true) || Game.Screen is not Player { ActivePlay: true };

    public static void Show(string text, Color? colour = null, int ms = 0)
    {
        double now = Game.NowMs;
        if (items.Count > 0 && items[^1].Text == text && now < items[^1].Shown + items[^1].Duration) return;
        if (!Allowed) { held.Enqueue((text, colour, ms)); return; }
        var lines = Wrap(text, TextWidth * GetScreenHeight() / 480f);
        float height = Math.Max(26.875f, lines.Length * LineH + 2 * Pad);
        foreach (var it in items) { it.RiseFrom = Rise(it, now); it.Rise += height; it.RiseAt = now; }
        items.Add(new Item { Lines = lines, Colour = colour ?? new Color(139, 0, 0, 255), Shown = now, Duration = ms > 0 ? ms : text.Length * 100, Height = height, Text = text });
    }

    static float Rise(Item it, double now) => it.RiseFrom + (it.Rise - it.RiseFrom) * (float)Easing.Apply(27, Math.Clamp((now - it.RiseAt) / 800, 0, 1));

    // Word wrap at spaces, long words broken before path separators (like the GDI text stable draws).
    static string[] Wrap(string text, float maxPx)
    {
        float size = Size * GetScreenHeight() / 480f;
        var lines = new List<string>();
        foreach (var para in text.Split('\n'))
        {
            string line = "";
            foreach (var word in para.Split(' '))
            {
                var parts = new List<string>();
                string rest = word;
                while (Game.BoldWidth(rest, size) > maxPx && rest.LastIndexOf('\\', rest.Length - 1) is int cut and > 0)
                {
                    int at = cut;
                    while (at > 0 && Game.BoldWidth(rest[..at], size) > maxPx) at = rest.LastIndexOf('\\', at - 1);
                    if (at <= 0) break;
                    parts.Add(rest[..at]);
                    rest = rest[at..];
                }
                parts.Add(rest);
                foreach (var p in parts)
                {
                    string next = line.Length == 0 ? p : line + (p == parts[0] ? " " : "") + p;
                    if (line.Length > 0 && (p != parts[0] || Game.BoldWidth(next, size) > maxPx)) { lines.Add(line); line = p; }
                    else line = next;
                }
            }
            lines.Add(line);
        }
        return [.. lines];
    }

    public static void Draw()
    {
        double now = Game.NowMs;
        if (!Allowed) { foreach (var it in items) if (double.IsNaN(it.Dismissed)) it.Dismissed = now; }
        else if (held.Count > 0 && now > released + 500) { released = now; var (t, c, ms) = held.Dequeue(); Show(t, c, ms); }
        if (items.Count == 0) return;
        int w = GetScreenWidth(), h = GetScreenHeight();
        float u = h / 480f;
        var mouse = Input.GetMousePosition();
        var tex = Game.Skin.DefaultTex("notification");
        for (int i = items.Count - 1; i >= 0; i--)
        {
            var it = items[i];
            double from = double.IsNaN(it.Unhovered) ? it.Shown : it.Unhovered;
            double end = !double.IsNaN(it.Dismissed) ? it.Dismissed + 100 : !double.IsNaN(it.Hovered) && double.IsNaN(it.Unhovered) ? double.MaxValue : from + 200 + it.Duration;
            if (now >= end) { items.RemoveAt(i); continue; }
            float alpha = (float)Math.Min(1, (now - it.Shown) / 200);
            if (!double.IsNaN(it.Dismissed)) alpha *= (float)(1 - (now - it.Dismissed) / 100);
            else if (end != double.MaxValue) alpha *= 1 - (float)Easing.Apply(2, Math.Clamp((now - (end - 500)) / 500, 0, 1));
            float slide = 100 * (1 - (float)Easing.Apply(26, Math.Clamp((now - it.Shown) / 800, 0, 1)));
            float right = w + slide * u, top = h - (28 + it.Height + Rise(it, now)) * u;
            var box = new Rectangle(right - Width * u, top, Width * u, it.Height * u);
            bool over = double.IsNaN(it.Dismissed) && CheckCollisionPointRec(mouse, box);
            if (over && (double.IsNaN(it.Hovered) || !double.IsNaN(it.Unhovered))) { it.Hovered = now; it.Unhovered = double.NaN; }
            else if (!over && !double.IsNaN(it.Hovered) && double.IsNaN(it.Unhovered)) it.Unhovered = now;
            if (over && Input.IsMouseButtonPressed(MouseButton.Left)) it.Dismissed = now;
            // the tint eases to white on hover and back over 60ms
            float white = double.IsNaN(it.Hovered) ? 0 : double.IsNaN(it.Unhovered) ? (float)Math.Min(1, (now - it.Hovered) / 60) : 1 - (float)Math.Min(1, (now - it.Unhovered) / 60);
            var tint = ColorAlpha(ColorLerp(it.Colour, Color.White, white), alpha);
            if (tex is { } t)
            {
                float k = t.Tex.Width / 244f; // texels per logical px
                float mid = it.Height - 8.125f - 18.75f;
                void Slice(float srcY, float srcH, float y, float dh) =>
                    DrawTexturePro(t.Tex, new Rectangle(0, srcY * k, t.Tex.Width, srcH * k), new Rectangle(right - Width * u, y, Width * u, dh * u), Vector2.Zero, 0, tint);
                Slice(0, 13, top, 8.125f);
                if (mid > 0) Slice(13, 13, top + 8.125f * u, mid);
                Slice(26, 30, top + (8.125f + Math.Max(0, mid)) * u, 18.75f);
            }
            else DrawRectangleRec(box, ColorAlpha(Color.Black, 0.8f * alpha));
            float tx = right - (3 + Pad + TextWidth) * u, ty = top + Pad * u;
            for (int l = 0; l < it.Lines.Length; l++)
                Game.TextBold(it.Lines[l], tx, ty + l * LineH * u, Size * u, ColorAlpha(Color.White, alpha));
        }
    }
}
