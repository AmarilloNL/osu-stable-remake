using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// The controls of stable's Windows dialogs (Song Setup, the Timing Setup Panel) drawn like Windows 10's, in the
// dialog's own units (pixels at 96 DPI) from its corner. Each frame a dialog's layout runs twice from one place: once
// taking the mouse (Begin(act: true)), once drawing it (Begin(act: false)).
sealed partial class Editor
{
    class WinUi
    {
        public Vector2 Pos = new(float.NaN), Mouse;
        public Vector2? Drag;
        public int Focus = -1, Open = -1, Dragging = -1;
        public bool Changed, Act, Blocked; // Blocked: a list or picker is open over the controls
        bool click, down;
        public static float S => AiS;
        public static float Fs => 11 * AiS;
        public static readonly Color Text = Color.Black, Grey = new(109, 109, 109, 255), Face = new(240, 240, 240, 255), Blue = new(0, 120, 215, 255);

        public void Begin(bool act, bool blocked)
        {
            Act = act;
            Blocked = blocked;
            click = act && Input.IsMouseButtonPressed(MouseButton.Left);
            down = Input.IsMouseButtonDown(MouseButton.Left);
        }
        public bool Clicked => click;
        public Rectangle R(float x, float y, float w, float h) { float s = S; return new Rectangle(Pos.X + x * s, Pos.Y + (AiCaption + y) * s, w * s, h * s); }
        public bool Hover(Rectangle r) => !Blocked && CheckCollisionPointRec(Mouse, r);
        public bool Hit(Rectangle r) => click && Hover(r);

        // the window: centred over the editor at first; its caption drags it, its X closes it (true)
        public bool UpdateFrame(Vector2 mouse, float w, float h)
        {
            float s = S;
            Mouse = mouse;
            if (float.IsNaN(Pos.X)) Pos = new((Game.W - w * s) / 2, (EH - (h + AiCaption) * s) / 2);
            if (Drag is { } dr)
            {
                if (!Input.IsMouseButtonDown(MouseButton.Left)) Drag = null;
                else Pos = new(Math.Clamp(mouse.X - dr.X, -w * s / 2, Game.W - w * s / 2), Math.Clamp(mouse.Y - dr.Y, 0, EH - AiCaption * s));
                return false;
            }
            if (!Input.IsMouseButtonPressed(MouseButton.Left) || Open >= 0) return false;
            var cap = new Rectangle(Pos.X, Pos.Y, w * s, AiCaption * s);
            if (CheckCollisionPointRec(mouse, new Rectangle(cap.X + cap.Width - 28 * s, cap.Y, 28 * s, cap.Height))) return true;
            if (CheckCollisionPointRec(mouse, cap)) Drag = mouse - Pos;
            return false;
        }
        public void DrawFrame(string title, float w, float h)
        {
            float s = S, fs = Fs;
            var whole = new Rectangle(Pos.X, Pos.Y, w * s, (h + AiCaption) * s);
            var cap = new Rectangle(Pos.X, Pos.Y, w * s, AiCaption * s);
            DrawRectangle((int)(whole.X + 4 * s), (int)(whole.Y + 4 * s), (int)whole.Width, (int)whole.Height, new Color(0, 0, 0, 70));
            DrawRectangleRec(whole, Face);
            DrawRectangleRec(cap, Color.White);
            Game.Text(title, cap.X + 6 * s, cap.Y + (cap.Height - fs) / 2, fs, Text);
            float cx = cap.X + cap.Width - 14 * s, cy = cap.Y + cap.Height / 2;
            DrawLineEx(new(cx - 4 * s, cy - 4 * s), new(cx + 4 * s, cy + 4 * s), 1.2f * s, Text);
            DrawLineEx(new(cx - 4 * s, cy + 4 * s), new(cx + 4 * s, cy - 4 * s), 1.2f * s, Text);
            DrawRectangleLinesEx(whole, 1, new Color(160, 160, 160, 255));
        }

        // typing into a text: true when it changed
        public static bool Type(ref string text)
        {
            string before = text;
            for (int c; (c = Input.GetCharPressed()) != 0;) if (c >= 32) text += char.ConvertFromUtf32(c);
            if ((Input.IsKeyPressed(KeyboardKey.Backspace) || Input.IsKeyPressedRepeat(KeyboardKey.Backspace)) && text.Length > 0) text = text[..^1];
            if (Input.CtrlHeld && Input.IsKeyPressed(KeyboardKey.V)) text += (GetClipboardText_() ?? "").Replace("\r", "").Replace("\n", " ");
            return text != before;
        }

