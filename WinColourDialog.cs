using System.Runtime.InteropServices;
using Raylib_cs;
using static Raylib_cs.Raylib;

namespace OsuStableRemake;

// Windows' own colour dialog (comdlg32 ChooseColor), as stable's WinForms ColorDialog shows it: fully open (the
// spectrum and the RGB / HSL boxes beside the basic colours), starting from the given colour, its 16 custom colours
// kept for the session. Modal over the game window; null when cancelled.
static class WinColourDialog
{
    [StructLayout(LayoutKind.Sequential)]
    struct ChooseColorW
    {
        public int Size;
        public IntPtr Owner, Instance;
        public int Rgb;
        public IntPtr CustomColours;
        public int Flags;
        public IntPtr CustData, Hook, TemplateName;
    }
    [DllImport("comdlg32.dll", EntryPoint = "ChooseColorW")] static extern bool ChooseColor(ref ChooseColorW cc);

    const int RgbInit = 0x1, FullOpen = 0x2;
    static readonly int[] custom = new int[16];

    // (on the game's own thread: the dialog disables and talks to its owner window, so a thread waiting for it would
    // deadlock; ChooseColor needs no COM apartment)
    public static unsafe Color? Pick(Color start)
    {
        var picked = Pick((IntPtr)GetWindowHandle(), start);
        Input.Muted = true; // (the click that closed it isn't the game's)
        return picked;
    }

    static Color? Pick(IntPtr owner, Color start)
    {
        IntPtr colours = Marshal.AllocHGlobal(16 * 4);
        try
        {
            Marshal.Copy(custom, 0, colours, 16);
            var cc = new ChooseColorW
            {
                Size = Marshal.SizeOf<ChooseColorW>(), Owner = owner, Rgb = start.R | start.G << 8 | start.B << 16,
                CustomColours = colours, Flags = RgbInit | FullOpen,
            };
            bool ok = ChooseColor(ref cc);
            Marshal.Copy(colours, custom, 0, 16);
            return ok ? new Color(cc.Rgb & 0xFF, cc.Rgb >> 8 & 0xFF, cc.Rgb >> 16 & 0xFF, 255) : null;
        }
        finally { Marshal.FreeHGlobal(colours); }
    }
}
