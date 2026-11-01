using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// stable's "Visual Settings" bar, in 640x480 units: while paused or
// failed, a 45-unit strip sits at the bottom (blue 3-unit line, 180-alpha black, "Visual" / "Settings"); hovering
// it slides it up to show the background dim slider and the per-beatmap toggles (storyboard, video, skin,
// hitsounds). Dim changes the global setting; the toggles are per map (osu!.db's, changes kept in remake.cfg).
sealed partial class Player
{
    static readonly Dictionary<string, (bool Storyboard, bool Video, bool Skin, bool Hitsounds)> mapToggles = [];
    float visualHeight; // units currently shown: 0 hidden, 45 collapsed, ExpandedHeight open
    const float ExpandedHeight = 150;
    int visualDragging = -1;

    // Per map like stable: changes are kept in remake.cfg (Visual_<md5> = storyboard,video,skin,hitsounds as 0/1);
    // otherwise the map's settings from osu!.db, then the global options.
    (bool Storyboard, bool Video, bool Skin, bool Hitsounds) Toggles
    {
        get
        {
            mapMd5 ??= Replays.Md5Of(map.File);
            if (mapToggles.TryGetValue(mapMd5, out var t)) return t;
            if (Config.Get($"Visual_{mapMd5}", "") is { Length: 7 } s)
                t = (s[0] == '1', s[2] == '1', s[4] == '1', s[6] == '1');
            else
            {
                var db = back.DbEntry(mapMd5);
                t = (db?.DisableStoryboard != true, db?.DisableVideo != true,
                    !Config.GetBool("IgnoreBeatmapSkins", false) && db?.IgnoreSkin != true,
                    !Config.GetBool("IgnoreBeatmapSamples", false) && db?.IgnoreSound != true);
            }
            return mapToggles[mapMd5] = t;
        }
        set
        {
            mapMd5 ??= Replays.Md5Of(map.File);
            mapToggles[mapMd5] = value;
            static char C(bool b) => b ? '1' : '0';
            Config.SetOwn($"Visual_{mapMd5}", $"{C(value.Storyboard)},{C(value.Video)},{C(value.Skin)},{C(value.Hitsounds)}");
            ApplyToggles();
        }
    }

