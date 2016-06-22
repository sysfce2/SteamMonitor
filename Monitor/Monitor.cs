using SteamKit2;
using System;
using System.Net;

namespace StatusService
{
    class Monitor
    {
        public IPEndPoint Server { get; private set; }

        readonly SteamClient Client;
        readonly SteamMonitorUser steamUser;
        readonly CallbackManager callbackMgr;

        bool IsDisconnecting;

        DateTime nextConnect = DateTime.MaxValue;

        public Monitor(IPEndPoint server)
        {
            Server = server;

            Client = new SteamClient();

            Client.ConnectionTimeout = TimeSpan.FromSeconds(15);

            steamUser = new SteamMonitorUser();
            Client.AddHandler(steamUser);

            callbackMgr = new CallbackManager(Client);
            callbackMgr.Subscribe<SteamClient.CMListCallback>(OnCMList);
            callbackMgr.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            callbackMgr.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            callbackMgr.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            callbackMgr.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);
        }

        public void Connect(DateTime? when = null)
        {
            if (when == null)
            {
                when = DateTime.Now;
            }

            nextConnect = when.Value;
        }

        public void Disconnect()
        {
            IsDisconnecting = true;
            Client.Disconnect();
        }

        public void DoTick()
        {
            // we'll check for callbacks every 10ms
            // thread quantum granularity might hose us,
            // but it should wake often enough to handle callbacks within a single thread

            callbackMgr.RunWaitCallbacks(TimeSpan.FromMilliseconds(10));

            if (DateTime.Now >= nextConnect)
            {
                nextConnect = DateTime.Now + TimeSpan.FromMinutes(1);

                Client.Connect(Server);
            }
        }

        protected void OnConnected(SteamClient.ConnectedCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                SteamManager.Instance.NotifyCMOffline(this, callback.Result, string.Format("OnConnected: {0}", callback.Result));

                return;
            }

            steamUser.LogOn();
        }

        protected void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            if (IsDisconnecting)
            {
                return;
            }

            int numSeconds = new Random().Next(10, 30);
            Connect(DateTime.Now + TimeSpan.FromSeconds(numSeconds));

            SteamManager.Instance.NotifyCMOffline(this, EResult.NoConnection, "OnDisconnected");
        }

        protected void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                SteamManager.Instance.NotifyCMOffline(this, callback.Result, string.Format("OnLoggedOn: {0}", callback.Result));

                return;
            }

            SteamManager.Instance.NotifyCMOnline(this, "Online");

            // schedule a random reconnect
            Connect(DateTime.Now
                + TimeSpan.FromMinutes(5)
                + TimeSpan.FromMinutes(new Random().NextDouble() * 5));
        }

        protected void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            SteamManager.Instance.NotifyCMOffline(this, callback.Result, string.Format("OnLoggedOff: {0}", callback.Result));
        }

        protected void OnCMList(SteamClient.CMListCallback callback)
        {
            SteamManager.Instance.UpdateCMList(callback.Servers);
        }
    }
}
