using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
            List<ServerRecord> servers;

            using (var db = GetConnection())
            {
                // Reset all statuses
                db.Execute("UPDATE `CMs` SET `Status` = 'Invalid', `LastAction` = 'Application start'");
                
                // Seed CM list with old CMs in the database
                servers = db.Query<string>("SELECT `Address` FROM `CMs`").Select(StringToServerRecord).ToList();
            }

            Log.WriteInfo("Database", "Got {0} old CMs", servers.Count);

            UpdateCMList(servers);

            UpdateCMListViaWebAPI().Forget();
        }

        public void Stop()
        {
            Log.WriteInfo("SteamManager", "Stopping");

            foreach (var monitor in monitors.Values)
            {
                Log.WriteInfo("SteamManager", "Disconnecting monitor {0}", monitor.Server.EndPoint);

                monitor.Disconnect();
            }

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
                UpdateCMListViaWebAPI().Forget();
            }
        }

        public void UpdateCMList(IEnumerable<ServerRecord> cmList)
        {
            var x = 0;

            foreach (var cm in cmList)
            {
                if (cm.ProtocolTypes.HasFlag(ProtocolTypes.WebSocket))
                {
                    continue;
                }

                if (monitors.Keys.FirstOrDefault(s => s.EndPoint.Equals(cm.EndPoint)) != null)
                {
                    continue;
                }

                var newMonitor = new Monitor(cm);

                monitors.TryAdd(cm, newMonitor);

                newMonitor.Connect(DateTime.Now + TimeSpan.FromSeconds(++x % 40));

                Log.WriteInfo("SteamManager", "CM {0} has been added to CM list", cm.EndPoint);
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
            var keyName = monitor.Server.EndPoint.ToString();

            Log.WriteInfo("CM", "{0,21} | {1,20} | {2}", keyName, result.ToString(), lastAction);

            try
            {
                using (var db = GetConnection())
                {
                    db.Execute(
                        "INSERT INTO `CMs` (`Address`, `Status`, `LastAction`) VALUES(@IP, @Status, @LastAction) ON DUPLICATE KEY UPDATE `Status` = VALUES(`Status`), `LastAction` = VALUES(`LastAction`)",
                        new
                        {
                            IP = keyName,
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
            NextCMListUpdate = DateTime.Now + TimeSpan.FromMinutes(15);

            Log.WriteInfo("Web API", "Updating CM list");

            try
            {
                var servers = (await LoadAsync()).ToList();

                Log.WriteInfo("Web API", "Got {0} CMs", servers.Count);

                UpdateCMList(servers);
            }
            catch (Exception e)
            {
                Log.WriteError("Web API", "{0}", e);
            }
        }
        
        private static ServerRecord StringToServerRecord(string server)
        {
            var ep = server.Split(':');

            if (ep.Length != 2)
            {
                throw new FormatException("Invalid endpoint format");
            }

            if (!IPAddress.TryParse(ep[0], out var ip))
            {
                throw new FormatException("Invalid ip-adress");
            }

            if (!int.TryParse(ep[1], out var port))
            {
                throw new FormatException("Invalid port");
            }

            return ServerRecord.CreateSocketServer(new IPEndPoint(ip, port));
        }

        private static Task<IEnumerable<ServerRecord>> LoadAsync()
        {
            var directory = WebAPI.GetAsyncInterface("ISteamDirectory");
            var args = new Dictionary<string, string>
            {
                { "cellid", "0" },
                { "maxcount", "1000" },
            };

            var task = directory.CallAsync(HttpMethod.Get, "GetCMList", version: 1, args: args);
            return task.ContinueWith(t =>
                {
                    var response = task.Result;
                    var result = (EResult)response["result"].AsInteger();

                    if (result != EResult.OK)
                    {
                        throw new InvalidOperationException(string.Format("Steam Web API returned EResult.{0}", result));
                    }

                    var list = response["serverlist"];

                    var endPoints = new List<ServerRecord>(list.Children.Count);
                    endPoints.AddRange(list.Children.Select(child => StringToServerRecord(child.Value)));

                    return endPoints.AsEnumerable();
                }, System.Threading.CancellationToken.None, TaskContinuationOptions.NotOnCanceled | TaskContinuationOptions.NotOnFaulted, TaskScheduler.Current);
        }

        private MySqlConnection GetConnection()
        {
            var connection = new MySqlConnection(databaseConnectionString);

            connection.Open();

            return connection;
        }
    }
}
