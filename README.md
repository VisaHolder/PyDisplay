# PyDisplay
A clean, always-on-top system monitor overlay for Windows 10 and 11. GPU, CPU, RAM, network, disk and drives in a small click-through window — one exe, nothing to install.

> **1.1.2 is a full rebuild.** PyDisplay is now a native Windows app (C# / .NET), not a Python script. Same idea, same features, a much cleaner look, more info, and no dependency manager. Your 1.x settings and themes are brought over automatically the first time you run it. The old Python version lives on the [`python-v1`](https://github.com/VisaHolder/PyDisplay/tree/python-v1) branch.

---

## Download
Grab **`PyDisplay.exe`** from the [latest release](https://github.com/VisaHolder/PyDisplay/releases/latest) and run it. That's it.

- No Python, no .NET, no installer — everything is inside the exe
- `PyDisplay-arm64.exe` is for ARM laptops (Snapdragon)
- Windows may show a SmartScreen warning the first time (the exe isn't code-signed): **More info → Run anyway**

**Coming from PyDisplay 1.x?** Just run the new exe. Your update speed, auto-clean, hidden sections, position and themes come over on first launch. (The old in-app updater can't install this version — download it from the release page.)

---

## Using it
- The overlay is **click-through** — your mouse goes straight through it to whatever's behind
- **Hold Ctrl** to drag it, click it, or right-click it
- **≡** (top right) opens the menu: tools, theme, settings, logging, hide to tray, quit
- **Click a section title** (with Ctrl) to fold it

## Looks
Three styles × two layouts, any theme, any installed font:

| Style | What it is |
|---|---|
| **rows** | labels, block bars and details |
| **graphs** | big numbers with 60-second graphs and per-core bars |
| **compact** | one line per section — the smallest |

Each works **vertical** or **horizontal** (horizontal compact = one slim strip). The overlay never changes size while it runs — every number has a fixed slot.

Six built-in themes (default, terminal, ice, sunset, midnight, light), editable colours, saved themes, and import of your 1.x themes.

## What it shows
- **GPU** — load, VRAM, temperature (with high/low), power out of the card's limit, core clock, fan, video encoder (busy while OBS records)
- **CPU** — load, per-core load, clock (with low/high), tasks and threads, the app using the most CPU
- **Memory** — used, peak, pagefile, the app using the most RAM
- **Network** — down/up, peaks, live ping, data used this session
- **Disk** — read/write · **Drives** — space used on every drive
- **Footer** — uptime, date, clock

Every extra line can be switched off in settings → info. NVIDIA cards get everything; AMD and Intel GPUs show load and VRAM.

## Tools
- **RAM cleaner** — safe or aggressive, step-by-step log, GB freed; **auto-clean** every 5/15/30/60 min that waits while a fullscreen game is running and never touches your clipboard. Some steps need admin — there's a one-click "restart as admin".
- **Speed test** — nearest speedtest.net server, 8 streams, download/upload/ping
- **IP lookup** — local and public IP (public stays hidden until you click, so it never shows on stream)
- **Logging** — a line of stats every 5/15/30/60 s to `stats-log.txt`

## Settings
look · info · sections · behaviour · theme · about — including update speed (0.5–5 s), always on top, click-through, lock position, tray icon, start with Windows, GPU picker, export/import settings, check for updates, and **remove PyDisplay from this PC**.

## Where things are kept
`%APPDATA%\PyDisplay\v2\` — settings, error log, stats log.
**Portable:** put the exe in a folder named `PyDisplay` and everything stays next to it instead.
Uninstall: settings → about → *remove PyDisplay from this PC*, then delete the exe.

---

## Building it yourself
Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download).
```
dotnet publish -c Release -r win-x64 -o publish/win-x64
```
The exe appears in `publish/win-x64/`. Use `-r win-arm64` for ARM.

Test switches (no windows shown): `--snap <dir>` (screenshots of every style/theme/window), `--selftest <file>`, `--settingstest <file>`, `--sizetest <file>`, `--uitest <file>`.

## Changelog
See [CHANGELOG.md](CHANGELOG.md).

## License
MIT
