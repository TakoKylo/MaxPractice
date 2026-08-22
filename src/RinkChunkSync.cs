// RinkChunkSync.cs - Long-range position sync, for horizontal sheet layouts only.
//
// SynchronizedObjectManager quantises every position into an Int16 against a fixed
// per-axis range - SynchronizedObjectData.POSITION_MIN/MAX_X is +/-25 m, Y and Z are
// +/-50 m - and CompressFloatToShort CLAMPS, so a coordinate past the range saturates
// at the boundary rather than wrapping. That is fine for one rink centred on the origin
// and fatal for a sheet parked 64 m down the X axis: every object out there pins to the
// edge of the box on every remote client.
//
// Note the box is not square. X is half of Y and Z, so a sideways layout runs out of
// wire in 25 m, not 50. See SheetLayout.SyncRange*.
//
// The fix is to send positions relative to a 32 m chunk instead of the world
// origin, and tell clients out-of-band which chunk each object currently lives in.
// A chunk-local offset never exceeds ~32 m, so the short never overflows.
//
// Two things make this safe to bolt onto a live server:
//
//   * Chunks are pinned to sheets, not derived from raw position. Every sheet
//     origin is snapped to the chunk grid, so the main rink is always chunk (0,0)
//     and its objects encode byte-for-byte the same as vanilla. A client without
//     MaxPractice still sees the main rink perfectly; it only misreads objects on
//     the extra sheets, which are 64 m away and out of sight anyway.
//
//   * A chunk change is announced a fixed number of ticks BEFORE it takes effect,
//     so the announcement has time to arrive before the first position encoded
//     against the new chunk does. Anything moving far enough in one tick to be a
//     teleport skips the deferral and switches immediately.
//
// None of this is installed for a vertical layout - there X and Z never leave the
// vanilla envelope, so the netcode is left completely untouched.

using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

namespace MaxPractice
{
    internal struct RinkChunkCoord : IEquatable<RinkChunkCoord>
    {
        public sbyte X;
        public sbyte Z;

        public RinkChunkCoord(sbyte x, sbyte z) { X = x; Z = z; }

        public Vector3 Origin => new Vector3(X * SheetLayout.ChunkSize, 0f, Z * SheetLayout.ChunkSize);

        public bool Equals(RinkChunkCoord other) => X == other.X && Z == other.Z;
        public override bool Equals(object obj) => obj is RinkChunkCoord c && Equals(c);
        public override int GetHashCode() => (X << 8) | (byte)Z;
        public static bool operator ==(RinkChunkCoord a, RinkChunkCoord b) => a.Equals(b);
        public static bool operator !=(RinkChunkCoord a, RinkChunkCoord b) => !a.Equals(b);
        public override string ToString() => $"({X},{Z})";
    }

    internal struct RinkChunkSlot
    {
        public RinkChunkCoord Current;
        public RinkChunkCoord Pending;
        public ushort PendingTick;
        public bool HasPending;

        public RinkChunkCoord ResolveAt(ushort tick)
        {
            if (!HasPending) return Current;
            // Unsigned subtraction wraps correctly across the ushort rollover.
            return (ushort)(tick - PendingTick) < 32768 ? Pending : Current;
        }
    }

    internal static class RinkChunkSync
    {
        private const string MessageName = "MaxPractice.RinkChunks";

        /// <summary>Ticks between announcing a chunk switch and applying it.</summary>
        private const int DeferTicks = 40;

        /// <summary>Past this much movement in one tick it is a teleport, not a walk.</summary>
        private const float TeleportSnapMeters = 48f;

        private static Harmony _harmony;
        private static bool _serverActive;
        private static bool _clientActive;
        private static bool _messageHandlerRegistered;

        private static MethodInfo _gatherMethod;
        private static FieldInfo _objectsField;
        private static FieldInfo _tickIdField;
        private static SynchronizedObjectManager _boundManager;
        private static List<SynchronizedObject> _boundObjects;

        private static readonly Dictionary<ushort, RinkChunkSlot> _slots = new Dictionary<ushort, RinkChunkSlot>();
        private static readonly Dictionary<ushort, Vector3> _worldPositions = new Dictionary<ushort, Vector3>();
        private static readonly Dictionary<ushort, int> _sheetOfObject = new Dictionary<ushort, int>();

        private static RinkChunkCoord[] _sheetChunks = new RinkChunkCoord[0];
        private static Vector3[] _sheetOrigins = new Vector3[0];

