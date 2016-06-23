using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using MySql.Data.MySqlClient;
using SteamKit2;

namespace StatusService
{
    class SteamManager
    {
        static SteamManager _instance = new SteamManager();

        public static SteamManager Instance { get { return _instance; } }

        readonly ConcurrentDictionary<IPEndPoint, Monitor> monitors;

        readonly string databaseConnectionString;

        DateTime NextCMListUpdate;

        SteamManager()
        {
            monitors = new ConcurrentDictionary<IPEndPoint, Monitor>();

            if (!File.Exists("database.txt"))
            {
                Log.WriteError("Database", "Put your MySQL connection string in database.txt");

                Environment.Exit(1);
            }

            databaseConnectionString = File.ReadAllText("database.txt").Trim();
        }

        public void Start()
        {
            // Reset all statuses
            MySqlHelper.ExecuteNonQuery(databaseConnectionString, "UPDATE `CMs` SET `Status` = 0, `LastAction` = 'Application start'");

            // Seed CM list with old CMs in the database
            var servers = new List<IPEndPoint>();

            using (var reader = MySqlHelper.ExecuteReader(databaseConnectionString, "SELECT `Address` FROM `CMs`"))
            {
                while (reader.Read())
                {
                    servers.Add(StringToIPEndPoint(reader.GetString("Address")));
                }
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
                Log.WriteInfo("SteamManager", "Disconnecting monitor {0}", monitor.Server.ToString());

                monitor.Disconnect();
            }

            // Reset all statuses
            MySqlHelper.ExecuteNonQuery(databaseConnectionString, "UPDATE `CMs` SET `Status` = 0, `LastAction` = 'Application stop'");
        }

        public void Crash()
        {
            MySqlHelper.ExecuteNonQuery(databaseConnectionString, "DELETE FROM `CMs`");
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

        public void UpdateCMList(IEnumerable<IPEndPoint> cmList)
        {
            var newCms = cmList.Except(monitors.Keys).ToList();

            if (newCms.Any())
            {
                HandleNewCMs(newCms);
            }
        }

        public void NotifyCMOnline(Monitor monitor, string lastAction)
        {
            UpdateCMStatus(monitor, true, EResult.OK, lastAction);
        }

        public void NotifyCMOffline(Monitor monitor, EResult result, string lastAction)
        {
            UpdateCMStatus(monitor, false, result, lastAction);
        }

        private void UpdateCMStatus(Monitor monitor, bool isOnline, EResult result, string lastAction)
        {
            string keyName = monitor.Server.ToString();

            Log.WriteInfo("CM", "{1,7} | {0,21} | {2,20} | {3}", keyName, isOnline ? "online" : "OFFLINE", result.ToString(), lastAction);

            try
            {
                MySqlHelper.ExecuteNonQuery(
                    databaseConnectionString,
                    "INSERT INTO `CMs` (`Address`, `Status`, `LastAction`) VALUES(@IP, @Status, @LastAction) ON DUPLICATE KEY UPDATE `Status` = @Status, `LastAction` = @LastAction",
                    new[]
                    {
                        new MySqlParameter("@IP", keyName),
                        new MySqlParameter("@Status", isOnline),
                        new MySqlParameter("@LastAction", lastAction)
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
            NextCMListUpdate = DateTime.Now + TimeSpan.FromMinutes(15);

            Log.WriteInfo("Web API", "Updating CM list");

            try
            {
                var servers = await LoadAsync();

                Log.WriteInfo("Web API", "Got {0} CMs", servers.Count());

                UpdateCMList(servers);

                // handle any CMs that have gone away
                //var goneCms = monitors.Keys.Except(servers).ToList();
                //HandleGoneCMs(goneCms);
            }
            catch (Exception e)
            {
                Log.WriteError("Web API", "{0}", e);
            }
        }

        private void HandleNewCMs(List<IPEndPoint> newCms)
        {
            int x = 0;

            foreach (var newServer in newCms)
            {
                var newMonitor = new Monitor(newServer);

                monitors.TryAdd(newServer, newMonitor);

                newMonitor.Connect(DateTime.Now + TimeSpan.FromSeconds(++x % 40));

                Log.WriteInfo("SteamManager", "CM {0} has been added to CM list", newServer);
            }
        }

        /*private void HandleGoneCMs(List<IPEndPoint> goneCms)
        {
            foreach (var goneServer in goneCms)
            {
                Monitor goneMonitor;

                if (!monitors.TryRemove(goneServer, out goneMonitor))
                {
                    continue;
                }

                goneMonitor.Disconnect();

                Log.WriteInfo("SteamManager", "CM {0} has been removed from CM list", goneServer);

                MySqlHelper.ExecuteNonQuery(
                    databaseConnectionString,
                    "DELETE FROM `CMs` WHERE `Address` = @IP",
                    new MySqlParameter("@IP", goneServer.ToString())
                );
            }
        }*/

        private static IPEndPoint StringToIPEndPoint(string server)
        {
            string[] ep = server.Split(':');

            if (ep.Length != 2)
            {
                throw new FormatException("Invalid endpoint format");
            }

            IPAddress ip;
            if (!IPAddress.TryParse(ep[0], out ip))
            {
                throw new FormatException("Invalid ip-adress");
            }

            int port;
            if (!int.TryParse(ep[1], out port))
            {
                throw new FormatException("Invalid port");
            }

            return new IPEndPoint(ip, port);
        }

        public static Task<IEnumerable<IPEndPoint>> LoadAsync()
        {
            var directory = WebAPI.GetAsyncInterface("ISteamDirectory");
            var args = new Dictionary<string, string>
            {
                { "cellid", "0" },
                { "maxcount", "1000" },
            };

            var task = directory.Call("GetCMList", version: 1, args: args, secure: true);
            return task.ContinueWith(t =>
                {
                    var response = task.Result;
                    var result = (EResult)response["result"].AsInteger((int)EResult.Invalid);
                    if (result != EResult.OK)
                    {
                        throw new InvalidOperationException(string.Format("Steam Web API returned EResult.{0}", result));
                    }

                    var list = response["serverlist"];

                    var endPoints = new List<IPEndPoint>(capacity: list.Children.Count);

                    foreach (var child in list.Children)
                    {
                        endPoints.Add(StringToIPEndPoint(child.Value));
                    }

                    return endPoints.AsEnumerable();
                }, System.Threading.CancellationToken.None, TaskContinuationOptions.NotOnCanceled | TaskContinuationOptions.NotOnFaulted, TaskScheduler.Current);
        }
    }
}
