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
| `traderOverstock.enabled` / `.maxRounds` / `.minIncreasePercent` / `.maxIncreasePercent` / `.clientIndicator` / `.excludedTraders` | Trader offers you can keep buying: see "Trader overstock" below. `maxRounds` is the number of extra rounds per offer between restocks (0 = no cap), the two percents are the range of each round's random price increase, `clientIndicator` lets the client plugin mark raised prices. Fence is never touched |
| `waterFilter.enabled` | Makes the water filter drain match the recipe SVM sets (`WaterFilterRate` resource over `WaterFilterTime` minutes). The server drains the filter at a fixed rate per second of crafting time, so a changed time or rate had little effect. With this on, the drain per craft follows the recipe. Work in progress: the drain isn't exact yet |

A rebuild never overwrites an existing `config.json` in the mod folder.

## Trader overstock

Most trader offers have unlimited stock and a personal purchase limit per restock ("Limit 5/5"). With this feature, using up that limit (or selling out an offer that has finite stock) starts a new round: your counter on the offer goes back to 0 and the price rises by a random percent between `minIncreasePercent` and `maxIncreasePercent`, on top of the earlier rounds. An offer can do that `maxRounds` times; when the trader really restocks, prices and rounds go back to normal.

- It is per profile. The raised prices are applied to what each profile is sent (the trader's offers and the same offers in the flea market search results), never to the shared trader data, and selling prices are not affected. Offers whose limit was already used up when the offers load (for example after a server restart) start their next round then.
- Prices: roubles are whole numbers, dollars and euros keep cents, barter items are rounded up (at least 1).
- The rounds are kept in memory only. A server restart forgets them, so prices go back to normal while the limit counters (kept in the profile) stay.
- Finite stock is the same for every profile, so the few offers that have it are refilled for everyone.
- The server charges what the client sends, so the price you see is the price you pay.
- The methods involved (`TradeHelper.BuyItem`, `TraderAssortHelper.GetAssort` and `ResetExpiredTrader`, the flea `RagfairController.GetOffers`) aren't virtual, so they are patched with Harmony (the server's own `0Harmony.dll`, referenced from `SPTPath` when building).
- The route `/customrptweaks/overstock/prices` lists the raised offers of the calling profile for the [client plugin](https://github.com/rp-an-87/CustomRpTweaks-Client), which colours them. It answers "disabled" when the feature or `clientIndicator` is off.

## Web page

The mod has its own page in the server's web UI (card "CustomRpTweaks" under Mod pages, or `/custom-rp-tweaks`, admin only) with every option above grouped in sections. The tweaks are applied once at server start, so saved changes take effect on the next start. The same `config.json` is also listed in the generic config editor (`/configs`).

## Build

Set `SPTPath` in `CustomRpTweaks.csproj.user` (your `SPT_Runtime` folder), then `dotnet build`. The DLL is copied to `user/mods/CustomRpTweaks`.

## License

MIT, see [LICENSE](LICENSE).
