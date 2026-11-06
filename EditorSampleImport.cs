using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// Compose → Sample import (mania, Ctrl+Shift+I): stable's small window for keysounds. The map folder's sound files;
// Apply gives the selected notes the picked file (or, with "Use basic sample", a custom sample set number) and the
// volume, Reset clears them, Sample adds the file as a storyboard sound at the current time, Play plays it.
// (Import, Delete and Sample List - a file dialog, deleting from the folder, the sound event list - aren't in.)
sealed partial class Editor
{
    sealed class SampleImportWindow
    {
        public List<string> Files = [];
        public int Selected = -1, Scroll, CustomSet, Volume = 70;
        public bool Basic;
        public Vector2 Pos = new(float.NaN);
        public Vector2? Drag;
        public double LastClickAt = double.NegativeInfinity;
        public Vector2 Mouse;
    }
    SampleImportWindow? sampleImport;
    const float SiW = 431, SiH = 351;

    void OpenSampleImport()
    {
        if (mode != 3) { Notification.Show("This feature is only available for mania beatmaps.", 3000); return; }
        sampleImport ??= new SampleImportWindow();
        try
        {
            sampleImport.Files = [.. Directory.EnumerateFiles(info.Folder).Select(Path.GetFileName).OfType<string>()
                .Where(f => Path.GetExtension(f).ToLowerInvariant() is ".wav" or ".ogg" or ".mp3" && !f.Contains(',')).Order(StringComparer.OrdinalIgnoreCase)]; // (a comma would break the lines it's written into)
        }
        catch (IOException) { sampleImport.Files = []; }
        sampleImport.Files.Insert(0, "None");
    }

    Rectangle SiRect(float x, float y, float w, float h) { var p = sampleImport!.Pos; float s = AiS; return new Rectangle(p.X + x * s, p.Y + (AiCaption + y) * s, w * s, h * s); }
    static readonly (string Text, float X, float Y, bool On)[] SiButtons =
        [("Play", 284, 136, true), ("Import", 232, 178, false), ("Delete", 334, 178, false), ("Apply", 283, 219, true),
         ("Sample", 232, 262, true), ("Sample List", 334, 262, false), ("Reset", 232, 305, true), ("Cancel", 333, 305, true)];
    const float SiRowH = 15;
    int SiRows => (int)((328 - 4) / SiRowH);

    // a note's hit sample, "set:addition:index:volume:file": its custom index, volume and file (null: as it was)
    void SetHitSample(EObj o, int? index, int volume, string? file)
    {
        var f = (o.Sample.Length > 0 ? o.Sample : "0:0:0:0:").Split(':').ToList();
        while (f.Count < 5) f.Add(f.Count < 4 ? "0" : "");
        for (int k = 0; k < 4; k++) if (f[k].Length == 0) f[k] = "0";
        if (index is { } i) f[2] = i.ToString(Inv);
        f[3] = volume.ToString(Inv);
        if (file != null) f[4] = file;
        o.Sample = string.Join(':', f);
        o.Raw = null;
    }

