using PuppeteerSharp;

namespace RestockRadar
{
    record CheckResult(string Text, bool? InStock, byte[]? Screenshot);

    class CheckFailedException(string message) : Exception(message);

    class SiteChecker
    {
        // Signs we've been served a bot wall instead of the real page.
        private static readonly string[] BlockedMarkers =
        {
            "Enter the characters you see below", "Robot Check", "Just a moment...", "Access Denied",
            "Pardon Our Interruption", "Are you a human", "unusual traffic", "Server Busy",
        };

        // Disabled buttons are read as "Unavailable: [label]" so their label never counts as in stock.
        public const string DisabledPrefix = "Unavailable: ";
        private static readonly System.Text.RegularExpressions.Regex DisabledButton =
            new(System.Text.RegularExpressions.Regex.Escape(DisabledPrefix) + @"\[[^\]]*\]");

        private readonly MonitorOptions _options;

        public SiteChecker(MonitorOptions options) => _options = options;

        public async Task<CheckResult> CheckAsync(IPage page, SiteOptions site)
        {
            var response = await page.GoToAsync(site.Url, new NavigationOptions
            {
                WaitUntil = new[] { WaitUntilNavigation.DOMContentLoaded },
                Timeout = _options.PageTimeoutSeconds * 1000,
            });

            if (response != null && (int)response.Status >= 400)
                throw new CheckFailedException($"HTTP {(int)response.Status} from {site.Url}");

            if (!string.IsNullOrWhiteSpace(site.Selector))
            {
                try { await page.WaitForSelectorAsync(site.Selector, new WaitForSelectorOptions { Timeout = 15000 }); }
                catch (WaitTaskTimeoutException) { /* reported below, after the bot-wall check */ }
            }
            // Let late scripts (stock widgets, price loaders) settle.
            await Task.Delay(2000);

            var bodyText = await page.EvaluateExpressionAsync<string>("document.body ? document.body.innerText : ''");
            var title = await page.GetTitleAsync();
            var blocked = BlockedMarkers.FirstOrDefault(m =>
                title.Contains(m, StringComparison.OrdinalIgnoreCase) ||
                (bodyText.Length < 5000 && bodyText.Contains(m, StringComparison.OrdinalIgnoreCase)));
            if (blocked != null)
                throw new CheckFailedException($"Looks like a bot check / error page (\"{blocked}\")");

            var text = await page.EvaluateFunctionAsync<string?>(@"(sel, removes) => {
                for (const r of removes) document.querySelectorAll(r).forEach(e => e.remove());
                let els = sel ? [...document.querySelectorAll(sel)] : [document.body];
                // Overlapping selectors (a box and something inside it) shouldn't count the text twice.
                els = els.filter(e => !els.some(o => o !== e && o.contains(e)));
                if (els.length === 0) return null;
                // A greyed-out 'Add to cart' still has its label, so it would look in stock. While reading,
                // swap each disabled control's content for 'Unavailable: [label]', then put it back so the
                // screenshot shows the real page.
                const disabled = [...document.querySelectorAll(
                    'button:disabled, [aria-disabled=""true""], fieldset:disabled button')];
                const saved = disabled.map(d => {
                    const label = d.innerText.replace(/\s+/g, ' ').trim();
                    const kids = [...d.childNodes];
                    d.textContent = label ? '" + DisabledPrefix + @"[' + label + ']' : '';
                    return () => d.replaceChildren(...kids);
                });
                try {
                    return els.map(e => e.innerText).join('\n');
                } finally {
                    saved.forEach(restore => restore());
                }
            }", site.Selector ?? "", site.RemoveSelectors);

            if (text == null)
                throw new CheckFailedException($"Selector '{site.Selector}' not found - the page layout may have changed");

            text = Normalise(text);
            return new CheckResult(text, EvaluateStock(site, text), await ScreenshotAsync(page, site));
        }

        public static bool? EvaluateStock(SiteOptions site, string text)
        {
            if (site.InStockText.Count == 0 && site.OutOfStockText.Count == 0)
                return null;

            if (site.OutOfStockText.Any(t => text.Contains(t, StringComparison.OrdinalIgnoreCase)))
                return false;

            // A disabled button (e.g. a greyed-out "Add to cart") is not a sign of stock.
            var enabledText = DisabledButton.Replace(text, "");
            return site.InStockText.Count == 0 || site.InStockText.Any(t => enabledText.Contains(t, StringComparison.OrdinalIgnoreCase));
        }

        private static async Task<byte[]?> ScreenshotAsync(IPage page, SiteOptions site)
        {
            try
            {
                // Shoot the whole browser view (a normal landscape desktop shot), with the watched element
                // scrolled near the top. Shooting just the element gave skinny strips - a buy-box column is
                // ~450px wide and thousands tall, which Telegram shrinks to look like a phone screenshot.
                if (!string.IsNullOrWhiteSpace(site.Selector))
                {
                    await page.EvaluateFunctionAsync(@"sel => {
                        const el = document.querySelector(sel);
                        if (!el) return;
                        // Leave room above for sticky site headers, which would otherwise cover the top of it.
                        window.scrollBy(0, el.getBoundingClientRect().top - 150);
                    }", site.Selector);
                    await Task.Delay(500); // lazy images and sticky headers settle after scrolling
                }
                return await page.ScreenshotDataAsync();
            }
            catch (Exception ex)
            {
                Log.Warn($"[{site.Name}] screenshot failed: {ex.Message}");
                return null;
            }
        }

        private static string Normalise(string text) =>
            string.Join("\n", text.Split('\n')
                .Select(l => string.Join(' ', l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
                .Where(l => l.Length > 0));
    }
}
