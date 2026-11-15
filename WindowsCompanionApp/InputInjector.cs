using System.Diagnostics;
using System.Runtime.InteropServices;

namespace VSDM18App;

/// <summary>
/// Sends keyboard / mouse input with SendInput.
///
/// Why this is more game-compatible than keybd_event + virtual-key codes:
///  * Games using DirectInput, Raw Input or GetAsyncKeyState read hardware SCAN CODES,
///    so we send scan codes (KEYEVENTF_SCANCODE) with the correct "extended key" flag.
///  * F13-F24 have no scan code on many layouts, so they come from a fixed table.
///  * Key down / key up are reference counted, so two buttons that share a modifier
///    don't release it early, and ReleaseAll() can never leave a key stuck.
/// </summary>
public static class InputInjector
{
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002,
                       KEYEVENTF_UNICODE = 0x0004, KEYEVENTF_SCANCODE = 0x0008;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
                       MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010,
                       MOUSEEVENTF_MIDDLEDOWN = 0x0020, MOUSEEVENTF_MIDDLEUP = 0x0040,
                       MOUSEEVENTF_XDOWN = 0x0080, MOUSEEVENTF_XUP = 0x0100;
    private const uint MAPVK_VK_TO_VSC_EX = 4;
    private const ushort VK_RETURN = 0x0D;

    // Scan codes (set 1) for F13..F24, which MapVirtualKey often can't resolve.
    private static readonly ushort[] F13ToF24 =
        { 0x64, 0x65, 0x66, 0x67, 0x68, 0x69, 0x6A, 0x6B, 0x6C, 0x6D, 0x6E, 0x76 };

    // Keys that need the extended flag even if MapVirtualKey doesn't say so.
    private static readonly HashSet<ushort> ExtendedVks = new()
    {
        0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, // PgUp PgDn End Home Left Up Right Down
        0x2D, 0x2E,                                     // Insert Delete
        0x5B, 0x5C, 0x5D,                               // LWin RWin Apps
        0x6F, 0x90, 0xA3, 0xA5                          // Divide NumLock RCtrl RAlt
    };

    private static readonly object Gate = new();
    private static readonly Dictionary<ushort, int> Down = new();

    /// <summary>Scancode works with nearly everything. VirtualKey is a fallback for odd apps.</summary>
    public static InputMode Mode { get; set; } = InputMode.Scancode;

    // ───── keyboard ─────

    public static void KeyDown(ushort vk)
    {
        if (vk == 0) return;
        lock (Gate)
        {
            Down.TryGetValue(vk, out int n);
            Down[vk] = n + 1;
            if (n == 0) SendKey(vk, false);
        }
    }

    public static void KeyUp(ushort vk)
    {
        if (vk == 0) return;
        lock (Gate)
        {
            if (!Down.TryGetValue(vk, out int n)) return;
            if (n <= 1) { Down.Remove(vk); SendKey(vk, true); }
            else Down[vk] = n - 1;
        }
    }

    public static void Tap(ushort vk, int holdMs = 30)
    {
        KeyDown(vk);
        Thread.Sleep(holdMs);
        KeyUp(vk);
    }

    public static void ReleaseAll()
    {
        lock (Gate)
        {
            foreach (var vk in Down.Keys.ToArray()) SendKey(vk, true);
            Down.Clear();
        }
    }

    /// <summary>Types text as Unicode characters (works in text boxes; most games ignore it).</summary>
    public static void TypeText(string text)
    {
        foreach (char c in text)
        {
            if (c == '\r') continue;
            if (c == '\n') { Tap(VK_RETURN); continue; }
            SendOne(UnicodeInput(c, false));
            SendOne(UnicodeInput(c, true));
        }
    }

    // ───── mouse ─────

    public static void MouseButton(string button, bool down)
    {
        uint flags, data = 0;
        switch ((button ?? "Left").ToLowerInvariant())
        {
            case "right": flags = down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP; break;
            case "middle": flags = down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP; break;
            case "x1": flags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = 1; break;
            case "x2": flags = down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP; data = 2; break;
            default: flags = down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP; break;
        }
        var input = new INPUT { type = INPUT_MOUSE };
        input.u.mi = new MOUSEINPUT { dwFlags = flags, mouseData = data };
        SendOne(input);
    }

    // ───── internals ─────

    private static void SendKey(ushort vk, bool up)
    {
        var ki = new KEYBDINPUT();
        uint flags = up ? KEYEVENTF_KEYUP : 0;

        if (Mode == InputMode.Scancode && TryGetScan(vk, out ushort scan, out bool ext))
        {
            ki.wVk = 0;
            ki.wScan = scan;
            flags |= KEYEVENTF_SCANCODE | (ext ? KEYEVENTF_EXTENDEDKEY : 0);
        }
        else
        {
            ki.wVk = vk;
            ki.wScan = (ushort)(MapVirtualKeyW(vk, 0) & 0xFF);
            if (ExtendedVks.Contains(vk)) flags |= KEYEVENTF_EXTENDEDKEY;
        }

        ki.dwFlags = flags;
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.u.ki = ki;
        SendOne(input);
    }

    private static bool TryGetScan(ushort vk, out ushort scan, out bool extended)
    {
        scan = 0; extended = false;

        // Browser / media / launch keys have no useful scan code, send them as virtual keys.
        if (vk >= 0xA6 && vk <= 0xB7) return false;

        if (vk >= 0x7C && vk <= 0x87)
        {
            scan = F13ToF24[vk - 0x7C];
            return true;
        }

        uint sc = MapVirtualKeyW(vk, MAPVK_VK_TO_VSC_EX);
        if ((sc & 0xFF00) == 0xE100) return false; // Pause: special multi-byte sequence
        extended = (sc & 0xFF00) == 0xE000 || ExtendedVks.Contains(vk);
        scan = (ushort)(sc & 0xFF);
        return scan != 0;
    }

    private static INPUT UnicodeInput(char c, bool up)
    {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.u.ki = new KEYBDINPUT
        {
            wVk = 0,
            wScan = c,
            dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0)
        };
        return input;
    }

    private static void SendOne(INPUT input)
    {
        var arr = new[] { input };
        if (SendInput(1, arr, Marshal.SizeOf<INPUT>()) == 0)
            Debug.WriteLine($"[InputInjector] SendInput failed, error {Marshal.GetLastWin32Error()}");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL, wParamH;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyW(uint uCode, uint uMapType);
}
