# AufgabenGepaeck (quest luggage)

Before the raid, packs the quest items of the selected map.

## Requirements

- SPT 4.1.6 (EFT 0.16.9). BepInEx ships with SPT.

## Installation

1. Download `AufgabenGepaeck-1.2.0.zip` from the [Releases](https://github.com/Kaanori/spt-aufgabengepaeck/releases) page.
2. Extract it **into your SPT game folder** (the folder that contains `EscapeFromTarkov.exe`).
   The ZIP already has the right layout; the mod ends up in `BepInEx/plugins/`.
   On Windows, make sure "Extract All" does not add an extra subfolder.
3. Restart the game.

## Uninstall

Delete the mod's files from `BepInEx/plugins/` (settings: `BepInEx/config/` for client mods) and restart the game.

## Version 1.2.0 (2026-09-19)

New setting **"Zuerst in den Sicherheitsbehaelter"** (first into the secure
container) (section `2. Wohin`, on by default). Previously the secure
container was at the end of the target list, so even with the container
allowed, quest items still ended up in the backpack as long as there was
room there. Now it is filled first, if allowed.

As a reminder: **the mod has no key.** It hooks into
`MatchMakerAcceptScreen.Show(...)` and packs automatically when the raid
confirmation screen opens.

## Version 1.1.0 (2026-09-19)

Fixed: in the "fehlt:" (missing:) messages the bare template ID was shown
instead of the item name, because `Aufgabenname()` simply returned
`bedarf.Vorlage`. There is no item object for missing things, so
`Anzeigename()` (which looks up `LocalizedName(Item)` via reflection) does
not apply.

New: `Gegenstandsnamen` reads `SPT/SPT_Data/database/locales/global/ge.json`
(falling back to `en.json`) at startup and resolves template IDs via the
keys `<id> Name`. Falls back to the ID if nothing is found.

## Origin

The source code was lost. This version was decompiled from
`BepInEx/plugins/AufgabenGepaeck.dll` (1.0.0) and rebuilt.
The original sits next to it as `AufgabenGepaeck.dll.original-1.0.0`.

Fixed up during decompilation:
- The iterator `AlleGegenstaende` could not be decompiled and was
  rewritten — it now goes through `CompoundItem` instead of the obfuscated
  `GClass3248`, which gets renamed with every EFT update.
- `[BepInDependency]` lost its arguments: back to `com.SPT.core`.
- Two `isinst` casts and `TryFindLocationForItem(..., ref)` -> `out`.

## Building from source

Needs the .NET 10 SDK and an SPT 4.1.6 installation to build against.

```bash
./build.sh             # Linux: build only
./build.sh --install   # build and copy into the game folder
```
```powershell
.\build.ps1            # Windows
.\build.ps1 -Install
```

The game folder is set in `Directory.Build.props` (Windows default `C:/SPT`)
or via the environment variable `SPT_DIR`.

## Support

Bugs and questions: [GitHub issues](https://github.com/Kaanori/spt-aufgabengepaeck/issues).

## License

MIT — see [LICENSE](LICENSE).
