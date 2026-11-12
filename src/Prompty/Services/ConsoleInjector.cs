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

    public static void SendPrompt(int pid, string text, bool submit = true) =>
        SendPrompt(pid, [PromptPart.FromText(text)], null, submit);

    /// <summary>
    /// Types text and images in order. For each image, <paramref name="putImageOnClipboard"/> places it on the
    /// clipboard, then Alt+V (Claude Code's image-paste key on Windows) makes Claude Code insert it as an
    /// "[Image #N]" chip at that point, exactly as when the user pastes a screenshot.
    /// </summary>
    public static void SendPrompt(int pid, IReadOnlyList<PromptPart> parts, Action<string>? putImageOnClipboard, bool submit = true)
    {
        // Normalize: CRLF to LF, trim the ends of the whole prompt, drop empty text parts.
        var list = parts.Select(p => p.ImagePath != null ? p : PromptPart.FromText(p.Text!.Replace("\r\n", "\n"))).ToList();
        if (list.Count > 0 && list[0].Text is { } first) list[0] = PromptPart.FromText(first.TrimStart());
        if (list.Count > 0 && list[^1].Text is { } last) list[^1] = PromptPart.FromText(last.TrimEnd());
        list = list.Where(p => p.ImagePath != null || p.Text!.Length > 0).ToList();

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

                var typedAnything = false;
                foreach (var part in list)
                {
                    if (part.ImagePath is { } image)
                    {
                        if (putImageOnClipboard == null) continue;
                        putImageOnClipboard(image);
                        Thread.Sleep(150);
                        Write(input, [AltKey('v', 0x56, true), AltKey('v', 0x56, false)]);
                        // Claude Code reads the clipboard asynchronously; wait so following text lands after the chip.
                        Thread.Sleep(ImagePasteDelayMs);
                        typedAnything = true;
                        continue;
                    }
                    var records = new List<INPUT_RECORD>();
                    var body = part.Text!;
                    if (body.Contains('\n'))
                    {
                        // On a fresh session the empty box shows a "Try ..." hint that a leading escape sequence can
                        // partially accept, so type the very first character plainly.
                        if (!typedAnything) { AddText(records, body[..1]); body = body[1..]; }
                        // Bracketed paste keeps newlines as newlines instead of submitting each line.
                        AddText(records, "\x1b[200~");
                        AddText(records, body);
                        AddText(records, "\x1b[201~");
                    }
                    else AddText(records, body);
                    Write(input, records);
                    typedAnything = true;
                }
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

    /// <summary>How long Claude Code gets to fetch a pasted image from the clipboard (it shells out to read it).</summary>
    public static int ImagePasteDelayMs { get; set; } = 1800;

    static INPUT_RECORD AltKey(char ch, ushort vk, bool down) => new()
    {
        EventType = KEY_EVENT,
        KeyEvent = new KEY_EVENT_RECORD
        {
            bKeyDown = down ? 1 : 0, wRepeatCount = 1, wVirtualKeyCode = vk, UnicodeChar = ch, dwControlKeyState = LEFT_ALT_PRESSED,
        },
    };

    const uint LEFT_ALT_PRESSED = 0x0002;

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

/// <summary>A piece of a prompt: text, or an image file to paste as an attachment.</summary>
public sealed record PromptPart(string? Text, string? ImagePath)
{
    public static PromptPart FromText(string text) => new(text, null);
    public static PromptPart FromImage(string path) => new(null, path);
}
