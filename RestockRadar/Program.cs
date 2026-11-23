using PuppeteerSharp;

namespace RestockRadar
{
    class Program
    {
        const string Usage = @"Usage: RestockRadar [options]
  (no options)      watch every enabled site in appsettings.json, forever
  --once            do a single pass and exit (for Task Scheduler)
  --product <mode>  override Monitor:Product - ps5disc, ps5digital, ps5pro, ps5 (disc or digital),
                    all, or a comma list like ps5disc,ps5pro
  --site <name>     only check this site (can repeat)
  --probe <name>    check one site, print the text it sees and save a screenshot, no alerts
  --login           open the browser on Amazon so you can sign in once; the session is kept
  --test-alert      send a test Telegram message";

        static async Task<int> Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            var once = false;
            var login = false;
            var testAlert = false;
            string? probe = null;
            ProductMode? product = null;
            var only = new List<string>();

            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i].ToLowerInvariant())
                {
                    case "--once": once = true; break;
                    case "--login": login = true; break;
                    case "--test-alert": testAlert = true; break;
                    case "--site" when i + 1 < args.Length: only.Add(args[++i]); break;
                    case "--probe" when i + 1 < args.Length: probe = args[++i]; break;
                    case "--product" when i + 1 < args.Length:
                        if (!Enum.TryParse<ProductMode>(args[++i], true, out var p) || p == ProductMode.None || int.TryParse(args[i], out _))
                        { Console.WriteLine($"Unknown product '{args[i]}' - use ps5disc, ps5digital, ps5pro, ps5, all, or e.g. ps5disc,ps5pro"); return 1; }
                        product = p;
                        break;
                    case "-h": case "--help": case "/?": Console.WriteLine(Usage); return 0;
                    default: Console.WriteLine($"Unknown argument '{args[i]}'\n\n{Usage}"); return 1;
                }
            }

            var config = AppConfig.Load();
            if (product != null) config.Monitor.Product = product.Value;
            var notifier = new Notifier(config.Telegram, config.Monitor.BeepOnAlert);

            if (testAlert)
            {
                await notifier.SendAsync("RestockRadar test alert - Telegram is working.");
                return 0;
            }

            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); Log.Info("Stopping after the current check..."); };

            await using var browser = await LaunchBrowserAsync(config.Monitor, forceVisible: login);

            if (login)
                return await LoginAsync(browser, config.Amazon);

            var mode = config.Monitor.Product;
            var sites = config.Sites.Where(s => s.Enabled && mode.Includes(s.Product)).ToList();
            if (probe != null) only = new List<string> { probe };
            if (only.Count > 0)
                sites = config.Sites.Where(s => only.Contains(s.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            if (sites.Count == 0)
            {
                Log.Error($"No sites to check for {mode.DisplayName()} - see the Sites section of appsettings.json.");
                return 1;
            }

            var monitor = new Monitor(config, browser, notifier);

            if (probe != null)
            {
                await monitor.ProbeAsync(sites[0]);
                return 0;
            }

            Log.Info($"Mode: {mode.DisplayName()}. Watching {string.Join(", ", sites.Select(s => s.Name))}");
            do
            {
                foreach (var site in sites)
                {
                    if (cts.IsCancellationRequested) break;
                    await monitor.CheckAsync(site);
                }

                if (once || cts.IsCancellationRequested) break;

                var delay = config.Monitor.IntervalSeconds + Random.Shared.Next(0, config.Monitor.JitterSeconds + 1);
                Log.Info($"Next check in {delay}s");
                try { await Task.Delay(TimeSpan.FromSeconds(delay), cts.Token); }
                catch (TaskCanceledException) { }
            } while (!cts.IsCancellationRequested);

            return 0;
        }

        static async Task<IBrowser> LaunchBrowserAsync(MonitorOptions options, bool forceVisible)
        {
            var chrome = options.ChromePath;
            if (string.IsNullOrWhiteSpace(chrome) || !File.Exists(chrome))
            {
                chrome = new[]
                {
                    Environment.ExpandEnvironmentVariables(@"%ProgramFiles%\Google\Chrome\Application\chrome.exe"),
                    Environment.ExpandEnvironmentVariables(@"%ProgramFiles(x86)%\Google\Chrome\Application\chrome.exe"),
                    Environment.ExpandEnvironmentVariables(@"%LocalAppData%\Google\Chrome\Application\chrome.exe"),
                }.FirstOrDefault(File.Exists);
            }

            if (chrome == null)
            {
                Log.Info("Chrome not found - downloading a copy for Puppeteer (one-off)...");
                chrome = (await new BrowserFetcher().DownloadAsync()).GetExecutablePath();
            }

            // A dedicated, persistent profile: cookies and the Amazon sign-in survive between runs.
            // Separate from your normal Chrome profile, so it won't touch your everyday browsing.
            return await Puppeteer.LaunchAsync(new LaunchOptions
            {
                ExecutablePath = chrome,
                Headless = options.Headless && !forceVisible,
                UserDataDir = Path.Combine(AppContext.BaseDirectory, options.ProfileDir),
                DefaultViewport = new ViewPortOptions { Width = options.ViewportWidth, Height = options.ViewportHeight },
                Args = new[] { "--disable-blink-features=AutomationControlled", "--no-first-run" },
            });
        }

        static async Task<int> LoginAsync(IBrowser browser, AmazonOptions amazon)
        {
            var page = (await browser.PagesAsync()).FirstOrDefault() ?? await browser.NewPageAsync();
            await page.GoToAsync($"{amazon.BaseUrl}/gp/sign-in.html");
            Log.Info("Sign in to Amazon in the browser window (tick 'Keep me signed in'), then press Enter here.");
            Console.ReadLine();

            var signedIn = await new AmazonBuyer(amazon).IsSignedInAsync(page);
            if (signedIn) Log.Info("Signed in - the session is saved in the browser profile.");
            else Log.Warn("Doesn't look signed in yet. Run --login again.");
            return signedIn ? 0 : 1;
        }
    }
}
