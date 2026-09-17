namespace Postgresfold.Scaffold.Domain.Util
{
    public static class Logger
    {
        private static object syncRoot = new object();

        public static void LogInfo(string message)
        {
            lock (syncRoot)
            {
                Console.ForegroundColor = ConsoleColor.Cyan; // Cyan for informational messages
                Console.WriteLine(message);
                Console.ResetColor();
            }
        }

        public static void LogDebug(string message)
        {
            //Console.WriteLine(message);
            //Console.ResetColor();
        }

        public static void LogSkipped(string message)
        {
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine($"[Skipped] {message}");
            Console.ResetColor();
        }

        public static void LogSuccess(string message)
        {
            lock (syncRoot)
            {
                Console.ForegroundColor = ConsoleColor.Green; // Lime is equivalent to green in console
                Console.WriteLine($"[Success] {message}");
                Console.ResetColor();
            }
        }

        public static void LogWarn(string message)
        {
            lock (syncRoot)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"[Warn] {message}");
                Console.ResetColor();
            }
        }

        public static void LogError(string message)
        {
            lock (syncRoot)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"[Error] {message}");
                Console.ResetColor();
            }
        }
    }
}
