// YoyoStringNetwork.cs - tells clients who is on the other end of which yoyo string.
//
// PropNetwork announces (puck -> kind); that is enough to build a cone, because a cone
// is entirely contained in the puck it rides on. A yoyo string is a link between two
// networked objects, so the announcement has to carry both ends: (puck -> owning player).
//
// The message is a FULL SET, not a delta. Yoyo pairings churn - every touch can move a
// player onto a different puck - and reconciling add/remove deltas against a client that
// missed one is far more code than just resending the (tiny) whole list. A client applies
// what it is told and drops any string not in it, so one message always leaves the client
// exactly right.
//
// Unlike props this does not broadcast purely on a timer: it sends when the set actually
// CHANGES, plus a slow heartbeat for late joiners. A 5 s timer would leave a freshly
// yanked puck stringless for most of the yank.

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace MaxPractice
{
    public static class YoyoStringNetwork
    {
        private const string MessageName = "MaxPractice.YoyoString";
        private const int MaxPairsPerMessage = 64;
        private const float HeartbeatInterval = 5f;
        private const float PendingTimeout = 20f;

        private static bool _handlerRegistered;
        private static float _nextHeartbeat;
        private static int _lastClientCount;

        // Server: what we last put on the wire, so an unchanged set costs nothing.
        private static readonly List<ulong> _sentPuckIds = new List<ulong>();
        private static readonly List<ulong> _sentOwnerIds = new List<ulong>();

        // Server scratch.
        private static readonly List<ulong> _puckIds = new List<ulong>();
        private static readonly List<ulong> _ownerIds = new List<ulong>();
        private static readonly List<KeyValuePair<ulong, Puck>> _pairs = new List<KeyValuePair<ulong, Puck>>();
        private static readonly List<ulong> _targetClients = new List<ulong>();

        // Client receive scratch, deliberately NOT the server's lists. A host never
        // receives its own announcement so they could not collide today, but sharing them
        // would make that a load-bearing detail rather than an incidental one.
        private static readonly List<ulong> _rxPuckIds = new List<ulong>();
        private static readonly List<ulong> _rxOwnerIds = new List<ulong>();

        // Client: live strings, keyed by the puck's NetworkObjectId, and who each is tied
        // to. The owner map is what lets an unchanged pairing be skipped entirely: on a
        // host ApplySet runs every frame, and re-resolving and re-binding a string that is
        // already correct is both wasted work and, until the visual stopped resetting
        // itself on every Bind, the reason the rope never got to simulate.
        private static readonly Dictionary<ulong, YoyoStringVisual> _live = new Dictionary<ulong, YoyoStringVisual>();
        private static readonly Dictionary<ulong, ulong> _liveOwner = new Dictionary<ulong, ulong>();

        // Announced pairings whose puck or player hasn't spawned locally yet.
        private static readonly Dictionary<ulong, ulong> _pending = new Dictionary<ulong, ulong>();
        private static readonly Dictionary<ulong, float> _pendingDeadline = new Dictionary<ulong, float>();

        // One per call site: ApplySet and ResolvePending must not share a scratch list.
        private static readonly List<ulong> _applyScratch = new List<ulong>();
        private static readonly List<ulong> _pendingScratch = new List<ulong>();

        /// <summary>Pumped every frame from PracticeManager, on both server and client.</summary>
        public static void Tick()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening)
            {
                if (_handlerRegistered || _live.Count > 0 || _pending.Count > 0) ResetLocalState();
                return;
            }

            if (!_handlerRegistered && nm.CustomMessagingManager != null)
            {
                try
                {
                    nm.CustomMessagingManager.RegisterNamedMessageHandler(MessageName, OnMessage);
                    _handlerRegistered = true;
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[MaxPractice] Could not register the yoyo-string message handler: {ex.Message}");
                }
            }

            if (nm.IsServer) ServerTick(nm);

            if (_pending.Count > 0) ResolvePending();
        }

        public static void Shutdown()
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (_handlerRegistered && nm != null && nm.CustomMessagingManager != null)
                    nm.CustomMessagingManager.UnregisterNamedMessageHandler(MessageName);
            }
            catch { }

            ResetLocalState();
        }

        private static void ResetLocalState()
        {
            _handlerRegistered = false;
            _nextHeartbeat = 0f;
            _lastClientCount = 0;

            foreach (var kv in _live)
            {
                if (kv.Value != null)
                {
                    try { UnityEngine.Object.Destroy(kv.Value.gameObject); } catch { }
                }
            }
            _live.Clear();
            _liveOwner.Clear();

            _pending.Clear();
            _pendingDeadline.Clear();
            _sentPuckIds.Clear();
            _sentOwnerIds.Clear();
        }

        // ------------------------------------------------------------------
        // Server
        // ------------------------------------------------------------------

        private static void ServerTick(NetworkManager nm)
        {
            BuildCurrentSet();

            // A host is a client too and never receives its own message, so it applies the
            // set directly. On a dedicated server HasGraphics is false and this is a no-op.
            if (PropStyle.HasGraphics) ApplySet(_puckIds, _ownerIds);

            bool joined = false;
            int clientCount = nm.ConnectedClientsIds != null ? nm.ConnectedClientsIds.Count : 0;
            if (clientCount > _lastClientCount) joined = true;
            _lastClientCount = clientCount;

            bool changed = !SameAsSent();
            bool heartbeat = Time.unscaledTime >= _nextHeartbeat;

            if (!changed && !heartbeat && !joined) return;

            _nextHeartbeat = Time.unscaledTime + HeartbeatInterval;
            Broadcast(nm);
        }

        /// <summary>
        /// Poll the live pairings rather than having every mutation site notify us.
        /// YoyoManager writes playerYoyoPucks from a dozen places - touches, yanks,
        /// detaches, disconnects, phase cleanup - and a poll cannot miss one.
        /// </summary>
        private static void BuildCurrentSet()
        {
            _puckIds.Clear();
            _ownerIds.Clear();

            if (!ConfigManager.Config.EnableYoyoString) return;

            YoyoManager.CollectActivePairs(_pairs);
            if (_pairs.Count == 0) return;

            for (int i = 0; i < _pairs.Count; i++)
            {
                var puck = _pairs[i].Value;
                if (puck == null) continue;

                var puckObj = puck.NetworkObject;
                if (puckObj == null || !puckObj.IsSpawned) continue;

                var owner = PracticeHelpers.FindPlayerBySteamId(_pairs[i].Key);
                if (owner == null) continue;

                var ownerObj = owner.NetworkObject;
                if (ownerObj == null || !ownerObj.IsSpawned) continue;

                _puckIds.Add(puckObj.NetworkObjectId);
                _ownerIds.Add(ownerObj.NetworkObjectId);
                if (_puckIds.Count >= MaxPairsPerMessage) break;
            }
        }

        private static bool SameAsSent()
        {
            if (_puckIds.Count != _sentPuckIds.Count) return false;
            for (int i = 0; i < _puckIds.Count; i++)
            {
                if (_puckIds[i] != _sentPuckIds[i]) return false;
                if (_ownerIds[i] != _sentOwnerIds[i]) return false;
            }
            return true;
        }

        private static void Broadcast(NetworkManager nm)
        {
            try
            {
                if (nm.CustomMessagingManager == null) return;

                _targetClients.Clear();
                var connected = nm.ConnectedClientsIds;
                if (connected != null)
                {
                    for (int i = 0; i < connected.Count; i++)
                    {
                        // The host already applied the set locally in ServerTick.
                        if (nm.IsHost && connected[i] == nm.LocalClientId) continue;
                        _targetClients.Add(connected[i]);
                    }
                }

                // Record what we would have sent even with nobody to send it to, so the
                // first client to join gets a broadcast on the join edge rather than
                // matching against a set that was never actually on the wire.
                _sentPuckIds.Clear();
                _sentPuckIds.AddRange(_puckIds);
                _sentOwnerIds.Clear();
                _sentOwnerIds.AddRange(_ownerIds);

                if (_targetClients.Count == 0) return;

                // An empty set is a real message: it is how a client is told to drop the
                // strings it is still drawing.
                int size = sizeof(int) + _puckIds.Count * (sizeof(ulong) * 2);
                using (var writer = new FastBufferWriter(size, Allocator.Temp))
                {
                    writer.WriteValueSafe(_puckIds.Count);
                    for (int i = 0; i < _puckIds.Count; i++)
                    {
                        writer.WriteValueSafe(_puckIds[i]);
                        writer.WriteValueSafe(_ownerIds[i]);
                    }

                    nm.CustomMessagingManager.SendNamedMessage(
                        MessageName, _targetClients, writer, NetworkDelivery.ReliableSequenced);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Failed to broadcast yoyo strings: {ex.Message}");
            }
        }

        // ------------------------------------------------------------------
        // Client
        // ------------------------------------------------------------------

        private static void OnMessage(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                // Only the server announces. A client that could send this could draw a
                // string between any two objects on everyone else's screen.
                if (senderClientId != NetworkManager.ServerClientId) return;

                reader.ReadValueSafe(out int count);
                if (count < 0 || count > MaxPairsPerMessage) return;

                _rxPuckIds.Clear();
                _rxOwnerIds.Clear();
                for (int i = 0; i < count; i++)
                {
                    reader.ReadValueSafe(out ulong puckId);
                    reader.ReadValueSafe(out ulong ownerId);
                    _rxPuckIds.Add(puckId);
                    _rxOwnerIds.Add(ownerId);
                }

                ApplySet(_rxPuckIds, _rxOwnerIds);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Malformed yoyo-string message: {ex.Message}");
            }
        }

        /// <summary>
        /// Make the local strings match the announced set exactly: bind or rebind everything
        /// listed, drop everything that isn't.
        /// </summary>
        private static void ApplySet(List<ulong> puckIds, List<ulong> ownerIds)
        {
            if (!PropStyle.HasGraphics) return;

            // Drop strings that are no longer announced, and any whose object died.
            _applyScratch.Clear();
            foreach (var kv in _live)
            {
                if (kv.Value == null) { _applyScratch.Add(kv.Key); continue; }
                if (!puckIds.Contains(kv.Key)) _applyScratch.Add(kv.Key);
            }
            for (int i = 0; i < _applyScratch.Count; i++)
            {
                if (_live.TryGetValue(_applyScratch[i], out var visual) && visual != null)
                {
                    try { UnityEngine.Object.Destroy(visual.gameObject); } catch { }
                }
                _live.Remove(_applyScratch[i]);
                _liveOwner.Remove(_applyScratch[i]);
            }

            // Un-announced PENDING entries have to go too, and they are not in _live to be
            // caught by the sweep above. Without this, a pairing that was announced, failed
            // to resolve, and was then dropped server-side would still be waiting - and
            // ResolvePending would bind it the moment the puck spawned, drawing a string the
            // server never asked for until the next full set arrived.
            _applyScratch.Clear();
            foreach (var kv in _pending)
            {
                if (!puckIds.Contains(kv.Key)) _applyScratch.Add(kv.Key);
            }
            for (int i = 0; i < _applyScratch.Count; i++)
            {
                _pending.Remove(_applyScratch[i]);
                _pendingDeadline.Remove(_applyScratch[i]);
            }

            for (int i = 0; i < puckIds.Count; i++)
            {
                // Already tied to this exact player - leave it entirely alone so its
                // simulation keeps running.
                if (_live.TryGetValue(puckIds[i], out var bound) && bound != null &&
                    _liveOwner.TryGetValue(puckIds[i], out ulong boundOwner) && boundOwner == ownerIds[i])
                {
                    continue;
                }

                if (TryBind(puckIds[i], ownerIds[i]))
                {
                    _pending.Remove(puckIds[i]);
                    _pendingDeadline.Remove(puckIds[i]);
                }
                else
                {
                    // The puck or the player hasn't spawned locally yet - very common,
                    // because the announcement races the puck's own spawn message.
                    _pending[puckIds[i]] = ownerIds[i];
                    _pendingDeadline[puckIds[i]] = Time.unscaledTime + PendingTimeout;
                }
            }
        }

        private static void ResolvePending()
        {
            _pendingScratch.Clear();
            foreach (var kv in _pending)
            {
                bool expired = _pendingDeadline.TryGetValue(kv.Key, out float deadline)
                               && Time.unscaledTime >= deadline;
                if (TryBind(kv.Key, kv.Value) || expired) _pendingScratch.Add(kv.Key);
            }

            for (int i = 0; i < _pendingScratch.Count; i++)
            {
                _pending.Remove(_pendingScratch[i]);
                _pendingDeadline.Remove(_pendingScratch[i]);
            }
        }

        private static bool TryBind(ulong puckNetworkObjectId, ulong ownerNetworkObjectId)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.SpawnManager == null) return false;

            var spawned = nm.SpawnManager.SpawnedObjects;
            if (!spawned.TryGetValue(puckNetworkObjectId, out var puckObj) || puckObj == null) return false;
            if (!spawned.TryGetValue(ownerNetworkObjectId, out var ownerObj) || ownerObj == null) return false;

            var puck = puckObj.GetComponent<Puck>();
            if (puck == null) return false;

            var owner = ownerObj.GetComponent<Player>();
            if (owner == null) return false;

            var visual = YoyoStringAsset.Attach(owner, puck);
            if (visual == null) return false;

            _live[puckNetworkObjectId] = visual;
            _liveOwner[puckNetworkObjectId] = ownerNetworkObjectId;
            return true;
        }
    }
}
