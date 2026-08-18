# Custom Growables

A BepInEx add-on for Silverpine 1.7.3 and Custom Item Loader 2.7.0. It turns
ordinary Custom Item Loader items into plantable, persistent growables by
reading an optional `growable` block from the same item definition.

Custom Growables supports:

- Any number of visible growth stages.
- Base-game sprites, pack-relative PNG/JPG/JPEG art, or one-direction GLB
  renders at every stage.
- Per-stage size multipliers layered on the plant's base world scale.
- Real-world-hour or processed-game-turn growth.
- Save/load persistence and offline real-time progress.
- Winter growth pausing.
- Player-property and roof planting restrictions.
- Multiple harvest outputs with quantity ranges and chances.
- Base-game or Custom Item Loader harvest items.
- Optional crop regrowth or persistent perennial harvest cycles.
- Separate saved maturity and harvest-readiness state for trees and bushes.
- A dedicated in-game Growables Editor backed by CIL's item and pack APIs.
- Native-style player and NPC harvesting interactions.
- Save-safe assembly-qualified serialization for the add-on's custom seed
  component.

## Requirements

- BepInEx for Silverpine 1.7.3
- Silverpine Modding Tools 1.4.0 or newer
- Custom Item Loader 2.7.0 or newer

Install `CustomGrowables.dll` in:

```text
BepInEx/plugins/CustomGrowables/
```

Keep item packs and all custom images or GLBs in Custom Item Loader's normal
folder:

```text
BepInEx/config/CustomItemLoader/
```

## In-game Growables Editor

Open `Modding Tools` on Silverpine's main menu and choose `Growables Editor`.
The editor lists the enabled items registered by Custom Item Loader. Create
the inventory/seed item in `Custom Item Editor` first, then select it in the
Growables Editor to configure:

- Annual, regrowing-crop, or perennial lifecycle behavior.
- Growth clock and planting restrictions.
- Base-game sprites, imported images, or imported GLB models for every stage.
- Stage order, durations, maturity, and the post-harvest return stage.
- One or more base-game or CIL harvest results.

Lifecycle modes, clocks, art sources, and stage references use dropdown-style
selection controls. Larger catalogs open a searchable selection screen:

- The harvest-item picker includes both registered CIL items and base-game
  items, while the text field remains available for direct ID/name entry.
- The artwork picker lists the native prefab sprite catalog plus existing
  PNG/JPG/JPEG images and GLB models in the owning CIL pack. `Import Image /
  GLB` copies new art into the pack's `assets` folder. GLB stages expose
  rotation, zoom, resolution, and a one-direction preview; they do not require
  front/back/left/right variants. The preview places the plant over
  Silverpine's native one-tile grass turf at its configured world size.
- Escape or `Cancel` returns to the growable without changing the value.

The Growables Editor does not duplicate CIL's item editor or own a second pack
format. It reads and atomically writes only the selected item's `growable`
extension through `CustomItemApi`; CIL continues to own the pack, item
identity, inventory visuals, cloning, and registration. Restart Silverpine
after saving to apply registration changes.

## Example item pack

The seed item and harvested item are normal Custom Item Loader entries. Add a
`growable` object only to the seed item:

```json
{
  "packId": "example.moonroot",
  "enabled": true,
  "items": [
    {
      "id": "moonroot",
      "name": "Moonroot",
      "description": "A pale medicinal root.",
      "image": "moonroot_item.png",
      "category": "Alchemy",
      "sound": "Plant",
      "value": 8,
      "bulk": 0.5
    },
    {
      "id": "moonroot_seeds",
      "name": "Bag of Moonroot Seeds",
      "description": "Seeds that thrive in open soil.",
      "image": "moonroot_seeds.png",
      "category": "Miscellaneous",
      "sound": "Plant",
      "value": 5,
      "bulk": 0.5,
      "growable": {
        "visibleName": "Moonroot",
        "clock": "RealTimeHours",
        "playerPropertyOnly": true,
        "allowRoofed": false,
        "growsInWinter": false,
        "stages": [
          {
            "sprite": "sprite_nature_seeds",
            "duration": 8
          },
          {
            "image": "moonroot_growing.png",
            "duration": 16,
            "pixelsPerUnit": 32,
            "pivotX": 0.5,
            "pivotY": 0.5
          },
          {
            "image": "moonroot_mature.png"
          }
        ],
        "harvest": [
          {
            "item": "example.moonroot:moonroot",
            "minimum": 1,
            "maximum": 3,
            "chance": 1.0
          }
        ]
      }
    }
  ]
}
```

Custom Item Loader ignores the behavior itself but preserves the `growable`
block when its item editor saves the pack. Use the dedicated Growables Editor
for normal editing; direct JSON remains supported.

Do not clone `Bag of Turnip Seeds`, the other native seed bags, or the native
saplings for a custom seed item. Those templates already contain Silverpine's
fixed `ItemComponent_Seeds`; combining it with the add-on component would
trigger two planting behaviors. The Growables Editor labels these items,
disables `Add Growable Behavior`, and refuses to save an attached growable.
Runtime registration retains the same check as a second line of defense.

