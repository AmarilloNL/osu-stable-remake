using System.Numerics;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// stable's Manage Collections dialog (F3 > Manage Collections), in its 640x480 units centred on the screen: a name box
// (448 x 15.5 at y 35.7, bold 15) where typing a name and Enter makes a collection (25 characters at most); under it
// the collections, 17 units apart from y 60 in a 250-unit scrolling area, each "name (maps on disk)" in a 448-wide
// box (regular 12, grey 200; hovered white, or yellow-green when it holds the selected map; bold once picked), with
// small 10-point buttons from 280 in: Rename (orange, 35 wide), +Set / + (green, 30 / 20: add the set / the map) and
// -Set / - (orange-red: remove them), the ones that would change nothing in grey. Then Delete the collection (red,
// asks first) and Close at y 320. Changes are written to stable's collection.db, only while stable is closed (it
// writes its own copy when it closes).
sealed partial class SongSelect
{
    string collectionName = "";
    DbBeatmap? collectionsFor; // the map the + / - buttons act on when it isn't song select's (a replay being watched)
    DbBeatmap? CollectionsMap => collectionsFor ?? Selected;

    // stable's replay overlay Collections button: the same dialog over the replay, for the replay's map
    internal void OpenCollectionsFor(DbBeatmap? map) { collectionsFor = map; OpenCollections(); }
    internal bool DialogOpen => dialog != null;
    // hosted by another screen: the dialog takes this frame's input (the host's own presses are muted until it ends)
    internal void HostDialog() { UpdateDialog(); while (Input.GetCharPressed() != 0) { } Input.Muted = true; }
    internal void HostDrawDialog() => DrawDialog();
    int collectionPicked = -1, collectionRenaming = -1;
    float collectionScroll;
    HashSet<string>? libraryMd5s;

    static readonly Color BoxBorder = new(180, 180, 180, 255), BoxFill = new(16, 16, 16, 255), RowGrey = new(200, 200, 200, 255),
        XnaOrange = new(255, 165, 0, 255), XnaGreen = new(0, 128, 0, 255), XnaYellowGreen = new(154, 205, 50, 255), XnaDarkOrange = new(255, 140, 0, 255),
        ButtonGrey = new(150, 150, 150, 255);

    // the one list of collections everything uses (the main menu's Jump To too): a second copy read later would also
    // reset what collection.db was when read, so this list could be saved over a newer file
    internal static List<(string Name, HashSet<string> Md5s)> Collections =>
        collections ??= OsuDb.LoadCollections(Path.Combine(Config.OsuDir, "collection.db"));

    void OpenCollections()
    {
        libraryMd5s = Lib.SelectMany(s => s.Diffs).Select(d => d.Md5).ToHashSet(StringComparer.OrdinalIgnoreCase);
        collectionName = "";
        collectionRenaming = -1;
        dialog = new("Collections", [("Delete the collection", Xna.Red, DeleteCollection), ("Close", Xna.Gray, () => { collectionsFor = null; if (GroupMode == "Collections") ApplyFilter(Selected); })],
            320, DrawCollections, UpdateCollections);
        dialogAt = Game.NowMs;
    }

    static float CollectionX => Game.W / 2f - 224 * Game.H / 480f;
    static float RowX => CollectionX - Game.H / 480f; // (the rows sit a unit left of the name box, measured)
    static Rectangle CollectionButton(int k, float rowY)
    {
        float u = Game.H / 480f;
        (float x, float w) = k switch { 0 => (280f, 35f), 1 => (325f, 30f), 2 => (360f, 20f), 3 => (385f, 30f), _ => (420f, 20f) };
        return new Rectangle(RowX + x * u, rowY + 2 * u, w * u, 11 * u);
    }

    float CollectionRowY(int i) => (59.7f + i * 17 - collectionScroll) * Game.H / 480f;

