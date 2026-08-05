using System;
using System.Runtime.CompilerServices;
using Verse;

namespace RimWorldDevBridge
{
    // Runtime-only, server-controlled authorization for remote mutations. The game object is never
    // retained; its process-local identity is bound to the current session and save transition.
    internal sealed class BridgeMutationConfirmation
    {
        private readonly object gate = new object();
        private string sessionId;
        private string gameIdentity;
        private string saveIdentity;
        private DateTime? confirmedUtc;

        internal void BindCurrentGame(string nextSessionId, Game game)
        {
            string nextGameIdentity = IdentityFor(game);
            string nextSaveIdentity = SaveIdentityFor(game);
            lock (gate)
            {
                if (!string.Equals(sessionId, nextSessionId, StringComparison.Ordinal) ||
                    !string.Equals(gameIdentity, nextGameIdentity, StringComparison.Ordinal) ||
                    !string.Equals(saveIdentity, nextSaveIdentity, StringComparison.Ordinal))
                    confirmedUtc = null;
                sessionId = nextSessionId;
                gameIdentity = nextGameIdentity;
                saveIdentity = nextSaveIdentity;
            }
        }

        internal void Invalidate(string nextSessionId)
        {
            lock (gate)
            {
                sessionId = nextSessionId;
                gameIdentity = null;
                saveIdentity = null;
                confirmedUtc = null;
            }
        }

        internal BridgeResult Confirm(string currentSessionId, string currentGameIdentity,
            string currentSaveIdentity)
        {
            lock (gate)
            {
                if (string.IsNullOrWhiteSpace(currentSessionId) ||
                    !string.Equals(sessionId, currentSessionId, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(gameIdentity) ||
                     !string.Equals(gameIdentity, currentGameIdentity, StringComparison.Ordinal) ||
                     !string.Equals(saveIdentity, currentSaveIdentity, StringComparison.Ordinal))
                    return BridgeResult.Fail(BridgeStatus.UNAVAILABLE, "no_game_loaded");
                confirmedUtc = DateTime.UtcNow;
                return BridgeResult.Ok("core.mutationConfirmation")
                    .Add("state", "confirmed")
                    .Add("gameIdentity", gameIdentity)
                    .Add("saveIdentity", saveIdentity)
                    .Warn("Remote tools may modify or destroy game state.");
            }
        }

        internal void Revoke()
        {
            lock (gate) confirmedUtc = null;
        }

        internal bool IsConfirmed(string currentSessionId, string currentGameIdentity,
            string currentSaveIdentity)
        {
            lock (gate)
            {
                return confirmedUtc.HasValue &&
                    string.Equals(sessionId, currentSessionId, StringComparison.Ordinal) &&
                    string.Equals(gameIdentity, currentGameIdentity, StringComparison.Ordinal) &&
                    string.Equals(saveIdentity, currentSaveIdentity, StringComparison.Ordinal);
            }
        }

        internal BridgeMutationConfirmationSnapshot Snapshot(string currentSessionId, bool remoteMutationEnabled)
        {
            lock (gate)
            {
                bool loaded = !string.IsNullOrWhiteSpace(gameIdentity) &&
                    string.Equals(sessionId, currentSessionId, StringComparison.Ordinal);
                bool confirmed = loaded && confirmedUtc.HasValue;
                return new BridgeMutationConfirmationSnapshot(remoteMutationEnabled, loaded, confirmed,
                    loaded ? (confirmed ? "confirmed" : "missing") : "no_game_loaded", sessionId,
                    gameIdentity, saveIdentity, confirmedUtc);
            }
        }

        internal static string IdentityFor(Game game)
        {
            return game == null ? null : "game-" + RuntimeHelpers.GetHashCode(game).ToString("X8");
        }

        internal static string SaveIdentityFor(Game game)
        {
            return game == null ? null : "save-" + RuntimeHelpers.GetHashCode(game).ToString("X8");
        }
    }

    internal sealed class BridgeMutationConfirmationSnapshot
    {
        internal readonly bool RemoteMutationEnabled;
        internal readonly bool GameLoaded;
        internal readonly bool Confirmed;
        internal readonly string State;
        internal readonly string SessionId;
        internal readonly string GameIdentity;
        internal readonly string SaveIdentity;
        internal readonly DateTime? ConfirmedUtc;

        internal BridgeMutationConfirmationSnapshot(bool remoteMutationEnabled, bool gameLoaded, bool confirmed,
            string state, string sessionId, string gameIdentity, string saveIdentity, DateTime? confirmedUtc)
        {
            RemoteMutationEnabled = remoteMutationEnabled;
            GameLoaded = gameLoaded;
            Confirmed = confirmed;
            State = state;
            SessionId = sessionId;
            GameIdentity = gameIdentity;
            SaveIdentity = saveIdentity;
            ConfirmedUtc = confirmedUtc;
        }

        internal bool Visible => GameLoaded && RemoteMutationEnabled;
    }
}
