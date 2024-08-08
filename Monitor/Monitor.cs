using System;
using System.Threading;
using System.Threading.Tasks;
using SteamKit2;

namespace StatusService
{
    class Monitor
    {
        public DatabaseRecord Server { get; set; }
        public uint Reconnecting { get; set; }

        readonly SteamClient Client;
        readonly SteamMonitorUser steamUser;
        readonly CallbackManager callbackMgr;
        readonly CancellationToken cancellationToken;

        bool IsDisconnecting;

        public EResult LastReportedStatus { get; set; }
        public DateTime LastSeen { get; set; }
        DateTime LastSuccess = DateTime.Now;
        DateTime nextConnect = DateTime.MaxValue;

        private static readonly TimeSpan NoSuccessRemoval = TimeSpan.FromDays(1);

        public Monitor(DatabaseRecord server, SteamConfiguration config)
        {
            Server = server;

            Client = new SteamClient(config);

            steamUser = new SteamMonitorUser();
            Client.AddHandler(steamUser);

            callbackMgr = new CallbackManager(Client);
            callbackMgr.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            callbackMgr.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            callbackMgr.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            callbackMgr.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

            cancellationToken = Program.Cts.Token;

            Task.Factory.StartNew(HandleCallbacks, cancellationToken, TaskCreationOptions.LongRunning | TaskCreationOptions.PreferFairness, TaskScheduler.Default);
        }

        public void Connect(DateTime when)
        {
            nextConnect = when;
        }

        public void Disconnect()
        {
            IsDisconnecting = true;
            Client.Disconnect();
        }

        public async Task HandleCallbacks()
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    await callbackMgr.RunWaitCallbackAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                //
            }
        }

        public void DoTick(DateTime now)
        {
            if (now >= nextConnect)
            {
                nextConnect = now + TimeSpan.FromMinutes(1);

                Reconnecting++;

                Task.Run(() => Client.Connect(Server.GetServerRecord()));
            }
        }

        private void OnConnected(SteamClient.ConnectedCallback callback)
        {
            Reconnecting = 0;

            steamUser.LogOn();
        }

        private void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            if (IsDisconnecting)
            {
                return;
            }

            var now = DateTime.Now;

            if (LastSuccess + NoSuccessRemoval < now && LastSeen + NoSuccessRemoval < now)
            {
                IsDisconnecting = true;
                Task.Run(() => SteamManager.Instance.RemoveCM(this));
                return;
            }

            var numSeconds = Random.Shared.Next(10, 60);
            Connect(now + TimeSpan.FromSeconds(numSeconds));

            // If Steam dies, don't say next connect is planned
            if (Reconnecting == 0)
            {
                Reconnecting = 2;
            }

            if (Reconnecting >= 10)
            {
                SteamManager.Instance.NotifyCMOffline(this, EResult.NoConnection, $"Disconnected (#{Reconnecting}) (Seen: {LastSeen} Success: {LastSuccess})");
            }
            else if (Reconnecting == 1)
            {
                SteamManager.Instance.NotifyCMOffline(this, EResult.OK, $"Reconnecting");
            }
            else
            {
                SteamManager.Instance.NotifyCMOffline(this, EResult.NoConnection, $"Disconnected (#{Reconnecting})");
            }
        }

        private void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            if (callback.Result != EResult.OK)
            {
                SteamManager.Instance.NotifyCMOffline(this, callback.Result, "Logon error");

                return;
            }

            SteamManager.Instance.NotifyCMOnline(this);

            LastSuccess = DateTime.Now;

            // schedule a random reconnect
            Connect(LastSuccess
                + TimeSpan.FromMinutes(5)
                + TimeSpan.FromMinutes(Random.Shared.NextDouble() * 5));
        }

        private void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            SteamManager.Instance.NotifyCMOffline(this, callback.Result, "Logged off");
        }
    }
}
