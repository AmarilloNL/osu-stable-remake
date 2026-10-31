using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// stable's "Change keyboard bindings" dialog: its dialog title, then a 420x420-unit scrolling list (from 110, 30 of
// the 640x480 frame) of 17-unit rows: category headers (16, yellow-green, centred) and bindings (14 on black with a
// light grey edge, grey when hovered) with the key in a gold-edged 12 label at 300; "Reset all bindings"
// ends it. Clicking a row waits for a key over the list: the binding's name (orange, 40, at 120), "Press a key to
// change the binding..." (40, at 240) and the conflicts notice (30, at 360). Bindings are saved to remake.cfg.
sealed class BindingsDialog
{
    readonly double openedAt = Game.NowMs;
    float scroll, scrollTarget;
    int binding = -1;
    public bool Closed { get; private set; }

    static float U => GetScreenHeight() / 480f;
    float X0 => GetScreenWidth() / U / 2 - 210; // the 640x480 frame is centred
    const float Top = 30, Height = 420, RowH = 17, RowW = 402;
    static float Edge => Math.Max(2, 1.4f * U); // measured: 3px at 1080p
    static int Count => KeyBindings.Rows.Length + 1; // + "Reset all bindings"

    public void Update()
    {
        float u = U;
        if (binding >= 0)
        {
            int key = Input.GetKeyPressed();
            if (key == (int)KeyboardKey.Escape) binding = -1;
            else if (key != 0)
            {
                Config.SetOwn(KeyBindings.Rows[binding].Key, CfgName((KeyboardKey)key));
                Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume);
                binding = -1;
            }
            return;
        }
        if (Input.IsKeyPressed(KeyboardKey.Escape)) { Closed = true; return; }
        scrollTarget = Math.Clamp(scrollTarget - VolumeControl.Wheel * 40, 0, Math.Max(0, Count * RowH - Height));
        scroll += (scrollTarget - scroll) * 0.3f;
        var mouse = Input.GetMousePosition();
        if (!Input.IsMouseButtonPressed(MouseButton.Left)) return;
        if (!CheckCollisionPointRec(mouse, new Rectangle(X0 * u, Top * u, RowW * u, Height * u))) { Closed = true; return; }
        int row = (int)((mouse.Y / u - Top + scroll) / RowH);
        if (row == KeyBindings.Rows.Length)
        {
            foreach (var r in KeyBindings.Rows) if (r.Key.Length > 0) Config.SetOwn(r.Key, r.Default);
            Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume);
        }
        else if (row >= 0 && row < KeyBindings.Rows.Length && KeyBindings.Rows[row].Key.Length > 0)
        {
            binding = row;
            Game.Skin.Sound("click-short")?.Play(BeatmapSamples.EffectVolume);
        }
    }

    public void Draw()
    {
        float u = U, a = (float)Math.Clamp((Game.NowMs - openedAt) / 200, 0, 1);
        Dialog.Backdrop(a);
        Dialog.Title("Change keyboard bindings", a);
        var mouse = Input.GetMousePosition();
        float x = X0 * u, w = RowW * u, rh = RowH * u;
        BeginScissorMode((int)x, (int)(Top * u), (int)(w + 2 * u), (int)(Height * u));
        for (int i = 0; i < Count; i++)
        {
            float y = (Top + i * RowH - scroll) * u;
            if (y + rh < Top * u || y > (Top + Height) * u) continue;
            var r = new Rectangle(x, y, w, rh);
            bool hover = binding < 0 && CheckCollisionPointRec(mouse, r);
            if (i == KeyBindings.Rows.Length)
            {
                DrawRectangleRec(r, ColorAlpha(hover ? Color.Red : Color.Black, a));
                DrawRectangleLinesEx(r, Edge, ColorAlpha(Color.LightGray, a));
                Centred("Reset all bindings", r, 14 * u, ColorAlpha(Color.White, a));
                continue;
            }
            var row = KeyBindings.Rows[i];
            if (row.Name[0] == '_')
            {
                DrawRectangleRec(r, ColorAlpha(Color.Black, a));
                Centred(row.Name[1..], r, 16 * u, ColorAlpha(new Color(154, 205, 50, 255), a));
                continue;
            }
            DrawRectangleRec(r, ColorAlpha(hover ? Color.Gray : Color.Black, a));
            DrawRectangleLinesEx(r, Edge, ColorAlpha(Color.LightGray, a));
            Game.Text(row.Name, x + 2 * u, y + rh / 2 - 8.85f * u, 14.7f * u, ColorAlpha(Color.White, a)); // stable: 14, renders 5% wider (measured)
            var k = new Rectangle(x + 300 * u, y + u, 100 * u, 15 * u);
            DrawRectangleRec(k, ColorAlpha(Color.Black, a));
            DrawRectangleLinesEx(k, Edge, ColorAlpha(new Color(211, 178, 0, 255), a));
            Centred(KeyBindings.Shown(row), new Rectangle(k.X, k.Y - 1.3f * u, k.Width, k.Height), 12 * u, ColorAlpha(Color.Gold, a));
        }
        // a scroll bar along the right while the list is hovered
        float total = Count * RowH;
        if (binding < 0 && CheckCollisionPointRec(mouse, new Rectangle(x, Top * u, w + 12 * u, Height * u)))
            DrawRectangleRec(new Rectangle(x + w + 10 * u, (Top + scroll / total * Height) * u, 5 * u, Height * Height / total * u), ColorAlpha(Color.White, a));
        EndScissorMode();
        if (binding >= 0)
        {
            DrawRectangle(0, 0, GetScreenWidth(), GetScreenHeight(), new Color(0, 0, 0, 200));
            float cx = GetScreenWidth() / 2f;
            void Lines(string text, float y, float size, Color c)
            {
                var lines = text.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                    Game.Text(lines[i], cx - Game.TextWidth(lines[i], size * u) / 2, (y - lines.Length * size / 2 + i * size) * u, size * u, c);
            }
            Lines(KeyBindings.Rows[binding].Name, 120, 40, Color.Orange);
            Lines("Press a key to change the binding...\nHit ESC to cancel.", 240, 40, Color.White);
            Lines("Notice: Be careful to avoid any\npotential binding conflicts!", 360, 30, Color.White);
        }
    }

    static void Centred(string s, Rectangle r, float size, Color c) =>
        Game.Text(s, r.X + r.Width / 2 - Game.TextWidth(s, size) / 2, r.Y + r.Height / 2 - size / 2, size, c);

    // osu!.cfg's names for keys (the .NET Keys enum): letters, D0-D9, Oem keys, LeftShift...
    static string CfgName(KeyboardKey k) => k switch
    {
        >= KeyboardKey.A and <= KeyboardKey.Z => ((char)k).ToString(),
        >= KeyboardKey.Zero and <= KeyboardKey.Nine => $"D{(char)k}",
        KeyboardKey.Grave => "OemTilde", KeyboardKey.Minus => "OemMinus", KeyboardKey.Equal => "OemPlus",
        KeyboardKey.LeftBracket => "OemOpenBrackets", KeyboardKey.RightBracket => "OemCloseBrackets", KeyboardKey.Backslash => "OemPipe",
        KeyboardKey.Semicolon => "OemSemicolon", KeyboardKey.Apostrophe => "OemQuotes", KeyboardKey.Comma => "OemComma",
        KeyboardKey.Period => "OemPeriod", KeyboardKey.Slash => "OemQuestion", KeyboardKey.Enter => "Enter",
        KeyboardKey.LeftShift => "LeftShift", KeyboardKey.RightShift => "RightShift", KeyboardKey.LeftControl => "LeftControl",
        KeyboardKey.RightControl => "RightControl", KeyboardKey.LeftAlt => "LeftAlt", KeyboardKey.RightAlt => "RightAlt",
        KeyboardKey.PageUp => "PageUp", KeyboardKey.PageDown => "PageDown",
        _ => k.ToString(),
    };
}
