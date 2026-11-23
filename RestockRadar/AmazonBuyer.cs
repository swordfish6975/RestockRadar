using System.Globalization;
using System.Text.RegularExpressions;
using OtpNet;
using PuppeteerSharp;

namespace RestockRadar
{
    record BuyResult(bool Success, string Message, byte[]? Screenshot = null);

    class AmazonBuyer
    {
        private static readonly string[] PlaceOrderSelectors =
        {
            "input[name='placeYourOrder1']", "#placeYourOrder input", "#submitOrderButtonId input",
            "#bottomSubmitOrderButtonId input", "#placeOrder",
        };

        private readonly AmazonOptions _options;

        public AmazonBuyer(AmazonOptions options) => _options = options;

        public async Task<bool> IsSignedInAsync(IPage page)
        {
            await page.GoToAsync(_options.BaseUrl, WaitUntilNavigation.DOMContentLoaded);
            var greeting = await page.EvaluateExpressionAsync<string>(
                "(document.querySelector('#nav-link-accountList') || {}).innerText || ''");
            return greeting.Length > 0 && !greeting.Contains("sign in", StringComparison.OrdinalIgnoreCase);
        }

        // wanted = the console(s) we're allowed to buy here: what the site sells AND what the current mode hunts for.
        public async Task<BuyResult> BuyAsync(IPage page, SiteOptions site, ProductMode wanted)
        {
            if (!await IsSignedInAsync(page) && !await SignInAsync(page))
                return new BuyResult(false, "Amazon: not signed in. Run with --login to sign in once.", await page.ScreenshotDataAsync());

            await page.GoToAsync(site.Url, WaitUntilNavigation.DOMContentLoaded);
            await page.WaitForSelectorAsync("#dp-container, #ppd", new WaitForSelectorOptions { Timeout = 15000 });

            var title = (await page.EvaluateExpressionAsync<string>("(document.querySelector('#productTitle') || {}).innerText || ''")).Trim();
            if (!TitleMatches(title, wanted))
                return new BuyResult(false, $"Amazon: listing \"{title}\" doesn't look like a {wanted.DisplayName()} console - not buying.", await page.ScreenshotDataAsync());

            var maxPrice = site.MaxPrice > 0 ? site.MaxPrice : _options.MaxPrice;
            var price = await ReadPriceAsync(page);
            if (maxPrice > 0 && (price == null || price > maxPrice))
                return new BuyResult(false, $"Amazon: price {price?.ToString("C") ?? "unknown"} is over the limit of {maxPrice:C} - not buying.", await page.ScreenshotDataAsync());

            if (_options.RequireSoldByAmazon && !await SoldByAmazonAsync(page))
                return new BuyResult(false, "Amazon: listing isn't sold by Amazon (marketplace seller) - not buying.", await page.ScreenshotDataAsync());

            if (await page.QuerySelectorAsync("#buy-now-button") != null)
            {
                await page.ClickAsync("#buy-now-button");
            }
            else if (await page.QuerySelectorAsync("#add-to-cart-button") != null)
            {
                await page.ClickAsync("#add-to-cart-button");
                await Task.Delay(2000);
                await page.GoToAsync($"{_options.BaseUrl}/gp/cart/view.html", WaitUntilNavigation.DOMContentLoaded);
                await page.ClickAsync("input[name='proceedToRetailCheckout']");
            }
            else
            {
                return new BuyResult(false, "Amazon: no Buy Now / Add to Cart button on the page.", await page.ScreenshotDataAsync());
            }

            // Buy Now either opens the "turbo checkout" pop-up or goes to the full checkout page.
            var placeOrder = await FindPlaceOrderButtonAsync(page, TimeSpan.FromSeconds(20));
            if (placeOrder == null)
                return new BuyResult(false, "Amazon: couldn't find the 'Place your order' button.", await page.ScreenshotDataAsync());

            if (_options.DryRun)
                return new BuyResult(false, $"Amazon DRY RUN: got to 'Place your order' at {price?.ToString("C") ?? "unknown price"}. Set Amazon:DryRun=false to buy for real.", await page.ScreenshotDataAsync());

            await placeOrder.ClickAsync();
            await Task.Delay(5000);

            var body = await page.EvaluateExpressionAsync<string>("document.body.innerText");
            var confirmed = body.Contains("Order placed", StringComparison.OrdinalIgnoreCase) ||
                            body.Contains("Thank you", StringComparison.OrdinalIgnoreCase);
            return new BuyResult(confirmed,
                confirmed ? $"Amazon: ORDER PLACED at {price?.ToString("C") ?? "unknown price"}!" : "Amazon: clicked 'Place your order' but didn't see a confirmation - check your orders.",
                await page.ScreenshotDataAsync());
        }