        public void Label(float x, float y, string t, bool bold = false, Color? c = null, float right = 0, bool on = true)
        {
            if (Act) return;
            float fs = Fs;
            float px = right > 0 ? R(x + right, 0, 0, 0).X - (bold ? Game.BoldWidth(t, fs) : Game.TextWidth(t, fs)) : R(x, 0, 0, 0).X;
            var col = on ? c ?? Text : Grey;
            if (bold) Game.TextBold(t, px, R(0, y, 0, 0).Y, fs, col); else Game.Text(t, px, R(0, y, 0, 0).Y, fs, col);
        }
        // a label wrapped at its width (15 apart, like Segoe UI 9pt)
        public void Wrap(float x, float y, float w, string t, Color? c = null, bool on = true)
        {
            if (Act) return;
            foreach (var para in t.Split('\n'))
            {
                string cur = "";
                foreach (var word in para.Split(' '))
                {
                    string next = cur.Length == 0 ? word : cur + " " + word;
                    if (cur.Length > 0 && Game.TextWidth(next, Fs) > w * S) { Label(x, y, cur, false, c, 0, on); y += 15; cur = word; }
                    else cur = next;
                }
                Label(x, y, cur, false, c, 0, on);
                y += 15;
            }
        }
        public bool Button(float x, float y, float w, float h, string t, bool on = true, bool def = false)
        {
            var r = R(x, y, w, h);
            if (Act) return on && Hit(r);
            float s = S, fs = Fs;
            bool hover = on && Hover(r);
            DrawRectangleRec(r, hover ? new Color(229, 241, 251, 255) : new Color(225, 225, 225, 255));
            DrawRectangleLinesEx(r, def || hover ? 2 * MathF.Max(1, s / 2) : 1, hover || def && on ? Blue : new Color(173, 173, 173, 255));
            Game.Text(t, r.X + r.Width / 2 - Game.TextWidth(t, fs) / 2, r.Y + (r.Height - fs) / 2, fs, on ? Text : Grey);
            return false;
        }
        public void Group(float x, float y, float w, float h, string t)
        {
            if (Act) return;
            var r = R(x, y + 7, w, h - 7);
            DrawRectangleLinesEx(r, 1, new Color(220, 220, 220, 255));
            var gap = R(x + 6, y, 0, 15);
            DrawRectangle((int)gap.X, (int)r.Y - 1, (int)(Game.TextWidth(t, Fs) + 4 * S), 3, Color.White);
            Game.Text(t, gap.X + 2 * S, gap.Y, Fs, Text);
        }
        // a text box (id: its focus; a click focuses it)
        public void Box(int id, string text, float x, float y, float w, float h, bool on = true)
        {
            var r = R(x, y, w, h);
            if (Act) { if (on && Hit(r)) Focus = id; return; }
            float s = S, fs = Fs;
            bool focus = Focus == id;
            DrawRectangleRec(r, on ? Color.White : Face);
            DrawRectangleLinesEx(r, 1, focus ? Blue : on ? new Color(122, 122, 122, 255) : new Color(204, 204, 204, 255));
            string shown = text + (focus && (int)(Game.NowMs / 500) % 2 == 0 ? "|" : "");
            float tw = Game.TextWidth(shown, fs);
            BeginScissorMode((int)r.X + 1, (int)(r.Y + Strip), (int)r.Width - 2, (int)r.Height);
            Game.Text(shown, r.X + 3 * s - (focus ? Math.Max(0, tw - r.Width + 8 * s) : 0), r.Y + (r.Height - fs) / 2, fs, on ? Text : Grey);
            EndScissorMode();
        }
        public void Check(ref bool v, float x, float y, string t, bool on = true)
        {
            float s = S, fs = Fs;
            var box = R(x, y + 3, 13, 13);
            var whole = R(x, y, 17 + Game.TextWidth(t, fs) / s, 19);
            if (Act) { if (on && Hit(whole)) { v = !v; Changed = true; } return; }
            DrawRectangleRec(box, Color.White);
            DrawRectangleLinesEx(box, 1, on ? new Color(51, 51, 51, 255) : new Color(204, 204, 204, 255));
            if (v) CheckMark(box, on ? Text : Grey);
            Game.Text(t, box.X + 17 * s, box.Y + (box.Height - fs) / 2, fs, on ? Text : Grey);
        }
        public static void CheckMark(Rectangle box, Color c)
        {
            float s = S;
            DrawLineEx(new(box.X + 3 * s, box.Y + 6.5f * s), new(box.X + 5.5f * s, box.Y + 9.5f * s), 1.5f * s, c);
            DrawLineEx(new(box.X + 5.5f * s, box.Y + 9.5f * s), new(box.X + 10 * s, box.Y + 3.5f * s), 1.5f * s, c);
        }
        public void Radio(ref int sel, int value, float x, float y, string t, bool on = true)
        {
            float s = S, fs = Fs;
            var whole = R(x, y, 17 + Game.TextWidth(t, fs) / s, 20);
            if (Act) { if (on && Hit(whole) && sel != value) { sel = value; Changed = true; } return; }
            var c = R(x + 6.5f, y + 10, 0, 0);
            DrawCircleV(new(c.X, c.Y), 6.5f * s, on ? new Color(51, 51, 51, 255) : new Color(204, 204, 204, 255));
            DrawCircleV(new(c.X, c.Y), 5.5f * s, Color.White);
            if (sel == value) DrawCircleV(new(c.X, c.Y), 3.5f * s, on ? Text : Grey);
            Game.Text(t, c.X + 10.5f * s, c.Y - fs / 2, fs, on ? Text : Grey);
        }
        // a track bar: dragged anywhere along it, to tens unless Shift is held (snap); the tooltip shows value / div
        public void Track(int id, ref int v, int min, int max, float x, float y, float w, int tick, bool snap, bool on = true, double div = 10)
        {
            float s = S;
            var r = R(x, y, w, 30);
            float left = r.X + 13 * s, span = r.Width - 26 * s;
            if (Act)
            {
                if (on && Hit(r)) Dragging = id;
                if (Dragging == id)
                {
                    if (!down) { Dragging = -1; return; }
                    int to = min + (int)Math.Round(Math.Clamp((Mouse.X - left) / span, 0, 1) * (max - min));
                    if (snap && !Input.ShiftHeld) to = Math.Clamp((int)Math.Round(to / 10.0) * 10, min, max);
                    if (to != v) { v = to; Changed = true; }
                }
                return;
            }
            var channel = new Rectangle(r.X + 8 * s, r.Y + 8 * s, r.Width - 16 * s, 4 * s);
            DrawRectangleRec(channel, new Color(231, 234, 234, 255));
            DrawRectangleLinesEx(channel, 1, new Color(214, 214, 214, 255));
            for (int t = min; t <= max; t += Math.Max(1, tick))
            {
                float tx = left + (max > min ? (t - min) / (float)(max - min) : 0) * span;
                DrawRectangle((int)tx, (int)(r.Y + 24 * s), 1, (int)((t == min || t == max ? 4 : 3) * s), new Color(196, 196, 196, 255));
            }
            float cx = left + (max > min ? (Math.Clamp(v, min, max) - min) / (float)(max - min) : 0) * span;
            var thumb = on ? Dragging == id ? new Color(204, 204, 204, 255) : Hover(r) ? new Color(23, 23, 23, 255) : Blue : new Color(204, 204, 204, 255);
            DrawRectangleRec(new Rectangle(cx - 5.5f * s, r.Y + 2 * s, 11 * s, 15 * s), thumb);
            DrawTriangle(new(cx - 5.5f * s, r.Y + 17 * s), new(cx, r.Y + 22 * s), new(cx + 5.5f * s, r.Y + 17 * s), thumb);
            if (on && (Dragging == id || Hover(r))) Tip(Num(v / div)); // (stable's tooltip: the value)
        }
        public void Link(float x, float y, string t)
        {
            if (Act) return;
            var p = R(x, y, 0, 0);
            Game.Text(t, p.X, p.Y, Fs, new Color(0, 102, 204, 255));
            DrawRectangle((int)p.X, (int)(p.Y + Fs + 1 * S), (int)Game.TextWidth(t, Fs), 1, new Color(0, 102, 204, 255));
        }
        // a drop-down list's closed box (a click opens list id)
        public void Drop(int id, string shown, float x, float y, float w, float h, bool on = true)
        {
            var r = R(x, y, w, h);
            if (Act) { if (on && Hit(r)) Open = id; return; }
            bool hover = on && Hover(r);
            DrawRectangleRec(r, hover ? new Color(229, 241, 251, 255) : new Color(225, 225, 225, 255));
            DrawRectangleLinesEx(r, 1, hover ? Blue : new Color(122, 122, 122, 255));
            Game.Text(shown, r.X + 3 * S, r.Y + (r.Height - Fs) / 2, Fs, on ? Text : Grey);
            Arrow(R(x + w - 17, y, 17, h), on);
        }
        public static void Arrow(Rectangle box, bool on)
        {
            float s = S, ax = box.X + box.Width / 2, ay = box.Y + box.Height / 2;
            DrawTriangle(new(ax - 3.5f * s, ay - 1.5f * s), new(ax, ay + 2 * s), new(ax + 3.5f * s, ay - 1.5f * s), on ? Text : Grey);
        }
        // an open drop-down's list under its box; the row clicked (-1 none)
        public static Rectangle ListRow(Rectangle box, int i) { float s = S; return new Rectangle(box.X + 1, box.Y + box.Height + 1 + i * 17 * s, box.Width - 2, 17 * s); }
        public void DrawList(Rectangle box, string[] items)
        {
            float s = S, fs = Fs;
            var all = new Rectangle(box.X, box.Y + box.Height, box.Width, items.Length * 17 * s + 2);
            DrawRectangleRec(all, Color.White);
            DrawRectangleLinesEx(all, 1, new Color(100, 100, 100, 255));
            for (int i = 0; i < items.Length; i++)
            {
                var row = ListRow(box, i);
                bool hover = CheckCollisionPointRec(Mouse, row);
                if (hover) DrawRectangleRec(row, Blue);
                Game.Text(items[i], row.X + 3 * s, row.Y + (row.Height - fs) / 2, fs, hover ? Color.White : Text);
            }
        }
        // a list box (one choice, the chosen one highlighted)
        public void ListBox(ref int sel, string[] items, float x, float y, float w, float h, bool on = true)
        {
            if (Act)
            {
                for (int i = 0; i < items.Length; i++) if (on && Hit(R(x + 1, y + 1 + i * 15, w - 2, 15)) && sel != i) { sel = i; Changed = true; }
                return;
            }
            var list = R(x, y, w, h);
            DrawRectangleRec(list, Color.White);
            DrawRectangleLinesEx(list, 1, new Color(130, 135, 144, 255));
            for (int i = 0; i < items.Length; i++)
            {
                var row = R(x + 1, y + 1 + i * 15, w - 2, 15);
                if (sel == i) DrawRectangleRec(row, on ? Blue : new Color(204, 204, 204, 255));
                Game.Text(items[i], row.X + 2 * S, row.Y + (row.Height - Fs) / 2, Fs, sel == i && on ? Color.White : on ? Text : Grey);
            }
        }
        // a number box with its up / down arrows (a click on an arrow steps it; true when it changed)
        public bool Numeric(ref double v, double min, double max, double step, string format, float x, float y, float w, float h, bool on = true)
        {
            var r = R(x, y, w, h);
            float s = S, fs = Fs;
            if (Act)
            {
                if (!on || !Hit(R(x + w - 16, y, 16, h))) return false;
                double to = Math.Clamp(v + (Mouse.Y < r.Y + r.Height / 2 ? step : -step), min, max);
                if (to == v) return false;
                v = to;
                Changed = true;
                return true;
            }
            DrawRectangleRec(r, on ? Color.White : Face);
            DrawRectangleLinesEx(r, 1, new Color(122, 122, 122, 255));
            Game.Text(v.ToString(format, Inv), r.X + 3 * s, r.Y + (r.Height - fs) / 2, fs, on ? Text : Grey);
            float ax = r.X + r.Width - 8 * s, my = r.Y + r.Height / 2;
            DrawTriangle(new(ax - 3 * s, my - 2 * s), new(ax + 3 * s, my - 2 * s), new(ax, my - 6 * s), on ? Text : Grey);
            DrawTriangle(new(ax - 3 * s, my + 2 * s), new(ax, my + 6 * s), new(ax + 3 * s, my + 2 * s), on ? Text : Grey);
            return false;
        }
        // a tab control's headers from (x, y) and its white page below (h tall from the headers' bottom)
        public void Tabs(ref int tab, string[] names, float x, float y, float w, float h)
        {
            float s = S, fs = Fs, tx = x + 2;
            for (int i = 0; i < names.Length; i++)
            {
                float tw = Game.TextWidth(names[i], fs) / s + 12;
                var r = R(tx, y + 2, tw, 20);
                if (Act) { if (Hit(r) && tab != i) { tab = i; Open = -1; } }
                else if (tab != i)
                {
                    DrawRectangleRec(r, Hover(r) ? new Color(229, 241, 251, 255) : Face);
                    DrawRectangleLinesEx(r, 1, new Color(217, 217, 217, 255));
                    Game.Text(names[i], r.X + 6 * s, r.Y + (r.Height - fs) / 2, fs, Text);
                }
                tx += tw;
            }
            if (Act) return;
            var page = R(x, y + 22, w, h);
            DrawRectangleRec(page, Color.White);
            DrawRectangleLinesEx(page, 1, new Color(217, 217, 217, 255));
            tx = x + 2;
            for (int i = 0; i < tab; i++) tx += Game.TextWidth(names[i], fs) / s + 12;
            float sw = Game.TextWidth(names[tab], fs) / s + 12;
            var sel = R(tx - 2, y, sw + 4, 23);
            DrawRectangleRec(sel, Color.White);
            DrawRectangleLinesEx(sel with { Height = sel.Height - 1 * s }, 1, new Color(217, 217, 217, 255));
            DrawRectangleRec(new Rectangle(sel.X + 1, sel.Y + sel.Height - 2 * s, sel.Width - 2, 3 * s), Color.White);
            Game.Text(names[tab], sel.X + 8 * s, sel.Y + (sel.Height - fs) / 2, fs, Text);
        }
    }
}
