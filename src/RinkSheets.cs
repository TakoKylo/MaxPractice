// RinkSheets.cs - On-demand practice sheets.
//
// A second rink costs real memory, physics and draw calls, and most of the time
// nobody wants one - so nothing is built at startup. The first player to ask for a
// sheet builds it; when the last player leaves it, it goes away again.
//
// Server and client hold different halves of a sheet. The server owns the colliders
// players skate on and is the only side that decides who is where. Clients own the
// visuals, built from a list the server broadcasts, so a client that joins mid-session
// - or one that never asks for a sheet - still sees the rinks everyone else is on.

using System;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

namespace MaxPractice
{
    public static class RinkSheets
    {
        private static MaxPractice.ModConfig cfg => ConfigManager.Config;

        private static readonly List<RinkSheetSlot> _slots = new List<RinkSheetSlot>();
        private static SheetLayoutMode _mode = SheetLayoutMode.Vertical;
        private static bool _layoutBuilt;
        private static bool _chunkSyncNeeded;

        // Server-side geometry, keyed by sheet index. Index 0 is never in here: the
        // main rink is the level's own.
        private static readonly Dictionary<int, GameObject> _serverRoots = new Dictionary<int, GameObject>();
        private static readonly Dictionary<int, GameObject> _clientRoots = new Dictionary<int, GameObject>();

        // Where each client-side root was actually built, so a server that changes its
        // layout mid-session doesn't leave visuals stranded at the old offsets.
        private static readonly Dictionary<int, Vector3> _clientOrigins = new Dictionary<int, Vector3>();

        private static readonly Dictionary<ulong, int> _clientSheet = new Dictionary<ulong, int>();
        private static readonly Dictionary<int, float> _lastOccupied = new Dictionary<int, float>();

        private static float _nextOccupancyScan;
        private const float OccupancyScanInterval = 2f;

        /// <summary>How far off a sheet's centre still counts as being on it.</summary>
        private const float SheetClaimRadius = 70f;

        // Which axes actually separate the sheets in the active layout. A vertical stack
        // only differs in Y, and the rink is 91 m long - so measuring the full 3D distance
        // would make a player standing behind the far net on their own sheet look closer
        // to the sheet above them. Only the separating axes get a vote.
        private static bool _separatesX, _separatesY, _separatesZ;

        internal static IReadOnlyList<RinkSheetSlot> Slots
        {
            get { EnsureLayout(); return _slots; }
        }

        /// <summary>Sheets with live server geometry right now (excludes the main rink).</summary>
        internal static int OpenCloneCount => _serverRoots.Count;

        /// <summary>True when this layout puts geometry outside the vanilla sync envelope.</summary>
        internal static bool ChunkSyncRequired
        {
            get { EnsureLayout(); return _chunkSyncNeeded; }
        }

        // ------------------------------------------------------------------
        // Layout
        // ------------------------------------------------------------------

        private static void EnsureLayout()
        {
            if (_layoutBuilt) return;
            _layoutBuilt = true;

            _mode = SheetLayout.ParseMode(cfg.RinkSheetLayout);

            // A horizontal grid only exists if positions can be re-encoded chunk-locally,
            // and that rewrites netcode internals this game build no longer has (see
            // RinkChunkSync.IsSupported). Asking now and falling back means an operator who
            // configured horizontal gets working stacked sheets and one clear line in the
            // log, instead of a layout that reports itself as built and then refuses every
            // /rink with "this game build doesn't support it".
            if (_mode == SheetLayoutMode.Horizontal && !RinkChunkSync.IsSupported())
            {
                Debug.LogWarning(
                    "[MaxPractice] RinkSheetLayout=horizontal needs chunk-local position encoding, and this game " +
                    "build's netcode no longer exposes what that patches. Falling back to a vertical stack, which " +
                    "needs no netcode changes. Set RinkSheetLayout=vertical to silence this.");
                _mode = SheetLayoutMode.Vertical;
            }

            // The arena's height matters here: a taller rink needs the sheets raised by
            // the same factor to keep the same clearance between them.
            float arenaY = ArenaScale.y;
            int requested = Mathf.Max(1, cfg.MaxRinkSheets);
            int supported = SheetLayout.MaxSupportedSheets(_mode, cfg.RinkSheetVerticalSpacing, arenaY);
            int count = Mathf.Min(requested, supported);

            if (count < requested)
            {
                Debug.LogWarning(
                    $"[MaxPractice] MaxRinkSheets={requested} does not fit a {_mode} layout at " +
                    $"{SheetLayout.EffectiveSpacing(cfg.RinkSheetVerticalSpacing, arenaY):F1} m spacing " +
                    $"({cfg.RinkSheetVerticalSpacing} m x {arenaY:F2} arena height) — positions past " +
                    $"{SheetLayout.SyncRangeMeters:F0} m do not survive the wire. Using {count}.");
            }

            _slots.Clear();
            _slots.AddRange(SheetLayout.Build(
                _mode, count, cfg.RinkSheetVerticalSpacing, cfg.RinkSheetGridSpacingX, cfg.RinkSheetGridSpacingZ,
                arenaY));

            _chunkSyncNeeded = SheetLayout.NeedsChunkSync(_slots);
            RinkChunkSync.Configure(_slots);
            ResolveSeparatingAxes();

            ConfigManager.Log(
                $"Practice sheets: {_slots.Count} slot(s), {_mode} layout, " +
                $"spacing {SheetLayout.EffectiveSpacing(cfg.RinkSheetVerticalSpacing, arenaY):F1} m " +
                $"(arena height x{arenaY:F2}), chunk sync {(_chunkSyncNeeded ? "required" : "not needed")}.");
        }

