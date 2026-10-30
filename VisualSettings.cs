using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// stable's "Visual Settings" bar, in 640x480 units: while paused or
// failed, a 45-unit strip sits at the bottom (blue 3-unit line, 180-alpha black, "Visual" / "Settings"); hovering
// it slides it up to show the background dim slider and the per-beatmap toggles (storyboard, video, skin,
// hitsounds). Dim changes the global setting; the toggles last for the map for this session.
// TODO: stable stores the per-map toggles/dim in osu!.db; ours are session-only (no writes to stable's files).
sealed partial class Player
{
    static readonly Dictionary<string, (bool Storyboard, bool Skin, bool Hitsounds)> mapToggles = [];
    float visualHeight; // units currently shown: 0 hidden, 45 collapsed, ExpandedHeight open
    const float ExpandedHeight = 140;
    int visualDragging = -1;

    (bool Storyboard, bool Skin, bool Hitsounds) Toggles
    {
        get
        {
            mapMd5 ??= Replays.Md5Of(map.File);
            return mapToggles.TryGetValue(mapMd5, out var t) ? t
                : (true, !Config.GetBool("IgnoreBeatmapSkins", false), !Config.GetBool("IgnoreBeatmapSamples", false));
        }
        set
        {
            mapMd5 ??= Replays.Md5Of(map.File);
            mapToggles[mapMd5] = value;
            ApplyToggles();
        }
    }

    void ApplyToggles()
    {
        var t = Toggles;
        samples.UseMapSamples = t.Hitsounds;
        colours = t.Skin && map.ComboColours.Count > 0
            ? map.ComboColours.Select(c => new Color(c.R, c.G, c.B, (byte)255)).ToArray()
            : skin.ComboColours;
    }

    bool VisualActive => (paused && !resumePending || failed && !double.IsPositiveInfinity(menuShownAt)) && playback == null;

    // Called from Update while paused/failed: slides the bar and handles its controls. Returns true if it took the click.
    bool UpdateVisualSettings(double dtMs)
    {
        float u = GetScreenHeight() / 480f;
        var mouse = Input.GetMousePosition();
        bool hover = VisualActive && mouse.Y > GetScreenHeight() - Math.Max(45, visualHeight) * u;
        float target = !VisualActive ? 0 : hover || visualDragging >= 0 ? ExpandedHeight : 45;
        visualHeight += (target - visualHeight) * (float)Math.Min(1, dtMs / 70);
        if (!VisualActive || visualHeight < ExpandedHeight - 2) { visualDragging = -1; return false; }

        float top = GetScreenHeight() - visualHeight * u;
        var slider = new Rectangle(24 * u, top + 63 * u, 200 * u, 10 * u);
        if (!Input.IsMouseButtonDown(MouseButton.Left)) visualDragging = -1;
        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            if (CheckCollisionPointRec(mouse, new Rectangle(slider.X - 5 * u, slider.Y, slider.Width + 10 * u, slider.Height))) visualDragging = 0; // + the knob at either end
            var t = Toggles;
            if (CheckCollisionPointRec(mouse, ToggleRect(top, 0, u))) Toggles = t with { Storyboard = !t.Storyboard };
            else if (CheckCollisionPointRec(mouse, ToggleRect(top, 2, u))) Toggles = t with { Skin = !t.Skin };
            else if (CheckCollisionPointRec(mouse, ToggleRect(top, 3, u))) Toggles = t with { Hitsounds = !t.Hitsounds };
            else if (visualDragging < 0) return mouse.Y > top;
        }
        if (visualDragging >= 0)
        {
            int dim = (int)Math.Round(Math.Clamp((mouse.X - slider.X) / slider.Width, 0, 1) * 100);
            if (dim != Config.GetInt("DimLevel", 80)) Config.SetOwn("DimLevel", dim.ToString());
            return true;
        }
        return false;
    }

    // Toggle rows: storyboard (x 260, row 0), video (x 520, row 0, unavailable), skin (row 1), hitsounds (row 2).
    static Rectangle ToggleRect(float top, int which, float u) => which switch
    {
        1 => new Rectangle(520 * u, top + 63 * u, 120 * u, 18 * u),
        _ => new Rectangle(260 * u, top + (63 + 20 * Math.Max(0, which - 1)) * u, 160 * u, 18 * u),
    };

    void DrawVisualSettings(int w, int h)
    {
        if (visualHeight < 1) return;
        float u = h / 480f, top = h - visualHeight * u;
        DrawRectangleRec(new Rectangle(0, top, w, 3 * u), new Color(0, 78, 155, 255));
        DrawRectangleRec(new Rectangle(0, top + 3 * u, w, h - top), new Color(0, 0, 0, 180));
        Game.TextBold("Visual", 4 * u, top + 1 * u, 24 * u, new Color(173, 216, 230, 255)); // LightBlue
        Game.TextBold("Settings", 53 * u, top + 16 * u, 22 * u, Color.White);
        if (visualHeight < 60) return;

        Game.TextBold("Background dim", 24 * u, top + 48 * u, 10 * u, Color.White);
        Game.TextBold("Toggles", 264 * u, top + 48 * u, 10 * u, Color.White);
        int dim = Config.GetInt("DimLevel", 80);
        float x0 = 24 * u, x1 = 224 * u, cy = top + 68 * u;
        DrawRectangleRec(new Rectangle(x0, cy - 0.5f * u, x1 - x0, u), new Color(232, 104, 161, 255));
        var knob = new Vector2(x0 + (x1 - x0) * dim / 100f, cy);
        DrawCircleV(knob, 4.3f * u, new Color(30, 20, 26, 255));
        DrawRing(knob, 3.4f * u, 4.3f * u, 0, 360, 24, new Color(232, 104, 161, 255));
        Game.Text($"{dim}%", x1 + 6 * u, cy - 5 * u, 10 * u, Color.White);

        var t = Toggles;
        (string Label, bool On, bool Enabled)[] rows = [("Storyboard", t.Storyboard, true), ("Video", false, false), ("Skin", t.Skin, true), ("Hitsounds", t.Hitsounds, true)];
        var pink = new Color(235, 117, 140, 255);
        for (int i = 0; i < rows.Length; i++)
        {
            var r = ToggleRect(top, i, u);
            var c = new Vector2(r.X + 4 * u, r.Y + 5 * u);
            var col = rows[i].Enabled ? pink : Color.DarkGray;
            if (rows[i].On) DrawCircleV(c, 3.8f * u, col);
            else DrawRing(c, 3.0f * u, 3.8f * u, 0, 360, 24, col);
            Game.Text(rows[i].Label, r.X + 12 * u, r.Y, 11 * u, rows[i].Enabled ? Color.White : Color.DarkGray);
        }
    }
}