    void ApplyToggles()
    {
        var t = Toggles;
        samples.UseMapSamples = t.Hitsounds;
        skin.SetMapSkin(t.Skin ? map.Folder : null);
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
        var slider = new Rectangle(20 * u, top + 63 * u, 200 * u, 10 * u);
        if (!Input.IsMouseButtonDown(MouseButton.Left)) visualDragging = -1;
        if (Input.IsMouseButtonPressed(MouseButton.Left))
        {
            if (CheckCollisionPointRec(mouse, new Rectangle(slider.X - 5 * u, slider.Y, slider.Width + 10 * u, slider.Height))) visualDragging = 0; // + the knob at either end
            var t = Toggles;
            int hit = -1;
            for (int i = 0; i < 4 && hit < 0; i++) if (ToggleEnabled(i) && CheckCollisionPointRec(mouse, ToggleRect(top, i, u))) hit = i;
            if (hit >= 0)
            {
                Toggles = hit switch
                {
                    0 => t with { Storyboard = !t.Storyboard }, 1 => t with { Video = !t.Video },
                    2 => t with { Skin = !t.Skin }, _ => t with { Hitsounds = !t.Hitsounds },
                };
                // the boxes say "Disable ..." / "Ignore ...": ticked when the thing is off
                bool ticked = hit switch { 0 => !Toggles.Storyboard, 1 => !Toggles.Video, 2 => !Toggles.Skin, _ => !Toggles.Hitsounds };
                skin.Sound(ticked ? "check-on" : "check-off")?.Play(BeatmapSamples.EffectVolume);
            }
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

    // stable's checkboxes (top-left of a 16-unit box at): storyboard (260, 68), video (520, 68), skin (260, 88),
    // hitsounds (260, 108); circle-empty/-full at 0.8 centred 8 units in, the 12-unit label 16 units right of the box.
    static Rectangle ToggleRect(float top, int which, float u) => which switch
    {
        1 => new Rectangle(520 * u, top + 68 * u, 110 * u, 16 * u),
        _ => new Rectangle(260 * u, top + (68 + 20 * Math.Max(0, which - 1)) * u, 160 * u, 16 * u),
    };

    // stable: storyboard only for a map with one (or already ticked), video only with a video (and the option on)
    bool ToggleEnabled(int which) => which switch
    {
        0 => storyboard != null || !Toggles.Storyboard,
        1 => Config.GetBool("Video", true) && (map.VideoFile.Length > 0 || !Toggles.Video),
        _ => true,
    };

    // stable fades the pause / fail menu out as the panel opens past 75 units
    float MenuAlpha => 1 - Math.Clamp((visualHeight - 75) / (ExpandedHeight - 75), 0, 1);

    void DrawVisualSettings(int w, int h)
    {
        if (visualHeight < 1) return;
        float u = h / 480f, top = h - visualHeight * u;
        DrawRectangleRec(new Rectangle(0, top, w, 3 * u), new Color(0, 78, 155, 255));
        DrawRectangleRec(new Rectangle(0, top + 3 * u, w, h - top), new Color(0, 0, 0, 180));
        Game.TextBold("Visual", 4 * u, top + 3 * u, 25 * u, new Color(173, 216, 230, 255)); // LightBlue; measured against stable at 1080p
        Game.TextBold("Settings", 53 * u, top + 18 * u, 22.7f * u, Color.White);
        if (visualHeight < 60) return;

        // measured against stable at 1080p: labels 10 units at (20, 48) and (260, 48); the dim slider 200 units from
        // (20, 68), full pink to the knob ring and half after it.
        Game.TextBold("Background dim:", 21.3f * u, top + 47.6f * u, 10.4f * u, Color.White);
        Game.TextBold("Toggles", 261.4f * u, top + 47.6f * u, 10.4f * u, Color.White);
        int dim = Config.GetInt("DimLevel", 80);
        float x0 = 20 * u, x1 = 220 * u, cy = top + 68.6f * u;
        var pink = new Color(236, 117, 140, 255);
        var knob = new Vector2(x0 + (x1 - x0) * dim / 100f, cy - 0.6f * u);
        DrawRectangleRec(new Rectangle(x0, cy - 0.45f * u, Math.Max(0, knob.X - 4.7f * u - x0), 0.9f * u), pink);
        DrawRectangleRec(new Rectangle(knob.X + 4.7f * u, cy - 0.45f * u, Math.Max(0, x1 + 2 * u - knob.X - 4.7f * u), 0.9f * u), ColorAlpha(pink, 0.5f));
        DrawRing(knob, 4.0f * u, 4.7f * u, 0, 360, 32, pink);
        var mouseAt = Input.GetMousePosition();
        bool overDim = visualDragging >= 0 || CheckCollisionPointRec(mouseAt, new Rectangle(x0 - 5 * u, top + 63 * u, x1 - x0 + 10 * u, 10 * u));

        var t = Toggles;
        (string Label, bool Ticked)[] rows = [("Disable storyboard", !t.Storyboard), ("Disable video", !t.Video), ("Ignore beatmap skin", !t.Skin), ("Ignore beatmap hitsounds", !t.Hitsounds)];
        for (int i = 0; i < rows.Length; i++)
        {
            var r = ToggleRect(top, i, u);
            bool enabled = ToggleEnabled(i);
            var c = new Vector2(r.X + 8 * u, r.Y + 8 * u);
            var col = enabled ? pink : Xna.Gray;
            if (Game.Skin.DefaultTex(rows[i].Ticked ? "circle-full" : "circle-empty") is { } sp) sp.Draw(c, 0.8f * GetScreenHeight() / 768f, col);
            else if (rows[i].Ticked) DrawCircleV(c, 3.6f * u, col);
            else DrawRing(c, 2.9f * u, 3.6f * u, 0, 360, 24, col);
            Game.Text(rows[i].Label, r.X + 17.4f * u, c.Y - 6.8f * u, 12.45f * u, enabled ? Color.White : Xna.Gray);
        }
        if (overDim) Options.DrawTooltip($"{dim}% dimmed", mouseAt); // stable's slider tooltip
    }
}