        /// <summary>Drops the cached layout so a config reload takes effect.</summary>
        public static void InvalidateLayout()
        {
            _layoutBuilt = false;
        }

        // ------------------------------------------------------------------
        // Queries
        // ------------------------------------------------------------------

        public static bool Enabled => cfg.EnableRinkSheets && Slots.Count > 1;

        internal static RinkSheetSlot SlotAt(int index)
        {
            EnsureLayout();
            return index >= 0 && index < _slots.Count ? _slots[index] : null;
        }

        private static void ResolveSeparatingAxes()
        {
            _separatesX = _separatesY = _separatesZ = false;
            if (_slots.Count < 2) return;

            Vector3 first = _slots[0].Origin;
            for (int i = 1; i < _slots.Count; i++)
            {
                Vector3 o = _slots[i].Origin;
                if (Mathf.Abs(o.x - first.x) > 0.01f) _separatesX = true;
                if (Mathf.Abs(o.y - first.y) > 0.01f) _separatesY = true;
                if (Mathf.Abs(o.z - first.z) > 0.01f) _separatesZ = true;
            }

            // Degenerate layout (every sheet at the same origin) - fall back to full 3D
            // rather than declaring everything to be on sheet 0.
            if (!_separatesX && !_separatesY && !_separatesZ)
                _separatesX = _separatesY = _separatesZ = true;
        }

        /// <summary>Which sheet a world position sits on. Falls back to the main rink.</summary>
        public static int SheetIndexAt(Vector3 position)
        {
            EnsureLayout();

            int best = 0;
            float bestDistance = float.MaxValue;
            for (int i = 0; i < _slots.Count; i++)
            {
                float d = SeparationDistanceSqr(position, _slots[i].Origin);
                if (d >= bestDistance) continue;
                bestDistance = d;
                best = i;
            }

            return bestDistance <= SheetClaimRadius * SheetClaimRadius ? best : 0;
        }

        private static float SeparationDistanceSqr(Vector3 position, Vector3 origin)
        {
            float total = 0f;
            if (_separatesX) { float d = position.x - origin.x; total += d * d; }
            if (_separatesY) { float d = position.y - origin.y; total += d * d; }
            if (_separatesZ) { float d = position.z - origin.z; total += d * d; }
            return total;
        }

        /// <summary>
        /// The offset to add to any main-rink coordinate to get its equivalent on the
        /// sheet this player is standing on. Zero for anyone on the main rink, which is
        /// what makes it safe to sprinkle over the existing commands.
        /// </summary>
        public static Vector3 OriginForPlayer(Player player)
        {
            try
            {
                if (!Enabled || player == null) return Vector3.zero;
                var body = player.PlayerBody;
                if (body == null) return Vector3.zero;

                RinkSheetSlot slot = SlotAt(SheetIndexAt(body.transform.position));
                return slot != null ? slot.Origin : Vector3.zero;
            }
            catch
            {
                return Vector3.zero;
            }
        }

        /// <summary>
        /// World Y that counts as "the floor" for whatever sheet this position is on.
        /// Zero on the arena rink, so anything measuring against it keeps behaving
        /// exactly as it did before sheets existed.
        /// </summary>
        /// <summary>
        /// Floor height for whichever sheet <paramref name="position"/> is on.
        ///
        /// Prefers the SERVER-ANNOUNCED origins over the local layout, for the same reason
        /// VisibleSheetFor does: a client whose maxpractice.json disagrees with the server's
        /// (sheets off, a different MaxRinkSheets, a different spacing) otherwise resolved
        /// against slots that do not exist on this server - or, with EnableRinkSheets off,
        /// against nothing at all and returned 0. StickFloorPatch feeds this straight into
        /// the blade floor clamp, so a wrong answer drags the stick of a player 40 m down on
        /// a sheet up to world zero against a PID gain of 500.
        /// </summary>
        public static float SheetFloorAt(Vector3 position)
        {
            if (_clientOrigins.Count > 0)
            {
                int announced = VisibleSheetFor(position);
                if (announced != 0 && _clientOrigins.TryGetValue(announced, out Vector3 origin))
                    return origin.y;
                return 0f;
            }

            if (!Enabled || (_serverRoots.Count == 0 && _clientRoots.Count == 0)) return 0f;
            RinkSheetSlot slot = SlotAt(SheetIndexAt(position));
            return slot != null ? slot.Origin.y : 0f;
        }

