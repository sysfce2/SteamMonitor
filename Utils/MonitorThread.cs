using System.Threading;

namespace StatusService
{
    class MonitorThread
    {
        readonly Thread monitorThread;
        bool monitorRunning;

        public MonitorThread()
        {
            monitorThread = new Thread(MonitorLoop)
            {
                Name = "MonitorLoop Thread"
            };
        }

        public void Start()
        {
            monitorRunning = true;
            monitorThread.Start();
        }

        public void Stop()
        {
            monitorRunning = false;
            monitorThread.Join();
        }

        private void MonitorLoop()
        {
            SteamManager.Instance.Start().GetAwaiter().GetResult();

            while (monitorRunning)
            {
                SteamManager.Instance.Tick();
            }

            Log.WriteInfo("Stopping MonitorLoop...");
            SteamManager.Instance.Stop().GetAwaiter().GetResult();
        }
    }
}
