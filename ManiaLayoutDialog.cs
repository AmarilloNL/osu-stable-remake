using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// Options → "osu!mania layout": the keys for each key count. Pick a count, click a column, press its key; the layout
// is saved the way stable keeps it (ManiaLayouts{n}K, ManiaLayoutSelected{n}K, in remake.cfg) and used in plays.
// "Default" goes back to stable's own layout for the count.
sealed class ManiaLayoutDialog
{
    readonly double openedAt = Game.NowMs;
    internal float Alpha => (float)Math.Clamp((Game.NowMs - openedAt) / 200, 0, 1);
    public bool Closed { get; private set; }
    int keys = 4, column = -1;

    static float U => Game.H / 480f;
    static Rectangle CountRect(int i) { float u = U; return new Rectangle(Game.W / 2f + (i - 5) * 44 * u + 2 * u, 70 * u, 40 * u, 20 * u); }
    Rectangle ColumnRect(int c) { float u = U, cw = Math.Min(40, 400f / keys); return new Rectangle(Game.W / 2f + (c - keys / 2f) * cw * u, 120 * u, (cw - 2) * u, 220 * u); }
    static Rectangle DefaultRect() { float u = U; return new Rectangle(Game.W / 2f - 60 * u, 360 * u, 120 * u, 22 * u); }

    public void Update()
    {
        if (column >= 0)
        {
            int key = Input.GetKeyPressed();
            if (key == (int)KeyboardKey.Escape) column = -1;
            else if (key != 0)
            {
                var layout = Player.ManiaKeys(keys).ToArray(); // (a copy: the defaults are shared)
                layout[column] = (KeyboardKey)key;
                Config.SetOwn($"ManiaLayouts{keys}K", string.Join(' ', layout.Select(k => k == KeyboardKey.Null ? "None" : BindingsDialog.CfgName(k))));
                Config.SetOwn($"ManiaLayoutSelected{keys}K", "0");
                Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume);
                column = -1;
            }
            return;
        }
        if (Input.IsKeyPressed(KeyboardKey.Escape)) { Closed = true; return; }
        if (!Input.IsMouseButtonPressed(MouseButton.Left)) return;
        var mouse = Input.GetMousePosition();
        for (int i = 0; i < 10; i++)
            if (CheckCollisionPointRec(mouse, CountRect(i))) { keys = i + 1; Game.Skin.Sound("click-short")?.Play(BeatmapSamples.EffectVolume); return; }
        for (int c = 0; c < keys; c++)
            if (CheckCollisionPointRec(mouse, ColumnRect(c))) { column = c; Game.Skin.Sound("click-short")?.Play(BeatmapSamples.EffectVolume); return; }
        if (CheckCollisionPointRec(mouse, DefaultRect()))
        {
            Config.SetOwn($"ManiaLayoutSelected{keys}K", "-1");
            Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume);
            return;
        }
        Closed = true; // (a click outside)
    }

    public void Draw()
    {
        float u = U, a = Alpha;
        Dialog.Backdrop(a);
        Dialog.Title("osu!mania layout", a);
        var mouse = Input.GetMousePosition();
        for (int i = 0; i < 10; i++)
        {
            var r = CountRect(i);
            bool on = keys == i + 1, hover = column < 0 && CheckCollisionPointRec(mouse, r);
            DrawRectangleRec(r, ColorAlpha(on ? new Color(255, 160, 40, 255) : hover ? Xna.Gray : Color.Black, a));
            DrawRectangleLinesEx(r, 1, ColorAlpha(Xna.LightGray, a));
            Centred($"{i + 1}K", r, 12 * u, ColorAlpha(Color.White, a));
        }
        var layout = Player.ManiaKeys(keys);
        for (int c = 0; c < keys; c++)
        {
            var r = ColumnRect(c);
            string kind = Player.ManiaColumnKind(c, keys);
            var tint = kind == "2" ? new Color(250, 170, 212, 255) : kind == "S" ? new Color(247, 236, 0, 255) : Color.White;
            bool hover = column < 0 && CheckCollisionPointRec(mouse, r);
            DrawRectangleRec(r, ColorAlpha(hover ? new Color(60, 60, 60, 255) : Color.Black, a));
            DrawRectangleLinesEx(r, 1, ColorAlpha(tint, a * 0.8f));
            var k = new Rectangle(r.X + 2 * u, r.Y + r.Height - 24 * u, r.Width - 4 * u, 20 * u);
            DrawRectangleLinesEx(k, 1, ColorAlpha(new Color(211, 178, 0, 255), a));
            Centred(layout[c] == KeyboardKey.Null ? "-" : Player.KeyName(layout[c]), k, 11 * u, ColorAlpha(Xna.Gold, a));
        }
        var d = DefaultRect();
        DrawRectangleRec(d, ColorAlpha(CheckCollisionPointRec(mouse, d) && column < 0 ? Xna.Red : Color.Black, a));
        DrawRectangleLinesEx(d, 1, ColorAlpha(Xna.LightGray, a));
        Centred("Default", d, 12 * u, ColorAlpha(Color.White, a));
        if (column >= 0)
        {
            DrawRectangle(0, 0, Game.W, Game.H, new Color(0, 0, 0, 200));
            void Line(string text, float y, float size, Color c) => Game.Text(text, Game.W / 2f - Game.TextWidth(text, size * u) / 2, (y - size / 2) * u, size * u, c);
            Line($"{keys}K: column {column + 1}", 120, 40, Xna.Orange);
            Line("Press a key to change the binding...", 220, 40, Color.White);
            Line("Hit ESC to cancel.", 260, 40, Color.White);
        }
    }

    static void Centred(string s, Rectangle r, float size, Color c) =>
        Game.Text(s, r.X + r.Width / 2 - Game.TextWidth(s, size) / 2, r.Y + r.Height / 2 - size / 2, size, c);
}