        /// <summary>
        /// Playable ice bounds for the sheet <paramref name="near"/> is on, in world
        /// space, following whatever CompetitiveAdjustments has done to the arena.
        ///
        /// The old numbers here were the stock rink's ±26 by ±42, hardcoded when a
        /// LevelManager lookup stopped compiling. On an arena scaled to 2x that clamps
        /// every cone, net and puck into the middle quarter of the ice.
        ///
        /// The result is then clipped to what the position encoding can actually carry.
        /// That is not belt-and-braces: the wire's X half-range is 25 m (SheetLayout,
        /// measured off SynchronizedObjectData) and the playable half-width handed out here
        /// is 26 m BEFORE any arena resize, so the unclipped box was already a metre wider
        /// than the wire on a stock rink and metres wider on a CompetitiveAdjustments one.
        /// Out-of-range positions saturate rather than wrap, so the symptom is a cone or a
        /// puck sitting against the boards for the player who placed it and stacked on the
        /// 25 m line for everyone else.
        /// </summary>
        public static void IceBoundsAt(Vector3 near, out float xMin, out float xMax, out float zMin, out float zMax)
        {
            const float StockHalfWidth = 26f;
            const float StockHalfLength = 42f;

            Vector3 origin = OriginForPosition(near);

            // Sheets are the arena's rink at an offset, so they share its bounds.
            Vector3 scale = ArenaScale;

            xMin = origin.x - StockHalfWidth * scale.x;
            xMax = origin.x + StockHalfWidth * scale.x;
            zMin = origin.z - StockHalfLength * scale.z;
            zMax = origin.z + StockHalfLength * scale.z;

            ClipToWire(ref xMin, ref xMax, SheetLayout.SyncRangeX);
            ClipToWire(ref zMin, ref zMax, SheetLayout.SyncRangeZ);
        }

        /// <summary>
        /// Trim one axis of a bounds pair to the wire's representable range.
        ///
        /// Collapses to the nearest representable point rather than inverting if a sheet is
        /// ever placed wholly outside the range: callers feed these straight into
        /// Mathf.Clamp, and a min above its max there returns the max, which would silently
        /// pin every spawn to one corner instead of failing visibly.
        /// </summary>
        private static void ClipToWire(ref float min, ref float max, float halfRange)
        {
            min = Mathf.Clamp(min, -halfRange, halfRange);
            max = Mathf.Clamp(max, -halfRange, halfRange);
            if (min > max) min = max;
        }

        /// <summary>
        /// The arena's current world scale. CompetitiveAdjustments resizes the rink by
        /// scaling the level root, so anything measuring the ice in metres has to read
        /// this rather than assume the stock dimensions.
        /// </summary>
        public static Vector3 ArenaScale
        {
            get
            {
                try
                {
                    Transform root = RinkCloneBuilder.DiagnosticLevelRoot();
                    if (root == null) return Vector3.one;

                    Vector3 s = root.lossyScale;
                    // A zero or negative axis would silently collapse every clamp.
                    if (s.x < 0.01f || s.y < 0.01f || s.z < 0.01f) return Vector3.one;
                    return s;
                }
                catch
                {
                    return Vector3.one;
                }
            }
        }

        /// <summary>
        /// World Y for something meant to sit <paramref name="heightAboveIce"/> above the
        /// ice, on whichever sheet <paramref name="near"/> is on.
        ///
        /// Most of the spawn code was written when the ice was the only ice, so it takes
        /// a player position and overwrites the Y with an absolute height - 0.08 for a
        /// cone, 0.05 for a puck. On a stacked sheet that plants everything back at the
        /// arena rink, 40 m above whoever asked for it. Same shape of bug as the stick
        /// clamp: a world constant standing in for "the floor".
        /// </summary>
        public static float IceHeightAt(Vector3 near, float heightAboveIce)
        {
            return SheetFloorAt(near) + heightAboveIce;
        }

        public static Vector3 OriginForPosition(Vector3 position)
        {
            if (!Enabled) return Vector3.zero;
            RinkSheetSlot slot = SlotAt(SheetIndexAt(position));
            return slot != null ? slot.Origin : Vector3.zero;
        }

        // ------------------------------------------------------------------
        // Lifecycle
        // ------------------------------------------------------------------