        // Fallback for when the saved session has expired. Needs Email/Password(/TotpSecret) configured.
        private async Task<bool> SignInAsync(IPage page)
        {
            if (string.IsNullOrWhiteSpace(_options.Email) || string.IsNullOrWhiteSpace(_options.Password))
                return false;

            Log.Info("Amazon: session expired, signing in...");
            await page.ClickAsync("#nav-link-accountList");
            await page.WaitForSelectorAsync("#ap_email, input[name='email']");
            await page.TypeAsync("#ap_email, input[name='email']", _options.Email, new() { Delay = 40 });
            if (await page.QuerySelectorAsync("#continue") != null)
                await page.ClickAsync("#continue");

            await page.WaitForSelectorAsync("#ap_password");
            await page.TypeAsync("#ap_password", _options.Password, new() { Delay = 40 });
            if (await page.QuerySelectorAsync("input[name='rememberMe']") is { } remember)
                await remember.ClickAsync();
            await page.ClickAsync("#signInSubmit");

            if (!string.IsNullOrWhiteSpace(_options.TotpSecret))
            {
                await page.WaitForSelectorAsync("#auth-mfa-otpcode", new WaitForSelectorOptions { Timeout = 15000 });
                var totp = new Totp(Base32Encoding.ToBytes(_options.TotpSecret.Replace(" ", "")));
                if (totp.RemainingSeconds() < 3)
                    await Task.Delay(3500);
                await page.TypeAsync("#auth-mfa-otpcode", totp.ComputeTotp());
                if (await page.QuerySelectorAsync("#auth-mfa-remember-device") is { } rememberDevice)
                    await rememberDevice.ClickAsync();
                await page.ClickAsync("#auth-signin-button");
            }

            await Task.Delay(3000);
            return await IsSignedInAsync(page);
        }

        // Guards against a listing that has been swapped for a bundle, accessory or a different console.
        public static bool TitleMatches(string title, ProductMode wanted)
        {
            var kind = ClassifyTitle(title);
            return kind != ProductMode.None && (wanted & kind) != 0;
        }

        // Which console a listing title describes, or None if it isn't a PS5 console.
        public static ProductMode ClassifyTitle(string title)
        {
            var isConsole = Regex.IsMatch(title, @"PlayStation\W*5|PS5", RegexOptions.IgnoreCase) &&
                            title.Contains("Console", StringComparison.OrdinalIgnoreCase);
            if (!isConsole) return ProductMode.None;
            // Whole words, so "Protect" or "Product" doesn't count as Pro.
            if (Regex.IsMatch(title, @"\bPro\b", RegexOptions.IgnoreCase)) return ProductMode.PS5Pro;
            if (Regex.IsMatch(title, @"\bDigital\b", RegexOptions.IgnoreCase)) return ProductMode.PS5Digital;
            return ProductMode.PS5Disc;
        }

        private async Task<decimal?> ReadPriceAsync(IPage page)
        {
            var raw = await page.EvaluateExpressionAsync<string>(@"(
                document.querySelector('#corePrice_feature_div .a-offscreen') ||
                document.querySelector('.priceToPay .a-offscreen') ||
                document.querySelector('#price_inside_buybox') || {}).textContent || ''");
            var match = Regex.Match(raw.Replace(",", ""), @"\d+(\.\d+)?");
            return match.Success ? decimal.Parse(match.Value, CultureInfo.InvariantCulture) : null;
        }

        private static async Task<bool> SoldByAmazonAsync(IPage page)
        {
            var seller = await page.EvaluateExpressionAsync<string>(@"[...document.querySelectorAll(
                '#merchantInfoFeature_feature_div, #merchant-info, #tabular-buybox, #sellerProfileTriggerId')]
                .map(e => e.innerText).join(' ')");
            return seller.Contains("Amazon", StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<IElementHandle?> FindPlaceOrderButtonAsync(IPage page, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                foreach (var frame in page.Frames)
                {
                    // Turbo checkout lives in an iframe.
                    var turbo = await frame.QuerySelectorAsync("#turbo-checkout-pyo-button");
                    if (turbo != null) return turbo;

                    foreach (var sel in PlaceOrderSelectors)
                        if (await frame.QuerySelectorAsync(sel) is { } btn)
                            return btn;
                }
                await Task.Delay(500);
            }
            return null;
        }
    }
}
