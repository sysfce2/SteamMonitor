using SteamKit2;
using SteamKit2.Internal;

namespace StatusService
{
    class SteamMonitorUser : ClientMsgHandler
    {
        public void LogOn()
        {
            var logonMsg = new ClientMsgProtobuf<CMsgClientLogon>(EMsg.ClientLogon);

            var steamId = new SteamID(0, SteamID.AllInstances, Client.Universe, EAccountType.AnonUser);

            logonMsg.ProtoHeader.steamid = steamId;
            logonMsg.Body.protocol_version = MsgClientLogon.CurrentProtocol;
            logonMsg.Body.obfustucated_private_ip = 1337;

            Client.Send(logonMsg);

            // See https://github.com/SteamRE/SteamKit/blob/f4ff8ed85155a9868c4ae730298847b4957b7a5d/SteamKit2/SteamKit2/Steam/Handlers/SteamGameServer/SteamGameServer.cs#L134
        }

        public override void HandleMsg(IPacketMsg packetMsg)
        {
        }
    }
}
