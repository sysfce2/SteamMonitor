using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
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

        readonly Random Random = new Random();

        readonly ConcurrentDictionary<ServerRecord, Monitor> monitors;

        readonly SteamConfiguration SharedConfig;

        readonly string databaseConnectionString;

        DateTime NextCMListUpdate;

        private SteamManager()
        {
            monitors = new ConcurrentDictionary<ServerRecord, Monitor>();

            SharedConfig = SteamConfiguration.Create(b => b
                .WithDirectoryFetch(false)
                .WithProtocolTypes(ProtocolTypes.Tcp | ProtocolTypes.WebSocket)
                .WithConnectionTimeout(TimeSpan.FromSeconds(15))
            );

            var path = Path.Combine(Path.GetDirectoryName(typeof(Program).Assembly.Location), "database.txt");

            if (!File.Exists(path))
            {
                Log.WriteError("Database", "Put your MySQL connection string in database.txt");

                Environment.Exit(1);
            }

            databaseConnectionString = File.ReadAllText(path).Trim();
        }

        public async Task Stop()
        {
            Log.WriteInfo("SteamManager", "Stopping");

            foreach (var monitor in monitors.Values)
            {
                Log.WriteInfo("SteamManager", "Disconnecting monitor {0}", ServerRecordToString(monitor.Server));

                monitor.Disconnect();
            }

            Log.WriteInfo("SteamManager", "All monitors disconnected");

            // Reset all statuses
            using var db = await GetConnection();
            await db.ExecuteAsync("UPDATE `CMs` SET `Status` = 'Invalid', `LastAction` = 'Application stop'");
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

        public uint GetNextRandom()
        {
            return (uint)Random.Next(0, int.MaxValue);
        }

        public void UpdateCMList(IEnumerable<ServerRecord> cmList)
        {
            var x = 0;

            foreach (var cm in cmList)
            {
                if (monitors.Keys.Any(s => s.Equals(cm)))
                {
                    continue;
                }

                var newMonitor = new Monitor(cm, SharedConfig);

                monitors.TryAdd(cm, newMonitor);

                _ = UpdateCMStatus(newMonitor, EResult.Invalid, "New server");

                newMonitor.Connect(DateTime.Now + TimeSpan.FromSeconds(++x % 40));
            }
        }

        public void NotifyCMOnline(Monitor monitor, string lastAction)
        {
            _ = UpdateCMStatus(monitor, EResult.OK, lastAction);
        }

        public void NotifyCMOffline(Monitor monitor, EResult result, string lastAction)
        {
            _ = UpdateCMStatus(monitor, result, lastAction);
        }

        private async Task UpdateCMStatus(Monitor monitor, EResult result, string lastAction)
        {
            var keyName = ServerRecordToString(monitor.Server);

            Log.WriteInfo("CM", "{0,40} | {1,10} | {2,20} | {3}", keyName, monitor.Server.ProtocolTypes, result, lastAction);

            try
            {
                using var db = await GetConnection();
                await db.ExecuteAsync(
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
                var servers = (await SteamDirectory.LoadAsync(SharedConfig, int.MaxValue, CancellationToken.None)).ToList();

                Log.WriteInfo("Web API", "Got {0} CMs", servers.Count);

                UpdateCMList(servers);
            }
            catch (Exception e)
            {
                Log.WriteError("Web API", "{0}", e);
            }
        }

        private async Task<MySqlConnection> GetConnection()
        {
            var connection = new MySqlConnection(databaseConnectionString);

            await connection.OpenAsync();

            return connection;
        }

        private static string ServerRecordToString(ServerRecord record)
        {
            return $"{record.GetHost()}:{record.GetPort()}";
        }
    }
}
