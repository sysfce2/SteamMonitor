using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using MySqlConnector;
using SteamKit2;
using SteamKit2.Discovery;

namespace StatusService
{
    class SteamManager
    {
        class DatabaseRecord
        {
            public string Address { get; set; }
            public bool IsWebSocket { get; set; }

            public ServerRecord GetServerRecord()
            {
                if (IsWebSocket)
                {
                    return ServerRecord.CreateWebSocketServer(Address);
                }

                ServerRecord.TryCreateSocketServer(Address, out var record);
                return record;
            }
        }

        public static SteamManager Instance { get; } = new SteamManager();

        public readonly Random Random = new Random();

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

            var path = Path.Combine(AppContext.BaseDirectory, "database.txt");

            if (!File.Exists(path))
            {
                Log.WriteError("Database", "Put your MySQL connection string in database.txt");

                Environment.Exit(1);
            }

            databaseConnectionString = File.ReadAllText(path).Trim();
        }

        public async Task Start()
        {
            await using var db = await GetConnection();

            // Seed CM list with old CMs in the database
            var servers = db.Query<DatabaseRecord>("SELECT `Address`, `IsWebSocket` FROM `CMs`").Select(x => x.GetServerRecord()).ToList();

            Log.WriteInfo(nameof(SteamManager), "Got {0} old CMs", servers.Count);

            UpdateCMList(servers);
        }

        public async Task Stop()
        {
            Log.WriteInfo(nameof(SteamManager), "Stopping");

            foreach (var monitor in monitors.Values)
            {
                Log.WriteInfo(nameof(SteamManager), "Disconnecting monitor {0}", ServerRecordToString(monitor.Server));

                monitor.Disconnect();
            }

            Log.WriteInfo(nameof(SteamManager), "All monitors disconnected");

            // Reset all statuses
            await using var db = await GetConnection();
            await db.ExecuteAsync($"UPDATE `CMs` SET `Status` = {EResult.Invalid}");
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

        public async Task RemoveCM(Monitor monitor)
        {
            var address = ServerRecordToString(monitor.Server);

            Log.WriteInfo(nameof(SteamManager), $"Removing server: {address}");

            monitors.TryRemove(monitor.Server, out _);

            try
            {
                await using var db = await GetConnection();
                await db.ExecuteAsync(
                    "DELETE FROM`CMs` WHERE `Address` = @Address AND `IsWebSocket` = @IsWebSocket",
                    new
                    {
                        Address = address,
                        IsWebSocket = (monitor.Server.ProtocolTypes & ProtocolTypes.WebSocket) > 0 ? 1 : 0,
                    }
                );
            }
            catch (MySqlException e)
            {
                Log.WriteError(nameof(SteamManager), $"Failed to remove server: {e.Message}");
            }
        }

        public void UpdateCMList(IEnumerable<ServerRecord> cmList)
        {
            var x = 0;

            foreach (var cm in cmList)
            {
                if (monitors.TryGetValue(cm, out var monitor))
                {
                    monitor.LastSeen = DateTime.Now;

                    continue;
                }

                var newMonitor = new Monitor(cm, SharedConfig);

                monitors.TryAdd(cm, newMonitor);

                _ = UpdateCMStatus(newMonitor, EResult.Pending, "New server");

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

            if (monitor.LastReportedStatus == result)
            {
                return;
            }

            try
            {
                await using var db = await GetConnection();
                await db.ExecuteAsync(
                    "INSERT INTO `CMs` (`Address`, `IsWebSocket`, `Status`) VALUES(@IP, @IsWebSocket, @Status) ON DUPLICATE KEY UPDATE `Status` = VALUES(`Status`)",
                    new
                    {
                        IP = keyName,
                        IsWebSocket = (monitor.Server.ProtocolTypes & ProtocolTypes.WebSocket) > 0,
                        Status = (int)result
                    }
                );

                monitor.LastReportedStatus = result;
            }
            catch (MySqlException e)
            {
                Log.WriteError(nameof(SteamManager), "Failed to update status of {0}: {1}", keyName, e.Message);
            }
        }

        private async Task UpdateCMListViaWebAPI()
        {
            Log.WriteInfo(nameof(SteamManager), "Updating CM list using webapi");

            try
            {
                var globalServers = (await SteamDirectory.LoadAsync(SharedConfig, int.MaxValue, CancellationToken.None)).ToList();
                var chinaRealmServers = (await LoadChinaCMList(SharedConfig)).Where(s => !globalServers.Contains(s)).ToList();
                var servers = globalServers.Concat(chinaRealmServers);

                Log.WriteInfo(nameof(SteamManager), $"Got {globalServers.Count} servers plus {chinaRealmServers.Count} chinese servers");

                UpdateCMList(servers);
            }
            catch (Exception e)
            {
                Log.WriteError(nameof(SteamManager), "Web API Exception: {0}", e);
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

        private static async Task<List<ServerRecord>> LoadChinaCMList(SteamConfiguration configuration)
        {
            var directory = configuration.GetAsyncWebAPIInterface("ISteamDirectory");
            var args = new Dictionary<string, object>
            {
                ["cellid"] = "47", // Shanghai
                ["maxcount"] = int.MaxValue.ToString(),
                ["steamrealm"] = "steamchina",
            };

            var response = await directory.CallAsync(HttpMethod.Get, "GetCMList", 1, args).ConfigureAwait(false);

            var result = (EResult)response["result"].AsInteger();
            if (result != EResult.OK)
            {
                throw new InvalidOperationException($"Steam Web API returned EResult.{result}");
            }

            var socketList = response["serverlist"];
            var websocketList = response["serverlist_websockets"];

            var serverRecords = new List<ServerRecord>(socketList.Children.Count + websocketList.Children.Count);

            foreach (var child in socketList.Children)
            {
                if (child.Value is null || !ServerRecord.TryCreateSocketServer(child.Value, out var record))
                {
                    continue;
                }

                serverRecords.Add(record);
            }

            foreach (var child in websocketList.Children)
            {
                if (child.Value is null)
                {
                    continue;
                }

                serverRecords.Add(ServerRecord.CreateWebSocketServer(child.Value));
            }

            return serverRecords;
        }
    }
}
