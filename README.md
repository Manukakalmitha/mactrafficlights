# Mac Traffic Lights v4.0

Mac Traffic Lights is a Windows 11 utility that replaces the normal top-right window controls with macOS-style traffic lights without patching Windows system files or injecting code into other apps.

## What changed in v4.0

v4.0 is a full rewrite from the old single-window overlay design.

The older v3 builds tracked only one target window at a time. That made the app feel toy-like whenever two windows were visible, because only the foreground window got traffic lights.

v4.0 uses a multi-window overlay manager:

- every visible normal app window can get its own traffic-light overlay
- the foreground window gets brighter active dots
- background windows get dimmed inactive dots, closer to macOS behavior
- overlays are z-order aware, so a covered window should not leave floating buttons above the front window
- Spotify and Discord still use micro-mask mode to avoid a large rectangle over custom titlebars
- normal apps use a color-matched patch over the native Windows minimize/maximize/close region
- drag-mask behavior remains, so native Windows controls stay hidden while windows move
- process and class names are cached to reduce repeated Windows API work
- settings are separated into `%APPDATA%\MacTrafficLightsV4`

The controls are:

- Red: close
- Yellow: minimize
- Green: maximize or restore

## Why this is not “embedded”

True embedding would require DLL injection or subclassing other apps from inside their own process. That is not used here because it can trigger antivirus, break apps, fail on elevated windows, and become risky like the transformation packs this project avoids.

v4.0 stays in the safer lane: it creates separate lightweight overlay windows and manages them carefully.

## Build and run

1. Extract this folder to a permanent location, for example `Documents\MacTrafficLights`.
2. Double-click `Build-and-Run.bat`.
3. After it builds, `MacTrafficLights.exe` can be launched directly.

The build uses the .NET Framework C# compiler already present on most Windows 11 installations. No third-party compiler or NuGet packages are required.

Windows SmartScreen can warn about `MacTrafficLights.exe` because it is a locally compiled unsigned personal executable. The complete source is included in `MacTrafficLights.cs`.

## Tray menu

Right-click the tray icon, or right-click any traffic-light overlay, to:

- enable or disable v4.0
- launch with Windows
- keep symbols visible instead of hover-only
- choose button size
- re-scan windows / force topmost
- inspect `Renderer status`
- toggle micro-mask mode for the current app
- nudge alignment left/right/up/down by 2 px
- exclude an app
- clear exclusions
- clear custom micro-mask apps
- exit completely

If the dots are too small or too large, use tray menu → `Button size`.

If the dots are slightly off, use tray menu → `Alignment`.

## Safety design

v4.0 does not:

- replace or modify files in `System32`
- patch `uxtheme.dll`
- modify Explorer or DWM binaries
- inject DLLs into Brave, Chrome, Spotify, Discord, or other apps
- permanently modify another app's window style

Exiting v4.0 immediately removes all overlays and exposes the original Windows controls again.

## Known limitations

- This is still an external overlay manager, not a real Windows theme engine.
- Elevated Administrator windows may ignore click commands from a normal non-admin process.
- Very unusual custom windows, games, exclusive fullscreen apps, and protected surfaces can behave differently.
- If two windows overlap exactly over a control area, v4.0 hides the lower overlay to avoid floating buttons.
- If MyDockFinder or another shell tool uses topmost windows aggressively, v4.0 tries to manage z-order safely but cannot become the system compositor.

## Uninstall

1. Right-click the tray icon and disable `Launch with Windows`.
2. Select `Exit`.
3. Delete the app folder.
4. Optionally remove `%APPDATA%\MacTrafficLightsV4` to clear saved v4 settings.

No restore point or Windows repair operation is needed to uninstall v4.0.
