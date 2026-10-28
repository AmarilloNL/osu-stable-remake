using System.Globalization;
using System.Numerics;
using Raylib_cs;

namespace StableRemake;

// All keyboard/mouse reads go through here so test runs can script input inside the game (no OS-level injection):
// --input "3:wheel 1, 3.5:key Up, 4:click 2400 1300, 5:down 100 100, 5.2:move 100 50, 5.4:up, 6:text abc, 7:hold Z 500"
// Times are seconds since launch (the --shots clock); positions are window pixels. Scripted input adds to real input.
static class Input
{
    enum Kind { KeyDown, KeyUp, MouseDown, MouseUp, Move, Wheel, Text }
    readonly record struct Event(double At, Kind Kind, int Code = 0, Vector2 Pos = default, string Text = "");

    static readonly List<Event> script = [];
    static int next;
    static readonly HashSet<int> keysDown = [], keysPressed = [], buttonsDown = [], buttonsPressed = [];
    static readonly Queue<int> chars = new(), keyQueue = new();
    static Vector2? mouse;
    static float wheel;
    public static bool ScriptOnly; // test runs: the real keyboard/mouse are ignored (someone may be using the PC)

    public static void Load(string spec)
    {
        foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int colon = part.IndexOf(':');
            double t = double.Parse(part[..colon], CultureInfo.InvariantCulture);
            var a = part[(colon + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            Vector2 P(int i) => a.Length > i + 1 ? new(F(a[i]), F(a[i + 1])) : mouse ?? default;
            int B() => a[0].StartsWith('r') ? (int)MouseButton.Right : (int)MouseButton.Left;
            switch (a[0].ToLowerInvariant())
            {
                case "key" or "hold":
                    int k = (int)Enum.Parse<KeyboardKey>(a[1], true);
                    script.Add(new(t, Kind.KeyDown, k));
                    script.Add(new(t + (a.Length > 2 ? F(a[2]) / 1000 : 0.03), Kind.KeyUp, k));
                    break;
                case "click" or "rclick":
                    if (a.Length > 2) script.Add(new(t, Kind.Move, Pos: P(1)));
                    script.Add(new(t, Kind.MouseDown, B()));
                    script.Add(new(t + 0.05, Kind.MouseUp, B()));
                    break;
                case "down" or "rdown":
                    if (a.Length > 2) script.Add(new(t, Kind.Move, Pos: P(1)));
                    script.Add(new(t, Kind.MouseDown, B()));
                    break;
                case "up" or "rup": script.Add(new(t, Kind.MouseUp, B())); break;
                case "move": script.Add(new(t, Kind.Move, Pos: P(1))); break;
                case "wheel": script.Add(new(t, Kind.Wheel, (int)F(a[1]))); break;
                case "text": script.Add(new(t, Kind.Text, Text: a[1])); break;
                default: throw new FormatException("--input: unknown event " + part);
            }
        }
        var sorted = script.OrderBy(e => e.At).ToList(); // stable: a click's move stays before its press
        script.Clear(); script.AddRange(sorted);
        static float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
    }

    // Once per frame, before anything reads input: applies the script's events that are due.
    public static void Update(double secs)
    {
        keysPressed.Clear(); buttonsPressed.Clear(); keyQueue.Clear(); chars.Clear(); wheel = 0; // per frame, like raylib's queues
        // one script timestamp per frame, so a stalled frame (a screenshot) can't merge a press with the move after it
        double at = next < script.Count ? script[next].At : 0;
        for (; next < script.Count && script[next].At <= secs && script[next].At == at; next++)
        {
            var e = script[next];
            // a release in the frame of its own press waits a frame, so the press is seen
            if (e.Kind == Kind.KeyUp && keysPressed.Contains(e.Code) || e.Kind == Kind.MouseUp && buttonsPressed.Contains(e.Code)) break;
            switch (e.Kind)
            {
                case Kind.KeyDown: keysDown.Add(e.Code); keysPressed.Add(e.Code); keyQueue.Enqueue(e.Code); break;
                case Kind.KeyUp: keysDown.Remove(e.Code); break;
                case Kind.MouseDown: buttonsDown.Add(e.Code); buttonsPressed.Add(e.Code); break;
                case Kind.MouseUp: buttonsDown.Remove(e.Code); break;
                case Kind.Move: mouse = e.Pos; break;
                case Kind.Wheel: wheel += e.Code; break;
                case Kind.Text: foreach (char c in e.Text) chars.Enqueue(c); break;
            }
        }
    }

    public static bool IsKeyPressed(KeyboardKey k) => !ScriptOnly && Raylib.IsKeyPressed(k) || keysPressed.Contains((int)k);
    public static bool IsKeyPressedRepeat(KeyboardKey k) => !ScriptOnly && Raylib.IsKeyPressedRepeat(k);
    public static bool IsKeyDown(KeyboardKey k) => !ScriptOnly && Raylib.IsKeyDown(k) || keysDown.Contains((int)k);
    public static bool IsMouseButtonPressed(MouseButton b) => !ScriptOnly && Raylib.IsMouseButtonPressed(b) || buttonsPressed.Contains((int)b);
    public static bool IsMouseButtonDown(MouseButton b) => !ScriptOnly && Raylib.IsMouseButtonDown(b) || buttonsDown.Contains((int)b);
    public static Vector2 GetMousePosition() => mouse ?? (ScriptOnly ? new(-1000, -1000) : Raylib.GetMousePosition()); // off-screen until scripted
    public static float GetMouseWheelMove() => (ScriptOnly ? 0 : Raylib.GetMouseWheelMove()) + wheel;
    public static int GetCharPressed() => !ScriptOnly && Raylib.GetCharPressed() is var c and not 0 ? c : chars.TryDequeue(out int s) ? s : 0;
    public static int GetKeyPressed() => !ScriptOnly && Raylib.GetKeyPressed() is var k and not 0 ? k : keyQueue.TryDequeue(out int s) ? s : 0;
}
