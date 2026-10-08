# Desktop lyrics native verifier

Run from the repository root on Windows:

```powershell
dotnet run --project tools/NekoPlayer.DesktopLyricsVerifier -- artifacts/desktop-lyrics-verifier
```

This verifier creates the production `DesktopLyricsWindow` and a small native underlay window owned by a separate process. Both fixture windows appear briefly near the primary work area's upper-left corner with `WS_EX_NOACTIVATE`. It does not load a player profile, create audio or network services, inject input, activate a window, or manipulate existing windows.

For unlocked, locked, unlocked, and locked states it checks that `WindowFromPoint` returns the expected HWND and records its owning process. The unlocked HWND must be the production desktop lyric window; the locked HWND must be the native window belonging to the other process. It also records unchanged foreground ownership and checks the real Avalonia render output for transparent background and cyan lyric pixels.

`results.json`, `locked.png`, and `unlocked.png` are saved to the output directory. A failed native selection or missing/opaque lyric rendering returns exit code 1. The fixture closes both windows on completion.
