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

namespace StatusService
{
    class SteamManager
    {
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
            var servers = (await db
                .QueryAsync<(string Address, bool IsWebSocket, string Datacenter)> ("SELECT `Address`, `IsWebSocket`, `Datacenter` FROM `CMs`"))
                .Select(s => new DatabaseRecord(s.Address, s.Datacenter, s.IsWebSocket))
                .ToList();

            Log.WriteInfo($"Got {servers.Count} old CMs");

            await UpdateCMList(servers);
        }

        public async Task Stop()
        {
            foreach (var monitor in monitors.Values)
            {
                Log.WriteInfo($"Disconnecting monitor {monitor.Server.GetString()}");

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
            var address = monitor.Server.GetString();

            Log.WriteInfo($"Removing server: {address}");

            monitors.TryRemove(monitor.Server.GetUniqueKey(), out _);

            try
            {
                await using var db = await GetConnection();
                await db.ExecuteAsync(
                    "DELETE FROM `CMs` WHERE `Address` = @Address AND `IsWebSocket` = @IsWebSocket",
                    new
                    {
                        Address = address,
                        IsWebSocket = monitor.Server.IsWebSocket,
                    }
                );
            }
            catch (MySqlException e)
            {
                Log.WriteError($"Failed to remove server: {e.Message}");
            }
        }

        private async Task UpdateCMList(IEnumerable<DatabaseRecord> cmList)
        {
            var x = 0;

            foreach (var cm in cmList.OrderBy(cm => cm.Port))
            {
                if (monitors.TryGetValue(cm.GetUniqueKey(), out var monitor))
                {
                    monitor.LastSeen = DateTime.Now;

                    // Server on a particular port may be dead, so change it
                    // for tcp servers port 27017 to be definitive
                    // for websockets, there's not always 443 port, and other ports follow tcp ones
                    if (monitor.Reconnecting > 2 && monitor.Server.Port != cm.Port && monitor.Server.Hostname != cm.Hostname)
                    {
                        Log.WriteInfo($"Changed {monitor.Server.GetString()} to {cm.GetString()}");

                        try
                        {
                            await using var db = await GetConnection();
                            await db.ExecuteAsync(
                                "UPDATE `CMs` SET `Address` = @Address WHERE `Address` = @OldAddress AND `IsWebSocket` = @IsWebSocket",
                                new
                                {
                                    Address = cm.GetString(),
                                    OldAddress = monitor.Server.GetString(),
                                    IsWebSocket = monitor.Server.IsWebSocket,
                                }
                            );
                        }
                        catch (MySqlException e)
                        {
                            Log.WriteError($"Failed to change {monitor.Server.GetString()}: {e.Message}");
                        }

                        monitor.Reconnecting = 0;
                        monitor.Server = cm;
                    }

                    continue;
                }

                var newMonitor = new Monitor(cm, SharedConfig);

                monitors.TryAdd(cm.GetUniqueKey(), newMonitor);

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
            var keyName = monitor.Server.GetString();
            var type = monitor.Server.IsWebSocket ? "WS" : "TCP";

            Log.WriteStatus($"> {keyName,40} | {type,-3} | {monitor.Server.Datacenter,-4} | {result,-20} | {lastAction}");

            if (monitor.LastReportedStatus == result)
            {
                return;
            }

            try
            {
                await using var db = await GetConnection();
                await db.ExecuteAsync(
                    "INSERT INTO `CMs` (`Address`, `IsWebSocket`, `Datacenter`, `Status`) VALUES(@IP, @IsWebSocket, @Datacenter, @Status) ON DUPLICATE KEY UPDATE `Status` = VALUES(`Status`)",
                    new
                    {
                        IP = keyName,
                        IsWebSocket = monitor.Server.IsWebSocket,
                        Datacenter = monitor.Server.Datacenter,
                        Status = (int)result,
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

        private static async Task<List<DatabaseRecord>> LoadCMList(SteamConfiguration configuration, uint cellId)
        {
            var directory = configuration.GetAsyncWebAPIInterface("ISteamDirectory");
            var args = new Dictionary<string, object>
            {
                ["cellid"] = cellId.ToString(),
                ["maxcount"] = int.MaxValue.ToString(),
            };

            if (cellId == 47)
            {
                args.Add("realm", "steamchina");
            }

            var response = await directory.CallAsync(HttpMethod.Get, "GetCMListForConnect", 1, args);
            var serverList = response["serverlist"];
            var serverRecords = new List<DatabaseRecord>(serverList.Children.Count);

            foreach (var child in serverList.Children)
            {
                serverRecords.Add(new DatabaseRecord(
                    child["endpoint"].AsString(),
                    child["dc"].AsString(),
                    child["type"].AsString() == "websockets"
                ));
            }

            return serverRecords;
        }
    }
}
