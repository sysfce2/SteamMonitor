using System;

namespace StatusService
{
    public static class Log
    {
        private enum Category
        {
            Info,
            Error
        }

        public static void WriteInfo(string component, string format, params object[] args)
        {
            WriteLine(Category.Info, component, format, args);
        }

        public static void WriteError(string component, string format, params object[] args)
        {
            WriteLine(Category.Error, component, format, args);
        }

        private static void WriteLine(Category category, string component, string format, params object[] args)
        {
            string logLine = string.Format(
                "{0} [{1}] {2}: {3}",
                DateTime.Now.ToLongTimeString(),
                category.ToString().ToUpper(),
                component,
                string.Format(format, args)
            );

            Console.WriteLine(logLine);
        }
    }
}
