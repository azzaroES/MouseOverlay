# MouseOverlay

Tiny tray-only cursor overlay for Windows (single ~30 KB exe, no runtime to install).

- Draws a **V arrow** or **circle** at the pointer, above every window - including apps that force their own cursor.
- **CPU / GPU temperature** in small text at the bottom-right of the primary screen (yellow at 80°, red at 90°).
- Everything is configured from the tray icon's right-click menu. Left-click the icon to toggle the overlay.

## Translate any selected text (branch `translate`)

Select text in any app, **Ctrl + right-click** it, and a small dark popup at the pointer shows the Google Translate result (source language auto-detected). Click the popup to copy the translation; click anywhere else to dismiss it.

- **Trigger** (tray → Translate selection → Trigger): Ctrl + right-click, middle click, Ctrl + middle click, or the Ctrl + Shift + Space hotkey. With the right-click triggers the app's own context menu still opens; press Esc to close it, the translation stays.
- **Translate to**: 17 languages in the menu, or type any Google language code. Optional second language for when the text is already in your target language (e.g. ro → en, en → ro).
- **How it gets the text**: it sends Ctrl+Insert to the app (Ctrl+C as a fallback, never in a terminal), reads the clipboard, then puts your previous clipboard contents back. Elevated apps and most games block that, so nothing happens there.
- Uses the same free endpoint the Google Translate website uses; no key, no account. It can rate-limit heavy use.
- Costs nothing while idle: the gesture is read by the same 8 ms tracker thread as the pointer; no hooks.

## Download

Grab `MouseOverlay.exe` from the [Releases](https://github.com/azzaroES/MouseOverlay/releases) page and run it. No installer, no runtime to install (uses the .NET Framework 4.8 that ships with Windows 10/11, x64).

## Build

```
build.cmd
```

Uses the C# compiler that ships with Windows (`%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`); no SDK needed.

## Menu

| Item | Notes |
|---|---|
| Shape / Size / Border width | Arrow tip or circle centre sits exactly on the real hotspot. |
| Center color / Border color / Hollow center | Standard colour picker. |
| Opacity | 25 - 100 %. |
| Real cursor ▸ **Replace with overlay shape** (default) | The Windows cursor itself becomes the overlay shape. It is drawn by the hardware cursor, so it has zero lag and shows everywhere - including the Start menu, volume / quick-settings flyouts and notification centre, where no app window can draw. The overlay window only appears when an app forces its own cursor, and the real cursor is hidden while it does. |
| Real cursor ▸ Replace arrow only | Same, but text (I-beam), resize, busy and hand cursors keep their normal look. |
| Real cursor ▸ Hide it | Real cursor hidden system-wide (Magnification API), overlay always drawn. Invisible over system flyouts. |
| Real cursor ▸ Leave it | Overlay drawn on top of the untouched real cursor. |
| Hide overlay when app hides cursor | Off by default. Turn on for FPS games that hide the pointer and lock it to the centre, otherwise the overlay sits there like a crosshair. |
| Show CPU / GPU temperature, text size | CPU = hottest ACPI thermal zone (no admin, no driver); GPU = NVIDIA NVML. Non-NVIDIA GPUs show `--`. |
| Start with Windows | Adds/removes a `HKCU\...\Run` entry. |

Settings are saved to `MouseOverlay.ini` next to the exe (or in `%LOCALAPPDATA%\MouseOverlay` if that folder is not writable).

When Windows shell UI is open (Start, Quick Settings, notification centre, the volume / media OSD, touch keyboard) the app hands the real cursor back for that moment, because no app window can draw over those surfaces.

## How it stays cheap

- Pointer is a per-pixel-alpha layered window; the shape is rendered once, moving it is a single position-only `UpdateLayeredWindow` call.
- Position is polled from a dedicated thread on an 8 ms high-resolution timer (no hooks, no raw-input flood, nothing in the input path).
- Z-order is only re-asserted when a foreign visible window actually sits above the overlay (checked 4x/s).
- Temperatures refresh every 2 s and the label only re-renders when the numbers change.

Measured on the dev machine: ~9 MB working set, 0 ms CPU over 20 s while the mouse was moving.

## Limits

- Exclusive-fullscreen games render straight to the display; no overlay window can draw over them (borderless/windowed works). In Replace mode the real cursor still has the overlay shape there.
- Windows keeps its own UI (Start menu, flyouts, task switcher) in a higher band; the overlay *window* cannot go above those - that is what Replace mode is for.
- Replaced cursors are restored on exit and on the next launch. If the process is killed, Windows keeps the replaced cursors until you relaunch the app, change the pointer scheme, or sign in again.