        private static ushort _encodeTick;
        private static ushort _decodeTick;

        private static Action<Dictionary<string, object>> _despawnListener;
        private static bool _despawnListenerBound;

        internal static bool ServerActive => _serverActive;
        internal static bool ClientActive => _clientActive;

        private static int _supported = -1;   // -1 unknown, 0 no, 1 yes

        /// <summary>
        /// Whether this game build still exposes the netcode internals the encoder rewrites.
        ///
        /// It does not, on b1153 and the current live build: SynchronizedObjectManager was
        /// restructured and Server_GatherSynchronizedObjectData, the `synchronizedObjects`
        /// list and `serverLastSentTickId` are all gone (there is a
        /// SynchronizedObjectRegistry and a serverTickCount now), as are
        /// SynchronizedObject.OnClientTick and OnClientSmoothTick.
        ///
        /// EnableServer already declined to arm in that case, but nothing ASKED before
        /// committing to a layout that needs it - so a server configured horizontal built
        /// its slots, reported "chunk sync required", and then failed every single /rink
        /// with "this game build doesn't support it". Probing up front lets the layout fall
        /// back to a vertical stack, which works everywhere, instead of offering a feature
        /// that cannot open. Cached: this is pure reflection over a fixed assembly.
        /// </summary>
        internal static bool IsSupported()
        {
            if (_supported >= 0) return _supported == 1;

            bool ok;
            try
            {
                ok = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_GatherSynchronizedObjectData") != null
                     && AccessTools.Field(typeof(SynchronizedObjectManager), "synchronizedObjects") != null
                     && AccessTools.Field(typeof(SynchronizedObjectManager), "serverLastSentTickId") != null
                     && (AccessTools.Method(typeof(SynchronizedObject), "OnClientTick") != null
                         || AccessTools.Method(typeof(SynchronizedObject), "OnClientSmoothTick") != null);
            }
            catch { ok = false; }

            _supported = ok ? 1 : 0;
            return ok;
        }

        /// <summary>Positions the server read this tick, keyed by network object id.</summary>
        internal static IDictionary<ushort, Vector3> WorldPositions => _worldPositions;

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        /// <summary>Tell the encoder where the sheets are. Call before enabling.</summary>
        internal static void Configure(List<RinkSheetSlot> slots)
        {
            if (slots == null || slots.Count == 0)
            {
                _sheetChunks = new RinkChunkCoord[0];
                _sheetOrigins = new Vector3[0];
                return;
            }

            _sheetChunks = new RinkChunkCoord[slots.Count];
            _sheetOrigins = new Vector3[slots.Count];
            for (int i = 0; i < slots.Count; i++)
            {
                Vector3 o = slots[i].Origin;
                _sheetOrigins[i] = o;
                _sheetChunks[i] = new RinkChunkCoord(
                    (sbyte)Mathf.Clamp(Mathf.RoundToInt(o.x / SheetLayout.ChunkSize), -128, 127),
                    (sbyte)Mathf.Clamp(Mathf.RoundToInt(o.z / SheetLayout.ChunkSize), -128, 127));
            }
        }

        internal static void EnableServer()
        {
            if (_serverActive) return;

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            if (_sheetChunks.Length == 0) return;

            _gatherMethod = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_GatherSynchronizedObjectData");
            _objectsField = AccessTools.Field(typeof(SynchronizedObjectManager), "synchronizedObjects");
            _tickIdField = AccessTools.Field(typeof(SynchronizedObjectManager), "serverLastSentTickId");

            if (_gatherMethod == null || _objectsField == null || _tickIdField == null)
            {
                Debug.LogWarning("[MaxPractice] Chunk sync: SynchronizedObjectManager internals not found — " +
                                 "extra sheets would desync, so the horizontal layout is unavailable on this build.");
                return;
            }

            EnsureHarmony();
            _harmony.Patch(_gatherMethod,
                prefix: new HarmonyMethod(typeof(RinkChunkSync), nameof(GatherPrefix)),
                postfix: new HarmonyMethod(typeof(RinkChunkSync), nameof(GatherPostfix)));

            MethodInfo force = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_ForceSynchronizeClientId");
            if (force != null)
                _harmony.Patch(force, prefix: new HarmonyMethod(typeof(RinkChunkSync), nameof(ForceSyncPrefix)));

            BindDespawnListener();
            _serverActive = true;
            ConfigManager.Log($"Chunk sync enabled for {_sheetChunks.Length} sheet(s) — positions now encode chunk-locally.");
        }

