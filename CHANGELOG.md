# Changelog

## 1.3.2 — 2026-11-08

- New setting `Spare items for place/leave objectives` (0-3, default 1): packs extra pieces for place/leave objectives (markers, beacons); never for hand-over items; unused spares return to the stash via the Quest Case clean-up.

## 1.3.1 — 2026-11-07

- The Quest Case now sorts like the stash (Sort button / sorting mods work inside it).
- After a raid the Quest Case is tidied up: everything not needed for your active quests goes back to the stash, keys go to the KM Case (Keymaster).

## 1.3.0 — 2026-11-03

- New **Quest Case**: 2x2 in the secure container, 10x30 = 300 cells inside (configurable in `questcase.json`), sold by Therapist at loyalty level 1. Quest items are packed into it first.
- Scrollable Quest Case window (mouse wheel, scrollbar, auto-scroll while dragging an item).
- Fix: since SPT 4.1 the mod found no quests at all (`conditions` changed from a list to an object), so nothing was packed.
- New server part in `SPT_Runtime/user/mods/AufgabenGepaeck/`.

## 1.2.0 — 2026-11-03

- First release as a standalone repository (SPT 4.1.6).
