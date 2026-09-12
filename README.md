# CPU TurboBoost Toggle [Windows 10/11]

A tiny Windows system-tray tool that toggles Intel Turbo Boost on and off with one click or a global hotkey. Very convenient for laptops when you want to switch it in real time.

<img width="292" height="232" alt="image" src="https://github.com/user-attachments/assets/562d354e-e6c6-40af-b094-cf306ebed834" />


## Install

1. Download `TurboToggle.exe` from [Releases](../../releases/latest).
2. Run it, allow the UAC prompt, the tray icon appears. No installation step.
   The exe is unsigned: SmartScreen shows "Unknown publisher" (More info → Run anyway).

- [VirusTotal Report](https://www.virustotal.com/gui/file/1a69832f9bc56aab23b1775bb6f1457f9bbf2673ec86a4c83584bed7a8861596)

## Features

Tray icon (green/grey), fast Enable/Disable, global hotkey with 6 presets + custom capture
(can be turned off entirely), Copilot-key support, en/ru/uk/zh languages, autostart, Windows 11 power-mode overlay support.
- **Portable:** no installer and no dependencies. A single self-contained `TurboToggle.exe` (~50 MB) that runs from any folder.
Settings live in `%APPDATA%\TurboToggle`, nothing else is written to the system (except the optional autostart entry, only if you enable it).
Crash diagnostics, if any, go to `%LOCALAPPDATA%\TurboToggle\error.log`.
- **Enable Turbo Boost on exit** - optionally restores the boost value when quitting
  (best-effort, skipped silently if a plan switch is detected, so exit never hangs).
- **Push notifications on mode change** - routine status tips on/off (errors always show).
- **Copilot-key support** - Assign the Copilot button for easy switching.

## Copilot key

Newer laptops have a dedicated Copilot key, physically it sends **Shift+Win+F23**.
Pick the `Copilot key` preset (or capture it via `Custom...`)
No need to disable anything in Windows and the classic "hotkey busy" problem doesn't apply.

## Requirements

- Windows 10 or 11 (x64)
- Administrator privileges
- Intel processor

## Build

- Run `src\publish.bat`, or:
  ```
  dotnet publish src\TurboToggle.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
  ```
- Output: `src\bin\Release\net10.0-windows\win-x64\publish\TurboToggle.exe`
- Requires [.NET 10 SDK](https://dotnet.microsoft.com/download).

The exe manifest requests administrator rights, so Windows shows the UAC prompt on start.

## License

MIT - see [LICENSE](LICENSE) for details.

## Known Limitations

- Requires administrator rights, needed for power scheme modification (UAC prompt on every start)
- Intel-only, AMD Turbo Core is not supported by this power API
- Unsigned executable, SmartScreen warning on first run

