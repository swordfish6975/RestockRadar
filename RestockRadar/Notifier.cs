using Telegram.Bot;
using Telegram.Bot.Types;

namespace RestockRadar
{
    class Notifier
    {
        private readonly TelegramOptions _options;
        private readonly bool _beep;
        private readonly TelegramBotClient? _bot;

        public Notifier(TelegramOptions options, bool beep)
        {
            _options = options;
            _beep = beep;
            if (!string.IsNullOrWhiteSpace(options.BotToken))
                _bot = new TelegramBotClient(options.BotToken);
            else
                Log.Warn("No Telegram bot token configured - alerts will only show in the console.");
        }

        public async Task SendAsync(string message, byte[]? photo = null)
        {
            Log.Alert(message);
            if (_beep && OperatingSystem.IsWindows())
                for (var i = 0; i < 3; i++) Console.Beep(1200, 250);

            if (_bot == null) return;

            try
            {
                var chatId = await GetChatIdAsync();
                if (chatId == null) return;

                if (photo != null)
                {
                    using var stream = new MemoryStream(photo);
                    // Captions are capped at 1024 chars.
                    await _bot.SendPhoto(chatId, InputFile.FromStream(stream, "screenshot.png"), caption: Truncate(message, 1024));
                }
                else
                {
                    await _bot.SendMessage(chatId, Truncate(message, 4096));
                }
            }
            catch (Exception ex)
            {
                Log.Error($"Telegram send failed: {ex.Message}");
            }
        }

        // Same trick as before: if no chat id is set, use whoever last messaged the bot.
        private async Task<string?> GetChatIdAsync()
        {
            if (!string.IsNullOrWhiteSpace(_options.ChatId)) return _options.ChatId;

            var updates = await _bot!.GetUpdates();
            var chat = updates.Select(u => u.Message?.Chat).LastOrDefault(c => c != null);
            if (chat == null)
            {
                Log.Warn("No Telegram chat id configured and nobody has messaged the bot yet. Send your bot a message, then try again.");
                return null;
            }

            _options.ChatId = chat.Id.ToString();
            Log.Info($"Using Telegram chat id {_options.ChatId} - put it in appsettings.local.json (Telegram:ChatId) to make it permanent.");
            return _options.ChatId;
        }

        private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
