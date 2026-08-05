using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;

namespace RimWorldDevBridge
{
    // Generation-scoped transport resources. Network workers may use this state,
    // but they must not access RimWorld or Unity state through it.
    internal sealed class BridgeTransportState
    {
        internal readonly int Generation;
        internal readonly TcpListener Listener;
        internal readonly string SessionId;
        internal readonly string Token;
        internal readonly ConcurrentDictionary<TcpClient, byte> Clients =
            new ConcurrentDictionary<TcpClient, byte>();
        internal volatile Timer IdleTimer;
        internal volatile bool Invalidated;
        internal int ActiveClients;

        internal BridgeTransportState(int generation, TcpListener listener, string sessionId, string token)
        {
            Generation = generation;
            Listener = listener;
            SessionId = sessionId;
            Token = token;
        }
    }
}
