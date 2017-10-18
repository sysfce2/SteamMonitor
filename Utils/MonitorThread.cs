using System.Threading;

namespace StatusService
{
    class MonitorThread
    {
        readonly Thread monitorThread;
        bool monitorRunning;

        public MonitorThread()
        {
            monitorThread = new Thread(MonitorLoop);
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
            SteamManager.Instance.Start();

            while (true)
            {
                if (!monitorRunning)
                {
                    Log.WriteInfo("MonitorThread", "Stopping");
                    SteamManager.Instance.Stop();
                    break;
                }

                SteamManager.Instance.Tick();
            }
        }
    }
}
