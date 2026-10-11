# gtfo-advanced-crosshair

BepInEx 6 (IL2CPP) plugin for [GTFO](https://store.steampowered.com/app/493520/GTFO/) that replaces the bottom-right watermark text (build / version string) with the **current local time** (`HH:mm:ss`).

## Features

- Shows a live system clock in the helmet-corner watermark slot
- Updates once per second (no per-frame text churn)
- Binds safely by polling until the watermark UI exists — no fragile Harmony patch on watermark setup

## Requirements

- [GTFO](https://store.steampowered.com/app/493520/GTFO/)
- [BepInEx 6 (Unity IL2CPP)](https://docs.bepinex.dev/) via [r2modman](https://thunderstore.io/c/gtfo/p/ebkr/r2modman/) (or equivalent)

## Install

1. Create or open an r2modman profile for GTFO with BepInEx installed.
2. Drop the built plugin into that profile’s BepInEx plugins folder, or install from Thunderstore when published.
3. Launch the game with **Play Modded**.

You should see the local time in the bottom-right watermark instead of the stock build text.

## Build (developers)

Copy `local.props.example` to `local.props` and set `BepInEx` to your profile’s BepInEx folder. That path is only for compile references and is not committed.

```powershell
dotnet build
```

Output:

`build\gtfo-advanced-crosshair.dll`

Copy that DLL into the profile’s `BepInEx\plugins\` folder, then launch GTFO via r2modman **Play Modded**.

## Verify

In the BepInEx console or `BepInEx\LogOutput.log`:

- `gtfo-advanced-crosshair is loaded!`
- `Clock overlay ready (awaits watermark bind).`
- `Harmony patches applied.`
- After the watermark UI is ready: `Clock bound to PUI_Watermark (poll).`

In-game, the bottom-right watermark should display `HH:mm:ss` and tick each second.

## How it works

`ClockOverlay` attaches as a Unity `MonoBehaviour`, walks `GuiManager.WatermarkLayer` → `PUI_Watermark` → `m_watermarkText`, and overwrites that TextMeshPro label with the local clock. Game types live under the profile’s `BepInEx\interop\` assemblies (inspect with ILSpy / dnSpyEx).
