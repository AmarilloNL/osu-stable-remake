using System.Globalization;
using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// The rest of stable's Song Setup, down the right of the Song Setup tab: the combo colours (picked from the palette),
// the countdown (its speed and how many beats early), the misc. toggles (letterbox in breaks, widescreen storyboard,
// epilepsy warning, samples matching the playback rate), the default sample set and (osu!) the stack leniency.
sealed partial class Editor
{
    int setupColourPick = -1; // the combo colour being picked (the next one: a new colour)

    float SetupX => Game.W / U - 190;
    Rectangle SetupRow(float y) { float u = U; return new Rectangle(SetupX * u, y * u, 180 * u, 13 * u); }
    Rectangle ComboSwatch(int i) { float u = U; return new Rectangle((SetupX + 2 + (i % 4) * 24) * u, (112 + i / 4 * 20) * u, 20 * u, 16 * u); }
    Rectangle ComboButton(int i) { float u = U; return new Rectangle((SetupX + 100 + i * 24) * u, 152 * u, 20 * u, 14 * u); }

    // [Colours]: Combo1..ComboN, as stable writes them ("Combo1 : r,g,b")
    List<(int R, int G, int B)> ComboColours()
    {
        var s = sections.FirstOrDefault(x => x.Name == "Colours");
        var list = new List<(int, int, int)>();
        for (int n = 1; n <= 8; n++)
        {
            var line = s?.Lines.FirstOrDefault(l => l.Split(':')[0].Trim() == $"Combo{n}");
            if (line == null) break;
            var v = line[(line.IndexOf(':') + 1)..].Split(',').Select(p => int.TryParse(p.Trim(), out int x) ? Math.Clamp(x, 0, 255) : 0).ToArray();
            list.Add(v.Length >= 3 ? (v[0], v[1], v[2]) : (255, 255, 255));
        }
        return list;
    }
    void SetComboColours(List<(int R, int G, int B)> colours) => Change(() =>
    {
        var s = sections.FirstOrDefault(x => x.Name == "Colours");
        if (s == null)
        {
            s = new Section("Colours");
            sections.Insert(Math.Max(0, sections.FindIndex(x => x.Name == "HitObjects")), s); // (before [HitObjects], where stable puts it)
        }
        int at = s.Lines.FindIndex(l => l.TrimStart().StartsWith("Combo"));
        s.Lines.RemoveAll(l => l.TrimStart().StartsWith("Combo"));
        at = at < 0 ? 0 : Math.Min(at, s.Lines.Count);
        s.Lines.InsertRange(at, colours.Select((c, i) => $"Combo{i + 1} : {c.R},{c.G},{c.B}"));
        modified = true;
        info = ReloadInfo();
    });

    string General(string key, string fallback) => Kv("General", key) is { Length: > 0 } v ? v : fallback;
    void SetGeneral(string key, string value) { if (General(key, "") != value) Change(() => SetKv("General", key, value)); }

