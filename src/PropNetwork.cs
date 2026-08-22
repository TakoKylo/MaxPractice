// PropNetwork.cs - Tells connected clients which pucks are wearing which prop.
//
// A prop (cone, puck shooter, mini net) is a purely local visual: a child mesh on a
// frozen handle puck. Netcode replicates the puck's transform but not mesh or
// collider assignments, so the server has to name the prop pucks and let each
// client build its own copy.
//
// The server re-broadcasts the full list on an interval instead of tracking
// per-client acks: the payload is a handful of ulongs, and a periodic resend
// transparently covers late joiners, clients whose puck NetworkObject hadn't
// spawned yet when the first message landed, and the puck-spawn/message race.

using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace MaxPractice
{
    public static class PropNetwork
    {
        private const string MessageName = "MaxPractice.Props";
        private const float BroadcastInterval = 5f;
        private const float PendingTimeout = 20f;

        // Budget the payload in BYTES, not in props.
        //
        // Netcode refuses to send an unfragmented named message past the transport's
        // MTU-derived limit (UnityTransport's default is 1300 bytes) and throws out of
        // SendNamedMessage - which the catch in Broadcast would swallow as one warning a
        // broadcast, leaving every remote client staring at bare pucks with no obvious
        // cause. A hardcoded prop count cannot express that limit safely, and this is not
        // hypothetical: adding the owner id took the per-entry size from 9 bytes to 17 and
        // silently halved the count that fits, from ~145 to ~77. Derived from the wire
        // layout so the next field to be added moves the cap with it.
        private const int MaxMessageBytes = 1200;   // headroom under the 1300 cap
        private const int BytesPerProp = sizeof(ulong) + sizeof(byte) + sizeof(ulong);
        private const int MaxPropsPerMessage = (MaxMessageBytes - sizeof(int)) / BytesPerProp;

        private static float _nextTruncationWarning;

        private static bool _handlerRegistered;
        private static float _nextBroadcast;
        private static int _lastClientCount;

        // Props announced by the server whose puck hasn't spawned locally yet.
        private static readonly Dictionary<ulong, PropKind> _pending = new Dictionary<ulong, PropKind>();
        private static readonly Dictionary<ulong, float> _pendingDeadline = new Dictionary<ulong, float>();
        // Last announced owner per prop puck, so a pending prop still gets its
        // nameplate when it finally resolves.
        private static readonly Dictionary<ulong, ulong> _pendingOwner = new Dictionary<ulong, ulong>();

        private static readonly List<ulong> _broadcastIds = new List<ulong>();
        private static readonly List<byte> _broadcastKinds = new List<byte>();
        // 0 = nobody / not resolvable. The shooter wears its owner's name and team
        // colour, and only the server knows who spawned what.
        private static readonly List<ulong> _broadcastOwners = new List<ulong>();
        private static readonly List<ulong> _settledIds = new List<ulong>();
        private static readonly List<ulong> _targetClients = new List<ulong>();

        /// <summary>
        /// Build a prop locally. The one place that maps a kind onto an asset, so
        /// the server path and the client path can't drift.
        /// </summary>
        /// <param name="owner">
        /// The player who spawned it, or null if unknown. Only the shooter uses it - it
        /// wears their name and their team's colour - but it is threaded through the one
        /// mapping function so the server path and the client path cannot drift.
        /// </param>
        public static bool ApplyLocal(Puck puck, PropKind kind, bool withCollision, Player owner = null)
        {
            switch (kind)
            {
                case PropKind.Cone: return ConeAsset.Apply(puck, withCollision);
                case PropKind.Shooter: return ShooterAsset.Apply(puck, withCollision, owner);
                case PropKind.MiniNet: return MiniNetAsset.Apply(puck, withCollision, owner);
                default: return false;
            }
        }

        /// <summary>Pumped every frame from PracticeManager on both server and client.</summary>
        public static void Tick()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening)
            {
                _handlerRegistered = false;
                _nextBroadcast = 0f;
                _lastClientCount = 0;
                if (_pending.Count > 0) { _pending.Clear(); _pendingDeadline.Clear(); _pendingOwner.Clear(); }
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
                    Debug.LogWarning($"[MaxPractice] Could not register the prop message handler: {ex.Message}");
                }
            }

            if (nm.IsServer)
            {
                // Someone just joined - announce now rather than making them stare
                // at bare pucks until the next interval.
                int clientCount = nm.ConnectedClientsIds != null ? nm.ConnectedClientsIds.Count : 0;
                if (clientCount > _lastClientCount) _nextBroadcast = 0f;
                _lastClientCount = clientCount;

                if (Time.unscaledTime >= _nextBroadcast)
                {
                    _nextBroadcast = Time.unscaledTime + BroadcastInterval;
                    Broadcast(nm);
                }
            }

            if (_pending.Count > 0)
            {
                // Props are a warmup feature, and a pending entry keeps retrying for
                // PendingTimeout (20 s) while it waits for the puck to spawn locally. An
                // announcement that arrived in the last seconds of warmup could therefore
                // still turn a live puck into a cone well into play, on the client only,
                // with no server-side state saying it should be there. Drop them instead.
                //
                // Checked here rather than relying on the warmup-exit cleanup, because this
                // is client-side state and the client has to be right on its own.
                if (!PracticeHelpers.IsWarmup)
                {
                    _pending.Clear();
                    _pendingDeadline.Clear();
                    _pendingOwner.Clear();
                }
                else
                {
                    ResolvePending();
                }
            }
        }

        /// <summary>Server-side: a prop just spawned, so push the list out next tick.</summary>
        public static void Announce()
        {
            _nextBroadcast = 0f;
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

            _handlerRegistered = false;
            _nextBroadcast = 0f;
            _lastClientCount = 0;
            _pending.Clear();
            _pendingDeadline.Clear();
            _pendingOwner.Clear();
        }

        // ------------------------------------------------------------------
        // Server
        // ------------------------------------------------------------------

        private static void Broadcast(NetworkManager nm)
        {
            try
            {
                // Prune first - this is the only place dead prop pucks get reaped
                // when a player clears just their own.
                PruneDead();
                if (MaxPracticePlugin.PropPucks.Count == 0) return;
                if (nm.CustomMessagingManager == null) return;

                // Address remote clients explicitly. A host has already built its
                // props locally, so sending it a copy of its own announcement is
                // pure noise.
                _targetClients.Clear();
                var connected = nm.ConnectedClientsIds;
                if (connected != null)
                {
                    for (int i = 0; i < connected.Count; i++)
                    {
                        if (nm.IsHost && connected[i] == nm.LocalClientId) continue;
                        _targetClients.Add(connected[i]);
                    }
                }
                if (_targetClients.Count == 0) return;

                _broadcastIds.Clear();
                _broadcastKinds.Clear();
                _broadcastOwners.Clear();
                foreach (var kvp in MaxPracticePlugin.PropPucks)
                {
                    var puck = kvp.Key;
                    if (puck == null) continue;

                    var netObj = puck.NetworkObject;
                    if (netObj == null || !netObj.IsSpawned) continue;

                    _broadcastIds.Add(netObj.NetworkObjectId);
                    _broadcastKinds.Add((byte)kvp.Value);
                    _broadcastOwners.Add(ResolveOwnerNetworkObjectId(puck));
                    if (_broadcastIds.Count >= MaxPropsPerMessage) break;
                }

                if (_broadcastIds.Count == 0) return;

                // Say so when props are being left out, rather than letting a rink full of
                // bare pucks look like a rendering bug.
                if (MaxPracticePlugin.PropPucks.Count > _broadcastIds.Count &&
                    Time.unscaledTime >= _nextTruncationWarning)
                {
                    _nextTruncationWarning = Time.unscaledTime + 30f;
                    Debug.LogWarning($"[MaxPractice] {MaxPracticePlugin.PropPucks.Count} props on the ice but only " +
                                     $"{MaxPropsPerMessage} fit one announcement; the rest will not appear on clients.");
                }

                int size = sizeof(int) + _broadcastIds.Count * (sizeof(ulong) + sizeof(byte) + sizeof(ulong));
                using (var writer = new FastBufferWriter(size, Allocator.Temp))
                {
                    writer.WriteValueSafe(_broadcastIds.Count);
                    for (int i = 0; i < _broadcastIds.Count; i++)
                    {
                        writer.WriteValueSafe(_broadcastIds[i]);
                        writer.WriteValueSafe(_broadcastKinds[i]);
                        writer.WriteValueSafe(_broadcastOwners[i]);
                    }

                    nm.CustomMessagingManager.SendNamedMessage(
                        MessageName, _targetClients, writer, NetworkDelivery.ReliableSequenced);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Failed to broadcast props: {ex.Message}");
            }
        }

        /// <summary>
        /// The owning player's NetworkObjectId, or 0 when nobody owns it or they have left.
        ///
        /// The player's OBJECT id rather than their name or steamId: Username and Team are
        /// NetworkVariables already replicated to every client, so sending one id lets each
        /// client read both itself - and keep reading them, so a nameplate follows a rename
        /// or a team switch without the server having to re-announce anything.
        /// </summary>
        private static ulong ResolveOwnerNetworkObjectId(Puck puck)
        {
            try
            {
                if (!MaxPracticePlugin.PropOwner.TryGetValue(puck, out ulong steamId)) return 0UL;

                var owner = PracticeHelpers.FindPlayerBySteamId(steamId);
                if (owner == null) return 0UL;

                var netObj = owner.NetworkObject;
                return netObj != null && netObj.IsSpawned ? netObj.NetworkObjectId : 0UL;
            }
            catch { return 0UL; }
        }

        /// <summary>Client-side counterpart: the announced owner for a prop puck, if resolvable.</summary>
        private static Player ResolveOwnerPlayer(ulong propNetworkObjectId)
        {
            if (!_pendingOwner.TryGetValue(propNetworkObjectId, out ulong ownerId) || ownerId == 0UL)
                return null;

            var nm = NetworkManager.Singleton;
            if (nm == null || nm.SpawnManager == null) return null;
            if (!nm.SpawnManager.SpawnedObjects.TryGetValue(ownerId, out var netObj) || netObj == null)
                return null;

            return netObj.GetComponent<Player>();
        }

        /// <summary>Drop destroyed pucks from the registries so they stop being announced.</summary>
        private static void PruneDead()
        {
            // Deliberately does NOT touch _settledIds. That list belongs to the CLIENT
            // pending sweep, this runs on the server side of a host, and both run in the
            // same Tick - so borrowing it here was one refactor away from a real bug for no
            // benefit, since the local lists below are what actually collect the dead keys.
            List<Puck> dead = null;
            foreach (var kvp in MaxPracticePlugin.PropPucks)
            {
                if (kvp.Key == null)
                {
                    dead = dead ?? new List<Puck>();
                    dead.Add(kvp.Key);
                }
            }
            if (dead != null)
            {
                foreach (var d in dead)
                {
                    MaxPracticePlugin.PropPucks.Remove(d);
                    MaxPracticePlugin.PropOwner.Remove(d);
                }
            }

            // PropOwner can also outlive PropPucks - DestroyProp clears both, but a puck
            // destroyed by anything else only shows up here.
            List<Puck> deadOwners = null;
            foreach (var kvp in MaxPracticePlugin.PropOwner)
            {
                if (kvp.Key == null)
                {
                    deadOwners = deadOwners ?? new List<Puck>();
                    deadOwners.Add(kvp.Key);
                }
            }
            if (deadOwners != null)
            {
                foreach (var d in deadOwners) MaxPracticePlugin.PropOwner.Remove(d);
            }

            PruneOwnerMap(MaxPracticePlugin.PlayerShooter);
            PruneOwnerMap(MaxPracticePlugin.PlayerMiniNet);
        }

        private static void PruneOwnerMap(Dictionary<ulong, Puck> map)
        {
            List<ulong> dead = null;
            foreach (var kvp in map)
            {
                if (kvp.Value == null)
                {
                    dead = dead ?? new List<ulong>();
                    dead.Add(kvp.Key);
                }
            }
            if (dead == null) return;
            foreach (var k in dead) map.Remove(k);
        }

        // ------------------------------------------------------------------
        // Client
        // ------------------------------------------------------------------

        private static void OnMessage(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                // Only the server announces props. Ignore anything a client sends.
                if (senderClientId != NetworkManager.ServerClientId) return;

                reader.ReadValueSafe(out int count);
                if (count <= 0 || count > MaxPropsPerMessage) return;

                for (int i = 0; i < count; i++)
                {
                    reader.ReadValueSafe(out ulong networkObjectId);
                    reader.ReadValueSafe(out byte rawKind);
                    reader.ReadValueSafe(out ulong ownerNetworkObjectId);

                    if (!Enum.IsDefined(typeof(PropKind), rawKind)) continue;
                    var kind = (PropKind)rawKind;

                    _pendingOwner[networkObjectId] = ownerNetworkObjectId;

                    if (TryApply(networkObjectId, kind))
                    {
                        _pending.Remove(networkObjectId);
                        _pendingDeadline.Remove(networkObjectId);
                        _pendingOwner.Remove(networkObjectId);
                    }
                    else
                    {
                        _pending[networkObjectId] = kind;
                        _pendingDeadline[networkObjectId] = Time.unscaledTime + PendingTimeout;
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Malformed prop message: {ex.Message}");
            }
        }

        private static void ResolvePending()
        {
            _settledIds.Clear();
            foreach (var kvp in _pending)
            {
                float deadline;
                bool expired = _pendingDeadline.TryGetValue(kvp.Key, out deadline) && Time.unscaledTime >= deadline;
                if (TryApply(kvp.Key, kvp.Value) || expired)
                    _settledIds.Add(kvp.Key);
            }

            for (int i = 0; i < _settledIds.Count; i++)
            {
                _pending.Remove(_settledIds[i]);
                _pendingDeadline.Remove(_settledIds[i]);
                _pendingOwner.Remove(_settledIds[i]);
            }
        }

        private static bool TryApply(ulong networkObjectId, PropKind kind)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || nm.SpawnManager == null) return false;

            if (!nm.SpawnManager.SpawnedObjects.TryGetValue(networkObjectId, out var netObj)) return false;
            if (netObj == null) return false;

            var puck = netObj.GetComponent<Puck>();
            if (puck == null) return false;

            // Visual only - physics is server-authoritative, and a second set of
            // colliders on the client would fight the replicated puck position.
            return ApplyLocal(puck, kind, false, ResolveOwnerPlayer(networkObjectId));
        }
    }
}