    bool UpdateSampleImport(Vector2 mouse)
    {
        var w = sampleImport!;
        float s = AiS;
        w.Mouse = mouse;
        if (float.IsNaN(w.Pos.X)) w.Pos = new((Game.W - SiW * s) / 2, (EH - (SiH + AiCaption) * s) / 2);
        if (Input.IsKeyPressed(KeyboardKey.Escape)) { sampleImport = null; Input.Muted = true; return true; }
        bool click = Input.IsMouseButtonPressed(MouseButton.Left);
        if (w.Drag is { } d)
        {
            if (!Input.IsMouseButtonDown(MouseButton.Left)) w.Drag = null;
            else w.Pos = new(Math.Clamp(mouse.X - d.X, -SiW * s / 2, Game.W - SiW * s / 2), Math.Clamp(mouse.Y - d.Y, 0, EH - AiCaption * s));
            return true;
        }
        var whole = new Rectangle(w.Pos.X, w.Pos.Y, SiW * s, (SiH + AiCaption) * s);
        if (!CheckCollisionPointRec(mouse, whole)) return false;
        var cap = new Rectangle(w.Pos.X, w.Pos.Y, SiW * s, AiCaption * s);
        if (click && CheckCollisionPointRec(mouse, new Rectangle(cap.X + cap.Width - 28 * s, cap.Y, 28 * s, cap.Height))) { sampleImport = null; return true; }
        if (click && CheckCollisionPointRec(mouse, cap)) { w.Drag = mouse - w.Pos; return true; }

        string? file = w.Selected > 0 && w.Selected < w.Files.Count ? w.Files[w.Selected] : null;
        void Play() { if (file != null) samples.File(file)?.Play(BeatmapSamples.EffectVolume * w.Volume / 100f); }
        // the list (a double click plays)
        var list = SiRect(8, 11, 204, 328);
        float wheel = Input.GetMouseWheelMove();
        if (CheckCollisionPointRec(mouse, list))
        {
            if (wheel != 0) w.Scroll = Math.Clamp(w.Scroll - Math.Sign(wheel) * 3, 0, Math.Max(0, w.Files.Count - SiRows));
            int row = (int)((mouse.Y - list.Y - 2 * s) / (SiRowH * s)) + w.Scroll;
            if (click && row >= 0 && row < w.Files.Count)
            {
                bool again = row == w.Selected && Game.NowMs - w.LastClickAt < 400;
                w.Selected = row;
                w.LastClickAt = Game.NowMs;
                if (again) { file = row > 0 ? w.Files[row] : null; Play(); }
            }
            return true;
        }
        // the numbers: the arrows (or the wheel) step them
        void Numeric(Rectangle r, ref int value, int min, int max)
        {
            if (!CheckCollisionPointRec(mouse, r)) return;
            int step = wheel != 0 ? Math.Sign(wheel) : click && mouse.X > r.X + r.Width - 16 * AiS ? (mouse.Y < r.Y + r.Height / 2 ? 1 : -1) : 0;
            value = Math.Clamp(value + step, min, max);
        }
        Numeric(SiRect(327, 29, 85, 21), ref w.CustomSet, 0, 1000);
        Numeric(SiRect(327, 61, 85, 21), ref w.Volume, 8, 100);
        if (click && CheckCollisionPointRec(mouse, SiRect(247, 95, 128, 19))) w.Basic = !w.Basic;
        if (!click) return true;
        for (int i = 0; i < SiButtons.Length; i++)
        {
            var (text, x, y, on) = SiButtons[i];
            if (!on || !CheckCollisionPointRec(mouse, SiRect(x, y, 86, 30))) continue;
            switch (text)
            {
                case "Play": Play(); break;
                case "Apply":
                    if (!w.Basic && w.Selected < 0) { Notification.Show("You havn't selected a sample yet.", 2000); break; }
                    if (selected.Count > 0) Change(() => { foreach (var o in selected) SetHitSample(o, w.Basic ? w.CustomSet : null, w.Volume, w.Basic ? null : file ?? ""); });
                    Notification.Show("Done", 1000);
                    break;
                case "Reset":
                    if (selected.Count > 0) Change(() => { foreach (var o in selected) SetHitSample(o, w.Basic ? 0 : null, 0, w.Basic ? null : ""); });
                    Notification.Show("Selected objects' hit samples have been reset.", 2000);
                    break;
                case "Sample": // a storyboard sound on the background layer, where stable puts it
                    if (file == null) break;
                    var ev = sections.First(sec => sec.Name == "Events");
                    int at = ev.Lines.FindIndex(l => l.Trim() == "//Storyboard Sound Samples");
                    string line = $"Sample,{(int)Math.Round(time)},0,\"{file}\",{w.Volume}";
                    Change(() => { if (at >= 0) ev.Lines.Insert(at + 1, line); else ev.Lines.Add(line); });
                    Notification.Show("Done", 1000);
                    break;
                case "Cancel": sampleImport = null; break;
            }
            return true;
        }
        return true;
    }

