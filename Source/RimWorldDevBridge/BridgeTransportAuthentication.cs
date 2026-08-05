using System;

namespace RimWorldDevBridge
{
    internal static class BridgeTransportAuthentication
    {
        internal static bool TrySplit(string raw, string expectedToken, out string payload)
        {
            payload = null;
            if (string.IsNullOrEmpty(raw) || string.IsNullOrEmpty(expectedToken)) return false;
            int separator = raw.IndexOf('|');
            if (separator <= 0) return false;
            string supplied = raw.Substring(0, separator);
            if (!ConstantTimeEquals(supplied, expectedToken)) return false;
            payload = raw.Substring(separator + 1);
            return true;
        }

        internal static bool ConstantTimeEquals(string left, string right)
        {
            if (left == null || right == null) return false;
            int difference = left.Length ^ right.Length;
            int length = Math.Max(left.Length, right.Length);
            for (int index = 0; index < length; index++)
            {
                int leftValue = index < left.Length ? left[index] : 0;
                int rightValue = index < right.Length ? right[index] : 0;
                difference |= leftValue ^ rightValue;
            }
            return difference == 0;
        }
    }
}