    // true when the content took Enter (the name box)
    bool UpdateCollections()
    {
        var list = Collections;
        float u = Game.H / 480f;
        var mouse = Input.GetMousePosition();
        bool click = Input.IsMouseButtonPressed(MouseButton.Left);
        // the scroll area
        float wheel = VolumeControl.Wheel;
        if (wheel != 0) collectionScroll = Math.Clamp(collectionScroll - wheel * 17, 0, Math.Max(0, list.Count * 17 - 250));
        // typing goes to the name being edited (a rename) or the name box
        bool renaming = collectionRenaming >= 0 && collectionRenaming < list.Count;
        string text = renaming ? renameText : collectionName;
        for (int ch = Input.GetCharPressed(); ch != 0; ch = Input.GetCharPressed())
            if (text.Length < 25) { text += char.ConvertFromUtf32(ch); TypingSound("key-press"); }
        if (Pressed(KeyboardKey.Backspace) && text.Length > 0) { text = DeleteBack(text); TypingSound("key-delete"); }
        if (renaming) renameText = text; else collectionName = text;
        if (Input.IsKeyPressed(KeyboardKey.Enter))
        {
            if (renaming) { RenameCollection(collectionRenaming, renameText.Trim()); return true; }
            string name = collectionName.Trim();
            if (name.Length == 0) return false;
            collectionName = "";
            if (!list.Any(c => c.Name == name) && CanWriteCollections())
            {
                list.Add((name, new HashSet<string>(StringComparer.OrdinalIgnoreCase)));
                SaveCollections();
            }
            return true;
        }
        if (!click) return false;
        float top = 60 * u, bottom = 310 * u;
        for (int i = 0; i < list.Count; i++)
        {
            float y = CollectionRowY(i);
            if (y + 15 * u < top || y > bottom || mouse.Y < top || mouse.Y > bottom) continue;
            for (int k = 0; k < 5; k++)
            {
                if (!CheckCollisionPointRec(mouse, CollectionButton(k, y))) continue;
                if (k == 0) { collectionRenaming = collectionRenaming == i ? -1 : i; renameText = list[i].Name; }
                else ChangeCollection(i, add: k <= 2, wholeSet: k is 1 or 3);
                return false;
            }
            if (CheckCollisionPointRec(mouse, new Rectangle(RowX, y, 448 * u, 15 * u))) { collectionPicked = i; Game.Skin.Sound("click-short-confirm")?.Play(BeatmapSamples.EffectVolume); }
        }
        return false;
    }

    string renameText = "";

    static bool CanWriteCollections()
    {
        if (System.Diagnostics.Process.GetProcessesByName("osu!").Length == 0) return true;
        Notification.Show("Close osu!stable first: it saves its own collections when it closes.", 3000);
        return false;
    }

    bool SaveCollections()
    {
        if (OsuDb.SaveCollections(Path.Combine(Config.OsuDir, "collection.db"), Collections)) return true;
        Notification.Show("collection.db changed or couldn't be written: read again, please try once more.", 3000);
        collections = null; // (read again on the next use)
        collectionPicked = collectionRenaming = -1;
        return false;
    }

    // + / +Set / - / -Set: the selected map, or its whole set, into or out of the collection
    void ChangeCollection(int i, bool add, bool wholeSet)
    {
        if (CollectionsMap is not { } b || !CanWriteCollections()) return;
        var (name, md5s) = Collections[i];
        var maps = wholeSet ? Lib.FirstOrDefault(s => s.Diffs.Contains(b))?.Diffs ?? [b] : [b];
        bool changed = false;
        foreach (var m in maps) changed |= add ? md5s.Add(m.Md5) : md5s.Remove(m.Md5);
        if (!changed || !SaveCollections()) return;
        string what = wholeSet ? $"{b.Artist} - {b.Title}" : $"{b.Artist} - {b.Title} [{b.Version}]";
        Notification.Show(add ? $"Added {what} to '{name}'." : $"Removed {what} from '{name}'.", 1500);
    }

    void RenameCollection(int i, string name)
    {
        collectionRenaming = -1;
        var list = Collections;
        if (name.Length == 0 || name == list[i].Name || list.Any(c => c.Name == name) || !CanWriteCollections()) return;
        list[i] = (name, list[i].Md5s);
        SaveCollections();
    }

    void DeleteCollection()
    {
        var list = Collections;
        if (collectionPicked < 0 || collectionPicked >= list.Count)
        {
            Notification.Show("Select a collection first!", 1500);
            OpenCollections();
            return;
        }
        int i = collectionPicked;
        OpenDialog($"Are you sure you want to delete {list[i].Name}?",
            ("Let's do it!", YellowGreen, () =>
            {
                if (CanWriteCollections()) { list.RemoveAt(i); collectionPicked = -1; SaveCollections(); }
                OpenCollections();
            }),
            ("Cancel", OrangeRed, OpenCollections));
    }

