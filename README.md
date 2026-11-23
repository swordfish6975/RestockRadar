# RestockRadar

Watches Australian retailers' PS5 product pages in a real Chrome browser and messages you on **Telegram** when one
comes into stock, with a screenshot of the product page. It can also **buy from Amazon automatically**, with price,
seller and right-console checks.

```
2026-11-23 15:51:02 INFO  Mode: PS5 Digital. Watching jbhifi-digital, goodguys-digital
2026-11-23 15:51:02 INFO  [jbhifi-digital] IN STOCK
2026-11-23 15:51:02 ALERT 🟢 PS5 Digital looks IN STOCK at jbhifi-digital!
```

**Contents:** [Quick start](#quick-start) · [Choosing a console](#choosing-a-console) · [Sites](#sites) ·
[Adding a site](#adding-a-site) · [Amazon auto-buy](#amazon-auto-buy) · [Running it](#running-it) ·
[How it decides](#how-it-decides) · [Files](#files) · [Configuration reference](#configuration-reference) ·
[Troubleshooting](#troubleshooting)

---

## Quick start

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Google Chrome. If Chrome isn't installed,
a copy is downloaded on the first run.

**1. Make a Telegram bot.** In Telegram, message **@BotFather**, send `/newbot` and copy the token it gives you.
Then send your new bot any message, so it knows where to reach you.

**2. Put your secrets in `RestockRadar/appsettings.local.json`.** This file is git-ignored and overrides
`appsettings.json`, which keeps only `YOUR_...` placeholders. Create it with:

```json
{
  "Telegram": {
    "BotToken": "123456789:AA...your token...",
    "ChatId": ""
  }
}
```

Leave `ChatId` empty and the app uses whoever last messaged the bot. It then logs the id, so you can paste it
in and make it permanent.

**3. Check alerts work:**

```
dotnet run --project RestockRadar -- --test-alert
```

**4. Pick your console** in `appsettings.json` (`Monitor:Product`, see below), then start watching:

```
dotnet run --project RestockRadar
```

---

## Choosing a console

Set `Monitor:Product` in `appsettings.json`, or override it for one run with `--product`.

| Mode | Hunts for |
|---|---|
| `PS5Disc` | standard PS5 (Slim) **with** a disc drive |
| `PS5Digital` | PS5 (Slim) Digital Edition, no disc drive |
| `PS5Pro` | PS5 Pro (only sold digital; the disc drive is a separate add-on) |
| `PS5` | either standard PS5, disc or digital |
| `All` | everything (`Both` still works as an old name for this) |

Modes combine: `"Product": "PS5Disc, PS5Pro"` in config, or `--product ps5disc,ps5pro` on the command line.
Each site is tagged with the console it sells, and only sites matching the mode are checked.

---

## Sites

The sites set up in `appsettings.json` right now:

| Name | Console | Retailer | Notes |
|---|---|---|---|
| `amazon-disc` | PS5Disc | Amazon AU | Original (pre-Slim) disc listing; Amazon says it may not return. Auto-buy on. |
| `jbhifi-disc` | PS5Disc | JB Hi-Fi | PS5 Slim (disc) |
| `jbhifi-digital` | PS5Digital | JB Hi-Fi | PS5 Slim Digital 825GB |
| `goodguys-digital` | PS5Digital | The Good Guys | Digital Edition Slim |
| `amazon-pro` | PS5Pro | Amazon AU | Auto-buy on, max $1,400 |
| `jbhifi-pro` | PS5Pro | JB Hi-Fi | |
| `target-pro` | PS5Pro | Target | |
| `sony-pro` | PS5Pro | Sony Store AU | |
| `harveynorman-pro` | PS5Pro | Harvey Norman | |
| `ebgames-pro` | PS5Pro | EB Games | Only sold as a bundle (2nd controller + 2yr EB Protect) |
| `kmart-pro` | PS5Pro | Kmart | **Disabled.** Kmart answers automated browsers with HTTP 429. |

Retailers checked but not added, as of Nov 2026:
- **Big W, Officeworks, Bing Lee:** don't list a new PS5 Pro.
- **The Good Guys:** sells only the Digital Edition (no disc or Pro).
- **Mighty Ape:** its search page returned "not found".

To pause a site without deleting it, add `"Enabled": false`.

---

## Adding a site

1. Add an entry to `Sites` in `appsettings.json`:

   ```json
   {
     "Name": "bigw-pro",
     "Product": "PS5Pro",
     "Url": "https://www.example.com.au/product/playstation-5-pro-console",
     "Selector": "",
     "InStockText": [ "Add to cart" ],
     "OutOfStockText": [ "Out of stock", "Sold out", "unavailable" ]
   }
   ```

2. Probe it. This loads the page once, sends no alerts and saves nothing to the state file:

   ```
   dotnet run --project RestockRadar -- --probe bigw-pro
   ```

   It prints:
   - the text it read
   - the stock verdict
   - a screenshot path
   - **where each stock word sits in the page**, as a CSS path

3. Set `Selector` to the buy box from that output, so reviews and "you might also like" carousels can't
   trigger false matches. A few examples:

   | Retailer | Selector |
   |---|---|
   | JB Hi-Fi | `#pdp-right-panel` |
   | Target | `[data-testid='controls-details-panel']` |
   | EB Games | `.product-actions` |

   Use `RemoveSelectors` to strip noisy bits inside it.

4. Probe again until the verdict is right.

**Tips**
- **Prefer out-of-stock words.** They win over in-stock words. A greyed-out (disabled) "Add to cart" button
  never counts as in stock: the app reads it as `Unavailable: [Add to cart]`.
- **The probe's text output stops at 4,000 characters.** A tighter `Selector` helps here too.
- **Testing a URL without editing the config:** use environment variables:
  `WC_Sites__90__Name=tmp WC_Sites__90__Url=https://... WC_Sites__90__Product=All`, then `--probe tmp`.

---

## Amazon auto-buy

When an Amazon site with `"AutoBuy": true` comes into stock, the app buys it in its own browser, in this order:

1. **Signed in?** It uses the saved session. If that has expired, it can sign in with optional saved
   credentials (see [Configuration reference](#configuration-reference)), but `--login` is the better route.
2. **Right console?** The listing title must match both the site's tag and your current mode:
   - title says "Pro" (whole word) → PS5 Pro
   - otherwise "Digital" → Digital Edition
   - otherwise → disc

   So a Digital Edition is never bought in `PS5Disc` mode, and a Pro never in `PS5` mode.
3. **Price** must be at or under the site's `MaxPrice` (or `Amazon:MaxPrice` if the site doesn't set one).
4. **Seller** must be Amazon itself, not a marketplace reseller (`RequireSoldByAmazon`).
5. **Checkout:** Buy Now, or Add to Cart → checkout, then **Place your order**.
6. **Only once:** after a successful order, `PurchasedAt` is recorded and it won't buy again.

You get a Telegram message with a screenshot whatever happens: bought, dry run, or skipped and why.

**Setting it up**

1. Run `dotnet run --project RestockRadar -- --login`. A browser opens on Amazon.
2. Sign in by hand, tick *Keep me signed in*, then press Enter in the console. The session is saved in
   `browser-profile/`.
3. Set `"Amazon": { "AutoBuy": true }`. **Keep `"DryRun": true`** at first. It goes all the way to
   *Place your order* and stops there.
4. Once a dry run reaches that button, set `"DryRun": false`.

To buy again after a successful order, clear `PurchasedAt` for that site in `data/state.json`.

---

## Running it

```
dotnet run --project RestockRadar                              # watch forever
dotnet run --project RestockRadar -- --once                    # one pass, then exit
dotnet run --project RestockRadar -- --product ps5disc,ps5pro  # override the mode for this run
dotnet run --project RestockRadar -- --site target-pro         # only this site (repeatable)
dotnet run --project RestockRadar -- --probe target-pro        # inspect one site, no alerts
dotnet run --project RestockRadar -- --login                   # sign in to Amazon once
dotnet run --project RestockRadar -- --test-alert              # send a test Telegram message
```

- **Watch mode** checks every site, then waits `IntervalSeconds` plus a random `0..JitterSeconds` and repeats.
- **Stopping:** Ctrl+C finishes the current check, then stops.
- **Task Scheduler:** schedule `RestockRadar.exe --once` every few minutes instead of leaving it running.
- **Only one copy at a time.** Every run shares `browser-profile/`, so a second copy hangs while the first holds it.
  The same goes for a Chrome window left over from a run that was killed.

---

## How it decides

**Stock mode** (a site has `InStockText` and/or `OutOfStockText`):

1. **Out-of-stock word present** → out of stock. These always win.
2. Otherwise, **in-stock word present** (ignoring disabled buttons) → in stock.
3. Otherwise → out of stock. If the site has only `OutOfStockText`, "no out-of-stock word" counts as in stock.

Alerts:
- 🟢 when a site goes from not-in-stock to in stock. **This includes a site's very first check**, so a new site
  that's already in stock alerts straight away.
- 🔴 once when it sells out again.
- Nothing while it stays the same.

**Change mode** (both keyword lists empty): the first check saves a baseline. After that, any change in the
watched text sends an alert with a short diff and a screenshot.

**Failures** (bot wall, HTTP 4xx/5xx, selector not found):
- ⚠️ once after `AlertAfterFailures` failed checks in a row
- ✅ when the site recovers

Bot walls are spotted by phrases like "Robot Check", "Pardon Our Interruption" and "Access Denied".

The last result for each site is saved after every check, so restarting doesn't repeat alerts.

---

## Files

Everything the app writes lives next to the exe (`RestockRadar/bin/Debug/net10.0/` when using `dotnet run`):

| Path | What |
|---|---|
| `data/state.json` | Last text, stock status, failure count and `PurchasedAt` for each site, keyed by site `Name` |
| `data/<site>/latest.png`, `previous.png` | Last two screenshots of each site: the 1280×900 browser view, scrolled so the watched area is near the top |
| `browser-profile/` | The app's own Chrome profile (cookies, Amazon sign-in). Separate from your everyday Chrome. |
| `restockradar.log` | Every log line |

Things to know:
- A Release build or `dotnet publish` runs from a different folder, so it starts with fresh state and needs
  `--login` again. Deleting `bin/` wipes all of it, including `PurchasedAt`.
- Renaming a site gives it fresh state.
- To reset one site, delete its entry from `state.json` while the app is stopped.

---

## Configuration reference

Settings are read in this order, later ones winning:
1. `appsettings.json`
2. `appsettings.local.json`
3. environment variables prefixed `WC_`, with `__` between levels (e.g. `WC_Telegram__BotToken`)

**`Monitor`**

| Key | Default | |
|---|---|---|
| `Product` | `PS5` | Which console(s) to hunt for. See [Choosing a console](#choosing-a-console). |
| `IntervalSeconds` / `JitterSeconds` | 120 / 30 | Wait between passes, plus up to this much random extra |
| `Headless` | false | Hide the browser. Some retailers block headless browsers more. |
| `ChromePath` | auto | Path to `chrome.exe` if it's somewhere unusual |
| `AlertAfterFailures` | 5 | Failed checks in a row before a ⚠️ alert |
| `BeepOnAlert` | true | Beep the PC speaker on every alert |
| `PageTimeoutSeconds` | 45 | Page load timeout |
| `ViewportWidth` / `ViewportHeight` | 1280 / 900 | Browser window size |
| `DataDir` / `ProfileDir` | `data` / `browser-profile` | Where state and the browser profile go |

**`Telegram`**

| Key | |
|---|---|
| `BotToken` | From @BotFather. With no token, alerts only show in the console. |
| `ChatId` | Empty = whoever last messaged the bot |

**`Amazon`**

| Key | Default | |
|---|---|---|
| `AutoBuy` | false | Master switch for auto-buy |
| `DryRun` | true | Stop at *Place your order* |
| `MaxPrice` | 0 | Price limit for sites without their own `MaxPrice`. 0 = no limit. |
| `RequireSoldByAmazon` | true | Skip marketplace sellers |
| `BaseUrl` | `https://www.amazon.com.au` | |
| `Email`, `Password`, `TotpSecret` | | Optional fallback sign-in if the saved session expires. Put them in `appsettings.local.json` only, never in `appsettings.json`. |

**`Sites[]`**

| Key | |
|---|---|
| `Name` | Unique name. Used in alerts, `--site` / `--probe`, and as the state key. |
| `Product` | `PS5Disc`, `PS5Digital`, `PS5Pro`. Use `PS5` if the page could be either edition, `All` for a general page. |
| `Url` | Product page |
| `Selector` | CSS selector(s) for the buy box. Blank = whole page. |
| `RemoveSelectors` | Elements to strip before reading (banners, carousels) |
| `InStockText` / `OutOfStockText` | Keywords, not case-sensitive. Both empty = change mode. |
| `Enabled` | `false` to skip the site |
| `AutoBuy`, `MaxPrice` | Amazon sites only |

---

## Troubleshooting

| Symptom | What to do |
|---|---|
| False "in stock" | `--probe` the site. Usually an in-stock word appears somewhere outside the buy box: tighten `Selector`, or add the retailer's out-of-stock wording to `OutOfStockText`. |
| Never says in stock when it is | The retailer's button wording differs (e.g. "Add To Basket"). `--probe` shows the exact text. |
| ⚠️ "failed N checks in a row" | `--probe` saves `probe-error.png` showing what the browser got. Bot walls often clear with `Headless: false` or a longer interval. If the layout changed, update `Selector`. |
| Run hangs at start | Another copy, or a leftover Chrome window from a killed run, is holding `browser-profile/`. Close it. |
| No Telegram messages | Run `--test-alert`. Check the token is in `appsettings.local.json`, and that you've messaged the bot at least once. |
| Amazon "not signed in" | Run `--login` again. |

---

## License

[MIT](LICENSE)
