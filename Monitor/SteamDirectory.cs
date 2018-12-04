using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using SteamKit2;
using SteamKit2.Discovery;

namespace StatusService
{
    // Mostly copied from SteamKit's LoadAsync because we have to set `maxcount`
    static class SteamDirectory
    {
        private static readonly Random Random = new Random();

        public static Task<IReadOnlyCollection<ServerRecord>> LoadAsync()
        {
            var directory = WebAPI.GetAsyncInterface("ISteamDirectory");
            var args = new Dictionary<string, string>
            {
                { "cellid", "0" },
                { "maxcount", "10000" },
            };

            var task = directory.CallAsync(HttpMethod.Get, "GetCMList", version: 1, args: args);
            return task.ContinueWith(_ =>
            {
                var response = task.Result;
                var result = (EResult)response["result"].AsInteger();

                if (result != EResult.OK)
                {
                    throw new InvalidOperationException(string.Format("Steam Web API returned EResult.{0}", result));
                }

                var socketList = response["serverlist"];
                var websocketList = response["serverlist_websockets"];

                var endPoints = new List<ServerRecord>(socketList.Children.Count + websocketList.Children.Count);

                endPoints.AddRange(socketList.Children.Select(child => StringToServerRecord(child.Value)));
                endPoints.AddRange(websocketList.Children.Select(child => ServerRecord.CreateWebSocketServer(child.Value)));

                return (IReadOnlyCollection<ServerRecord>)endPoints;
            }, System.Threading.CancellationToken.None, TaskContinuationOptions.NotOnCanceled | TaskContinuationOptions.NotOnFaulted, TaskScheduler.Current);
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

        public static string ServerRecordToString(ServerRecord record)
        {
            return $"{record.GetHost()}:{record.GetPort()}";
        }

        public static uint GetNextRandom()
        {
            return (uint)Random.Next(0, int.MaxValue);
        }
    }
}
