using System;

namespace StatusService
{
    public static class Log
    {
        public static void WriteStatus(string log)
        {
            WriteLine(log);
        }

        public static void WriteInfo(string log)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            WriteLine(log);
            Console.ResetColor();
        }

        public static void WriteError(string log)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            WriteLine(log);
            Console.ResetColor();
        }

        private static void WriteLine(string log)
        {
            Console.WriteLine($"{DateTime.Now.ToLongTimeString()} {log}");
        }
    }
}
