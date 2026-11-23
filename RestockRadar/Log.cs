namespace RestockRadar
{
    static class Log
    {
        private static readonly object Gate = new();

        public static void Info(string msg) => Write("INFO ", msg, ConsoleColor.Gray);
        public static void Warn(string msg) => Write("WARN ", msg, ConsoleColor.Yellow);
        public static void Error(string msg) => Write("ERROR", msg, ConsoleColor.Red);
        public static void Alert(string msg) => Write("ALERT", msg, ConsoleColor.Green);

        private static void Write(string level, string msg, ConsoleColor color)
        {
            lock (Gate)
            {
                var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {level} {msg}";
                var old = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine(line);
                Console.ForegroundColor = old;
                try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "restockradar.log"), line + Environment.NewLine); }
                catch (IOException) { }
            }
        }
    }
}