        /// <summary>
        /// Build a sheet's server geometry if it isn't already standing. Returns false
        /// only when the level isn't loaded or the templates are missing.
        /// </summary>
        private static bool EnsureOpen(int index, out string error)
        {
            error = null;
            EnsureLayout();

            RinkSheetSlot slot = SlotAt(index);
            if (slot == null)
            {
                error = "That rink doesn't exist.";
                return false;
            }

            if (slot.IsMain) return true;                    // the level's own rink
            if (_serverRoots.ContainsKey(index)) return true;

            if (!RinkCloneBuilder.TemplatesAvailable())
            {
                error = "The rink isn't loaded yet. Try again in a moment.";
                return false;
            }

            // Horizontal layouts push geometry past the sync envelope, so the encoder
            // has to be running before anything is standing out there.
            if (_chunkSyncNeeded)
            {
                RinkChunkSync.EnableServer();
                if (!RinkChunkSync.ServerActive)
                {
                    error = "Extra sheets need position chunking, which this game build doesn't support.";
                    return false;
                }
            }

            GameObject root = RinkCloneBuilder.BuildServer(slot);
            if (root == null)
            {
                error = "Could not build that rink.";
                return false;
            }

            _serverRoots[index] = root;
            _lastOccupied[index] = Time.realtimeSinceStartup;


            // A host renders its own world, so it needs the visuals it would otherwise
            // have received over the wire.
            if (IsHostClient()) EnsureClientVisuals(slot);

            RinkSheetNetwork.Announce();
            return true;
        }

        private static void Close(int index)
        {
            RinkSheetSlot slot = SlotAt(index);
            if (slot == null || slot.IsMain) return;

            // Last look before the colliders go. The reap only ever picks sheets the
            // occupancy scan called empty, but the scan skips anyone without a body -
            // someone mid-respawn - and dropping a player into the void is a far worse
            // failure than tearing down a sheet a moment late.
            EvacuateSheet(index);

            if (_serverRoots.TryGetValue(index, out GameObject root))
            {
                RinkCloneBuilder.DestroyRoot(root);
                _serverRoots.Remove(index);
                ConfigManager.Log($"Sheet {slot.Label}: torn down (empty).");
            }

            _lastOccupied.Remove(index);
            DestroyClientVisuals(index);

            var stale = new List<ulong>();
            foreach (var kvp in _clientSheet)
                if (kvp.Value == index) stale.Add(kvp.Key);
            for (int i = 0; i < stale.Count; i++) _clientSheet.Remove(stale[i]);

            RinkSheetNetwork.Announce();
        }

        /// <summary>Send anyone standing on this sheet back to the arena rink.</summary>
        private static void EvacuateSheet(int index)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;

            var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            var connected = nm.ConnectedClientsIds;
            if (pm == null || connected == null) return;

            for (int i = 0; i < connected.Count; i++)
            {
                ulong clientId = connected[i];
                Player player = null;
                try { player = pm.GetPlayerByClientId(clientId); }
                catch { }

                var body = player != null ? player.PlayerBody : null;
                if (body == null) continue;
                if (SheetIndexAt(body.transform.position) != index) continue;

                TryMove(clientId, 0, out _, announce: false);
                PracticeHelpers.SendChatMessage(null,
                    "<size=70%>That practice rink was empty, so it came down. You're back on the main rink.</size>",
                    clientId);
            }
        }

        /// <summary>Pull everyone back to the main rink and tear every clone down.</summary>
        public static void CloseAll(string reason = null)
        {
            var nm = NetworkManager.Singleton;
            if (nm != null && nm.IsServer)
            {
                // Re-read who is actually standing where first. Anyone missed here is
                // left hanging in the air when their sheet's colliders go away.
                ScanOccupancy(force: true);

                var occupied = new List<ulong>(_clientSheet.Keys);
                for (int i = 0; i < occupied.Count; i++)
                {
                    if (_clientSheet.TryGetValue(occupied[i], out int sheet) && sheet != 0)
                        TryMove(occupied[i], 0, out _, announce: false);
                }
            }

            var indices = new List<int>(_serverRoots.Keys);
            for (int i = 0; i < indices.Count; i++) Close(indices[i]);

            _clientSheet.Clear();
            _lastOccupied.Clear();
            ReleaseIdleSubsystems();

            if (!string.IsNullOrEmpty(reason)) ConfigManager.Log($"Practice sheets closed: {reason}");
        }

        /// <summary>
        /// Hand back everything that only exists to serve an open sheet, once none are.
        ///
        /// Closing the sheets used to leave all of this standing: HandleNetworkDown released
        /// it, but that only runs when the session ENDS, and the warmup exit goes through
        /// CloseAll instead. So leaving warmup tore down the geometry and left behind the
        /// per-frame pump that draws it and the Harmony patches that encode its positions -
        /// both running for the whole of the game that followed, and every game after it.
        ///
        /// Both re-arm on their own: RinkCloneVisuals.Ensure rebuilds the pump when a sheet
        /// next registers a draw, EnableServer is called from the open path, and a client
        /// re-arms decode on the next chunk message it receives.
        ///
        /// Deliberately NOT released here: RinkCloneBuilder's template cache and the computed
        /// layout. Those are session-scoped by design - the arena does not change between
        /// warmups - and dropping them would make the first /rink of every warmup pay the
        /// whole clone build again. HandleNetworkDown does drop them, because there the next
        /// session really might be a different arena.
        /// </summary>
        private static void ReleaseIdleSubsystems()
        {
            if (_serverRoots.Count != 0 || _clientRoots.Count != 0) return;

            RinkCloneAudio.Restore();
            RinkCloneVisuals.Shutdown();
            RinkChunkSync.Disable();
        }