    // true when it took the click
    bool UpdateSetupExtras(Vector2 mouse)
    {
        bool click = Input.IsMouseButtonPressed(MouseButton.Left);
        if (setupColourPick >= 0)
        {
            if (Input.IsKeyPressed(KeyboardKey.Escape)) { setupColourPick = -1; return true; }
            if (!click) return true;
            for (int i = 0; i < BasicColours.Length; i++)
                if (CheckCollisionPointRec(mouse, ColourSwatch(i)))
                {
                    uint c = BasicColours[i];
                    var colours = ComboColours();
                    var picked = ((int)(c >> 16 & 0xFF), (int)(c >> 8 & 0xFF), (int)(c & 0xFF));
                    if (setupColourPick < colours.Count && colours[setupColourPick] == picked) break; // (the same colour)
                    if (setupColourPick < colours.Count) colours[setupColourPick] = picked; else colours.Add(picked);
                    SetComboColours(colours);
                }
            setupColourPick = -1;
            return true;
        }
        if (!click) return false;
        var combos = ComboColours();
        for (int i = 0; i < combos.Count; i++)
            if (CheckCollisionPointRec(mouse, ComboSwatch(i))) { setupColourPick = i; return true; }
        if (CheckCollisionPointRec(mouse, ComboButton(0)) && combos.Count < 8) { setupColourPick = combos.Count; return true; } // (New Combo Colour)
        if (CheckCollisionPointRec(mouse, ComboButton(1)) && combos.Count > 0) { combos.RemoveAt(combos.Count - 1); SetComboColours(combos); return true; } // (Remove Combo Colour)

        int countdown = int.TryParse(General("Countdown", "1"), out int cd) ? cd : 1;
        if (CheckCollisionPointRec(mouse, SetupRow(186))) { SetGeneral("Countdown", countdown == 0 ? "1" : "0"); return true; }
        if (countdown > 0 && CheckCollisionPointRec(mouse, SetupRow(200))) { SetGeneral("Countdown", (countdown % 3 + 1).ToString(CultureInfo.InvariantCulture)); return true; } // normal, half, double
        if (countdown > 0 && CheckCollisionPointRec(mouse, SetupRow(214)))
        {
            int offset = int.TryParse(General("CountdownOffset", "0"), out int o) ? o : 0;
            SetGeneral("CountdownOffset", ((offset + 1) % 4).ToString(CultureInfo.InvariantCulture));
            return true;
        }
        string[] toggles = ["LetterboxInBreaks", "WidescreenStoryboard", "EpilepsyWarning", "SamplesMatchPlaybackRate"];
        for (int i = 0; i < toggles.Length; i++)
            if (CheckCollisionPointRec(mouse, SetupRow(246 + i * 14))) { SetGeneral(toggles[i], General(toggles[i], "0") == "1" ? "0" : "1"); return true; }
        if (CheckCollisionPointRec(mouse, SetupRow(318)))
        {
            string set = General("SampleSet", "Normal");
            SetGeneral("SampleSet", set == "Normal" ? "Soft" : set == "Soft" ? "Drum" : "Normal");
            return true;
        }
        // the map-wide volume and custom sample set: every timing point's (only when they all share one, like stable's)
        for (int k = 0; k < 2; k++)
        {
            var r = SetupRow(332 + k * 14);
            if (!CheckCollisionPointRec(mouse, r) || timing.Count == 0) continue;
            var values = timing.Select(p => k == 0 ? p.Volume : p.Index).Distinct().ToList();
            if (values.Count != 1) return true;
            int step = (mouse.X < r.X + r.Width / 2 ? -1 : 1) * (k == 0 ? 5 : 1), to = Math.Clamp(values[0] + step, k == 0 ? 5 : 0, 100);
            if (to != values[0]) Change(() => { foreach (var p in timing) { if (k == 0) p.Volume = to; else p.Index = to; p.Raw = null; } });
            return true;
        }
        if (mode == 0 && CheckCollisionPointRec(mouse, SetupRow(374)))
        {
            // Rarely Stack (0.2) to Always Stack (1.0): the left half lowers, the right raises
            double stack = double.TryParse(General("StackLeniency", "0.7"), NumberStyles.Float, CultureInfo.InvariantCulture, out double sl) ? sl : 0.7;
            var r = SetupRow(374);
            stack = Math.Clamp(Math.Round(stack + (mouse.X < r.X + r.Width / 2 ? -0.1 : 0.1), 1), 0.2, 1.0);
            SetGeneral("StackLeniency", stack.ToString(CultureInfo.InvariantCulture));
            return true;
        }
        return false;
    }

