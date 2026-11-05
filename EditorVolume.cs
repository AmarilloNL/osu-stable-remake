using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// View → Volume: stable's small window with the music and sample (effect) volume, 0-100, as the options set them.
sealed partial class Editor
{
    sealed class VolumeWindow
    {
        public Vector2 Pos = new(float.NaN);
        public Vector2? Drag;
        public int Dragging = -1; // the slider being dragged
        public Vector2 Mouse;
    }
    VolumeWindow? volumeWindow;
    const float VolW = 324, VolH = 89;
    static readonly (string Label, string Key, float Y)[] VolRows = [("Music", "VolumeMusic", 12), ("Sample", "VolumeEffect", 47)];

    Rectangle VolRect(float x, float y, float w, float h) { var p = volumeWindow!.Pos; float s = AiS; return new Rectangle(p.X + x * s, p.Y + (AiCaption + y) * s, w * s, h * s); }
    // a slider's track (the thumb runs along it)
    Rectangle VolTrack(int i) => VolRect(51 + 12, VolRows[i].Y + 10, 240 - 24, 4);

    bool UpdateVolumeWindow(Vector2 mouse)
    {
        var w = volumeWindow!;
        float s = AiS;
        w.Mouse = mouse;
        if (float.IsNaN(w.Pos.X)) w.Pos = new((Game.W - VolW * s) / 2, (Game.H - (VolH + AiCaption) * s) / 2);
        bool click = Input.IsMouseButtonPressed(MouseButton.Left), held = Input.IsMouseButtonDown(MouseButton.Left);
        if (w.Drag is { } d)
        {
            if (!held) w.Drag = null;
            else w.Pos = new(Math.Clamp(mouse.X - d.X, -VolW * s / 2, Game.W - VolW * s / 2), Math.Clamp(mouse.Y - d.Y, 0, Game.H - AiCaption * s));
            return true;
        }
        if (w.Dragging >= 0)
        {
            if (!held) { w.Dragging = -1; return true; }
            var t = VolTrack(w.Dragging);
            int value = (int)Math.Round(Math.Clamp((mouse.X - t.X) / t.Width, 0, 1) * 100);
            if (Config.GetInt(VolRows[w.Dragging].Key, 80) != value)
            {
                Config.SetOwn(VolRows[w.Dragging].Key, value.ToString());
                if (music != null) music.Volume = Config.MusicVolume;
            }
            return true;
        }
        var whole = new Rectangle(w.Pos.X, w.Pos.Y, VolW * s, (VolH + AiCaption) * s);
        if (!CheckCollisionPointRec(mouse, whole)) return false;
        if (!click) return true;
        var cap = new Rectangle(w.Pos.X, w.Pos.Y, VolW * s, AiCaption * s);
        if (CheckCollisionPointRec(mouse, new Rectangle(cap.X + cap.Width - 28 * s, cap.Y, 28 * s, cap.Height))) { volumeWindow = null; return true; }
        if (CheckCollisionPointRec(mouse, cap)) { w.Drag = mouse - w.Pos; return true; }
        for (int i = 0; i < VolRows.Length; i++)
            if (CheckCollisionPointRec(mouse, VolRect(51, VolRows[i].Y, 240, 30))) { w.Dragging = i; return true; }
        return true;
    }

    void DrawVolumeWindow()
    {
        var w = volumeWindow!;
        float s = AiS, fs = 11 * s;
        if (float.IsNaN(w.Pos.X)) return;
        Color face = new(240, 240, 240, 255), text = Color.Black;
        var whole = new Rectangle(w.Pos.X, w.Pos.Y, VolW * s, (VolH + AiCaption) * s);
        var cap = new Rectangle(w.Pos.X, w.Pos.Y, VolW * s, AiCaption * s);
        DrawRectangle((int)(whole.X + 4 * s), (int)(whole.Y + 4 * s), (int)whole.Width, (int)whole.Height, new Color(0, 0, 0, 70));
        DrawRectangleRec(whole, face);
        DrawRectangleRec(cap, Color.White);
        Game.Text("Volume", cap.X + 6 * s, cap.Y + (cap.Height - fs) / 2, fs, text);
        float cx = cap.X + cap.Width - 14 * s, cy = cap.Y + cap.Height / 2;
        DrawLineEx(new(cx - 4 * s, cy - 4 * s), new(cx + 4 * s, cy + 4 * s), 1.2f * s, text);
        DrawLineEx(new(cx - 4 * s, cy + 4 * s), new(cx + 4 * s, cy - 4 * s), 1.2f * s, text);
        DrawRectangleLinesEx(whole, 1, new Color(160, 160, 160, 255));
        for (int i = 0; i < VolRows.Length; i++)
        {
            var (label, key, y) = VolRows[i];
            int value = Config.GetInt(key, 80);
            var l = VolRect(i == 0 ? 8 : 7, y + 5, 0, 0);
            Game.Text(label, l.X, l.Y, fs, text);
            var v = VolRect(296, y + 5, 0, 0);
            Game.Text(value.ToString(), v.X, v.Y, fs, text);
            // a trackbar like Windows': the groove, ticks every 10, the thumb
            var t = VolTrack(i);
            DrawRectangleRec(t, new Color(231, 234, 234, 255));
            DrawRectangleLinesEx(t, 1, new Color(214, 214, 214, 255));
            for (int k = 0; k <= 10; k++) DrawRectangleRec(new Rectangle(t.X + t.Width * k / 10f, t.Y + 14 * s, Math.Max(1, s), 3 * s), new Color(196, 196, 196, 255));
            float tx = t.X + t.Width * value / 100f;
            bool hot = w.Dragging == i || CheckCollisionPointRec(w.Mouse, new Rectangle(tx - 6 * s, t.Y - 9 * s, 12 * s, 22 * s));
            var thumb = new Rectangle(tx - 5 * s, t.Y - 9 * s, 10 * s, 20 * s);
            DrawRectangleRec(thumb, hot ? new Color(23, 23, 23, 255) : new Color(0, 122, 217, 255));
        }
    }
}