        public static void Shutdown()
        {
            CloseAll();

            var clientIndices = new List<int>(_clientRoots.Keys);
            for (int i = 0; i < clientIndices.Count; i++) DestroyClientVisuals(clientIndices[i]);
            _clientRoots.Clear();

            RinkCloneAudio.Restore();
            RinkCloneVisuals.Shutdown();
            RinkSheetNetwork.Shutdown();
            RinkChunkSync.Shutdown();
            RinkCloneBuilder.ResetCache();
            _layoutBuilt = false;
        }

        // ------------------------------------------------------------------
        // Moving players
        // ------------------------------------------------------------------

        /// <summary>
        /// Put a player on a sheet. <paramref name="index"/> is 0-based; the chat
        /// commands hand out 1-based numbers.
        /// </summary>
        public static bool TryMove(ulong clientId, int index, out string message, bool announce = true)
        {
            message = null;
            EnsureLayout();

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer)
            {
                message = "Rink changes are server-side.";
                return false;
            }

            if (!Enabled)
            {
                message = "Extra practice rinks are disabled on this server.";
                return false;
            }

            RinkSheetSlot target = SlotAt(index);
            if (target == null)
            {
                message = $"There's no rink {index + 1}. Try /rinks.";
                return false;
            }

            var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            Player player = pm != null ? pm.GetPlayerByClientId(clientId) : null;
            if (player == null)
            {
                message = "Could not find your player.";
                return false;
            }

            var body = player.PlayerBody;
            if (body == null)
            {
                message = "You need to be on the ice first.";
                return false;
            }

            if (!EnsureOpen(index, out string error))
            {
                message = error ?? "Could not open that rink.";
                return false;
            }

            Vector3 current = body.transform.position;
            int fromIndex = SheetIndexAt(current);
            if (fromIndex == index)
            {
                _clientSheet[clientId] = index;
                message = $"You're already on {target.Label}.";
                return true;
            }

            RinkSheetSlot from = SlotAt(fromIndex) ?? _slots[0];

            // Carry the player's spot across rather than dumping everyone on the same
            // dot - but in PROPORTION, not in metres.
            //
            // The sheets are stock size while the arena rink follows
            // CompetitiveAdjustments, so the two are no longer the same rink. Somebody
            // standing on the boards of a 1.2x arena is 30 m out; copying that straight
            // onto a stock sheet puts them 30 m out there too, which is through the
            // boards. Normalise out the rink they are leaving and back into the one they
            // are arriving on, so "at the blue line" stays at the blue line.
            Vector3 fromScale = SheetScale(fromIndex);
            Vector3 toScale = SheetScale(index);

            Vector3 local = current - from.Origin;
            local.x = SafeRescale(local.x, fromScale.x, toScale.x);
            local.z = SafeRescale(local.z, fromScale.z, toScale.z);

            // Anything still outside the target's own ice was never on a rink to begin
            // with; centre ice is a better answer than a spot in the crowd.
            const float StockHalfWidth = 26f;
            const float StockHalfLength = 42f;
            if (Mathf.Abs(local.x) > StockHalfWidth * toScale.x ||
                Mathf.Abs(local.z) > StockHalfLength * toScale.z)
                local = Vector3.zero;

            Vector3 destination = target.Origin + new Vector3(local.x, 0f, local.z);
            destination.y = RinkCloneBuilder.SpawnHeightFor(target.Origin);

            try
            {
                body.Server_Teleport(destination, body.transform.rotation);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[MaxPractice] Sheet teleport failed: {ex.Message}");
                message = "Teleport failed.";
                return false;
            }

            _clientSheet[clientId] = index;
            _lastOccupied[index] = Time.realtimeSinceStartup;

            // They just left a sheet, which may have been the last one standing on it.
            ScanSoon();

            message = target.IsMain
                ? "Back on the main rink."
                : $"Moved to {target.Label}. Your cones and pucks stayed behind on the rink you left.";

