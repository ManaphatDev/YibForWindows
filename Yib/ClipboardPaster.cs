using System.Collections.Specialized;
using System.Runtime.InteropServices;

namespace Yib;

internal static class ClipboardPaster
{
    private const int ClipboardSettleDelayMs = 80;

    // After the target window is foreground, give it a moment to restore focus to its
    // inner edit control (Electron/Chromium apps like Discord restore DOM focus on
    // WM_ACTIVATE asynchronously) before the keystrokes arrive.
    private const int FocusSettleDelayMs = 120;

    // Space the synthesized key events out instead of firing all four in one burst -
    // Chromium-based apps frequently drop a zero-gap Ctrl+V that a human's slower
    // keypress would have registered.
    private const int KeyEventDelayMs = 30;

    // The clipboard is a single shared system resource; another process holding it open
    // makes SetFileDropList throw transiently. Retry a few times before giving up.
    private const int ClipboardRetries = 5;
    private const int ClipboardRetryDelayMs = 60;

    public static void PasteFiles(IEnumerable<string> filePaths, IntPtr targetWindow)
    {
        var collection = new StringCollection();
        foreach (string path in filePaths.Reverse())
        {
            collection.Add(path);
        }

        if (collection.Count == 0)
        {
            return;
        }

        // All the work below blocks its thread for up to ~1s (clipboard settle + foreground
        // poll + spaced keystrokes). It MUST NOT run on the UI thread: the WH_MOUSE_LL hook
        // is serviced there, and a blocked UI thread causes Windows to silently unhook it
        // (LowLevelHooksTimeout), so the app would stop detecting shakes after a paste.
        // A dedicated STA thread is required because Clipboard.* only works under STA.
        var thread = new Thread(() => PasteOnStaThread(collection, targetWindow))
        {
            IsBackground = true,
            Name = "Yib.ClipboardPaste",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    private static void PasteOnStaThread(StringCollection collection, IntPtr targetWindow)
    {
        try
        {
            if (!TrySetClipboard(collection))
            {
                return;
            }

            Thread.Sleep(ClipboardSettleDelayMs);

            // The dial overlay stole focus while it was open; bring the original window back
            // to the foreground (and wait for the switch to land) so Ctrl+V goes there.
            NativeMethods.RestoreForeground(targetWindow);
            Thread.Sleep(FocusSettleDelayMs);

            SendCtrlV();
        }
        catch (Exception ex)
        {
            // Doing nothing is always better than crashing a tray app. Log for diagnosis.
            Program.LogError(ex);
        }
    }

    private static bool TrySetClipboard(StringCollection collection)
    {
        for (int attempt = 0; attempt < ClipboardRetries; attempt++)
        {
            try
            {
                Clipboard.SetFileDropList(collection);
                return true;
            }
            catch (ExternalException)
            {
                // Clipboard locked by another process - transient. Wait and retry.
                Thread.Sleep(ClipboardRetryDelayMs);
            }
        }

        return false;
    }

    private static void SendCtrlV()
    {
        SendKey(NativeMethods.VK_CONTROL, keyUp: false);
        Thread.Sleep(KeyEventDelayMs);
        SendKey(NativeMethods.VK_V, keyUp: false);
        Thread.Sleep(KeyEventDelayMs);
        SendKey(NativeMethods.VK_V, keyUp: true);
        Thread.Sleep(KeyEventDelayMs);
        SendKey(NativeMethods.VK_CONTROL, keyUp: true);
    }

    private static void SendKey(ushort vk, bool keyUp)
    {
        var inputs = new[] { KeyInput(vk, keyUp) };
        int size = Marshal.SizeOf(typeof(NativeMethods.INPUT));
        NativeMethods.SendInput(1, inputs, size);
    }

    private static NativeMethods.INPUT KeyInput(ushort vk, bool keyUp) => new()
    {
        type = NativeMethods.INPUT_KEYBOARD,
        ki = new NativeMethods.KEYBDINPUT
        {
            wVk = vk,
            wScan = 0,
            dwFlags = keyUp ? NativeMethods.KEYEVENTF_KEYUP : 0,
            time = 0,
            dwExtraInfo = IntPtr.Zero,
        }
    };
}
