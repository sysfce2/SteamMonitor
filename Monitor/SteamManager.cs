using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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

        public static SteamManager Instance { get; } = new();
        public readonly Random Random = new();

        readonly ConcurrentDictionary<string, Monitor> monitors;
        readonly SteamConfiguration SharedConfig;
        readonly string databaseConnectionString;
        DateTime NextCMListUpdate;
        uint CellID;

        private SteamManager()
        {
            monitors = new ConcurrentDictionary<string, Monitor>();

            SharedConfig = SteamConfiguration.Create(b => b
                .WithDirectoryFetch(false)
                .WithProtocolTypes(ProtocolTypes.Tcp | ProtocolTypes.WebSocket)
                .WithConnectionTimeout(TimeSpan.FromSeconds(15))
            );

            var path = Path.Combine(AppContext.BaseDirectory, "database.txt");

            if (!File.Exists(path))
            {
                Log.WriteError("Put your MySQL connection string in database.txt");

                Environment.Exit(1);
            }

            databaseConnectionString = File.ReadAllText(path).Trim();
        }

        public async Task Start()
        {
            await using var db = await GetConnection();

            // Seed CM list with old CMs in the database
            var servers = db.Query<DatabaseRecord>("SELECT `Address`, `IsWebSocket` FROM `CMs`").Select(x => x.GetServerRecord()).ToList();

            Log.WriteInfo($"Got {servers.Count} old CMs");

            await UpdateCMList(servers);
        }

        public async Task Stop()
        {
            Log.WriteInfo("Stopping...");

            foreach (var monitor in monitors.Values)
            {
                Log.WriteInfo($"Disconnecting monitor {ServerRecordToString(monitor.Server)}");

                monitor.Disconnect();
            }

            Log.WriteInfo("All monitors disconnected");

            // Reset all statuses
            await using var db = await GetConnection();
            await db.ExecuteAsync($"UPDATE `CMs` SET `Status` = {(int)EResult.Invalid}");
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
                NextCMListUpdate = DateTime.Now + TimeSpan.FromMinutes(11) + TimeSpan.FromSeconds(Random.Next(10, 120));

                Task.Run(UpdateCMListViaWebAPI);
            }
        }

        public async Task RemoveCM(Monitor monitor)
        {
            var address = ServerRecordToString(monitor.Server);

            Log.WriteInfo($"Removing server: {address}");

            monitors.TryRemove(monitor.Server.GetHost(), out _);

            try
            {
                await using var db = await GetConnection();
                await db.ExecuteAsync(
                    "DELETE FROM `CMs` WHERE `Address` = @Address AND `IsWebSocket` = @IsWebSocket",
                    new
                    {
                        Address = address,
                        IsWebSocket = (monitor.Server.ProtocolTypes & ProtocolTypes.WebSocket) > 0 ? 1 : 0,
                    }
                );
            }
            catch (MySqlException e)
            {
                Log.WriteError($"Failed to remove server: {e.Message}");
            }
        }

        public async Task UpdateCMList(IEnumerable<ServerRecord> cmList)
        {
            var x = 0;

            foreach (var cm in cmList)
            {
                if (monitors.TryGetValue(cm.GetHost(), out var monitor))
                {
                    monitor.LastSeen = DateTime.Now;

                    // Server on a particular port may be dead, so change it
                    if (monitor.Reconnecting > 2 && monitor.Server.ProtocolTypes == cm.ProtocolTypes && !monitor.Server.EndPoint.Equals(cm.EndPoint))
                    {
                        Log.WriteInfo($"Changed {monitor.Server.EndPoint} to {cm.EndPoint}");

                        try
                        {
                            await using var db = await GetConnection();
                            await db.ExecuteAsync(
                                "UPDATE `CMs` SET `Address` = @Address WHERE `Address` = @OldAddress AND `IsWebSocket` = @IsWebSocket",
                                new
                                {
                                    Address = ServerRecordToString(cm),
                                    OldAddress = ServerRecordToString(monitor.Server),
                                    IsWebSocket = (monitor.Server.ProtocolTypes & ProtocolTypes.WebSocket) > 0,
                                }
                            );
                        }
                        catch (MySqlException e)
                        {
                            Log.WriteError($"Failed to change {cm.EndPoint}: {e.Message}");
                        }

                        monitor.Reconnecting = 0;
                        monitor.Server = cm;
                    }

                    continue;
                }

                var newMonitor = new Monitor(cm, SharedConfig);

                monitors.TryAdd(cm.GetHost(), newMonitor);

                await UpdateCMStatus(newMonitor, EResult.Pending, "New server");

                newMonitor.Connect(DateTime.Now + TimeSpan.FromSeconds(++x % 40));
            }

            if (x > 0)
            {
                Log.WriteInfo($"There are now {monitors.Count} monitors, added {x} new ones");
            }
        }

        public void NotifyCMOnline(Monitor monitor)
        {
            _ = UpdateCMStatus(monitor, EResult.OK, "Online");
        }

        public void NotifyCMOffline(Monitor monitor, EResult result, string lastAction)
        {
            _ = UpdateCMStatus(monitor, result, lastAction);
        }

        private async Task UpdateCMStatus(Monitor monitor, EResult result, string lastAction)
        {
            var keyName = ServerRecordToString(monitor.Server);

            Log.WriteStatus($"> {keyName,40} | { monitor.Server.ProtocolTypes,10} | {result,20} | {lastAction}");

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
                Log.WriteError($"Failed to update status of {keyName}: {e.Message}");
            }
        }

        private async Task UpdateCMListViaWebAPI()
        {
            Log.WriteInfo("Updating CM list using webapi");

            try
            {
                var globalServers = (await LoadCMList(SharedConfig, CellID)).ToList();

                Log.WriteInfo($"Got {globalServers.Count} servers from cell {CellID}");

                if (CellID % 10 == 0)
                {
                    var chinaRealmServers = (await LoadCMList(SharedConfig, 47)).Where(s => !globalServers.Contains(s)).ToList(); // Shanghai cell
                    var servers = globalServers.Concat(chinaRealmServers);

                    Log.WriteInfo($"Got {chinaRealmServers.Count} chinese servers");

                    await UpdateCMList(servers);
                }
                else
                {
                    await UpdateCMList(globalServers);
                }
            }
            catch (Exception e)
            {
                Log.WriteError($"Web API Exception: {e}");
            }

            if (++CellID >= 220)
            {
                CellID = 0;
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

        private static async Task<List<ServerRecord>> LoadCMList(SteamConfiguration configuration, uint cellId)
        {
            var directory = configuration.GetAsyncWebAPIInterface("ISteamDirectory");
            var args = new Dictionary<string, object>
            {
                ["cellid"] = cellId.ToString(),
                ["maxcount"] = int.MaxValue.ToString(),
            };

            if (cellId == 47)
            {
                args.Add("steamrealm", "steamchina");
            }

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
