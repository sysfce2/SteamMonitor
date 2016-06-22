using System;

namespace StatusService
{
    static class Program
    {
        static void Main()
        {
            var monitorThread = new MonitorThread();

            Console.CancelKeyPress += delegate
            {
                Log.WriteInfo("Program", "Stopping");

                monitorThread.Stop();
            };

            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                Log.WriteError("Program", "Unhandled exception: {0}", e.ExceptionObject);

                if(e.IsTerminating)
                {
                    SteamManager.Instance.Crash();
                }
            };

            monitorThread.Start();
        }
    }
}