        /// <summary>
        /// Pure clients only. A host reads true world positions in-process, so decoding
        /// would double-apply the chunk offset.
        /// </summary>
        internal static void EnableClient()
        {
            if (_clientActive) return;

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsClient || nm.IsServer) return;

            // Decoding needs somewhere to decode. Without at least one of the two tick
            // hooks, Expand is never called and arming would claim a decode that isn't
            // happening - which HandleNetworkDown and the sheet broadcast both read back.
            MethodInfo tick = AccessTools.Method(typeof(SynchronizedObject), "OnClientTick");
            MethodInfo smooth = AccessTools.Method(typeof(SynchronizedObject), "OnClientSmoothTick");
            if (tick == null && smooth == null)
            {
                Debug.LogWarning("[MaxPractice] Chunk sync: SynchronizedObject's client tick hooks are not on this " +
                                 "game build, so extra-sheet positions cannot be decoded. Objects on the far sheets " +
                                 "will be in the wrong place; ask the server for a vertical layout.");
                return;
            }

            EnsureHarmony();

            // Every Patch call is guarded, and NOTHING is left half-applied on failure.
            //
            // Harmony matches an injected parameter by NAME against the original, and throws
            // when it cannot find one. SyncRpcPrefix asks for `tickId`, which this build's
            // Server_SynchronizeObjectsRpc(SynchronizedObjectTickHeader tickHeader, ...) does
            // not have - so the unguarded call threw straight through EnableClient into the
            // caller's catch, which logged it as a malformed message and then tried again on
            // the next one, forever.
            try
            {
                if (tick != null)
                    _harmony.Patch(tick, prefix: new HarmonyMethod(typeof(RinkChunkSync), nameof(OnClientTickPrefix)));

                if (smooth != null)
                    _harmony.Patch(smooth, prefix: new HarmonyMethod(typeof(RinkChunkSync), nameof(OnClientSmoothTickPrefix)));

                // Optional: without it _decodeTick stays at 0 and a pending switch resolves a
                // little early or late, which is a seam on one object for a few ticks. Losing
                // the decode entirely because this one signature moved is far worse, so it is
                // only patched when the parameter it reads is actually there.
                MethodInfo rpc = AccessTools.Method(typeof(SynchronizedObjectManager), "Server_SynchronizeObjectsRpc");
                if (rpc != null && HasParameter(rpc, "tickId", typeof(ushort)))
                    _harmony.Patch(rpc, prefix: new HarmonyMethod(typeof(RinkChunkSync), nameof(SyncRpcPrefix)));
                else
                    Debug.LogWarning("[MaxPractice] Chunk sync: no ushort 'tickId' on Server_SynchronizeObjectsRpc, " +
                                     "so deferred chunk switches resolve against tick 0.");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Chunk sync decode could not be installed: {ex.Message}");
                try { _harmony.UnpatchSelf(); } catch { }
                _harmony = null;
                return;
            }

            BindDespawnListener();
            _clientActive = true;
            ConfigManager.Log("Chunk sync decode armed — this server runs extra practice sheets.");
        }

        /// <summary>
        /// Read the manager's current tick without boxing.
        ///
        /// FieldInfo.GetValue returns object, so every call allocates a box for the ushort.
        /// A compiled delegate reads the field directly; it is built once and reused.
        /// </summary>
        private static Func<object, ushort> _tickReader;

        private static ushort ReadTick(object mgr)
        {
            if (_tickReader == null)
            {
                if (_tickIdField == null) return 0;
                try
                {
                    var target = System.Linq.Expressions.Expression.Parameter(typeof(object), "m");
                    var access = System.Linq.Expressions.Expression.Field(
                        System.Linq.Expressions.Expression.Convert(target, _tickIdField.DeclaringType),
                        _tickIdField);
                    _tickReader = System.Linq.Expressions.Expression
                        .Lambda<Func<object, ushort>>(access, target).Compile();
                }
                catch
                {
                    // Fall back to reflection rather than losing chunk sync entirely.
                    _tickReader = m => (ushort)_tickIdField.GetValue(m);
                }
            }

            try { return _tickReader(mgr); }
            catch { return 0; }
        }

