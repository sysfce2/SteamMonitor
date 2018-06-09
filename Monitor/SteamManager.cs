using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using MySql.Data.MySqlClient;
using SteamKit2;
using SteamKit2.Discovery;

namespace StatusService
{
    class SteamManager
    {
        public static SteamManager Instance { get; } = new SteamManager();

        readonly ConcurrentDictionary<ServerRecord, Monitor> monitors;

        readonly string databaseConnectionString;

        DateTime NextCMListUpdate;

        private SteamManager()
        {
            monitors = new ConcurrentDictionary<ServerRecord, Monitor>();

            var path = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location), "database.txt");

            if (!File.Exists(path))
            {
                Log.WriteError("Database", "Put your MySQL connection string in database.txt");

                Environment.Exit(1);
            }

            databaseConnectionString = File.ReadAllText(path).Trim();
        }

        public void Start()
        {
            // Remove old CMs from the database
            Crash();
        }

        public void Stop()
        {
            Log.WriteInfo("SteamManager", "Stopping");

            foreach (var monitor in monitors.Values)
            {
                Log.WriteInfo("SteamManager", "Disconnecting monitor {0}", SteamDirectory.ServerRecordToString(monitor.Server));

                monitor.Disconnect();
            }

            Log.WriteInfo("SteamManager", "All monitors disconnected");

            // Reset all statuses
            using (var db = GetConnection())
            {
                db.Execute("UPDATE `CMs` SET `Status` = 'Invalid', `LastAction` = 'Application stop'");
            }
        }

        public void Crash()
        {
            using (var db = GetConnection())
            {
                db.Execute("DELETE FROM `CMs`");
            }
        }

        public void Tick()
        {
            var monitorsCached = monitors.Values.ToList();

            foreach (var monitor in monitorsCached)
            {
                monitor.DoTick();
            }

            if (DateTime.Now > NextCMListUpdate)
            {
                NextCMListUpdate = DateTime.Now + TimeSpan.FromMinutes(15);

                Task.Run(UpdateCMListViaWebAPI);
            }
        }

        public void UpdateCMList(IEnumerable<ServerRecord> cmList)
        {
            var x = 0;

            foreach (var cm in cmList)
            {
                if (monitors.Keys.FirstOrDefault(s => s.Equals(cm)) != null)
                {
                    continue;
                }

                var newMonitor = new Monitor(cm);

                monitors.TryAdd(cm, newMonitor);

                UpdateCMStatus(newMonitor, EResult.Invalid, "New server");

                newMonitor.Connect(DateTime.Now + TimeSpan.FromSeconds(++x % 40));
            }
        }

        public void NotifyCMOnline(Monitor monitor, string lastAction)
        {
            UpdateCMStatus(monitor, EResult.OK, lastAction);
        }

        public void NotifyCMOffline(Monitor monitor, EResult result, string lastAction)
        {
            UpdateCMStatus(monitor, result, lastAction);
        }

        private void UpdateCMStatus(Monitor monitor, EResult result, string lastAction)
        {
            var keyName = SteamDirectory.ServerRecordToString(monitor.Server);

            Log.WriteInfo("CM", "{0,40} | {1,10} | {2,20} | {3}", keyName, monitor.Server.ProtocolTypes, result, lastAction);

            try
            {
                using (var db = GetConnection())
                {
                    db.Execute(
                        "INSERT INTO `CMs` (`Address`, `IsWebSocket`, `Status`, `LastAction`) VALUES(@IP, @IsWebSocket, @Status, @LastAction) ON DUPLICATE KEY UPDATE `Status` = VALUES(`Status`), `LastAction` = VALUES(`LastAction`)",
                        new
                        {
                            IP = keyName,
                            IsWebSocket = (monitor.Server.ProtocolTypes & ProtocolTypes.WebSocket) > 0,
                            Status = result.ToString(),
                            LastAction = lastAction
                        }
                    );
                }
            }
            catch (MySqlException e)
            {
                Log.WriteError("UpdateCM", "Failed to update status of {0}: {1}", keyName, e.Message);
            }
        }

        private async Task UpdateCMListViaWebAPI()
        {
            Log.WriteInfo("Web API", "Updating CM list");

            try
            {
                var servers = (await SteamDirectory.LoadAsync()).ToList();

                Log.WriteInfo("Web API", "Got {0} CMs", servers.Count);

                UpdateCMList(servers);
            }
            catch (Exception e)
            {
                Log.WriteError("Web API", "{0}", e);
            }
        }
        
        private MySqlConnection GetConnection()
        {
            var connection = new MySqlConnection(databaseConnectionString);

            connection.Open();

            return connection;
        }
    }
}
