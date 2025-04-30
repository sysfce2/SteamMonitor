﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MySqlConnector;
using SteamKit2;

namespace StatusService
{
    class SteamManager
    {
        private const int HighestCellId = 220;

        public static SteamManager Instance { get; } = new();

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
                .WithProtocolTypes(ProtocolTypes.WebSocket)
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
            NextCMListUpdate = DateTime.Now.AddMinutes(20);

            await using var db = await GetConnection();
            var servers = new List<DatabaseRecord>();

            // Seed CM list with old CMs in the database
            await using var cmd = new MySqlCommand("SELECT `Address`, `IsWebSocket`, `Datacenter` FROM `CMs`", db);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var address = reader.GetString(0);
                var isWebSocket = reader.GetBoolean(1);
                var datacenter = reader.GetString(2);

                servers.Add(new DatabaseRecord(address, datacenter, isWebSocket));
            }

            Log.WriteInfo($"Got {servers.Count} old CMs");

            await UpdateCMList(servers);

            _ = Task.Run(ScanAllCellIds);
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
            await using var cmd = new MySqlCommand($"UPDATE `CMs` SET `Status` = {(int)EResult.Invalid}", db);
            await cmd.ExecuteNonQueryAsync();
        }

        public void Tick()
        {
            var monitorsCached = monitors.Values.ToList();
            var now = DateTime.Now;

            foreach (var monitor in monitorsCached)
            {
                monitor.DoTick(now);
            }

            if (now > NextCMListUpdate)
            {
                NextCMListUpdate = now + TimeSpan.FromMinutes(11) + TimeSpan.FromSeconds(Random.Shared.Next(10, 120));

                Task.Run(UpdateCMListViaWebAPI);
            }

            Thread.Sleep(1000);
        }

        public async Task RemoveCM(Monitor monitor)
        {
            var address = monitor.Server.GetString();

            Log.WriteInfo($"Removing server: {address}");

            monitors.TryRemove(monitor.Server.GetUniqueKey(), out _);

            try
            {
                await using var db = await GetConnection();
                await using var cmd = new MySqlCommand("DELETE FROM `CMs` WHERE `Address` = @Address AND `IsWebSocket` = @IsWebSocket", db);
                cmd.Parameters.AddWithValue("@Address", address);
                cmd.Parameters.AddWithValue("@IsWebSocket", monitor.Server.IsWebSocket);

                await cmd.ExecuteNonQueryAsync();
            }
            catch (MySqlException e)
            {
                Log.WriteError($"Failed to remove server: {e.Message}");
            }
        }

        private async Task UpdateCMList(IEnumerable<DatabaseRecord> cmList)
        {
            var x = 0;
            var now = DateTime.Now;
            var changed = new HashSet<string>();

            foreach (var cm in cmList.OrderBy(cm => cm.Port))
            {
                var uniqueKey = cm.GetUniqueKey();

                if (monitors.TryGetValue(uniqueKey, out var monitor))
                {
                    monitor.LastSeen = now;

                    // Server on a particular port may be dead, so change it
                    // for tcp servers port 27017 to be definitive
                    // for websockets, there's not always 443 port, and other ports follow tcp ones
                    if (monitor.Reconnecting > 2 && monitor.Server.Port != cm.Port && !changed.Contains(uniqueKey))
                    {
                        Log.WriteInfo($"Changed {monitor.Server.GetString()} to {cm.GetString()}");

                        try
                        {
                            await using var db = await GetConnection();
                            await using var cmd = new MySqlCommand("UPDATE `CMs` SET `Address` = @Address WHERE `Address` = @OldAddress AND `IsWebSocket` = @IsWebSocket", db);
                            cmd.Parameters.AddWithValue("@Address", cm.GetString());
                            cmd.Parameters.AddWithValue("@OldAddress", monitor.Server.GetString());
                            cmd.Parameters.AddWithValue("@IsWebSocket", monitor.Server.IsWebSocket);

                            await cmd.ExecuteNonQueryAsync();
                        }
                        catch (MySqlException e)
                        {
                            Log.WriteError($"Failed to change {monitor.Server.GetString()}: {e.Message}");
                        }

                        monitor.Server = cm;
                        changed.Add(uniqueKey);
                    }

                    continue;
                }

                var newMonitor = new Monitor(cm, SharedConfig);

                monitors.TryAdd(uniqueKey, newMonitor);

                await UpdateCMStatus(newMonitor, EResult.Pending, "New server");

                newMonitor.Connect(now + TimeSpan.FromSeconds(++x % 40));
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
                await using var cmd = new MySqlCommand("INSERT INTO `CMs` (`Address`, `IsWebSocket`, `Datacenter`, `Status`) VALUES(@IP, @IsWebSocket, @Datacenter, @Status) ON DUPLICATE KEY UPDATE `Status` = VALUES(`Status`)", db);
                cmd.Parameters.AddWithValue("@IP", keyName);
                cmd.Parameters.AddWithValue("@IsWebSocket", monitor.Server.IsWebSocket);
                cmd.Parameters.AddWithValue("@Datacenter", monitor.Server.Datacenter);
                cmd.Parameters.AddWithValue("@Status", (int)result);

                await cmd.ExecuteNonQueryAsync();

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

            if (++CellID >= HighestCellId)
            {
                CellID = 0;
            }
        }

        private async Task ScanAllCellIds()
        {
            Log.WriteInfo("Updating CM list using webapi by checking all cellids");

            foreach (var cellId in Enumerable.Range(0, HighestCellId).OrderBy(x => Random.Shared.Next()).Select(v => (uint)v))
            {
                try
                {
                    var servers = (await LoadCMList(SharedConfig, cellId)).ToList();

                    Log.WriteInfo($"Got {servers.Count} servers from cell {cellId}");

                    await UpdateCMList(servers);
                    await Task.Delay(Random.Shared.Next(10, 1000));
                }
                catch (Exception e)
                {
                    Log.WriteError($"Web API Exception: {e}");
                }
            }
        }

        private async Task<MySqlConnection> GetConnection()
        {
            var connection = new MySqlConnection(databaseConnectionString);

            await connection.OpenAsync(Program.Cts.Token);

            return connection;
        }

        private static async Task<List<DatabaseRecord>> LoadCMList(SteamConfiguration configuration, uint cellId)
        {
            var directory = configuration.GetAsyncWebAPIInterface("ISteamDirectory");
            var args = new Dictionary<string, object?>
            {
                ["cmtype"] = "websockets",
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
                var endpoint = child["endpoint"].AsString();
                var dc = child["dc"].AsString();

                if (endpoint == null || dc == null)
                {
                    continue;
                }

                serverRecords.Add(new DatabaseRecord(
                    endpoint,
                    dc,
                    child["type"].AsString() == "websockets"
                ));
            }

            return serverRecords;
        }
    }
}