        private static void EnsureHarmony()
        {
            _harmony = _harmony ?? new Harmony("GAFURIX.MaxPractice.RinkChunkSync");
        }

        /// <summary>
        /// Whether a patch that injects <paramref name="name"/> would bind. Harmony throws
        /// rather than skipping when an injected parameter has no counterpart, so this is
        /// checked before patching instead of caught afterwards.
        /// </summary>
        private static bool HasParameter(MethodInfo method, string name, Type type)
        {
            try
            {
                foreach (ParameterInfo p in method.GetParameters())
                {
                    if (p.Name == name && p.ParameterType == type) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Pucks spawn and despawn constantly. Without this the chunk table would keep an
        /// entry for every puck the server has ever seen, on both sides of the wire.
        /// </summary>
        private static void BindDespawnListener()
        {
            if (_despawnListenerBound) return;

            try
            {
                _despawnListener = _despawnListener ?? new Action<Dictionary<string, object>>(OnObjectDespawned);
                EventManager.AddEventListener("Event_Everyone_OnSynchronizedObjectDespawned", _despawnListener);
                _despawnListenerBound = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Could not watch synchronized despawns: {ex.Message}");
            }
        }

        private static void UnbindDespawnListener()
        {
            if (!_despawnListenerBound) return;

            try { EventManager.RemoveEventListener("Event_Everyone_OnSynchronizedObjectDespawned", _despawnListener); }
            catch { }
            _despawnListenerBound = false;
        }

        private static void OnObjectDespawned(Dictionary<string, object> message)
        {
            try
            {
                if (message == null || !message.ContainsKey("synchronizedObject")) return;
                if (message["synchronizedObject"] is SynchronizedObject obj)
                    Forget((ushort)obj.NetworkObjectId);
            }
            catch { }
        }

        internal static void Disable()
        {
            bool wasActive = _serverActive || _clientActive;

            UnbindDespawnListener();
            _serverActive = false;
            _clientActive = false;
            _boundManager = null;
            _boundObjects = null;
            _slots.Clear();
            _worldPositions.Clear();
            _sheetOfObject.Clear();

            try { _harmony?.UnpatchSelf(); }
            catch { }
            _harmony = null;

            if (wasActive) ConfigManager.Log("Chunk sync disabled — back to vanilla position encoding.");
        }

        /// <summary>Pumped every frame so the named-message handler survives reconnects.</summary>
        internal static void Tick()
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening)
            {
                _messageHandlerRegistered = false;
                return;
            }

            if (_messageHandlerRegistered || nm.CustomMessagingManager == null) return;

            try
            {
                nm.CustomMessagingManager.RegisterNamedMessageHandler(MessageName, OnChunkMessage);
                _messageHandlerRegistered = true;
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Could not register the chunk message handler: {ex.Message}");
            }
        }

        internal static void Shutdown()
        {
            try
            {
                var nm = NetworkManager.Singleton;
                if (_messageHandlerRegistered && nm != null && nm.CustomMessagingManager != null)
                    nm.CustomMessagingManager.UnregisterNamedMessageHandler(MessageName);
            }
            catch { }

            _messageHandlerRegistered = false;
            Disable();
        }

        // ------------------------------------------------------------------
        // Server encode
        // ------------------------------------------------------------------

        /// <summary>
        /// Runs before the vanilla gather so the whole batch encodes against one
        /// snapshot of the chunk table, and so the true world positions are captured
        /// before vanilla has a chance to overflow them into the shorts.
        /// </summary>
        internal static void GatherPrefix(SynchronizedObjectManager __instance)
        {
            if (!_serverActive) return;

            try
            {
                SynchronizedObjectManager mgr = __instance;
                if (mgr == null) return;

                if (!ReferenceEquals(mgr, _boundManager))
                {
                    _boundManager = mgr;
                    _boundObjects = _objectsField.GetValue(mgr) as List<SynchronizedObject>;
                    _slots.Clear();
                    _worldPositions.Clear();
                    _sheetOfObject.Clear();
                }
                if (_boundObjects == null) return;

                // FieldInfo.GetValue boxes the ushort, and this is the patched gather hot
                // path - it runs at the manager's 100 Hz tick for as long as sheets are open.
                _encodeTick = ReadTick(mgr);

                for (int i = 0; i < _boundObjects.Count; i++)
                {
                    SynchronizedObject obj = _boundObjects[i];
                    if (obj == null) continue;

                    ushort id = (ushort)obj.NetworkObjectId;
                    Vector3 pos = obj.transform.position;
                    _worldPositions[id] = pos;

                    int sheet = ResolveSheet(id, pos);
                    RinkChunkCoord target = _sheetChunks[sheet];

                    if (!_slots.TryGetValue(id, out RinkChunkSlot slot))
                    {
                        // First sighting: the client has no idea where this object lives,
                        // so announce immediately rather than deferring.
                        Announce(id, target, ushort.MaxValue);
                        continue;
                    }

                    RinkChunkCoord current = slot.ResolveAt(_encodeTick);
                    if (target == current)
                        continue;

                    Vector3 currentOrigin = current.Origin;
                    bool teleported = Mathf.Abs(pos.x - currentOrigin.x) > TeleportSnapMeters ||
                                      Mathf.Abs(pos.z - currentOrigin.z) > TeleportSnapMeters;

                    if (teleported)
                    {
                        // A jump between sheets is already a visual discontinuity; taking
                        // the chunk with it costs nothing and avoids 40 ticks of the
                        // object being encoded against a chunk it has already left.
                        Announce(id, target, ushort.MaxValue);
                        continue;
                    }

                    // Same switch already in flight — don't spam it every tick.
                    if (slot.HasPending && slot.Pending == target && !TickReached(_encodeTick, slot.PendingTick))
                        continue;

                    Announce(id, target, DeferredTick(_encodeTick));
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[MaxPractice] Chunk sync encode failed, reverting to vanilla sync: {ex}");
                Disable();
            }
        }

        /// <summary>
        /// Vanilla has already written the overflowed world shorts into the result.
        /// Overwrite X and Z from the cached world positions, chunk-local, so the value
        /// that actually goes on the wire always fits.
        /// </summary>
        internal static void GatherPostfix(ref SynchronizedObjectData[] __result)
        {
            if (!_serverActive || __result == null) return;

            for (int i = 0; i < __result.Length; i++)
            {
                ushort id = __result[i].NetworkObjectId;
                if (!_slots.TryGetValue(id, out RinkChunkSlot slot)) continue;
                if (!_worldPositions.TryGetValue(id, out Vector3 world)) continue;

                Vector3 origin = slot.ResolveAt(_encodeTick).Origin;

                // Encode with the game's OWN compressor and the game's OWN per-axis range,
                // because the client decodes with DecompressShortToFloat against exactly
                // those constants and the two have to be inverses.
                //
                // This used to be a hand-rolled `(short)(delta * 655f)` on both axes, which
                // was wrong twice over. The range is NOT square: POSITION_MIN/MAX_X is +/-25
                // while Z is +/-50, so X needs 65535/50 = 1310.7 counts per metre and Z needs
                // 65535/100 = 655.35. Sending X at half its scale put every object at half
                // its true chunk-local X on every remote client. And the raw cast had no
                // clamp, so an out-of-range value wrapped instead of saturating the way
                // CompressFloatToShort does.
                //
                // The ranges come from SheetLayout.SyncRange*, which is where this repo
                // records SynchronizedObjectData's POSITION_MIN/MAX_* literals - the game's
                // own fields are not public, so they cannot be named here directly.
                __result[i].X = NetworkingUtils.CompressFloatToShort(
                    world.x - origin.x, -SheetLayout.SyncRangeX, SheetLayout.SyncRangeX);
                __result[i].Z = NetworkingUtils.CompressFloatToShort(
                    world.z - origin.z, -SheetLayout.SyncRangeZ, SheetLayout.SyncRangeZ);
            }
        }

        /// <summary>A late joiner needs the whole chunk table before its first tick lands.</summary>
        internal static void ForceSyncPrefix(ulong clientId)
        {
            if (!_serverActive || _slots.Count == 0) return;

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            if (nm.IsHost && clientId == nm.LocalClientId) return;

            try
            {
                // Read the live tick rather than reusing the one the last gather left
                // behind: a switch that is about to apply must be reported as pending,
                // not as already current.
                ushort tick = _encodeTick;
                SynchronizedObjectManager mgr = NetworkBehaviourSingleton<SynchronizedObjectManager>.Instance;
                if (mgr != null && _tickIdField != null)
                {
                    try { tick = (ushort)_tickIdField.GetValue(mgr); }
                    catch { }
                }

                ushort count = (ushort)Mathf.Min(_slots.Count, ushort.MaxValue - 1);
                int size = sizeof(byte) + sizeof(ushort) + count * (sizeof(ushort) * 2 + sizeof(sbyte) * 2);

                using (var writer = new FastBufferWriter(size, Allocator.Temp, size * 2))
                {
                    writer.WriteValueSafe((byte)1);
                    writer.WriteValueSafe(count);

                    int written = 0;
                    foreach (var kvp in _slots)
                    {
                        if (written++ >= count) break;
                        // Resolve to a single current chunk: a joiner has no history to
                        // apply a pending switch against.
                        // Send the PENDING switch when there is one, not just Current. The
                    // snapshot used to collapse an in-flight switch to Current with
                    // switchTick = MaxValue ("nothing pending"), and the server never
                    // re-announces it - GatherPrefix sees current == target once the tick
                    // passes and skips - so a client that joined inside the deferral window
                    // decoded that object against the old chunk origin for the whole session.
                    RinkChunkCoord c = kvp.Value.HasPending ? kvp.Value.Pending : kvp.Value.ResolveAt(tick);
                        writer.WriteValueSafe(kvp.Key);
                        writer.WriteValueSafe(c.X);
                        writer.WriteValueSafe(c.Z);
                        writer.WriteValueSafe(ushort.MaxValue);
                    }

                    // FRAGMENTED: this packs the whole chunk table into one message, one
                    // entry per live synchronized object, and nothing bounds that count -
                    // handle pucks are explicitly exempt from the puck cap. Past ~216 entries
                    // the unfragmented send blew the transport's 1300-byte limit, threw, and
                    // was swallowed as a single warning - leaving the joining client with no
                    // chunk table at all, which Expand() renders as every object frozen at
                    // its spawn transform.
                    nm.CustomMessagingManager.SendNamedMessage(MessageName, clientId, writer, NetworkDelivery.ReliableFragmentedSequenced);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Chunk snapshot to client {clientId} failed: {ex.Message}");
            }
        }

        private static int ResolveSheet(ushort id, Vector3 pos)
        {
            int nearest = NearestSheet(pos);

            // Hysteresis: an object only changes sheet once it is closer to the new
            // origin than half the gap, so something resting between two sheets does
            // not flip back and forth and announce every tick.
            if (_sheetOfObject.TryGetValue(id, out int previous) &&
                previous >= 0 && previous < _sheetOrigins.Length && previous != nearest)
            {
                float toPrevious = SqrPlanarDistance(pos, _sheetOrigins[previous]);
                float toNearest = SqrPlanarDistance(pos, _sheetOrigins[nearest]);
                if (toNearest > toPrevious * 0.64f) nearest = previous;   // 0.8^2
            }

            _sheetOfObject[id] = nearest;
            return nearest;
        }

        private static int NearestSheet(Vector3 pos)
        {
            int best = 0;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < _sheetOrigins.Length; i++)
            {
                float d = SqrPlanarDistance(pos, _sheetOrigins[i]);
                if (d >= bestDistance) continue;
                bestDistance = d;
                best = i;
            }
            return best;
        }

        private static float SqrPlanarDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return dx * dx + dz * dz;
        }

        private static void Announce(ushort id, RinkChunkCoord chunk, ushort switchTick)
        {
            ApplyAnnounce(id, chunk, switchTick);

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer || nm.CustomMessagingManager == null) return;

            // The host's own client reads world positions in-process; only remote
            // clients have anything to decode.
            int remotes = nm.IsHost ? nm.ConnectedClientsIds.Count - 1 : nm.ConnectedClientsIds.Count;
            if (remotes <= 0) return;

            try
            {
                const int size = sizeof(byte) + sizeof(ushort) + sizeof(sbyte) * 2 + sizeof(ushort);
                using (var writer = new FastBufferWriter(size, Allocator.Temp, size * 2))
                {
                    writer.WriteValueSafe((byte)0);
                    writer.WriteValueSafe(id);
                    writer.WriteValueSafe(chunk.X);
                    writer.WriteValueSafe(chunk.Z);
                    writer.WriteValueSafe(switchTick);
                    nm.CustomMessagingManager.SendNamedMessageToAll(MessageName, writer, NetworkDelivery.Reliable);
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Chunk announce failed: {ex.Message}");
            }
        }

        internal static void Forget(ushort id)
        {
            _slots.Remove(id);
            _worldPositions.Remove(id);
            _sheetOfObject.Remove(id);
        }

        // ------------------------------------------------------------------
        // Shared table
        // ------------------------------------------------------------------

        private static void ApplyAnnounce(ushort id, RinkChunkCoord chunk, ushort switchTick)
        {
            _slots.TryGetValue(id, out RinkChunkSlot slot);

            if (switchTick == ushort.MaxValue)
            {
                slot.Current = chunk;
                slot.HasPending = false;
                slot.Pending = default(RinkChunkCoord);
                slot.PendingTick = 0;
            }
            else
            {
                // Commit whatever was already in flight before queueing the next one.
                if (slot.HasPending) slot.Current = slot.Pending;
                slot.Pending = chunk;
                slot.PendingTick = switchTick;
                slot.HasPending = true;
            }

            _slots[id] = slot;
        }

        private static bool TickReached(ushort now, ushort target) => (ushort)(now - target) < 32768;

        /// <summary>
        /// The tick a switch announced now should take effect on.
        ///
        /// ushort.MaxValue is the "apply immediately" sentinel, so the deferred tick must
        /// never land on it. This used to be `(ushort)((tick + DeferTicks) % ushort.MaxValue)`,
        /// which dodges the sentinel but is modulo 65535 on a counter that wraps at 65536 -
        /// so every value after a rollover was one tick out. Wrap properly and step over the
        /// sentinel instead: same guarantee, no drift.
        /// </summary>
        private static ushort DeferredTick(ushort now)
        {
            ushort target = (ushort)(now + DeferTicks);
            return target == ushort.MaxValue ? (ushort)0 : target;
        }

        // ------------------------------------------------------------------
        // Client decode
        // ------------------------------------------------------------------

        private static void OnChunkMessage(ulong senderClientId, FastBufferReader reader)
        {
            try
            {
                if (senderClientId != NetworkManager.ServerClientId) return;

                var nm = NetworkManager.Singleton;
                if (nm != null && nm.IsServer) return;

                reader.ReadValueSafe(out byte type);
                if (type == 0)
                {
                    ReadOne(ref reader);
                }
                else if (type == 1)
                {
                    reader.ReadValueSafe(out ushort count);
                    for (int i = 0; i < count; i++) ReadOne(ref reader);
                }

                // A server that talks chunks is a server running extra sheets, and every
                // position it sends from here on is chunk-local — so decoding has to be
                // on before the next tick lands, not whenever the sheet list turns up.
                if (!_clientActive) EnableClient();
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Malformed chunk message: {ex.Message}");
            }
        }

        private static void ReadOne(ref FastBufferReader reader)
        {
            reader.ReadValueSafe(out ushort id);
            reader.ReadValueSafe(out sbyte cx);
            reader.ReadValueSafe(out sbyte cz);
            reader.ReadValueSafe(out ushort switchTick);
            ApplyAnnounce(id, new RinkChunkCoord(cx, cz), switchTick);
        }

        /// <summary>
        /// Captures the tick the batch was encoded against, so every object in it
        /// resolves pending switches the same way the server did.
        /// </summary>
        internal static void SyncRpcPrefix(ushort tickId)
        {
            _decodeTick = tickId;
        }

        internal static void OnClientTickPrefix(SynchronizedObject __instance, ref Vector3 position)
        {
            Expand(__instance, ref position);
        }

        internal static void OnClientSmoothTickPrefix(SynchronizedObject __instance, ref Vector3 position)
        {
            Expand(__instance, ref position);
        }

        /// <summary>
        /// Vanilla decoded the shorts into a chunk-local float. Add the chunk origin at
        /// the float stage: doing it in the short layer would just overflow again.
        /// </summary>
        private static void Expand(SynchronizedObject obj, ref Vector3 position)
        {
            if (!_clientActive || obj == null) return;

            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsServer) return;

            ushort id = (ushort)obj.NetworkObjectId;
            if (!_slots.TryGetValue(id, out RinkChunkSlot slot))
            {
                // No announcement yet. Applying a zero offset would drag an object from
                // one of the far sheets across the map, so hold it where it is instead.
                position = obj.transform.position;
                return;
            }

            Vector3 origin = slot.ResolveAt(_decodeTick).Origin;
            position.x += origin.x;
            position.z += origin.z;
        }
    }
}