## Growth stages and art

`stages` must contain at least two entries. Each stage must specify exactly one
art source:

- `sprite`: Exact asset name of a sprite referenced by a loaded base-game
  prefab, or an explicit Unity Resources path. The plugin indexes native
  prefab renderers and the turnip crop's mature sprite; it deliberately does
  not scan or bulk-load Silverpine's enormous full sprite library.
- `image`: PNG, JPG, or JPEG path relative to the item-pack JSON. The path
  cannot leave the pack directory.
- `model`: GLB path relative to the item-pack JSON. Custom Item Loader renders
  one transparent, tightly cropped sprite from `rotation`, `zoom`, and
  `resolution`. The result is cached inside the pack's `.cache` folder and is
  reused until the model or render settings change.

Examples of native sprite names include:

```text
sprite_nature_seeds
sprite_nature_turnip_seedling
sprite_nature_turnip_mature
sprite_nature_sapling_oak
sprite_nature_sapling_pine
```

Base-game art retains its native pivot and pixels-per-unit. Custom image and
GLB art default to 32 pixels per unit and a centered `(0.5, 0.5)` pivot; override
`pixelsPerUnit`, `pivotX`, or `pivotY` on that stage when needed.

GLB-specific stage fields are `rotation` (`[x, y, z]`, default
`[20, 135, 0]`), `zoom` (default `1`), and `resolution` (default `512`). Unlike
turnable items or furniture, a growable generates only this one view.

Every stage also accepts `scale` from `0.1` to `10` (default `1`). This is a
multiplier: an overall growable scale of `1.5` and stage scale of `0.5` display
that stage at an effective `0.75×` world scale. Stage scaling works with native
sprites and imported images as well as rendered GLBs.

`duration` belongs to the stage containing it and controls how long that stage
lasts before advancing. The mature final stage does not need a duration.

## Growable fields

| Field | Default | Meaning |
|---|---:|---|
| `visibleName` | Derived from seed item name | Plant name used in interaction and NPC text. |
| `clock` | `RealTimeHours` | `RealTimeHours` or `GameTurns`; controls the unit used by stage durations. |
| `playerPropertyOnly` | `true` | Requires the player's current tile to pass Silverpine's owned-property check. |
| `allowRoofed` | `false` | Allows planting beneath a roof tile. |
| `growsInWinter` | `false` | When false, active growth pauses in winter. |
| `blocksMovement` | `false` | Adds an impassable turf collider to the plant. |
| `replaceExistingPlants` | `true` | Removes an existing plant/herb/crop on the planting tile. |
| `interactionTurns` | `5` | Turns spent planting, destroying, or harvesting. |
| `perennial` | `false` | When true, the plant becomes mature once and retains that saved maturity across later harvest cycles. Requires `regrowStage`. |
| `matureStage` | `-1` | First stage considered permanently mature. `-1` uses `regrowStage` for perennials and the final stage otherwise. |
| `regrowStage` | `-1` | `-1` destroys the plant after harvest; otherwise resets its appearance and harvest timer to this zero-based non-final stage. |
| `scale` | `1` | Base world scale for the generated plant prefab, from `0.1` to `10`. Each stage multiplies this value by its own `scale`. |
| `stages` | required | Ordered art and duration definitions. |
| `harvest` | required | One or more item-result definitions. |

For turn-based growth, each duration is a processed game-turn count. For
real-time growth, durations are hours and elapsed time while the game is
closed is applied when the save is loaded. If the plant is loaded during
winter and `growsInWinter` is false, that offline interval is not applied.

### Perennial example

An apple tree can grow through juvenile stages once, then cycle only between
a mature bare tree and a harvest-ready fruiting tree:

```json
{
  "perennial": true,
  "matureStage": 2,
  "regrowStage": 2,
  "stages": [
    { "image": "apple_sapling.png", "duration": 168 },
    { "image": "apple_young.png", "duration": 336 },
    { "image": "apple_tree_bare.png", "duration": 72 },
    { "image": "apple_tree_fruit.png" }
  ],
  "harvest": [
    {
      "item": "example.orchard:apple",
      "minimum": 2,
      "maximum": 5,
      "chance": 1.0
    }
  ]
}
```

On the first pass, the tree reaches stage 2 and permanently records that it is
mature. Stage 3 is harvest-ready. Harvesting returns the same saved tree
object to stage 2 for 72 hours; it never returns to either juvenile stage.

## Harvest results

`harvest[].item` accepts either:

- A Custom Item Loader qualified ID such as `example.moonroot:moonroot`.
- A base-game display name such as `Turnip`.

`minimum` and `maximum` are inclusive. `chance` is evaluated once per harvest
result and ranges from `0` to `1`. Results that do not fit in the receiving
inventory are placed in a ground loot container on the crop tile.

## Build

```text
dotnet build "Plugin Development/CustomGrowables/CustomGrowables.csproj" -c Release
```

Copy the resulting `CustomGrowables.dll` into the plugin folder shown above.

## Credits

Created by **Saelac and ChatGPT**.
