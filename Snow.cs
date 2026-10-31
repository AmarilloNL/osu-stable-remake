using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// stable's menu snow (the Snow option), in 640x480 units on its 60Hz tick: each tick a flake (the skin's menu-snow,
// else stable's mode-osu-med) may start above the screen - always half the time in kiai, otherwise with chance
// 0.4 x min(0.5, max(0.1, (music level - 30000) / 35536)) - at a random x, alpha 0..0.7, scale 0.1..0.6, sideways
// speed -500..500 changing by -2..1 a tick. It falls a unit a tick, drifts by speed / 5000 and spins by
// speed / 25000; cursor movement blows on it (10 x the move). A flake stops on the bottom or on the pile in its
// column, raises that column's pile to a quarter of its height below its centre and fades out over a minute;
// piles sink 0.05 units a tick.
static class Snow
{
    sealed class Flake { public Vector2 Pos; public float Alpha, Scale, Rot; public int Vx, Ax; public double StoppedAt = double.NaN; }
    static readonly List<Flake> flakes = [];
    static float[] pile = [];
    static double tickAt = double.NaN;
    static Vector2 cursorWas;

    public static void Update(SongSelect songs)
    {
        double now = Game.NowMs;
        if (double.IsNaN(tickAt)) tickAt = now;
        float u = GetScreenHeight() / 480f, wu = GetScreenWidth() / u;
        if (pile.Length != (int)wu + 1) pile = new float[(int)wu + 1];
        var tex = Tex();
        var cursor = Input.GetMousePosition() / u;
        bool on = Config.GetBool("MenuSnow", false);
        for (; now - tickAt >= 1000 / 60.0; tickAt += 1000 / 60.0)
        {
            if (cursorWas != Vector2.Zero && cursorWas != cursor)
                foreach (var f in flakes) if (double.IsNaN(f.StoppedAt)) f.Vx += (int)((cursor.X - cursorWas.X) * 10);
            cursorWas = cursor;
            for (int i = 0; i < pile.Length; i++) if (pile[i] > 0 && pile[i] < 480) pile[i] += 0.05f;
            if (on && tex != null)
            {
                int level = 0;
                if (songs.Music is { } m) { var (l, r) = m.Level; level = l + r; }
                float chance = songs.Beat() is { Kiai: true } ? 0.5f : Math.Min(0.5f, Math.Max(0.1f, (level - 30000) / 35536f)) * 0.4f;
                var rnd = Random.Shared;
                if (rnd.NextDouble() > 1 - chance)
                    flakes.Add(new Flake { Pos = new Vector2(rnd.NextSingle() * wu, -50), Alpha = rnd.NextSingle() * 0.7f,
                        Scale = 0.1f + rnd.NextSingle() * 0.5f, Vx = rnd.Next(-500, 500), Ax = rnd.Next(-2, 2) });
            }
            float half = tex is { } t ? t.Height / 1.6f / 2 : 0;
            foreach (var f in flakes)
            {
                if (!double.IsNaN(f.StoppedAt)) continue;
                bool inside = f.Pos.X >= 0 && f.Pos.X < wu;
                int col = (int)Math.Clamp(f.Pos.X, 0, pile.Length - 1);
                float bottom = f.Pos.Y + f.Scale * half, top = inside ? pile[col] : 0;
                if (bottom >= 480 || top > 0 && top <= bottom)
                {
                    f.StoppedAt = now;
                    if (inside) pile[col] = f.Pos.Y + f.Scale * half / 2;
                    continue;
                }
                f.Vx += f.Ax;
                f.Pos.X += f.Vx / 5000f;
                f.Pos.Y += 1;
                f.Rot += f.Vx / 10000f * 0.4f;
            }
        }
        flakes.RemoveAll(f => now - f.StoppedAt > 60000);
    }

    public static void Draw()
    {
        if (flakes.Count == 0 || Tex() is not { } tex) return;
        float u = GetScreenHeight() / 480f, sd = GetScreenHeight() / 768f;
        double now = Game.NowMs;
        BeginBlendMode(BlendMode.Additive);
        foreach (var f in flakes)
        {
            float a = f.Alpha * (double.IsNaN(f.StoppedAt) ? 1 : 1 - (float)((now - f.StoppedAt) / 60000));
            tex.Draw(f.Pos * u, sd * f.Scale, ColorAlpha(Color.White, a), f.Rot * 180 / MathF.PI);
        }
        EndBlendMode();
    }

    static Sprite? Tex() => Game.Skin.Own("menu-snow") ? Game.Skin.Tex("menu-snow") : Game.Skin.Tex("mode-osu-med");
}
