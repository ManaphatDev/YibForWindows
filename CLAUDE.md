# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Yib for Windows: a tiny system-tray app (C# / .NET 8 / WinForms). You shake the mouse → a rotary-phone-style dial pops up at the cursor → you aim at a number (1–9, 0 = 10) and click → it grabs that many most-recent files from Downloads/Desktop/a custom folder, puts them on the clipboard as a file drop, and sends Ctrl+V into whatever window had focus. It's a Windows port of the macOS/Swift "Yib" by PEESAMAC.

The UI is intentionally Thai-language (menu labels, readout text) — keep that convention when editing user-facing strings.

## Commands

Run from the `Yib/` directory (where `Yib.csproj` lives).

```bash
dotnet build                         # debug build
dotnet run                           # build + launch the tray app
dotnet publish -c Release -r win-x64 # release exe -> bin/Release/net8.0-windows/win-x64/publish/Yib.exe
```

There are **no automated tests** and no linter configured. Verification is manual: run the app, shake the mouse, confirm the dial appears and pasting works into a target window (Explorer, a chat app, etc.). The csproj is currently a plain WinForms project; the single-file/self-contained publish settings described in `YibForWindows-PLAN.md` §4 are *not yet applied* to `Yib.csproj`.

## Reference docs

- `YibForWindows-PLAN.md` — the original full spec (in Thai). Module-by-module behavior, sensitivity thresholds, color palette, and the phased implementation order. Read it before adding features.
- `yib-demo.html` — the approved interactive mock of the dial UI. `DialForm.cs` is built to match it; open it in a browser when changing dial visuals/interaction.

## Architecture

Single project, flat namespace `Yib`. Entry point `Program.cs` is `[STAThread]`, enforces a single instance via a named `Mutex`, and runs `TrayApplicationContext` (no main window).

The end-to-end flow, and which file owns each step:

1. **`MouseShakeDetector`** installs a low-level `WH_MOUSE_LL` hook and watches X-axis movement over a ~600ms window, counting direction reversals + total distance. When both exceed the sensitivity threshold it fires `ShakeDetected(Point)`. Thresholds per sensitivity live in the `Thresholds` table.
2. **`TrayApplicationContext`** owns the tray icon/menu, all settings wiring, and the orchestration. On shake it `Post`s back to the UI thread (the hook callback must return fast), disables shake detection, records the current foreground window, and opens `DialForm` modally.
3. **`DialForm`** is the rotary dial. It renders itself as a layered window (see below) and returns `PickedCount` + `PickedFolder`.
4. On `DialogResult.OK`, `TrayApplicationContext` calls **`FilePicker`** (recent files, newest-first) → **`ClipboardPaster`** (reverses to oldest-first, sets the file drop list, restores the saved foreground window, sends Ctrl+V).
5. **`Settings`** is JSON at `%APPDATA%\Yib\settings.json`; **`AudioPlayer`** plays the embedded click; **`NativeMethods`** holds every P/Invoke.

### Threading model (important)

Two constraints drive a lot of the design:

- The **mouse hook callback runs on the message loop** — doing real work there lags the whole system's cursor. So the callback only records points/decides quickly, and on a hit it `_uiContext.Post(...)`s the heavy dial+paste work back to the STA UI thread.
- **Clipboard and WinForms require the STA UI thread**, so that Post is also what gets the work onto the right thread. `AudioPlayer` is the exception: `waveOut*` playback blocks for the clip duration, so it's queued to a thread-pool thread.

### Layered-window rendering in DialForm

`DialForm` does **not** use normal WinForms `OnPaint`. It's a `WS_EX_LAYERED` borderless form drawn by composing a 32bpp premultiplied-ARGB `Bitmap` in `Render()` and pushing it via `UpdateLayeredWindow` (`PushLayeredBitmap`). This is what gives anti-aliased rounded corners and per-pixel alpha (open fade-in, glow, shadows) that a `Region`-clipped form can't. Consequences to remember:

- The window's hit area is a full square; the rounded card shape is only visual. Clicks in the transparent corners are hit-tested against `_cardPath` and treated as "outside" → cancel.
- An animation `Timer` (~16ms) keeps `Render()` running continuously for the idle float + open/highlight/lift/fire animations. A confirmed pick plays the "fire" animation to completion *before* `DialogResult` is set, so the dial doesn't close out from under the animation.
- Almost all geometry is derived from `_cardSize` via the `*Fraction` constants at the top of the file — scale things by editing those, not by hardcoding pixels.

### Focus restoration (the fragile part)

Pasting into another app from a focus-less tray app is unreliable by default. `NativeMethods.RestoreForeground` works around it: it briefly `AttachThreadInput`s to the target window's thread (to satisfy Windows' foreground-lock rules), brings the window forward, then **polls until the foreground switch actually lands** (the switch is async) before returning. `ClipboardPaster` then waits an extra beat and spaces out the synthesized key events, because Chromium/Electron apps drop a zero-gap Ctrl+V. If paste-targeting regresses, this is where to look.

## Gotchas when editing

- **Never let the hook delegate be GC'd** — `MouseShakeDetector` holds `_proc` as a field on purpose; the hook dies silently otherwise.
- **`NativeMethods.INPUT` layout is deliberate** — the union sits at `FieldOffset(8)` (not 4) and includes `MOUSEINPUT` so `Marshal.SizeOf` is 40 bytes. Getting this wrong makes `SendInput` silently no-op. See the comment there.
- Dial number `0` means **10 files**, not 0 (`NumberToRequestedCount`). Numbers requesting more files than exist are dimmed.
- `FilePicker` returns newest→oldest; `ClipboardPaster` reverses to oldest→newest before pasting — keep that ordering contract.
- Follow the plan's **phased rollout**: implement one phase at a time and stop for a manual test before the next.
- Not a git repository.
