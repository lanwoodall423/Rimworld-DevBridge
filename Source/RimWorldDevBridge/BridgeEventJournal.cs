using System;
using System.Collections.Generic;
using System.Linq;

namespace RimWorldDevBridge
{
    // The journal owns bounded event retention and its locked projection; diagnostics only routes EVENTS.
    internal static class BridgeEventJournal
    {
        private const int Limit = 512;
        private static readonly object Gate = new object();
        private static readonly Queue<EventRecord> Values = new Queue<EventRecord>();
        private static long sequence;

        internal static void Record(string kind, string detail)
        {
            lock (Gate)
            {
                Values.Enqueue(new EventRecord
                {
                    Sequence = ++sequence,
                    Utc = DateTime.UtcNow,
                    Kind = BridgeText.Clean(kind),
                    Detail = BridgeText.Clean(detail)
                });
                while (Values.Count > Limit) Values.Dequeue();
            }
        }

        internal static BridgeResult Report(BridgeRequest request)
        {
            BridgeQuery query = BridgeQuery.Parse(request.Argument, request.SessionId, request.Command,
                out BridgeResult failure);
            if (failure != null) return failure;
            List<EventRecord> values;
            lock (Gate) values = Values.Where(item => string.IsNullOrEmpty(query.Filter) ||
                item.Kind.IndexOf(query.Filter, StringComparison.OrdinalIgnoreCase) >= 0 ||
                item.Detail.IndexOf(query.Filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            BridgeResult result = BridgeResult.Ok("core.events").Add("total", values.Count)
                .Add("offset", query.Offset).Add("limit", query.Limit);
            foreach (EventRecord item in values.Skip(query.Offset).Take(query.Limit))
                result.AddLine("event=seq:" + item.Sequence + " utc:" + item.Utc.ToString("o") +
                    " kind:" + item.Kind + " detail:" + item.Detail);
            int next = query.Offset + query.Limit;
            result.Add("hasMore", next < values.Count);
            if (next < values.Count)
            {
                result.Truncated = true;
                result.ContinuationCursor = BridgeCursor.Encode(request.SessionId, request.Command,
                    query.CursorScope, next);
            }
            return result;
        }

        private sealed class EventRecord
        {
            internal long Sequence;
            internal DateTime Utc;
            internal string Kind;
            internal string Detail;
        }
    }
}
