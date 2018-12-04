using System;

namespace StatusService
{
    static class Program
    {
        static void Main()
        {
            Console.Title = "Steam Monitor";

            var monitorThread = new MonitorThread();

            Console.CancelKeyPress += delegate
            {
                Log.WriteInfo("Program", "Stopping");

                monitorThread.Stop();

                Environment.Exit(0);
            };

            AppDomain.CurrentDomain.UnhandledException += (object sender, UnhandledExceptionEventArgs e) =>
            {
                Log.WriteError("Program", "Unhandled exception: {0}", e.ExceptionObject);

                if (e.IsTerminating)
                {
                    SteamManager.Instance.Crash();
                }
            };

            monitorThread.Start();
        }
    }
}
