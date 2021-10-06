using SteamKit2;
using SteamKit2.Discovery;

namespace StatusService
{
    class DatabaseRecord
    {
        public string Hostname { get; }
        public int Port { get; }
        public bool IsWebSocket { get; }
        public string Datacenter { get; }

        public DatabaseRecord(string address, string datacenter, bool isWebsocket)
        {
            var indexOfColon = address.IndexOf(':');
            var portNumber = address.Substring(indexOfColon + 1);

            Hostname = address.Substring(0, indexOfColon);
            Port = int.Parse(portNumber);
            IsWebSocket = isWebsocket;
            Datacenter = datacenter;
        }

        public ServerRecord GetServerRecord() =>
            ServerRecord.CreateServer(Hostname, Port, IsWebSocket ? ProtocolTypes.WebSocket : ProtocolTypes.Tcp);

        public string GetUniqueKey() => $"{IsWebSocket}@{Hostname}";
        public string GetString() => $"{Hostname}:{Port}";
    }
}
