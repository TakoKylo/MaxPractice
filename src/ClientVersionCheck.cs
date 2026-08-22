// ClientVersionCheck.cs - Tell a player their MaxPractice is out of date, from the server.
//
// Steam reports a Workshop item as current long before the file on disk actually is, so a
// player can sit on a build that is weeks stale and be told by every UI they have that they
// are up to date. That is the case that generates bug reports, and it is invisible from the
// client side by definition - a build cannot know about a change made after it shipped.
//
// THE SIGNAL IS SILENCE. This is lifted, with thanks, from CompetitiveAdjustments'
// ClientVersionCheck, which solves the same problem on the same game: a current client sends
// a hello on connect; an old one does not, because the message did not exist when it was
// built, and no message invented later can ever be understood by a build that predates it.
// So absence is the detection. The cost is a grace timer, because "has not spoken yet" and
// "cannot speak" look identical until enough time has passed.
//
// Two of their decisions are kept deliberately:
//
//   Never kick. A stale client plays fine for the most part; the mod degrades rather than
//   breaks. Being ejected from a practice server over a Workshop cache is a worse experience
//   than the bug being reported.
//
//   Whisper, do not broadcast. Naming someone publicly for a stale file gets results and is a
//   social decision rather than a technical one. Not ours to make by default.
//
// The wording says unsubscribe and resubscribe rather than "update", because Steam insisting
// the item is current is the entire problem.

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace MaxPractice
{
    internal static class ClientVersionCheck
    {
        /// <summary>
        /// Bump on every Workshop upload. The number only has to increase - the server
        /// compares it against its own, so what it counts is unimportant.
        /// </summary>
        internal const int Build = 1;

        private const string MessageName = "MaxPractice/ClientVersion";

        /// <summary>
        /// How long a client gets to say hello before silence is taken as "too old to".
        /// Generous: a slow load, a scene change and a mod sync all land in here.
        /// </summary>
        private const float GraceSeconds = 25f;

        /// <summary>Never nag the same player more often than this.</summary>
        private const float RenotifySeconds = 600f;

        private static bool _handlerRegistered;
        private static bool _announced;
        private static float _nextSweep;

        private static readonly Dictionary<ulong, float> _firstSeen = new Dictionary<ulong, float>();
        private static readonly Dictionary<ulong, int> _reportedBuild = new Dictionary<ulong, int>();
        private static readonly Dictionary<ulong, float> _lastNotified = new Dictionary<ulong, float>();

        // ------------------------------------------------------------------

        /// <summary>Pumped every frame from PracticeManager, on both sides.</summary>
        internal static void Tick()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening)
            {
                _handlerRegistered = false;
                _announced = false;
                _firstSeen.Clear();
                _reportedBuild.Clear();
                _lastNotified.Clear();
                return;
            }

            if (!_handlerRegistered && nm.CustomMessagingManager != null)
            {
                try
                {
                    nm.CustomMessagingManager.RegisterNamedMessageHandler(MessageName, OnClientVersion);
                    _handlerRegistered = true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MaxPractice] Could not register the version handler: {ex.Message}");
                }
            }

            if (nm.IsServer) TickServer(nm);
            else TickClient(nm);
        }

        // ------------------------------------------------------------------
        // Client: say hello, once, as soon as there is a server to hear it
        // ------------------------------------------------------------------

        private static void TickClient(NetworkManager nm)
        {
            if (_announced || !nm.IsConnectedClient || nm.CustomMessagingManager == null) return;

            try
            {
                using (var writer = new FastBufferWriter(sizeof(int), Allocator.Temp))
                {
                    writer.WriteValueSafe(Build);
                    nm.CustomMessagingManager.SendNamedMessage(
                        MessageName, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
                }
                _announced = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Version hello failed: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------
        // Server: note who spoke, and whisper to those who could not
        // ------------------------------------------------------------------

        private static void OnClientVersion(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (nm == null || !nm.IsServer) return;      // clients ignore their own echo

                reader.ReadValueSafe(out int build);
                _reportedBuild[senderClientId] = build;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Malformed version message from {senderClientId}: {ex.Message}");
            }
        }

        private static void TickServer(NetworkManager nm)
        {
            if (Time.realtimeSinceStartup < _nextSweep) return;
            _nextSweep = Time.realtimeSinceStartup + 2f;

            var connected = nm.ConnectedClientsIds;
            if (connected == null) return;

            float now = Time.realtimeSinceStartup;

            for (int i = 0; i < connected.Count; i++)
            {
                ulong clientId = connected[i];
                if (nm.IsHost && clientId == nm.LocalClientId) continue;   // that is us

                if (!_firstSeen.ContainsKey(clientId))
                {
                    _firstSeen[clientId] = now;
                    continue;
                }

                if (now - _firstSeen[clientId] < GraceSeconds) continue;

                // Spoke, and is current: nothing to say.
                if (_reportedBuild.TryGetValue(clientId, out int theirBuild) && theirBuild >= Build) continue;

                if (_lastNotified.TryGetValue(clientId, out float last) && now - last < RenotifySeconds) continue;
                _lastNotified[clientId] = now;

                Notify(clientId, _reportedBuild.ContainsKey(clientId), theirBuild);
            }
        }

        private static void Notify(ulong clientId, bool spoke, int theirBuild)
        {
            // "Reported an older build" and "never spoke at all" are different facts, and
            // the second is the one that means really old. Say which.
            string detail = spoke
                ? $"yours is build {theirBuild}, this server is on {Build}"
                : "yours is old enough that it can't report its version";

            PracticeHelpers.SendChatMessage(null,
                "<size=70%><color=#c8a04a>Your MaxPractice is out of date: " + detail + ". " +
                "Steam often reports the item as current when it isn't: unsubscribe and resubscribe " +
                "to force it, then restart Puck. Practice commands still work in the meantime.</color></size>",
                clientId);

            ConfigManager.Log($"Version check: client {clientId} is out of date ({detail}).");
        }

        internal static void ForgetClient(ulong clientId)
        {
            _firstSeen.Remove(clientId);
            _reportedBuild.Remove(clientId);
            _lastNotified.Remove(clientId);
        }

        internal static void Shutdown()
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (_handlerRegistered && nm != null && nm.CustomMessagingManager != null)
                    nm.CustomMessagingManager.UnregisterNamedMessageHandler(MessageName);
            }
            catch { }

            _handlerRegistered = false;
            _announced = false;
            _firstSeen.Clear();
            _reportedBuild.Clear();
            _lastNotified.Clear();
        }
    }
}
