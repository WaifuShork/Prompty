using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Prompty.Services;

/// <summary>
/// Types a prompt into a specific Claude Code process by writing key events into its console's
/// input buffer. Claude Code receives it exactly as if the user typed it: no window focus,
/// no global keystrokes, and no other console is touched.
/// </summary>
public static class ConsoleInjector
{
    static readonly object Gate = new(); // AttachConsole is process-wide

    public static void SendPrompt(int pid, string text, bool submit = true)
    {
        var body = text.Replace("\r\n", "\n").Trim();
        var records = new List<INPUT_RECORD>();
        if (body.Contains('\n'))
        {
            // Type the first character plainly so the input box is non-empty: on a fresh session the
            // empty box shows a "Try …" hint that a leading escape sequence can partially accept.
            AddText(records, body[..1]);
            // Bracketed paste keeps newlines as newlines instead of submitting each line.
            AddText(records, "\x1b[200~");
            AddText(records, body[1..]);
            AddText(records, "\x1b[201~");
        }
        else
        {
            AddText(records, body);
        }

        lock (Gate)
        {
            FreeConsole();
            if (!AttachConsole((uint)pid))
                throw new InvalidOperationException(
                    $"Couldn't attach to Claude Code's console (pid {pid}): {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
            // While attached, a Ctrl+C or close in that console must not take Prompty down.
            SetConsoleCtrlHandler(IntPtr.Zero, true);
            try
            {
                using var input = CreateFileW("CONIN$", GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
                    IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
                if (input.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Couldn't open the console input buffer");

                Write(input, records);
                if (submit)
                {
                    Thread.Sleep(250); // let the TUI finish ingesting the paste before Enter
                    Write(input, [Key('\r', VK_RETURN, true), Key('\r', VK_RETURN, false)]);
                }
            }
            finally
            {
                SetConsoleCtrlHandler(IntPtr.Zero, false);
                FreeConsole();
            }
        }
    }

    static void AddText(List<INPUT_RECORD> records, string s)
    {
        foreach (var ch in s)
        {
            ushort vk = ch switch { '\x1b' => VK_ESCAPE, '\n' => VK_RETURN, _ => 0 };
            records.Add(Key(ch, vk, true));
            records.Add(Key(ch, vk, false));
        }
    }

    static void Write(SafeFileHandle input, IReadOnlyList<INPUT_RECORD> records)
    {
        const int chunk = 256;
        for (var i = 0; i < records.Count; i += chunk)
        {
            var slice = records.Skip(i).Take(chunk).ToArray();
            if (!WriteConsoleInputW(input, slice, (uint)slice.Length, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Writing to the console input failed");
            if (records.Count > chunk) Thread.Sleep(5);
        }
    }

    static INPUT_RECORD Key(char ch, ushort vk, bool down) => new()
    {
        EventType = KEY_EVENT,
        KeyEvent = new KEY_EVENT_RECORD { bKeyDown = down ? 1 : 0, wRepeatCount = 1, wVirtualKeyCode = vk, UnicodeChar = ch },
    };

    const ushort KEY_EVENT = 1, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B;
    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

    [StructLayout(LayoutKind.Explicit, CharSet = CharSet.Unicode)]
    struct INPUT_RECORD
    {
        [FieldOffset(0)] public ushort EventType;
        [FieldOffset(4)] public KEY_EVENT_RECORD KeyEvent;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct KEY_EVENT_RECORD
    {
        public int bKeyDown;
        public ushort wRepeatCount;
        public ushort wVirtualKeyCode;
        public ushort wVirtualScanCode;
        public char UnicodeChar;
        public uint dwControlKeyState;
    }

    [DllImport("kernel32.dll", SetLastError = true)] static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool WriteConsoleInputW(SafeFileHandle input, INPUT_RECORD[] records, uint length, out uint written);
}
