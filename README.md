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
| `insurance.enabled` / `.disableKeys` / `.disableKeycards` | Makes mechanical keys (and optionally keycards) uninsurable, which also drops them from "insure all" |
| `stacks.enabled` / `.items` | Sets the max stack size per item template id. Default entry: GP coin `5d235b4d86f7742e017bc88a` = 5000 (vanilla 100) |
| `bossGpCoins.enabled` / `.boss` / `.follower` | Bosses and followers carry a GP coin stack with `chance` percent (default 25%), `min` to `max` coins (bosses 10-25, followers 1-5). Replaces the Bosses Have GP Coins mod, which doesn't work with Progressive Bot System, so disable that mod. Applied at server start |

A rebuild never overwrites an existing `config.json` in the mod folder.

## Web page

The mod has its own page in the server's web UI (card "CustomRpTweaks" under Mod pages, or `/custom-rp-tweaks`, admin only) with every option above grouped in sections. The tweaks are applied once at server start, so saved changes take effect on the next start. The same `config.json` is also listed in the generic config editor (`/configs`).

## Build

Set `SPTPath` in `CustomRpTweaks.csproj.user` (your `SPT_Runtime` folder), then `dotnet build`. The DLL is copied to `user/mods/CustomRpTweaks`.

## License

MIT, see [LICENSE](LICENSE).
