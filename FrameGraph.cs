using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// stable's frame time graph (Ctrl+F11, for the session): the last 800 frames at the bottom right of the game, one
// pixel column each, newest on the right. Each column stacks the frame's phases from the bottom at 5px a ms (the top
// is 20ms) in stable's colours: update YellowGreen, draw BlueViolet, swap (present) Red, between frames GhostWhite,
// sleep (the frame limiter) DarkBlue; above them a dark (50,40,40) fill whose alpha steps down every 5ms. A garbage
// collection drops a 3px dot (green / yellow / red by generation) at the top that moves along with the frames.
// Holding Ctrl pauses it and shows the phase names. Sizes are window pixels, whatever the resolution.
static class FrameGraph
{
    public static bool On;
    const int Width = 800, Height = 100;
    static Texture2D tex;
    static readonly Color[] column = new Color[Height];
    static int at; // the column written next
    static readonly List<(int Age, int Gen)> gcs = [];
    static readonly int[] gcCounts = new int[3];
    static readonly (string Name, Color Colour)[] Phases =
    [
        ("Update", new(154, 205, 50, 255)), ("Draw", new(138, 43, 226, 255)), ("SwapBuffer", new(255, 0, 0, 255)),
        ("BetweenFrames", new(248, 248, 255, 255)), ("Sleep", new(0, 0, 139, 255)),
    ];
    static readonly Color[] GcColours = [new(0, 128, 0, 255), new(255, 255, 0, 255), new(255, 0, 0, 255)]; // XNA Green / Yellow / Red

    static Color Empty(int row) => new(50, 40, 40, (int)(180 * (1 - row * 4 / 100 / 8f))); // (stable's integer bands)

    public static void Toggle()
    {
        On = !On;
        if (On) Toast.Show("Frame time graph is now visible. Press Ctrl+F11 to hide.", new Color(255, 255, 0, 255), 300000); // stable's (Yellow, 5 minutes)
        if (!On || tex.Id != 0) return;
        var all = new Color[Width * Height];
        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++) all[y * Width + x] = Empty(y);
        var img = GenImageColor(Width, Height, Color.Blank);
        tex = LoadTextureFromImage(img);
        UnloadImage(img);
        UpdateTexture(tex, all);
    }

    // the last frame's phase times (ms), in stable's stacking order
    public static void Add(double update, double draw, double swap, double between, double sleep)
    {
        if (!On || Input.CtrlHeld) return;
        Span<double> ms = [update, draw, swap, between, sleep];
        int top = Height; // the next free row going up
        for (int p = 0; p < ms.Length; p++)
            for (int px = (int)(ms[p] * 5); px > 0 && top > 0; px--) column[--top] = Phases[p].Colour;
        for (int row = top - 1; row >= 0; row--) column[row] = Empty(row);
        UpdateTextureRec(tex, new Rectangle(at, 0, 1, Height), column);
        at = (at + 1) % Width;
        for (int i = gcs.Count - 1; i >= 0; i--)
            if (gcs[i].Age + 1 > Width) gcs.RemoveAt(i); else gcs[i] = (gcs[i].Age + 1, gcs[i].Gen);
        for (int g = 0; g < 3; g++)
            if (GC.CollectionCount(g) is var n && n != gcCounts[g]) { gcCounts[g] = n; gcs.Add((0, g)); }
    }

    public static void Draw()
    {
        if (!On) return;
        float x0 = Game.W - Width, y0 = Game.H - Height + 1;
        // the ring buffer drawn oldest first: columns at..799, then 0..at-1
        DrawTextureRec(tex, new Rectangle(at, 0, Width - at, Height), new Vector2(x0, y0), Color.White);
        DrawTextureRec(tex, new Rectangle(0, 0, at, Height), new Vector2(x0 + Width - at, y0), Color.White);
        foreach (var (age, gen) in gcs) DrawRectangle((int)(x0 + Width - 1 - age), (int)y0, 3, 3, GcColours[gen]);
        float size = 18 * 1.035f;
        Game.Text("20ms", x0, y0, size, Color.White);
        Game.Text("0ms", x0, y0 + Height - size, size, Color.White);
        if (!Input.CtrlHeld) return;
        float x = x0 + Game.TextWidth("20ms", size) + 0.6f * size, big = 27 * 1.035f; // (stable's padded text bitmaps space them)
        foreach (var (name, colour) in Phases)
        {
            Game.Text(name, x, y0, big, colour);
            x += Game.TextWidth(name, big) + 0.28f * big;
        }
    }
}
