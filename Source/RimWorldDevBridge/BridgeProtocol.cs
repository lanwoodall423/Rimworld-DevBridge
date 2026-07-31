using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Text;

namespace RimWorldDevBridge
{
    public static class BridgeProtocol
    {
        public const string BridgeVersion = "2.0.2";
        public const int ProtocolVersion = 10;
        public const string CoreSchema = "v10-typed-core";
        public const int MaxRequestBytes = 32768;
        public const int MaxArgumentBytes = 24576;
        public const int MaxResponseBytes = 262144;
        public const int MaxLineBytes = 4096;
        public const int MaxBatchSections = 12;
        public const int MaxMacroCalls = 16;
        public const int MaxPageSize = 200;
        public const int DefaultPageSize = 50;
        public const int DefaultDeadlineMs = 4500;
        public const int MaximumDeadlineMs = 120000;

        public static bool TryParse(string raw, string currentSessionId, out BridgeRequest request,
            out BridgeResult failure)
        {
            request = null;
            failure = null;
            if (raw == null)
            {
                failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "empty_request");
                return false;
            }
            if (Encoding.UTF8.GetByteCount(raw) > MaxRequestBytes)
            {
                failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "request_too_large");
                return false;
            }
            string line = raw.Replace('\r', '\n').Split(new[] { '\n' },
                StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
            string[] parts = line.Split(new[] { '|' }, 4);
            if (parts.Length < 2)
            {
                failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_request_shape",
                    "Expected id|COMMAND|argument|options.");
                return false;
            }
            string id = parts[0].Trim();
            string command = BridgeText.NormalizeCommand(parts[1]);
            string argument = parts.Length > 2 ? parts[2] : string.Empty;
            if (id.Length == 0 || id.Length > 96 || command.Length == 0 || command.Length > 96)
            {
                failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_request_identity");
                return false;
            }
            if (Encoding.UTF8.GetByteCount(argument) > MaxArgumentBytes)
            {
                failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "argument_too_large");
                return false;
            }
            Dictionary<string, string> options;
            try
            {
                options = ParseOptions(parts.Length > 3 ? parts[3] : string.Empty);
            }
            catch (Exception exception)
            {
                failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_request_options",
                    exception.GetBaseException().Message);
                return false;
            }
            int timeoutMs = DefaultDeadlineMs;
            string timeoutValue = Value(options, "timeoutMs");
            if (!string.IsNullOrEmpty(timeoutValue) &&
                (!int.TryParse(timeoutValue, out timeoutMs) || timeoutMs < 50 || timeoutMs > MaximumDeadlineMs))
            {
                failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_timeout",
                    "timeoutMs must be an integer from 50 through " + MaximumDeadlineMs + ".");
                return false;
            }
            request = new BridgeRequest
            {
                RequestId = id,
                SessionId = Value(options, "session") ?? currentSessionId,
                Command = command,
                Argument = argument,
                EnqueuedUtc = DateTime.UtcNow,
                DeadlineUtc = DateTime.UtcNow.AddMilliseconds(timeoutMs),
                IdempotencyKey = Value(options, "idempotency"),
                OutputFormat = NormalizeFormat(Value(options, "format")),
                DetailLevel = Value(options, "detail") ?? "compact",
                AllowExpensive = ParseBool(Value(options, "allowExpensive")),
                AuthToken = Value(options, "lease")
            };
            return true;
        }

        public static string Serialize(BridgeResult result, string format)
        {
            if (string.Equals(format, "json", StringComparison.OrdinalIgnoreCase))
                return SerializeJson(result);
            return SerializeLines(result);
        }

        public static string SerializeLines(BridgeResult result)
        {
            List<string> lines = new List<string>
            {
                "id=" + BridgeText.Clean(result?.RequestId ?? "unknown"),
                "status=" + (result?.Status.ToString() ?? BridgeStatus.ERROR.ToString()),
                "session=" + BridgeText.Clean(result?.SessionId ?? "none"),
                "command=" + BridgeText.Clean(result?.Command ?? "none"),
                "provider=" + BridgeText.Clean(result?.Provider ?? "core") + " version:" +
                    BridgeText.Clean(result?.ProviderVersion ?? BridgeVersion),
                "mode=" + (result?.Mode.ToString() ?? BridgeCommandMode.PureRead.ToString()),
                "schema=" + BridgeText.Clean(result?.Schema ?? "core.result") + " version:" +
                    (result?.SchemaVersion ?? 1),
                "timing=queueMs:" + (result?.QueueDelayMs ?? 0d).ToString("0.###",
                    System.Globalization.CultureInfo.InvariantCulture) + " executionMs:" +
                    (result?.ExecutionMs ?? 0d).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "ticks=before:" + (result?.TickBefore ?? -1) + " after:" + (result?.TickAfter ?? -1),
                "mutation=" + BridgeText.Clean(result?.MutationSummary ?? "none")
            };
            if (result != null)
            {
                lines.AddRange(result.Data.Select(field => BridgeText.Clean(field.Name) + "=" +
                    BridgeText.Clean(field.Value)));
                lines.AddRange(result.Lines.Select(BridgeText.Clean));
                lines.AddRange(result.Warnings.Select(value => "warning=" + BridgeText.Clean(value)));
                if (result.Truncated)
                    lines.Add("truncated=true cursor:" + BridgeText.Clean(result.ContinuationCursor ?? "none"));
            }
            return BoundLines(lines, result);
        }

        private static string SerializeJson(BridgeResult result)
        {
            JsonResult payload = JsonResult.From(result);
            string value;
            using (MemoryStream stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(JsonResult)).WriteObject(stream, payload);
                value = Encoding.UTF8.GetString(stream.ToArray());
            }
            if (Encoding.UTF8.GetByteCount(value) <= MaxResponseBytes) return value;
            result.Truncated = true;
            result.Warn("JSON payload exceeded responseBytes; line-compatible metadata was returned.");
            return SerializeLines(result);
        }

        private static string BoundLines(IEnumerable<string> source, BridgeResult result)
        {
            StringBuilder output = new StringBuilder();
            foreach (string original in source ?? Enumerable.Empty<string>())
            {
                string line = BoundUtf8(original ?? string.Empty, MaxLineBytes);
                int candidate = Encoding.UTF8.GetByteCount(line) + (output.Length == 0 ? 0 : 1);
                if (Encoding.UTF8.GetByteCount(output.ToString()) + candidate > MaxResponseBytes)
                {
                    if (result != null) result.Truncated = true;
                    Append(output, "truncated=true reason:responseBytes");
                    break;
                }
                Append(output, line);
            }
            return output.ToString();
        }

        private static string BoundUtf8(string value, int maxBytes)
        {
            if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;
            int count = Math.Min(value.Length, maxBytes / 2);
            while (count > 0 && Encoding.UTF8.GetByteCount(value.Substring(0, count)) > maxBytes - 16) count--;
            return value.Substring(0, count) + "...<truncated>";
        }

        private static void Append(StringBuilder builder, string line)
        {
            if (builder.Length > 0) builder.Append('\n');
            builder.Append(line);
        }

        internal static Dictionary<string, string> ParseOptions(string value)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string entry in (value ?? string.Empty).Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int split = entry.IndexOf('=');
                if (split <= 0) continue;
                string rawKey = entry.Substring(0, split);
                string rawItem = entry.Substring(split + 1);
                if (!HasValidPercentEncoding(rawKey) || !HasValidPercentEncoding(rawItem))
                    throw new InvalidDataException("Option percent-encoding is malformed.");
                string key = Uri.UnescapeDataString(rawKey);
                string item = Uri.UnescapeDataString(rawItem);
                result[key] = item;
            }
            return result;
        }

        private static bool HasValidPercentEncoding(string value)
        {
            for (int i = 0; i < (value?.Length ?? 0); i++)
            {
                if (value[i] != '%') continue;
                if (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2]))
                    return false;
                i += 2;
            }
            return true;
        }

        internal static string Value(IDictionary<string, string> values, string key)
        {
            return values != null && values.TryGetValue(key, out string value) ? value : null;
        }

        internal static bool ParseBool(string value) =>
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase) || value == "1";

        internal static int ParseBoundedInt(string value, int fallback, int minimum, int maximum)
        {
            return int.TryParse(value, out int parsed) ? Math.Max(minimum, Math.Min(maximum, parsed)) : fallback;
        }

        private static string NormalizeFormat(string value) =>
            string.Equals(value, "json", StringComparison.OrdinalIgnoreCase) ? "json" : "line";

        [System.Runtime.Serialization.DataContract]
        private sealed class JsonResult
        {
            [System.Runtime.Serialization.DataMember(Order = 1)] public string requestId;
            [System.Runtime.Serialization.DataMember(Order = 2)] public string sessionId;
            [System.Runtime.Serialization.DataMember(Order = 3)] public string command;
            [System.Runtime.Serialization.DataMember(Order = 4)] public string provider;
            [System.Runtime.Serialization.DataMember(Order = 5)] public string providerVersion;
            [System.Runtime.Serialization.DataMember(Order = 6)] public string mode;
            [System.Runtime.Serialization.DataMember(Order = 7)] public string status;
            [System.Runtime.Serialization.DataMember(Order = 8)] public string schema;
            [System.Runtime.Serialization.DataMember(Order = 9)] public int schemaVersion;
            [System.Runtime.Serialization.DataMember(Order = 10)] public double queueDelayMs;
            [System.Runtime.Serialization.DataMember(Order = 11)] public double executionMs;
            [System.Runtime.Serialization.DataMember(Order = 12)] public int tickBefore;
            [System.Runtime.Serialization.DataMember(Order = 13)] public int tickAfter;
            [System.Runtime.Serialization.DataMember(Order = 14)] public bool truncated;
            [System.Runtime.Serialization.DataMember(Order = 15)] public string cursor;
            [System.Runtime.Serialization.DataMember(Order = 16)] public string mutation;
            [System.Runtime.Serialization.DataMember(Order = 17)] public List<BridgeField> data;
            [System.Runtime.Serialization.DataMember(Order = 18)] public List<string> lines;
            [System.Runtime.Serialization.DataMember(Order = 19)] public List<string> warnings;

            internal static JsonResult From(BridgeResult result) => new JsonResult
            {
                requestId = result?.RequestId,
                sessionId = result?.SessionId,
                command = result?.Command,
                provider = result?.Provider,
                providerVersion = result?.ProviderVersion,
                mode = result?.Mode.ToString(),
                status = result?.Status.ToString(),
                schema = result?.Schema,
                schemaVersion = result?.SchemaVersion ?? 1,
                queueDelayMs = result?.QueueDelayMs ?? 0d,
                executionMs = result?.ExecutionMs ?? 0d,
                tickBefore = result?.TickBefore ?? -1,
                tickAfter = result?.TickAfter ?? -1,
                truncated = result?.Truncated ?? false,
                cursor = result?.ContinuationCursor,
                mutation = result?.MutationSummary,
                data = result?.Data ?? new List<BridgeField>(),
                lines = result?.Lines ?? new List<string>(),
                warnings = result?.Warnings ?? new List<string>()
            };
        }
    }

    internal sealed class BridgeQuery
    {
        internal string Filter = string.Empty;
        internal int Limit = BridgeProtocol.DefaultPageSize;
        internal int Offset;
        internal string Fields;

        internal static BridgeQuery Parse(string argument, string sessionId, string command,
            out BridgeResult failure)
        {
            failure = null;
            BridgeQuery query = new BridgeQuery();
            string value = argument ?? string.Empty;
            if (value.IndexOf('=') < 0)
            {
                query.Filter = value.Trim();
                return query;
            }
            Dictionary<string, string> options = BridgeProtocol.ParseOptions(value.Replace(';', '&'));
            query.Filter = BridgeProtocol.Value(options, "filter") ?? string.Empty;
            query.Fields = BridgeProtocol.Value(options, "fields");
            query.Limit = BridgeProtocol.ParseBoundedInt(BridgeProtocol.Value(options, "limit"),
                BridgeProtocol.DefaultPageSize, 1, BridgeProtocol.MaxPageSize);
            string cursor = BridgeProtocol.Value(options, "cursor");
            if (!string.IsNullOrEmpty(cursor) && !BridgeCursor.TryDecode(cursor, sessionId, command,
                query.Filter, out query.Offset))
            {
                failure = BridgeResult.Fail(BridgeStatus.INVALID_ARGUMENT, "invalid_cursor");
                return null;
            }
            return query;
        }
    }

    internal static class BridgeCursor
    {
        internal static string Encode(string session, string command, string filter, int offset)
        {
            string raw = string.Join("\n", session ?? "", command ?? "", filter ?? "", offset.ToString());
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(raw)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        internal static bool TryDecode(string cursor, string session, string command, string filter, out int offset)
        {
            offset = 0;
            try
            {
                string value = cursor.Replace('-', '+').Replace('_', '/');
                value = value.PadRight((value.Length + 3) / 4 * 4, '=');
                string[] parts = Encoding.UTF8.GetString(Convert.FromBase64String(value)).Split('\n');
                return parts.Length == 4 && parts[0] == (session ?? "") && parts[1] == (command ?? "") &&
                    parts[2] == (filter ?? "") && int.TryParse(parts[3], out offset) && offset >= 0;
            }
            catch { return false; }
        }
    }
}