    void DrawCollections(float alpha)
    {
        float u = Game.H / 480f, x0 = CollectionX;
        var mouse = Input.GetMousePosition();
        // the name box
        var box = new Rectangle(x0, 35.7f * u, 448 * u, 16 * u);
        DrawRectangleRec(box, ColorAlpha(BoxFill, alpha));
        DrawRectangleLinesEx(box, 1, ColorAlpha(BoxBorder, alpha));
        string shown = collectionName.Length > 0 ? collectionName : "Select a collection or create a new one here...";
        Game.TextBold(shown, x0 + 2 * u, box.Y + box.Height / 2 - 7.75f * u, 15.5f * u, ColorAlpha(Color.White, alpha));
        // the collections
        var list = Collections;
        var b = CollectionsMap;
        string? tip = null;
        BeginScissorMode(0, (int)(60 * u), Game.W, (int)(250 * u));
        for (int i = 0; i < list.Count; i++)
        {
            float y = CollectionRowY(i);
            if (y + 15 * u < 60 * u || y > 310 * u) continue;
            var (name, md5s) = list[i];
            float rx = RowX;
            bool holds = b != null && md5s.Contains(b.Md5), hover = CheckCollisionPointRec(mouse, new Rectangle(rx, y, 448 * u, 15 * u));
            // measured on stable: black box, 1px border white (yellow-green when the collection holds the selected map),
            // the whole row at grey 200 (white while hovered; the hovered yellow-green border lightens about 45%)
            var row = new Rectangle(rx, y, 448 * u, 15 * u);
            // (while renaming: a yellow border, the bare name)
            var rowEdge = collectionRenaming == i ? Color.Yellow : holds ? XnaYellowGreen : Color.White;
            var border = hover ? (rowEdge.Equals(Color.White) ? Color.White : ColorLerp(rowEdge, Color.White, 0.45f)) : Tint(rowEdge, 200);
            DrawRectangleRec(row, ColorAlpha(Color.Black, alpha));
            DrawRectangleLinesEx(row, 1, ColorAlpha(border, alpha));
            string label = collectionRenaming == i ? renameText : $"{(name.Length > 40 ? name[..40] + "..." : name)} ({md5s.Count(m => libraryMd5s?.Contains(m) != false)})";
            var colour = hover ? Color.White : RowGrey;
            float ts = 12.4f * u, ty = y + 7.5f * u - ts / 2;
            if (i == collectionPicked) Game.TextBold(label, rx + 2 * u, ty, ts, ColorAlpha(colour, alpha));
            else Game.TextRegular(label, rx + 2 * u, ty, ts, ColorAlpha(colour, alpha));
            // buttons: their colour with a 1px border in the hover colour, all at grey 150 when they'd change nothing
            string[] labels = ["Rename", "+Set", "+", "-Set", "-"];
            for (int k = 0; k < 5; k++)
            {
                var r = CollectionButton(k, y);
                bool over = CheckCollisionPointRec(mouse, r);
                if (over && k > 0) tip = ButtonTips[k - 1];
                (Color bg, Color edge) = k == 0 ? (XnaOrange, XnaOrange) : k <= 2 ? (XnaGreen, XnaYellowGreen) : (OrangeRed, XnaDarkOrange);
                byte shade = k == 0 || (k <= 2 ? !holds : holds) ? (byte)255 : (byte)150;
                DrawRectangleRec(r, ColorAlpha(Tint(bg, shade), alpha));
                DrawRectangleLinesEx(r, 1, ColorAlpha(Tint(edge, shade), alpha));
                float s = 10.3f * u;
                Game.TextRegular(labels[k], r.X + r.Width / 2 - Game.RegularWidth(labels[k], s) / 2, r.Y + r.Height / 2 - s / 2, s, ColorAlpha(Tint(Color.White, shade), alpha));
            }
        }
        EndScissorMode();
        if (tip != null) Options.DrawScreenTooltip(tip, mouse);
    }

    static Color Tint(Color c, byte k) => new((byte)(c.R * k / 255), (byte)(c.G * k / 255), (byte)(c.B * k / 255), c.A);

    static readonly string[] ButtonTips = ["Add set to collection", "Add to collection", "Remove set from collection", "Remove from collection"];
}
