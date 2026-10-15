# gtfo-advanced-crosshair

Vibe coded with AI, mod adds clearly visible, contrast crosshair with additional features: melee charge timing, magazine indicator, and capacity readouts for packs, items, and tools.

## Features

- Configurable center and outline shapes.
- Optional melee charge hint that indicates melee charge.
- The crosshair follows recoil that stays on the screen while the camera stays still.
- Magazine dots beside the outline. A full magazine hides them. Spent rounds stay visible at a lower opacity, and a magazine under five rounds draws one larger dot per round. The dots flash as a reload completes.
- `EMPTY` under the other hints while the wielded gun's magazine has no rounds.
- Remaining pack capacity, consumable uses, and tool capacity (0 to 100) at the visor center.
- Magazine and reserve ammo below the center while aiming through a weapon scope.

Shapes for Center, Outline, and Hint:

- Dot
- Dots
- Cross
- T
- Diagonal
- Triangle
- Chevron
- Lines
- Arcs
- Circle
- Square
- Diamond
- Corners

![Center, outline, and hint shapes](shape-preview.png)

By default the reticle is shown for primary, special, and melee weapons, and hidden for tools, packs, and items. Capacity hints use their own toggles, so they can appear while the reticle for that slot is off.

## Requirements

- No special requirements

## Install

