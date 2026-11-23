using Microsoft.Extensions.Configuration;

namespace RestockRadar
{
    class AppConfig
    {
        public MonitorOptions Monitor { get; set; } = new();
        public TelegramOptions Telegram { get; set; } = new();
        public AmazonOptions Amazon { get; set; } = new();
        public List<SiteOptions> Sites { get; set; } = new();

        public static AppConfig Load()
        {
            var baseDir = AppContext.BaseDirectory;
            var config = new ConfigurationBuilder()
                .SetBasePath(baseDir)
                .AddJsonFile("appsettings.json", optional: false)
                // Secrets live here (git-ignored), never in source.
                .AddJsonFile("appsettings.local.json", optional: true)
                // e.g. WC_Telegram__BotToken, WC_Amazon__Password
                .AddEnvironmentVariables("WC_")
                .Build();

            var app = config.Get<AppConfig>() ?? new AppConfig();

            // Untouched "YOUR_..." placeholders count as not set.
            app.Telegram.BotToken = Unplaceholder(app.Telegram.BotToken);
            app.Telegram.ChatId = Unplaceholder(app.Telegram.ChatId);
            app.Amazon.Email = Unplaceholder(app.Amazon.Email);
            app.Amazon.Password = Unplaceholder(app.Amazon.Password);
            app.Amazon.TotpSecret = Unplaceholder(app.Amazon.TotpSecret);

            return app;
        }

        private static string Unplaceholder(string value) =>
            value.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase) ? "" : value;
    }

    // Flags, so modes combine: "PS5Disc, PS5Pro" in config or "ps5disc,ps5pro" on the command line.
    [Flags]
    enum ProductMode
    {
        None = 0,
        PS5Disc = 1,                // PS5 (Slim) with a disc drive
        PS5Digital = 2,             // PS5 (Slim) Digital Edition, no disc drive
        PS5Pro = 4,                 // PS5 Pro (only sold digital; the disc drive is an add-on)
        PS5 = PS5Disc | PS5Digital, // either standard PS5
        All = PS5 | PS5Pro,
        Both = All,                 // old name for All, kept so existing configs still work
    }

    static class ProductModeExtensions
    {
        public static string DisplayName(this ProductMode mode)
        {
            if (mode == ProductMode.All) return "any PS5";
            var parts = new List<string>();
            if (mode.HasFlag(ProductMode.PS5)) parts.Add("PS5 (disc or digital)");
            else if (mode.HasFlag(ProductMode.PS5Disc)) parts.Add("PS5 Disc");
            else if (mode.HasFlag(ProductMode.PS5Digital)) parts.Add("PS5 Digital");
            if (mode.HasFlag(ProductMode.PS5Pro)) parts.Add("PS5 Pro");
            return parts.Count > 0 ? string.Join(" / ", parts) : "nothing";
        }

        // A site is watched when what it sells overlaps what we're hunting for. A site tagged PS5
        // (could be either edition) is watched in PS5Disc and PS5Digital mode; one tagged All in every mode.
        public static bool Includes(this ProductMode mode, ProductMode siteProduct) => (mode & siteProduct) != 0;
    }

    class MonitorOptions
    {
        // Which console(s) to hunt for: PS5Disc, PS5Digital, PS5Pro, PS5 (disc or digital), All, or a
        // combination like "PS5Disc, PS5Pro". Sites selling something else are skipped.
        public ProductMode Product { get; set; } = ProductMode.PS5;
        public int IntervalSeconds { get; set; } = 120;
        public int JitterSeconds { get; set; } = 30;
        public bool Headless { get; set; } = false;
        public string? ChromePath { get; set; }
        public string ProfileDir { get; set; } = "browser-profile";
        public string DataDir { get; set; } = "data";
        public int ViewportWidth { get; set; } = 1280;
        public int ViewportHeight { get; set; } = 900;
        public int PageTimeoutSeconds { get; set; } = 45;
        // Tell me once when a site has failed this many checks in a row (blocked, captcha, layout change...).
        public int AlertAfterFailures { get; set; } = 5;
        public bool BeepOnAlert { get; set; } = true;
    }

    class TelegramOptions
    {
        public string BotToken { get; set; } = "";
        public string ChatId { get; set; } = "";
    }

    class AmazonOptions
    {
        public bool AutoBuy { get; set; } = false;
        // Walks all the way to the "Place your order" button but doesn't press it.
        public bool DryRun { get; set; } = true;
        public decimal MaxPrice { get; set; } = 0;
        public bool RequireSoldByAmazon { get; set; } = true;
        public string BaseUrl { get; set; } = "https://www.amazon.com.au";
        // Optional - only used if the saved browser session has expired. Prefer running with --login once.
        public string Email { get; set; } = "";
        public string Password { get; set; } = "";
        public string TotpSecret { get; set; } = "";
    }

    class SiteOptions
    {
        public string Name { get; set; } = "";
        public string Url { get; set; } = "";
        // Which console this page sells: PS5Disc, PS5Digital, PS5Pro; PS5 if it could be either edition,
        // All for a general page.
        public ProductMode Product { get; set; } = ProductMode.PS5;
        public bool Enabled { get; set; } = true;
        // CSS selector(s) for the part of the page that matters (buy box, stock label...). Blank = whole page.
        public string? Selector { get; set; }
        // Elements stripped before reading the page (banners, carousels, recommendations - anything noisy).
        public List<string> RemoveSelectors { get; set; } = new();
        // If any of these appear in the watched text the item counts as in stock...
        public List<string> InStockText { get; set; } = new();
        // ...unless one of these appears too. With neither list set, any change in the text triggers an alert.
        public List<string> OutOfStockText { get; set; } = new();
        // Hand off to the Amazon buyer when this site comes into stock.
        public bool AutoBuy { get; set; } = false;
        // Price limit for this listing; 0 = use Amazon:MaxPrice.
        public decimal MaxPrice { get; set; } = 0;
    }
}
