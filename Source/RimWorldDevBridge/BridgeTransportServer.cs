using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace RimWorldDevBridge
{
    // Owns worker-side socket protocol handling only. All RimWorld/Unity access
    // remains behind the preparation and lifecycle callbacks supplied by Runtime.
    internal sealed class BridgeTransportServer
    {
        private readonly BridgeTransportState state;
        private readonly Func<BridgeTransportState, bool> isCurrent;
        private readonly Func<BridgeRequest, BridgePreparationResult> prepare;
        private readonly Func<BridgeRequest, BridgeResult> enqueue;
        private readonly Func<string, string, bool> cancel;
        private readonly Func<int> clientLimit;
        private readonly Func<BridgeResult, BridgeRequest, string, string, BridgeResult> decorate;
        private readonly Func<string> currentSession;
        private readonly Action<BridgeTransportState> onIdle;
        private readonly Action<BridgeTransportState> refreshIndicator;
        private readonly Action<BridgeTransportState> markClientStateDirty;
        private readonly Action onActivity;

        internal BridgeTransportServer(BridgeTransportState state,
            Func<BridgeTransportState, bool> isCurrent,
            Func<BridgeRequest, BridgePreparationResult> prepare,
            Func<BridgeRequest, BridgeResult> enqueue,
            Func<string, string, bool> cancel,
            Func<int> clientLimit,
            Func<BridgeResult, BridgeRequest, string, string, BridgeResult> decorate,
            Func<string> currentSession,
            Action<BridgeTransportState> onIdle,
            Action<BridgeTransportState> refreshIndicator,
            Action<BridgeTransportState> markClientStateDirty,
            Action onActivity)
        {
            this.state = state;
            this.isCurrent = isCurrent;
            this.prepare = prepare;
            this.enqueue = enqueue;
            this.cancel = cancel;
            this.clientLimit = clientLimit;
            this.decorate = decorate;
            this.currentSession = currentSession;
            this.onIdle = onIdle;
            this.refreshIndicator = refreshIndicator;
            this.markClientStateDirty = markClientStateDirty;
            this.onActivity = onActivity;
        }

        internal void Start()
        {
            Thread listenerThread = new Thread(Listen)
            {
                IsBackground = true,
                Name = "RimWorld Dev Bridge v2"
            };
            listenerThread.Start();
            state.IdleTimer = new Timer(_ => onIdle(state), null, 10000, 10000);
        }

        internal static void Close(BridgeTransportState stale)
        {
            if (stale == null) return;
            stale.Invalidated = true;
            try { stale.IdleTimer?.Dispose(); } catch { }
            try { stale.Listener?.Stop(); } catch { }
            foreach (TcpClient client in stale.Clients.Keys)
            {
                try { client.Close(); } catch { }
            }
            stale.Clients.Clear();
            Volatile.Write(ref stale.ActiveClients, 0);
        }

        private void Listen()
        {
            while (isCurrent(state))
            {
                TcpClient client = null;
                try
                {
                    client = state.Listener.AcceptTcpClient();
                    if (!isCurrent(state))
                    {
                        client.Close();
                        client = null;
                        return;
                    }
                    if (Interlocked.Increment(ref state.ActiveClients) > clientLimit())
                    {
                        Interlocked.Decrement(ref state.ActiveClients);
                        WriteDirect(client, "id=unknown\nstatus=BUSY\nerror=connected_client_limit");
                        client = null;
                        continue;
                    }
                    state.Clients.TryAdd(client, 0);
                    TcpClient accepted = client;
                    client = null;
                    if (!isCurrent(state) || !state.Clients.ContainsKey(accepted))
                    {
                        RemoveClient(accepted);
                        continue;
                    }
                    if (!ThreadPool.QueueUserWorkItem(_ => HandleClient(state, accepted)))
                    {
                        RemoveClient(accepted);
                        refreshIndicator(state);
                    }
                    else
                    {
                        refreshIndicator(state);
                    }
                }
                catch (SocketException) { if (!isCurrent(state)) return; }
                catch (ObjectDisposedException) { return; }
                catch { try { client?.Close(); } catch { } }
            }
        }

        private void HandleClient(BridgeTransportState clientState, TcpClient client)
        {
            BridgeRequest request = null;
            try
            {
                if (!isCurrent(clientState)) return;
                client.NoDelay = true;
                client.ReceiveTimeout = BridgeProtocol.MaximumDeadlineMs;
                client.SendTimeout = BridgeProtocol.MaximumDeadlineMs;
                using (client)
                using (NetworkStream stream = client.GetStream())
                using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true))
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true)
                { AutoFlush = true, NewLine = "\n" })
                {
                    string raw;
                    try { raw = ReadBoundedLine(reader, BridgeProtocol.MaxRequestBytes); }
                    catch (InvalidDataException exception)
                    {
                        BridgeResult invalid = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT,
                            "request_too_large", exception.Message);
                        decorate(invalid, null, "core", BridgeProtocol.BridgeVersion);
                        writer.Write(BridgeProtocol.Serialize(invalid, "line"));
                        return;
                    }
                    if (!isCurrent(clientState) || string.IsNullOrEmpty(clientState.Token) ||
                        string.IsNullOrEmpty(clientState.SessionId) || !BridgeTransportAuthentication.TrySplit(raw,
                            clientState.Token, out string payload))
                    {
                        writer.Write("id=unknown\nstatus=FORBIDDEN\nerror=authentication_failed");
                        return;
                    }
                    onActivity();
                    if (!BridgeProtocol.TryParse(payload, clientState.SessionId, out request,
                        out BridgeResult parseFailure))
                    {
                        decorate(parseFailure, request, "core", BridgeProtocol.BridgeVersion);
                        writer.Write(BridgeProtocol.Serialize(parseFailure, "line"));
                        return;
                    }
                    request.TransportGeneration = clientState.Generation;
                    if (!isCurrent(clientState))
                    {
                        writer.Write("id=unknown\nstatus=FORBIDDEN\nerror=stale_transport");
                        return;
                    }
                    if (request.Command == "CANCEL")
                    {
                        BridgeResult cancelled = BridgeResult.Ok("core.cancel")
                            .Add("cancelled", cancel(request.Argument, request.AgentId));
                        decorate(cancelled, request, "core", BridgeProtocol.BridgeVersion);
                        writer.Write(BridgeProtocol.Serialize(cancelled, request.OutputFormat));
                        return;
                    }
                    long prepareStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    BridgePreparationResult preparation = prepare(request);
                    request.PreparationMs = BridgeTiming.Milliseconds(prepareStart);
                    BridgeCommandDescriptor descriptor = preparation.Descriptor;
                    if (descriptor == null)
                    {
                        BridgeResult unavailable = preparation.Failure ?? BridgeResult.Fail(
                            BridgeAdapterCatalog.Indexing ? BridgeStatus.BUSY : BridgeStatus.NOT_FOUND,
                            BridgeAdapterCatalog.Indexing ? "adapter_indexing" : "unknown_command");
                        decorate(unavailable, request, "core", BridgeProtocol.BridgeVersion);
                        writer.Write(BridgeProtocol.Serialize(unavailable, request.OutputFormat));
                        return;
                    }
                    if (preparation.Failure != null)
                    {
                        decorate(preparation.Failure, request, descriptor.Provider, descriptor.ProviderVersion);
                        writer.Write(BridgeProtocol.Serialize(preparation.Failure, request.OutputFormat));
                        return;
                    }
                    if (!isCurrent(clientState) || request.SessionId != currentSession())
                    {
                        writer.Write("id=unknown\nstatus=FORBIDDEN\nerror=stale_transport");
                        return;
                    }
                    request.EnqueuedUtc = DateTime.UtcNow;
                    BridgeResult enqueueFailure = enqueue(request);
                    if (enqueueFailure != null)
                    {
                        decorate(enqueueFailure, request, descriptor.Provider, descriptor.ProviderVersion);
                        writer.Write(BridgeProtocol.Serialize(enqueueFailure, request.OutputFormat));
                        return;
                    }
                    while (!request.Done.Wait(20))
                    {
                        if (!isCurrent(clientState))
                        {
                            request.Cancelled = true;
                            if (!request.Started) cancel(request.RequestId, null);
                        }
                        if (request.Expired)
                        {
                            request.Cancelled = true;
                            if (!request.Started) cancel(request.RequestId, null);
                        }
                        if (Disconnected(client))
                        {
                            request.ClientDisconnected = true;
                            if (!request.Started) cancel(request.RequestId, null);
                            return;
                        }
                    }
                    writer.Write(BridgeProtocol.Serialize(request.Result, request.OutputFormat));
                    onActivity();
                }
            }
            catch { if (request != null && !request.Started) request.ClientDisconnected = true; }
            finally
            {
                RemoveClient(client);
                refreshIndicator(clientState);
            }
        }

        private void RemoveClient(TcpClient client)
        {
            if (client == null) return;
            if (state.Clients.TryRemove(client, out _))
            {
                Interlocked.Decrement(ref state.ActiveClients);
                if (isCurrent(state)) markClientStateDirty(state);
            }
            try { client.Close(); } catch { }
        }

        private static bool Disconnected(TcpClient client)
        {
            try { return client.Client.Poll(0, SelectMode.SelectRead) && client.Client.Available == 0; }
            catch { return true; }
        }

        private static string ReadBoundedLine(StreamReader reader, int maxBytes)
        {
            StringBuilder value = new StringBuilder();
            while (true)
            {
                int next = reader.Read();
                if (next < 0 || next == '\n') break;
                if (next != '\r') value.Append((char)next);
                if (value.Length > maxBytes) throw new InvalidDataException("Request exceeds maximum bytes.");
            }
            if (Encoding.UTF8.GetByteCount(value.ToString()) > maxBytes)
                throw new InvalidDataException("Request exceeds maximum bytes.");
            return value.ToString();
        }

        private static void WriteDirect(TcpClient client, string response)
        {
            try
            {
                using (client)
                using (NetworkStream stream = client.GetStream())
                using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    writer.Write(response);
            }
            catch { try { client?.Close(); } catch { } }
        }
    }
}