Install [gtfo_advanced_crosshair](https://thunderstore.io/c/gtfo/p/Vyacheslav_921/gtfo_advanced_crosshair/) with r2modman, then launch GTFO with **Play Modded**.

The package places `gtfo-advanced-crosshair.dll` in the profile's `BepInEx\plugins` folder. You can copy that DLL there yourself if you are not using r2modman.

Settings are saved in `BepInEx\config\gtfo-advanced-crosshair.cfg`. Colors are hex `RRGGBB` or `RRGGBBAA`, with no leading `#`. Example: `FF3355`.

## Configuration

### 1 - General

| Setting | Default | What it does |
| --- | --- | --- |
| Crosshair enabled | true | Turns the reticle on. |
| Static | false | Keeps Center, Outline, and the melee hint at their configured look. Disables usage-state colors, the auto-attack blink, and the melee charge animation. |

### Show for:

| Setting | Default | What it does |
| --- | --- | --- |
| Weapons | true | Crosshair while holding a primary weapon, special weapon, or melee weapon. |
| Tools | false | Crosshair while holding any tool. |
| Packs | false | Crosshair while holding a resource pack. |
| Items | false | Crosshair while holding consumables or carried objects. |

### 2 - Center

| Setting | Default | What it does |
| --- | --- | --- |
| Hide when scoped | true | Hides the crosshair while aiming through a magnified scope. |
| Follow recoil | true | Moves the crosshair with weapon recoil that does not turn the camera. Shotguns stay centered. |
| Shape | Arcs | Center shape. Same list as in Features. |
| Color | 5eb18e | Center color. |
| Size | 22 | Center shape diameter in HUD units. Range 1 to 32. |
| Thickness | 2.8 | Center shape thickness. For Dot and Dots, this controls dot size. Range 0.5 to 16. |
| Opacity | 0.7 | Center opacity. 0 is invisible, 1 is solid. |
| Auto attack blink color | ff2a2a | Color the outline moves toward just before an automatic melee attack. |
| Auto attack blink thickness increase | 50 | Percent by which Center and Outline thickness grows during that blink. Range 0 to 200. |

### 3 - Outline

| Setting | Default | What it does |
| --- | --- | --- |
| Shape | Arcs | Outline shape. Same list as in Features. |
| Color | b00a22 | Outline color. |
| Size | 32 | Outline shape diameter in HUD units. Range 1 to 64. |
| Thickness | 3.4 | Outline shape thickness. For Dot and Dots, this controls dot size. Range 0.5 to 16. |
| Opacity | 0.8 | Outline opacity. 0 is invisible, 1 is solid. |
| Spacing | 3 | Radial distance from the center shape. Range 0 to 32. |

### 4 - Hint

| Setting | Default | What it does |
| --- | --- | --- |
| Enabled | true | Melee charge hint. It appears, shrinks to the crosshair, then changes color and thickens, and hides 100 ms before an automatic attack. A charge with no automatic attack stays on the shrink. |
| Show always | false | Keeps the hint visible while no melee attack is charging. |
| Shape | Arcs | Melee charge hint shape. Same list as in Features. |
| Color | ffffff | Melee charge hint color. |
| Auto attack color | cb2769 | Hint color as an automatic melee attack approaches. |
| Auto attack thickness increase | 110 | Percent by which hint thickness grows as an automatic melee attack approaches. Range 0 to 200. |
| Size | 78 | Starting diameter of the melee charge hint, in HUD units. Range 8 to 64. Drawn size also stays at least as large as the crosshair. |
| Thickness | 1.25 | Hint shape thickness. For Dot and Dots, this controls dot size. Range 0.5 to 16. |
| Opacity | 0.8 | Hint opacity. 0 is invisible, 1 is solid. |

### 5 - Mag counter

Dots to the right of the outline.

| Setting | Default | What it does |
| --- | --- | --- |
| Enabled | true | Shows the magazine dots beside the crosshair. |
| Size | 3.2 | Magazine dot diameter in HUD units. Clips shorter than five rounds scale each dot up slightly. Range 1 to 16. |
| Opacity | 0.8 | Opacity of rounds still in the magazine once the dots have faded in. 0 is invisible, 1 is solid. |
| Spent opacity | 0.18 | Opacity of spent rounds once the dots have faded in. 0 is invisible, 1 is solid. |

### 5 - Packs Hints

Remaining pack capacity at the visor center while holding a resource pack.

| Setting | Default | What it does |
| --- | --- | --- |
| Enabled | true | Shows remaining pack capacity at the visor center while holding a resource pack. |
| Size | 18 | Pack capacity hint height in HUD units. Range 2 to 48. |
| Opacity | 0.7 | Pack hint text opacity. 0 is invisible, 1 is solid. |
| Tool color | 6eccff | Tool refill pack capacity. |
| Medpack color | ff7893 | Health pack capacity. |
| Ammo color | e0d489 | Ammo pack capacity. |
| Disinf color | 87a0e1 | Disinfection pack capacity. |

### 6 - Items Hints

Remaining uses at the visor center while holding a level consumable.

| Setting | Default | What it does |
| --- | --- | --- |
| Enabled | true | Shows remaining uses at the visor center while holding a level consumable. |
| Size | 18 | Item count hint height in HUD units. Range 2 to 48. |
| Opacity | 0.7 | Item hint text opacity. 0 is invisible, 1 is solid. |
| Glow color | 6affeb | Glow stick. |
| Fog color | 93c9c1 | Fog repeller. |
| Foam color | cff8a3 | C-foam grenade. |
| Melter color | ffffff | Melter. |
| Health syringe color | ff7290 | Health syringe. |
| Melee syringe color | 98a607 | Melee syringe. |
| Mine color | f8df81 | Mine. |
| Foam mine color | cff8a3 | C-foam mine. |
| Flashlight color | ede78d | Flashlight. |
| Heavy fog color | ffffff | Heavy fog repeller. |

### 7 - Tools Hints

Remaining class-tool capacity, from 0 to 100 percent of its maximum, at the visor center.

| Setting | Default | What it does |
| --- | --- | --- |
| Enabled | true | Shows remaining tool capacity, from 0 to 100 percent of its maximum, at the visor center while holding a class tool. |
| Size | 18 | Tool capacity hint height in HUD units. Range 2 to 48. |
| Turret color | ffb15a | Sentry gun capacity. |
| Foam color | 8dffb0 | C-foam launcher capacity. |
| Mine color | f8df81 | Mine deployer capacity. |
| Foam mine color | cff8a3 | C-foam mine deployer capacity. |
| Bio tracker color | 6ecbff | Bio tracker. The tracker is marked infinite, so no capacity number is shown. |

### 8 - Weapon Ammo

Shown below the screen center while aiming through a magnified scope. The upper line is rounds in the magazine. The lower line is reserve ammo.

| Setting | Default | What it does |
| --- | --- | --- |
| Enabled | true | While aiming through a magnified scope, shows magazine rounds and reserve ammo below the screen center. |
| Margin | 110 | Distance from the screen center to the weapon ammo hint. Range 0 to 480. |
| Color | ffffff | Weapon ammo color at a full magazine or a full reserve. |
| Low amount red color hint | true | Shifts each line from Color toward Low color as that ammo pool runs out. The magazine line follows rounds left in the magazine. The reserve line follows ammo outside the magazine. |
| Low color | ff2a2a | Color a line reaches when that pool is empty. Used while Low amount red color hint is on. |
| Size | 18 | Magazine line height in HUD units. Reserve ammo uses a smaller size. Range 2 to 48. |

### 9 - Mag Hint

| Setting | Default | What it does |
| --- | --- | --- |
| Enabled | true | Shows `EMPTY` below the other hints while the wielded gun's magazine is empty. A reload that starts empty keeps it up until that reload finishes. |
| Size | 18 | Mag hint text height in HUD units. Range 2 to 48. |
| Color | ff2a2a | `EMPTY` text color. |
| Shimmer intensity | 0.6 | How strongly the mag hint gleams. 0 keeps a steady color, 1 is the strongest shimmer. |
| Full opacity at | 0.7 | Magazine fill where the magazine dots reach Mag counter opacity. 0.7 reaches it at 70% of the magazine and keeps it there as fewer rounds remain. A full magazine always hides the dots. The last 10 rounds always use that opacity, so a short magazine does not stay faded. `EMPTY` stays hidden until the magazine is empty. |

## Building

Copy `local.props.example` to `local.props` and set `BepInEx` to your profile's BepInEx folder. That path is only used to compile.

```powershell
dotnet build
```

The plugin is written to `build\gtfo-advanced-crosshair.dll`.

## License

This mod is [MIT](LICENSE) licensed. You may use, change, and share it, including in other mods, as long as the license notice stays with the source.