            if (announce) RinkSheetNetwork.Announce();
            return true;
        }

        /// <summary>
        /// How big a given sheet is relative to stock. Only the arena's own rink follows
        /// CompetitiveAdjustments; clone sheets are built at stock size and stay there.
        /// </summary>
        private static Vector3 SheetScale(int index)
        {
            // Sheets carry the arena's scale, so every sheet shares it.
            return ArenaScale;
        }

        private static float SafeRescale(float value, float from, float to)
        {
            if (Mathf.Abs(from) < 1e-4f) return value;
            return value / from * to;
        }

        /// <summary>Lowest-numbered sheet that nobody is standing on, or -1 if they're all busy.</summary>
        public static int FirstFreeSheet()
        {
            EnsureLayout();
            ScanOccupancy(force: true);

            for (int i = 1; i < _slots.Count; i++)
            {
                if (!IsOccupied(i)) return i;
            }
            return -1;
        }

        private static bool IsOccupied(int index)
        {
            foreach (var kvp in _clientSheet)
                if (kvp.Value == index) return true;
            return false;
        }

        public static string BuildSheetList(ulong clientId)
        {
            EnsureLayout();
            if (!Enabled) return "Extra practice rinks are disabled on this server.";

            ScanOccupancy(force: true);
            _clientSheet.TryGetValue(clientId, out int mine);

            var parts = new List<string>();
            for (int i = 0; i < _slots.Count; i++)
            {
                int players = 0;
                foreach (var kvp in _clientSheet)
                    if (kvp.Value == i) players++;

                // An empty-but-open sheet reads as stuck unless the grace period is shown -
                // it is deliberately kept up for a moment so a respawn does not cost you
                // the rink you were just on.
                string state;
                if (i == 0) state = "main";
                else if (!_serverRoots.ContainsKey(i)) state = "closed";
                else if (players > 0) state = players + (players == 1 ? " player" : " players");
                else
                {
                    float timeout = Mathf.Max(0f, cfg.RinkSheetIdleTimeoutSeconds);
                    float since = _lastOccupied.TryGetValue(i, out float last)
                        ? Time.realtimeSinceStartup - last
                        : 0f;
                    state = timeout > 0f
                        ? $"empty, closing in {Mathf.Max(1f, timeout - since):F0}s"
                        : "empty";
                }

                parts.Add($"/rink {i + 1} ({state}{(i == mine ? ", you" : "")})");
            }

            return "Rinks: " + string.Join("   ", parts.ToArray());
        }

        // ------------------------------------------------------------------
        // Tick
        // ------------------------------------------------------------------

        /// <summary>Pumped every frame from PracticeManager, on server and client alike.</summary>
        public static void Tick()
        {
            RinkSheetNetwork.Tick();
            RinkChunkSync.Tick();

            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsListening)
            {
                HandleNetworkDown();
                return;
            }

            if (!nm.IsServer) return;
            if (_serverRoots.Count == 0) return;

            if (Time.realtimeSinceStartup < _nextOccupancyScan) return;
            _nextOccupancyScan = Time.realtimeSinceStartup + OccupancyScanInterval;

            ScanOccupancy(force: false);
            ReapIdleSheets();
        }



        /// <summary>
        /// Rebuild the who-is-where map from live body positions rather than trusting the
        /// last teleport: a player who changes position or team gets respawned on the main
        /// rink by the game, and nothing tells us about it.
        /// </summary>
        private static void ScanOccupancy(bool force)
        {
            var nm = NetworkManager.Singleton;
            if (nm == null || !nm.IsServer) return;
            if (!force && _serverRoots.Count == 0) return;

            _clientSheet.Clear();

            var pm = MonoBehaviourSingleton<PlayerManager>.Instance;
            if (pm == null) return;

            var connected = nm.ConnectedClientsIds;
            if (connected == null) return;

            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < connected.Count; i++)
            {
                ulong clientId = connected[i];
                Player player = null;
                try { player = pm.GetPlayerByClientId(clientId); }
                catch { }

                if (player == null || MaxPracticePlugin.FakePlayers.Contains(player)) continue;

                var body = player.PlayerBody;
                if (body == null) continue;

                int sheet = SheetIndexAt(body.transform.position);
                _clientSheet[clientId] = sheet;
                if (sheet != 0) _lastOccupied[sheet] = now;
            }
        }

        private static void ReapIdleSheets()
        {
            float timeout = Mathf.Max(0f, cfg.RinkSheetIdleTimeoutSeconds);
            if (timeout <= 0f) return;

            float now = Time.realtimeSinceStartup;
            List<int> doomed = null;

            foreach (var kvp in _serverRoots)
            {
                float last;
                if (!_lastOccupied.TryGetValue(kvp.Key, out last)) last = now;
                if (now - last < timeout) continue;

                doomed = doomed ?? new List<int>();
                doomed.Add(kvp.Key);
            }

            if (doomed == null) return;
            for (int i = 0; i < doomed.Count; i++) Close(doomed[i]);
        }

        /// <summary>
        /// Re-check who is where on the next tick instead of waiting out the scan
        /// interval. Called whenever someone might have just vacated a sheet, so an
        /// empty one starts its countdown the moment it empties.
        /// </summary>
        private static void ScanSoon()
        {
            _nextOccupancyScan = 0f;
        }

        public static void OnClientDisconnected(ulong clientId)
        {
            _clientSheet.Remove(clientId);
            ScanSoon();
        }

        /// <summary>
        /// The session ended - left the server, host shut down, level unloaded. Sheet
        /// roots are DontDestroyOnLoad, so without this they would follow the player into
        /// the menu and onto the next (possibly vanilla) server, still being drawn.
        /// </summary>
        private static void HandleNetworkDown()
        {
            if (_serverRoots.Count == 0 && _clientRoots.Count == 0 && !RinkChunkSync.ClientActive) return;

            var serverIndices = new List<int>(_serverRoots.Keys);
            for (int i = 0; i < serverIndices.Count; i++)
            {
                RinkCloneBuilder.DestroyRoot(_serverRoots[serverIndices[i]]);
                _serverRoots.Remove(serverIndices[i]);
            }

            var clientIndices = new List<int>(_clientRoots.Keys);
            for (int i = 0; i < clientIndices.Count; i++) DestroyClientVisuals(clientIndices[i]);

            _clientSheet.Clear();
            _lastOccupied.Clear();

            // The arena's own audio sources were only borrowed - give their ranges back
            // before this session's state is dropped.
            RinkCloneAudio.Restore();

            // And the proxy draw rig. UnregisterSheet above empties its draw lists but
            // leaves the LIGHTMAPPED MATERIAL cache, which holds a Material copy per arena
            // material and is only freed by Shutdown - so hopping between servers leaked a
            // fresh set every session for the life of the process.
            RinkCloneVisuals.Shutdown();

            // Chunk decode must never outlive the server that asked for it: left armed
            // against a vanilla server, every object would be held in place waiting for
            // announcements that never come.
            RinkChunkSync.Disable();
            RinkCloneBuilder.ResetCache();
            _layoutBuilt = false;

            ConfigManager.Log("Practice sheets cleared (left the session).");
        }

        // ------------------------------------------------------------------
        // Client visuals
        // ------------------------------------------------------------------

        /// <summary>Every sheet currently standing, for the network broadcast.</summary>
        internal static void CollectOpenSheets(List<RinkSheetSlot> open)
        {
            open.Clear();
            foreach (var kvp in _serverRoots)
            {
                RinkSheetSlot slot = SlotAt(kvp.Key);
                if (slot != null) open.Add(slot);
            }
            open.Sort((a, b) => a.Index.CompareTo(b.Index));
        }

        /// <summary>
        /// Server said these sheets exist, and where. Build the visuals for any we're
        /// missing and drop the ones that are gone.
        ///
        /// The origins come off the wire rather than out of the local config on purpose:
        /// a client whose maxpractice.json disagrees with the server's would otherwise
        /// draw the rinks somewhere nobody is skating.
        /// </summary>
        internal static void ApplyRemoteSheets(List<RinkSheetSlot> announced)
        {
            for (int i = 0; i < announced.Count; i++)
            {
                RinkSheetSlot slot = announced[i];
                if (slot == null || slot.IsMain) continue;

                // An origin that moved means the server changed layout under us - drop
                // the stale geometry so it gets rebuilt in the right place.
                if (_clientRoots.ContainsKey(slot.Index) &&
                    _clientOrigins.TryGetValue(slot.Index, out Vector3 built) &&
                    (built - slot.Origin).sqrMagnitude > 0.01f)
                {
                    DestroyClientVisuals(slot.Index);
                }

                EnsureClientVisuals(slot);
            }

            List<int> gone = null;
            foreach (var kvp in _clientRoots)
            {
                bool stillListed = false;
                for (int i = 0; i < announced.Count; i++)
                {
                    if (announced[i] != null && announced[i].Index == kvp.Key) { stillListed = true; break; }
                }
                if (stillListed) continue;

                gone = gone ?? new List<int>();
                gone.Add(kvp.Key);
            }

            if (gone == null) return;
            for (int i = 0; i < gone.Count; i++) DestroyClientVisuals(gone[i]);
        }

        private static void EnsureClientVisuals(RinkSheetSlot slot)
        {
            if (slot == null || slot.IsMain) return;
            if (_clientRoots.ContainsKey(slot.Index)) return;
            if (RinkCloneBuilder.IsHeadless()) return;      // dedicated servers draw nothing
            if (!RinkCloneBuilder.TemplatesAvailable()) return;

            GameObject root = RinkCloneBuilder.BuildClient(slot);
            if (root == null) return;

            _clientRoots[slot.Index] = root;
            _clientOrigins[slot.Index] = slot.Origin;
            InvalidateSheetMask();
        }

        private static void DestroyClientVisuals(int index)
        {
            _clientOrigins.Remove(index);
            InvalidateSheetMask();
            if (!_clientRoots.TryGetValue(index, out GameObject root)) return;
            RinkCloneVisuals.UnregisterSheet(index);
            RinkCloneBuilder.DestroyRoot(root);
            _clientRoots.Remove(index);

            // Last sheet down: the arena's own audio sources go back to their real range,
            // and the draw pump and chunk decode go with them.
            //
            // This is the path a pure CLIENT takes on the warmup exit. CloseAll walks
            // _serverRoots, which is empty there, so a client's own teardown only happens
            // when the server's next sheet announcement arrives and clears these roots.
            ReleaseIdleSubsystems();
        }

        // ------------------------------------------------------------------
        // Diagnostics (see RinkSheetDiagnostics)
        // ------------------------------------------------------------------

        internal static List<int> DiagnosticSheetIndices()
        {
            var indices = new List<int>();
            foreach (var kvp in _serverRoots) if (!indices.Contains(kvp.Key)) indices.Add(kvp.Key);
            foreach (var kvp in _clientRoots) if (!indices.Contains(kvp.Key)) indices.Add(kvp.Key);
            indices.Sort();
            return indices;
        }

        /// <summary>
        /// Which sheet a local viewpoint is on, judged against the origins the server
        /// actually announced rather than this client's own config — the two can
        /// disagree, and drawing the wrong sheet is worse than drawing none.
        ///
        /// Returns 0 (the arena rink) when nothing is closer.
        /// </summary>
        // Cached separating-axis mask for VisibleSheetFor.
        //
        // VisibleSheetFor is one of the hottest client-side calls in the mod: the minimap
        // patch asks it once per dot per frame and the puck-elevation patch once per puck per
        // frame. It used to derive this mask by walking _clientOrigins on every one of those
        // calls, before doing the nearest-origin search it actually needs the walk for. The
        // mask is a pure function of _clientOrigins, which changes only when a sheet's visuals
        // are built or destroyed, so it is computed once per change instead.
        private static bool _maskValid;
        private static bool _maskX, _maskY, _maskZ, _maskAny;

        private static void InvalidateSheetMask() => _maskValid = false;

        /// <summary>Axes the sheets actually differ along. False when they differ along none.</summary>
        private static bool SheetAxisMask(out bool sx, out bool sy, out bool sz)
        {
            if (!_maskValid)
            {
                _maskX = false; _maskY = false; _maskZ = false;
                foreach (var kvp in _clientOrigins)
                {
                    if (Mathf.Abs(kvp.Value.x) > 0.01f) _maskX = true;
                    if (Mathf.Abs(kvp.Value.y) > 0.01f) _maskY = true;
                    if (Mathf.Abs(kvp.Value.z) > 0.01f) _maskZ = true;
                }
                _maskAny = _maskX || _maskY || _maskZ;
                _maskValid = true;
            }

            sx = _maskX; sy = _maskY; sz = _maskZ;
            return _maskAny;
        }

        internal static int VisibleSheetFor(Vector3 viewpoint)
        {
            if (_clientOrigins.Count == 0) return 0;

            // Same axis-masking as SheetIndexAt: only the axes the sheets actually
            // differ along get a vote, or a player behind the far net on their own sheet
            // measures closer to the one above them.
            if (!SheetAxisMask(out bool sx, out bool sy, out bool sz)) return 0;

            int best = 0;
            float bestDistance = Masked(viewpoint, Vector3.zero, sx, sy, sz);

            foreach (var kvp in _clientOrigins)
            {
                float d = Masked(viewpoint, kvp.Value, sx, sy, sz);
                if (d >= bestDistance) continue;
                bestDistance = d;
                best = kvp.Key;
            }

            return best;
        }

        /// <summary>
        /// True once the server has announced at least one clone sheet to this client.
        /// Anything client-side that has to behave differently while sheets are up
        /// should gate on this rather than on <see cref="Enabled"/>, which only reads
        /// the LOCAL config and is therefore meaningless on a connected client.
        /// </summary>
        internal static bool HasAnnouncedSheets => _clientOrigins.Count > 0;

        /// <summary>
        /// Where the server said this sheet stands, which is not necessarily where this
        /// client's own config would have put it — same reasoning as VisibleSheetFor.
        /// The main rink is the level's own geometry and is always at zero.
        /// </summary>
        internal static Vector3 AnnouncedOrigin(int index)
        {
            if (index <= 0) return Vector3.zero;
            return _clientOrigins.TryGetValue(index, out Vector3 origin) ? origin : Vector3.zero;
        }

        private static float Masked(Vector3 a, Vector3 b, bool sx, bool sy, bool sz)
        {
            float total = 0f;
            if (sx) { float d = a.x - b.x; total += d * d; }
            if (sy) { float d = a.y - b.y; total += d * d; }
            if (sz) { float d = a.z - b.z; total += d * d; }
            return total;
        }

        internal static GameObject DiagnosticServerRoot(int index)
        {
            return _serverRoots.TryGetValue(index, out GameObject root) ? root : null;
        }

        internal static GameObject DiagnosticClientRoot(int index)
        {
            return _clientRoots.TryGetValue(index, out GameObject root) ? root : null;
        }

        private static bool IsHostClient()
        {
            var nm = NetworkManager.Singleton;
            return nm != null && nm.IsServer && nm.IsClient;
        }
    }
}
