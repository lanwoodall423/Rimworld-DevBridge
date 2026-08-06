using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Verse;

namespace RimWorldDevBridge
{
    // Runtime-only, server-controlled authorization for remote mutations. The game object is never
    // retained; its process-local identity is bound to the current session and save transition.
    internal sealed class BridgeMutationConfirmation
    {
        internal const string Warning = "Remote tools may modify or destroy this game.";

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
                    .Warn(Warning);
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
            if (game == null || game.InitData == null) return null;
            string loadedSave = null;
            try
            {
                FieldInfo field = game.InitData.GetType().GetField("gameToLoad",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                loadedSave = field?.GetValue(game.InitData) as string;
            }
            catch
            {
                return null;
            }
            if (string.IsNullOrWhiteSpace(loadedSave)) return null;

            // Hash only the server-observed GameInitData.gameToLoad value with a versioned domain.
            // The raw save name/path is never retained or published; the identity lasts only for
            // this process/session binding and changes when the loaded-save value changes.
            string material = "rimworld-devbridge-save-v1\n" + loadedSave.Trim().Replace('\\', '/');
            using (SHA256 sha = SHA256.Create())
            {
                byte[] digest = sha.ComputeHash(Encoding.UTF8.GetBytes(material));
                return "save-v1-" + BitConverter.ToString(digest).Replace("-", string.Empty)
                    .ToLowerInvariant();
            }
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
