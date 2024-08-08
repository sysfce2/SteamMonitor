using System;
using System.Threading;
using System.Threading.Tasks;

namespace StatusService
{
    static class Program
    {
        public static CancellationTokenSource Cts = new();

        static async Task Main()
        {
            Console.Title = "Steam Monitor";

            Console.CancelKeyPress += delegate
            {
                Log.WriteInfo("Stopping via Ctrl-C...");

                Cts.Cancel();

                Environment.Exit(0);
            };

            AppDomain.CurrentDomain.ProcessExit += (sender, e) =>
            {
                Cts.Cancel();
            };

            AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
            {
                Log.WriteError($"Unhandled exception: {e.ExceptionObject}");
            };

            await SteamManager.Instance.Start();

            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromSeconds(4));
                var token = Cts.Token;

                while (await timer.WaitForNextTickAsync(token))
                {
                    SteamManager.Instance.Tick();
                }
            }
            catch (OperationCanceledException)
            {
                //
            }

            Log.WriteInfo("Stopping...");
            await SteamManager.Instance.Stop();
        }
    }
}