    void DrawSampleImport()
    {
        var w = sampleImport!;
        float s = AiS, fs = 11 * s;
        if (float.IsNaN(w.Pos.X)) return;
        Color face = new(240, 240, 240, 255), text = Color.Black, grey = new(160, 160, 160, 255);
        var mouse = w.Mouse;
        var whole = new Rectangle(w.Pos.X, w.Pos.Y, SiW * s, (SiH + AiCaption) * s);
        var cap = new Rectangle(w.Pos.X, w.Pos.Y, SiW * s, AiCaption * s);
        DrawRectangle((int)(whole.X + 4 * s), (int)(whole.Y + 4 * s), (int)whole.Width, (int)whole.Height, new Color(0, 0, 0, 70));
        DrawRectangleRec(whole, face);
        DrawRectangleRec(cap, Color.White);
        Game.Text("Sample import", cap.X + 6 * s, cap.Y + (cap.Height - fs) / 2, fs, text);
        float cx = cap.X + cap.Width - 14 * s, cy = cap.Y + cap.Height / 2;
        DrawLineEx(new(cx - 4 * s, cy - 4 * s), new(cx + 4 * s, cy + 4 * s), 1.2f * s, text);
        DrawLineEx(new(cx - 4 * s, cy + 4 * s), new(cx + 4 * s, cy - 4 * s), 1.2f * s, text);
        DrawRectangleLinesEx(whole, 1, grey);

        var list = SiRect(8, 11, 204, 328);
        DrawRectangleRec(list, Color.White);
        DrawRectangleLinesEx(list, 1, new Color(130, 135, 144, 255));
        for (int r = 0; r < SiRows && r + w.Scroll < w.Files.Count; r++)
        {
            var row = new Rectangle(list.X + 2 * s, list.Y + (2 + r * SiRowH) * s, list.Width - 4 * s, SiRowH * s);
            bool sel = r + w.Scroll == w.Selected;
            if (sel) DrawRectangleRec(row, new Color(0, 120, 215, 255));
            Game.Text(w.Files[r + w.Scroll], row.X + 2 * s, row.Y + (row.Height - fs) / 2, fs, sel ? Color.White : text);
        }

        var group = SiRect(232, 9, 187, 113);
        DrawRectangleLinesEx(group, 1, new Color(220, 220, 220, 255));
        DrawRectangleRec(new Rectangle(group.X + 6 * s, group.Y - fs / 2, Game.TextWidth("Basic", fs) + 4 * s, fs), face);
        Game.Text("Basic", group.X + 8 * s, group.Y - fs / 2 - 1, fs, text);
        Game.Text("CustomSet", SiRect(244, 31, 0, 0).X, SiRect(244, 31, 0, 0).Y, fs, text);
        Game.Text("Volume", SiRect(244, 63, 0, 0).X, SiRect(244, 63, 0, 0).Y, fs, text);
        foreach (var (r, v) in new[] { (SiRect(327, 29, 85, 21), w.CustomSet), (SiRect(327, 61, 85, 21), w.Volume) })
        {
            DrawRectangleRec(r, Color.White);
            DrawRectangleLinesEx(r, 1, new Color(122, 122, 122, 255));
            Game.Text(v.ToString(), r.X + 3 * s, r.Y + (r.Height - fs) / 2, fs, text);
            float ax = r.X + r.Width - 9 * s;
            DrawTriangle(new(ax, r.Y + 4 * s), new(ax - 3 * s, r.Y + 8 * s), new(ax + 3 * s, r.Y + 8 * s), text);
            DrawTriangle(new(ax - 3 * s, r.Y + r.Height - 8 * s), new(ax, r.Y + r.Height - 4 * s), new(ax + 3 * s, r.Y + r.Height - 8 * s), text);
        }
        var box = SiRect(247, 98, 13, 13);
        DrawRectangleRec(box, Color.White);
        DrawRectangleLinesEx(box, 1, new Color(51, 51, 51, 255));
        if (w.Basic)
        {
            DrawLineEx(new(box.X + 3 * s, box.Y + 6.5f * s), new(box.X + 5.5f * s, box.Y + 9.5f * s), 1.5f * s, text);
            DrawLineEx(new(box.X + 5.5f * s, box.Y + 9.5f * s), new(box.X + 10 * s, box.Y + 3.5f * s), 1.5f * s, text);
        }
        Game.Text("Use basic sample", box.X + 17 * s, box.Y + (box.Height - fs) / 2, fs, text);

        foreach (var (label, x, y, on) in SiButtons)
        {
            var r = SiRect(x, y, 86, 30);
            bool hover = on && CheckCollisionPointRec(mouse, r);
            DrawRectangleRec(r, hover ? new Color(229, 241, 251, 255) : new Color(225, 225, 225, 255));
            DrawRectangleLinesEx(r, 1, hover ? new Color(0, 120, 215, 255) : new Color(173, 173, 173, 255));
            Game.Text(label, r.X + r.Width / 2 - Game.TextWidth(label, fs) / 2, r.Y + (r.Height - fs) / 2, fs, on ? text : grey);
        }
    }
}
