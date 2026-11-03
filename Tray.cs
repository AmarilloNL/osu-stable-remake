using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace OsuStableRemake;

// The notification-area icon the Boss key hides the game behind, like stable's: clicking it brings the game back.
// It lives on a thread of its own with a message-only window that receives its clicks.
static unsafe class Tray
{
    static volatile bool clicked;
    static Thread? thread;
    static IntPtr window;

    // true once after the icon was clicked
    public static bool TakeClick() { bool c = clicked; clicked = false; return c; }

    public static void Show((string Title, string Text)? balloon)
    {
        if (thread != null) return;
        clicked = false;
        using var ready = new ManualResetEventSlim();
        thread = new Thread(() => Run(balloon, ready)) { IsBackground = true, Name = "tray" };
        thread.Start();
        ready.Wait(1000);
    }

    public static void Hide()
    {
        if (thread == null) return;
        if (window != 0) PostMessageW(window, WM_CLOSE, 0, 0);
        thread.Join(500);
        thread = null;
    }

    const uint WM_DESTROY = 0x2, WM_CLOSE = 0x10, WM_LBUTTONUP = 0x202, Callback = 0x8001; // WM_APP + 1
    const uint NIM_ADD = 0, NIM_DELETE = 2, NIF_MESSAGE = 1, NIF_ICON = 2, NIF_INFO = 0x10, NIIF_INFO = 1;

    static void Run((string Title, string Text)? balloon, ManualResetEventSlim ready)
    {
        var module = GetModuleHandleW(null);
        fixed (char* name = "osu!stable remake tray")
        {
            var wc = new WndClassEx { cbSize = sizeof(WndClassEx), lpfnWndProc = &WndProc, hInstance = module, lpszClassName = name };
            RegisterClassExW(&wc); // (fails harmlessly when already registered)
            window = CreateWindowExW(0, name, null, 0, 0, 0, 0, 0, -3, 0, module, 0); // HWND_MESSAGE
        }
        var data = new NotifyIconData { cbSize = sizeof(NotifyIconData), hWnd = window, uFlags = NIF_MESSAGE | NIF_ICON, uCallbackMessage = Callback, hIcon = LoadIconW(0, 32512) }; // IDI_APPLICATION
        if (balloon is var (title, text))
        {
            data.uFlags |= NIF_INFO;
            data.dwInfoFlags = NIIF_INFO;
            data.uTimeout = 10000;
            Copy(title, data.szInfoTitle, 64);
            Copy(text, data.szInfo, 256);
        }
        Shell_NotifyIconW(NIM_ADD, &data);
        ready.Set();
        Msg msg;
        while (GetMessageW(&msg, 0, 0, 0) > 0) { TranslateMessage(&msg); DispatchMessageW(&msg); }
        Shell_NotifyIconW(NIM_DELETE, &data);
        window = 0;
    }

    static void Copy(string s, char* to, int size)
    {
        int n = Math.Min(s.Length, size - 1);
        for (int i = 0; i < n; i++) to[i] = s[i];
        to[n] = '\0';
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    static IntPtr WndProc(IntPtr hwnd, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case Callback when (uint)(lParam & 0xFFFF) == WM_LBUTTONUP: clicked = true; return 0;
            case WM_CLOSE: DestroyWindow(hwnd); return 0;
            case WM_DESTROY: PostQuitMessage(0); return 0;
        }
        return DefWindowProcW(hwnd, message, wParam, lParam);
    }

    struct WndClassEx
    {
        public int cbSize, style;
        public delegate* unmanaged[Stdcall]<IntPtr, uint, nuint, nint, IntPtr> lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public char* lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    struct NotifyIconData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        public fixed char szTip[128];
        public uint dwState, dwStateMask;
        public fixed char szInfo[256];
        public uint uTimeout;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    struct Msg { public IntPtr hwnd; public uint message; public nuint wParam; public nint lParam; public uint time; public int x, y, priv; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandleW(string? name);
    [DllImport("user32.dll")] static extern ushort RegisterClassExW(WndClassEx* wc);
    [DllImport("user32.dll")] static extern IntPtr CreateWindowExW(int exStyle, char* cls, char* title, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern IntPtr DefWindowProcW(IntPtr hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] static extern bool PostMessageW(IntPtr hwnd, uint message, nuint wParam, nint lParam);
    [DllImport("user32.dll")] static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] static extern int GetMessageW(Msg* msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool TranslateMessage(Msg* msg);
    [DllImport("user32.dll")] static extern IntPtr DispatchMessageW(Msg* msg);
    [DllImport("user32.dll")] static extern IntPtr LoadIconW(IntPtr instance, IntPtr name);
    [DllImport("shell32.dll")] static extern bool Shell_NotifyIconW(uint message, NotifyIconData* data);
}
