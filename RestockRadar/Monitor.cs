using PuppeteerSharp;

namespace RestockRadar
{
    class Monitor
    {
        private readonly AppConfig _config;
        private readonly IBrowser _browser;
        private readonly Notifier _notifier;
        private readonly SiteChecker _checker;
        private readonly StateStore _state;
        private readonly string _dataDir;

        public Monitor(AppConfig config, IBrowser browser, Notifier notifier)
        {
            _config = config;
            _browser = browser;
            _notifier = notifier;
            _checker = new SiteChecker(config.Monitor);
            _dataDir = Path.Combine(AppContext.BaseDirectory, config.Monitor.DataDir);
            _state = new StateStore(_dataDir);
        }

        public async Task CheckAsync(SiteOptions site)
        {
            var state = _state.Get(site.Name);
            await using var page = await _browser.NewPageAsync();
            try
            {
                var result = await _checker.CheckAsync(page, site);
                SaveScreenshot(site, result.Screenshot);
                await HandleResultAsync(site, state, result);
            }
            catch (Exception ex)
            {
                await HandleFailureAsync(site, state, ex);
            }
            finally
            {
                state.LastChecked = DateTime.Now;
                _state.Save();
            }
        }

        public async Task ProbeAsync(SiteOptions site)
        {
            await using var page = await _browser.NewPageAsync();
            try
            {
                var result = await _checker.CheckAsync(page, site);
                var path = SaveScreenshot(site, result.Screenshot);
                Console.WriteLine($"--- Text seen for '{site.Name}' (selector: {site.Selector ?? "<whole page>"}) ---");
                Console.WriteLine(result.Text.Length > 4000 ? result.Text[..4000] + "\n…(truncated)" : result.Text);
                Console.WriteLine("---");
                Console.WriteLine($"Stock verdict: {result.InStock?.ToString() ?? "n/a (change-detection mode)"}");
                if (path != null) Console.WriteLine($"Screenshot: {path}");

                // Show where stock words live in the page, to help pick a Selector that covers just the buy box.
                var words = site.InStockText.Concat(site.OutOfStockText)
                    .Concat(new[] { "Add to cart", "Add to basket", "Out of stock", "Sold out", "unavailable", "Notify me" })
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var hits = await page.EvaluateFunctionAsync<string[]>(@"words => {
                    const path = el => {
                        const parts = [];
                        for (; el && el !== document.body && parts.length < 6; el = el.parentElement) {
                            let p = el.tagName.toLowerCase();
                            if (el.id) p += '#' + el.id;
                            const cls = [...el.classList].slice(0, 2).join('.');
                            if (cls) p += '.' + cls;
                            for (const a of ['data-testid', 'data-test']) if (el.getAttribute(a)) p += `[${a}='${el.getAttribute(a)}']`;
                            parts.unshift(p);
                        }
                        return parts.join(' > ');
                    };
                    const out = [];
                    const walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
                    for (let n; (n = walker.nextNode()) && out.length < 25;) {
                        const t = n.textContent.trim();
                        const w = words.find(w => t.toLowerCase().includes(w.toLowerCase()));
                        if (w && t.length < 200 && n.parentElement.offsetParent !== null) out.push(`""${t.slice(0, 60)}""  ->  ${path(n.parentElement)}`);
                    }
                    return out;
                }", (object)words);
                if (hits.Length > 0)
                {
                    Console.WriteLine("--- Where stock words appear (visible elements) ---");
                    foreach (var h in hits) Console.WriteLine(h);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[{site.Name}] {ex.Message}");
                var path = Path.Combine(_dataDir, site.Name, "probe-error.png");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await page.ScreenshotAsync(path);
                Console.WriteLine($"Screenshot of what the browser saw: {path}");
            }
        }

        private async Task HandleResultAsync(SiteOptions site, SiteState state, CheckResult result)
        {
            if (state.FailureAlertSent)
                await _notifier.SendAsync($"✅ {site.Name} is checking OK again.");
            state.ConsecutiveFailures = 0;
            state.FailureAlertSent = false;

            var previousText = state.LastText;
            var wasInStock = state.InStock;
            state.LastText = result.Text;
            state.InStock = result.InStock;

            if (result.InStock is bool inStock)
            {
                Log.Info($"[{site.Name}] {(inStock ? "IN STOCK" : "out of stock")}");
                if (inStock && wasInStock != true)
                {
                    state.LastChanged = DateTime.Now;
                    await _notifier.SendAsync($"🟢 {site.Product.DisplayName()} looks IN STOCK at {site.Name}!\n{site.Url}", result.Screenshot);
                    if (site.AutoBuy)
                        await TryBuyAsync(site, state);
                }
                else if (!inStock && wasInStock == true)
                {
                    state.LastChanged = DateTime.Now;
                    await _notifier.SendAsync($"🔴 {site.Product.DisplayName()} is out of stock again at {site.Name}.");
                }
                return;
            }

            // No stock keywords configured: alert on any change, like the old screenshot diff did.
            if (previousText == null)
            {
                Log.Info($"[{site.Name}] first check, baseline saved");
            }
            else if (previousText != result.Text)
            {
                state.LastChanged = DateTime.Now;
                await _notifier.SendAsync($"🔔 {site.Name} changed\n{site.Url}\n\n{Diff(previousText, result.Text)}", result.Screenshot);
            }
            else
            {
                Log.Info($"[{site.Name}] no change");
            }
        }

        private async Task HandleFailureAsync(SiteOptions site, SiteState state, Exception ex)
        {
            state.ConsecutiveFailures++;
            Log.Warn($"[{site.Name}] check failed ({state.ConsecutiveFailures} in a row): {ex.Message}");

            if (state.ConsecutiveFailures >= _config.Monitor.AlertAfterFailures && !state.FailureAlertSent)
            {
                state.FailureAlertSent = true;
                await _notifier.SendAsync($"⚠️ {site.Name} has failed {state.ConsecutiveFailures} checks in a row: {ex.Message}\nRun --probe {site.Name} to see what's going on.");
            }
        }

        private async Task TryBuyAsync(SiteOptions site, SiteState state)
        {
            var amazon = _config.Amazon;
            if (!amazon.AutoBuy)
            {
                Log.Info($"[{site.Name}] auto-buy is off (Amazon:AutoBuy=false)");
                return;
            }
            if (state.PurchasedAt != null)
            {
                Log.Info($"[{site.Name}] already bought one on {state.PurchasedAt:d} - not buying again (clear PurchasedAt in data/state.json to re-arm)");
                return;
            }

            await using var page = await _browser.NewPageAsync();
            try
            {
                var result = await new AmazonBuyer(amazon).BuyAsync(page, site, site.Product & _config.Monitor.Product);
                if (result.Success) state.PurchasedAt = DateTime.Now;
                await _notifier.SendAsync(result.Success ? $"🎉 {result.Message}" : result.Message, result.Screenshot);
            }
            catch (Exception ex)
            {
                await _notifier.SendAsync($"Amazon auto-buy failed: {ex.Message}", await TryScreenshot(page));
            }
        }

        private string? SaveScreenshot(SiteOptions site, byte[]? png)
        {
            if (png == null) return null;
            var dir = Path.Combine(_dataDir, site.Name);
            Directory.CreateDirectory(dir);
            var latest = Path.Combine(dir, "latest.png");
            if (File.Exists(latest)) File.Copy(latest, Path.Combine(dir, "previous.png"), true);
            File.WriteAllBytes(latest, png);
            return latest;
        }

        private static async Task<byte[]?> TryScreenshot(IPage page)
        {
            try { return await page.ScreenshotDataAsync(); }
            catch { return null; }
        }

        // Crude line diff - enough to see "Sold out" turn into "Add to cart" in a Telegram message.
        private static string Diff(string before, string after)
        {
            var oldLines = before.Split('\n').ToHashSet();
            var newLines = after.Split('\n').ToHashSet();
            var removed = before.Split('\n').Where(l => !newLines.Contains(l)).Take(8).Select(l => "− " + l);
            var added = after.Split('\n').Where(l => !oldLines.Contains(l)).Take(8).Select(l => "+ " + l);
            var diff = string.Join("\n", removed.Concat(added));
            return diff.Length > 0 ? diff : "(line order changed)";
        }
    }
}
