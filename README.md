# CustomRpTweaks

Small SPT 4.1 server mod that applies a few gameplay tweaks from a `config.json`, editable from the server's web config editor (`/configs`).

Based on the tweak features of [LootValuePlus](https://github.com/rp-an-87/LootValuePlus) by rp-an-87, split into its own mod.

## Options (`config.json`)

| Key | Effect |
|---|---|
| `extracts.chances.enabled` / `.locations` | Sets the appearance chance (0-100) of every variable extract, per map (`bigmap`, `interchange`, `laboratory`, `lighthouse`, `shoreline`, `tarkovstreets`, `woods`, ...) |
| `extracts.coop.enabled` / `.amount` | Turns scav-cooperation extracts into paid extracts costing `amount` euros |
| `btr.enabled` / `.locations.<Map>` | Per map (`Woods`, `TarkovStreets`, ...): spawn `chance`, `stopDuration` and `spawnTime` (`min`/`max`, seconds) |
| `containers.allowItems.thiccItemCase` / `.itemCase` | Which cases get extra allowed item groups |
| `containers.allowItems.items.secureContainers` / `.ammoBoxes` / `.specials` | Which item groups are added to those cases |
| `pockets.enabled` / `.specialSlots` / `.specialSlotsUnheard` | Total special slots on the pockets, for standard editions and for Unheard Edition (3 = vanilla, max 6). Extra slots copy the item filters of SpecialSlot3 |
| `traderRestock.enabled` / `.minSeconds` / `.maxSeconds` / `.excludedTraders` | Caps every trader's restock interval to at most `minSeconds`-`maxSeconds` (a trader that is already faster keeps its timer). Fence and Ref are excluded by default |

A rebuild never overwrites an existing `config.json` in the mod folder.

## Build

Set `SPTPath` in `CustomRpTweaks.csproj.user` (your `SPT_Runtime` folder), then `dotnet build`. The DLL is copied to `user/mods/CustomRpTweaks`.

## License

MIT, see [LICENSE](LICENSE).