    void DrawSetupExtras(float u)
    {
        var mouse = Input.GetMousePosition();
        void Header(string text, float y) => Game.TextBold(text, SetupX * u, y * u, 11 * u, Color.White);
        void Row(string text, float y, bool? on = null)
        {
            var r = SetupRow(y);
            bool hover = CheckCollisionPointRec(mouse, r);
            if (on is { } o)
            {
                var at = new Vector2(r.X + 5 * u, r.Y + 6.5f * u);
                var pink = new Color(235, 117, 140, 255);
                if (o) DrawCircleV(at, 3 * u, pink); else DrawRing(at, 2.3f * u, 3 * u, 0, 360, 24, pink);
            }
            Game.Text(text, r.X + (on == null ? 2 : 14) * u, r.Y, 10 * u, hover ? new Color(255, 200, 120, 255) : Color.White);
        }
        DrawRectangle((int)((SetupX - 6) * u), (int)(80 * u), (int)(186 * u), (int)(312 * u), new Color(0, 0, 0, 200));

        Header("Colours", 96);
        var combos = ComboColours();
        for (int i = 0; i < combos.Count; i++)
        {
            var r = ComboSwatch(i);
            DrawRectangleRec(r, new Color(combos[i].R, combos[i].G, combos[i].B, 255));
            DrawRectangleLinesEx(r, 1, CheckCollisionPointRec(mouse, r) ? Color.White : Color.Gray);
            Game.Text($"{i + 1}", r.X + 2 * u, r.Y + 2 * u, 8 * u, combos[i].R + combos[i].G + combos[i].B > 382 ? Color.Black : Color.White);
        }
        if (combos.Count == 0) Game.Text("(the skin's)", (SetupX + 2) * u, 114 * u, 9 * u, Color.Gray);
        string[] buttons = ["+", "-"];
        for (int i = 0; i < 2; i++)
        {
            var r = ComboButton(i);
            bool enabled = i == 0 ? combos.Count < 8 : combos.Count > 0;
            DrawRectangleRec(r, new Color(40, 40, 40, 255));
            DrawRectangleLinesEx(r, 1, enabled ? Color.LightGray : Color.DarkGray);
            Game.Text(buttons[i], r.X + r.Width / 2 - Game.TextWidth(buttons[i], 11 * u) / 2, r.Y + 1 * u, 11 * u, enabled ? Color.White : Color.Gray);
        }

        int countdown = int.TryParse(General("Countdown", "1"), out int cd) ? cd : 1;
        Header("Countdown", 172);
        Row("Enable countdown", 186, countdown > 0);
        if (countdown > 0)
        {
            Row($"Speed: {countdown switch { 2 => "Half", 3 => "Double", _ => "Normal" }}", 200);
            Row($"Countdown Offset: {General("CountdownOffset", "0")} beat(s) early", 214);
        }
        Header("Misc. Toggles", 232);
        Row("Letterbox during breaks", 246, General("LetterboxInBreaks", "0") == "1");
        Row("Widescreen storyboard", 260, General("WidescreenStoryboard", "0") == "1");
        Row("Display epilepsy warning", 274, General("EpilepsyWarning", "0") == "1");
        Row("Samples match playback rate", 288, General("SamplesMatchPlaybackRate", "0") == "1");
        Header("Audio", 304);
        Row($"Default sample set: {General("SampleSet", "Normal")}", 318);
        var volumes = timing.Select(p => p.Volume).Distinct().ToList();
        var sets = timing.Select(p => p.Index).Distinct().ToList();
        Row(volumes.Count == 1 ? $"Sample volume: {volumes[0]}%  (- / +)" : "Sample volume: per timing section", 332);
        Row(sets.Count == 1 ? $"Custom sample set: {(sets[0] == 0 ? "off" : sets[0].ToString(CultureInfo.InvariantCulture))}  (- / +)" : "Custom sample set: per timing section", 346);
        if (mode == 0)
        {
            Header("Advanced", 360);
            Row($"Stack Leniency: {General("StackLeniency", "0.7")}  (- / +)", 374);
        }

        if (setupColourPick >= 0)
        {
            var first = ColourSwatch(0);
            var box = new Rectangle(first.X - 10 * u, first.Y - 26 * u, 8 * 25 * u + 17 * u, 6 * 21 * u + 34 * u);
            DrawRectangleRec(box, new Color(240, 240, 240, 255));
            DrawRectangleLinesEx(box, 1, new Color(160, 160, 160, 255));
            Game.Text(setupColourPick < combos.Count ? $"Combo {setupColourPick + 1}:" : "New combo colour:", box.X + 8 * u, box.Y + 6 * u, 10 * u, Color.Black);
            for (int i = 0; i < BasicColours.Length; i++)
            {
                var r = ColourSwatch(i);
                uint c = BasicColours[i];
                DrawRectangleRec(r, new Color((int)(c >> 16 & 0xFF), (int)(c >> 8 & 0xFF), (int)(c & 0xFF), 255));
                DrawRectangleLinesEx(r, 1, CheckCollisionPointRec(mouse, r) ? Color.Black : new Color(130, 130, 130, 255));
            }
        }
    }
}
