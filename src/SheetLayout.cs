// SheetLayout.cs - Where the extra practice sheets sit, and why they sit there.
//
// Puck quantises every synchronized position into an Int16, PER AXIS, against a
// fixed range: SynchronizedObjectData holds POSITION_MIN/MAX_X = +/-25 and
// POSITION_MIN/MAX_Y and _Z = +/-50, and NetworkingUtils.CompressFloatToShort maps
// [min,max] onto the full short. The vanilla rink is ~45 m wide and ~91.5 m long,
// which only just fits that box centred on the origin - there is no room to put a
// second sheet beside it.
//
// Two corrections to what this file used to say, both verified against the shipped
// metadata (see SyncRange* below):
//
//   The box is NOT a uniform +/-50. X is half of Y and Z. A rink laid out sideways
//   runs out of wire in 25 m, not 50.
//
//   Out-of-range does NOT wrap. CompressFloatToShort is InverseLerp (which clamps
//   to [0,1]) followed by an explicit Mathf.Clamp, so a coordinate past the range
//   SATURATES at the boundary. The failure mode is an object pinned to the edge of
//   the box on every remote client, not one teleporting somewhere absurd - which is
//   still broken, just differently, and matters when reading a bug report.
//
// Two ways out, and the layout mode picks between them:
//
//   Vertical (default) - stack the clones straight down. X and Z never change, so
//     every coordinate stays inside the vanilla envelope and the netcode is left
//     completely alone. Costs nothing, works against unmodded clients, and the
//     only limit is how many 20-something-metre floors fit above -50 m.
//
//   Horizontal - lay the sheets out on a grid like a real multi-sheet facility.
//     This needs RinkChunkSync to re-encode positions chunk-locally, which means
//     patching the sync hot path. Sheets are pinned to the 32 m chunk grid so the
//     main rink always lands in chunk (0,0) and encodes byte-identically to
//     vanilla - an unmodded client still sees the main sheet correctly.

using System.Collections.Generic;
using UnityEngine;

namespace MaxPractice
{
    internal enum SheetLayoutMode
    {
        Vertical,
        Horizontal,
    }

    internal sealed class RinkSheetSlot
    {
        public int Index;          // 0 = the level's own rink
        public string Id;          // "rink1", "rink2", ...
        public string Label;       // "Rink 1", "Rink 2", ...
        public Vector3 Origin;     // offset from the level's own rink

        public bool IsMain => Index == 0;
    }

    internal static class SheetLayout
    {
        // Half-extent of the position encoding, per axis, straight off
        // SynchronizedObjectData's POSITION_MIN_*/POSITION_MAX_* literals. A coordinate
        // outside these is clamped to the boundary before it goes on the wire, so nothing
        // this mod places may push past them.
        //
        // X being half of Y and Z is the trap: it is nearly a metre tighter than the
        // playable width this mod hands out (IceBoundsAt's 26 m), and CompetitiveAdjustments
        // widening the arena pushes it much further out still.

        /// <summary>Half-extent of the wire's X range. HALF of Y and Z - do not assume symmetry.</summary>
        public const float SyncRangeX = 25f;

        /// <summary>Half-extent of the wire's Y range. This is the one a vertical stack spends.</summary>
        public const float SyncRangeY = 50f;

        /// <summary>Half-extent of the wire's Z range.</summary>
        public const float SyncRangeZ = 50f;

        /// <summary>
        /// The vertical budget, kept under the old name because that is the axis every
        /// caller of it was actually talking about. Use <see cref="SyncRangeX"/> for X.
        /// </summary>
        public const float SyncRangeMeters = SyncRangeY;

        /// <summary>
        /// Vertical clearance kept past the outermost sheet: pucks get popped, players
        /// jump, and a coordinate that crosses ±50 m does not clip, it wraps.
        ///
        /// This only binds on the TOP sheet - the bottom one has the whole downward half of
        /// the envelope under it and nothing travels down through the ice - so it is really
        /// "how far above a sheet's ice can something get before it desyncs".
        ///
        /// 2 m was the value while UsableRange still scaled with the arena, where the cap
        /// was mostly unreachable; with the range fixed at vanilla it becomes the live
        /// budget the moment a scaled arena pushes the requested spacing over it, and 2 m
        /// is not enough - a body spawns 0.93 m up on its own, so a bump or a popped puck
        /// clears it. 6 m costs nothing in practice: the default stock layout (40 m
        /// spacing) is nowhere near the cap either way, and the only configs that change
        /// are the ones that were being clamped, which lose ~4 m of a separation they had
        /// far more of than they needed.
        /// </summary>
        private const float VerticalHeadroom = 6f;

        /// <summary>Chunk grid used by <see cref="RinkChunkSync"/>. Grid origins snap to it.</summary>
        public const float ChunkSize = 32f;

        private const int GridColumns = 3;

        public static SheetLayoutMode ParseMode(string raw)
        {
            if (!string.IsNullOrEmpty(raw) && raw.Trim().ToLowerInvariant().StartsWith("h"))
                return SheetLayoutMode.Horizontal;
            return SheetLayoutMode.Vertical;
        }

        /// <summary>
        /// How many sheets the requested layout can actually carry. Vertical is capped
        /// by the sync envelope; horizontal is capped by the sbyte chunk grid, which is
        /// far larger than anything a practice server needs.
        /// </summary>
        public static int MaxSupportedSheets(SheetLayoutMode mode, float verticalSpacing, float arenaScaleY = 1f)
        {
            if (mode == SheetLayoutMode.Horizontal) return 9;

            // Sheets alternate below and above the arena rink, so the budget is the
            // usable range in BOTH directions, not just downward.
            float spacing = EffectiveSpacing(verticalSpacing, arenaScaleY);
            int levels = Mathf.FloorToInt(UsableRange() / spacing);
            return Mathf.Max(1, 1 + levels * 2);
        }

        /// <summary>
        /// Spacing in world metres, grown with the arena's height.
        ///
        /// A rink scaled to 1.23 is 1.23 times taller, so a gap measured against the
        /// stock rink no longer clears it - the sheets need raising by the same factor.
        /// The ±50 m sync envelope does NOT grow to match, so the result is capped: past
        /// that the sheet would simply not be on the wire. A tall enough arena therefore
        /// gets FEWER sheets rather than desynced ones.
        /// </summary>
        public static float EffectiveSpacing(float verticalSpacing, float arenaScaleY)
        {
            float spacing = Mathf.Max(8f, verticalSpacing) * Mathf.Max(0.05f, arenaScaleY);
            return Mathf.Min(spacing, UsableRange());
        }

        /// <summary>
        /// How far a sheet may sit from the arena rink before positions stop surviving
        /// the wire. Fixed at the vanilla figure, whatever the arena is scaled to.
        ///
        /// The ±50 m comes from the 16-bit position encoding, and it is per-AXIS. A
        /// resized arena does not buy vertical room: CompetitiveAdjustments redistributes
        /// the budget ACROSS axes rather than raising it - its own log reads "wire range
        /// now X +/-30.5m Y +/-50.0m Z +/-60.5m" on a 1.2x rink, i.e. X shrank, Z grew,
        /// and Y stayed exactly where vanilla left it.
        ///
        /// Nothing on our side moves that ceiling either. RinkChunkSync only re-encodes
        /// X and Z (RinkChunkCoord.Origin has y = 0), and a vertical layout does not even
        /// arm it - NeedsChunkSync tests o.x/o.z only. So Y is always raw vanilla, and
        /// scaling this with the arena would place the outer sheets past the wrap.
        /// </summary>
        private static float UsableRange()
        {
            return SyncRangeMeters - VerticalHeadroom;
        }

        /// <summary>
        /// Vertical offset for a sheet: 0, −s, +s, −2s, +2s …
        ///
        /// Stacking straight down runs out of room fast — the ±50 m sync envelope means
        /// 40 m spacing only fits one sheet below before the next would be at −80 and
        /// off the wire. Alternating uses the range on both sides of the rink instead,
        /// which buys a third sheet at full spacing rather than making them huddle.
        ///
        /// The sheet above is only tolerable because a client draws just the sheet its
        /// own player is standing on; without that, everyone on the arena rink would be
        /// skating under a second rink hanging in the sky.
        /// </summary>
        private static float VerticalOffset(int index, float spacing)
        {
            if (index <= 0) return 0f;

            int level = (index + 1) / 2;          // 1,1,2,2,3,3…
            bool below = (index % 2) == 1;        // odd indices go down first
            return (below ? -1f : 1f) * level * spacing;
        }

        public static List<RinkSheetSlot> Build(
            SheetLayoutMode mode, int sheetCount, float verticalSpacing, float gridSpacingX, float gridSpacingZ,
            float arenaScaleY = 1f)
        {
            int count = Mathf.Clamp(sheetCount, 1, MaxSupportedSheets(mode, verticalSpacing, arenaScaleY));
            float spacingY = EffectiveSpacing(verticalSpacing, arenaScaleY);
            var slots = new List<RinkSheetSlot>(count);

            for (int i = 0; i < count; i++)
            {
                Vector3 origin = mode == SheetLayoutMode.Horizontal
                    ? GridOrigin(i, gridSpacingX, gridSpacingZ)
                    : new Vector3(0f, VerticalOffset(i, spacingY), 0f);

                slots.Add(new RinkSheetSlot
                {
                    Index = i,
                    Id = "rink" + (i + 1),
                    Label = "Rink " + (i + 1),
                    Origin = i == 0 ? Vector3.zero : origin,
                });
            }

            return slots;
        }

        /// <summary>
        /// Grid origins are snapped to the chunk grid so every sheet sits at the centre
        /// of its own chunk. Without that a sheet could straddle a chunk boundary and
        /// objects would flip chunks mid-rink, which is exactly the case the deferred
        /// chunk switch is worst at.
        /// </summary>
        private static Vector3 GridOrigin(int index, float spacingX, float spacingZ)
        {
            float x = SnapToChunk((index % GridColumns) * Mathf.Max(ChunkSize, spacingX));
            float z = SnapToChunk((index / GridColumns) * Mathf.Max(2f * ChunkSize, spacingZ));
            return new Vector3(x, 0f, z);
        }

        private static float SnapToChunk(float value)
        {
            return Mathf.Round(value / ChunkSize) * ChunkSize;
        }

        /// <summary>
        /// True when the layout pushes rink geometry outside the vanilla sync envelope,
        /// i.e. when positions have to be re-encoded chunk-locally to survive the wire.
        /// A vertical stack never does; any horizontal offset always does.
        /// </summary>
        public static bool NeedsChunkSync(List<RinkSheetSlot> slots)
        {
            if (slots == null) return false;
            for (int i = 0; i < slots.Count; i++)
            {
                Vector3 o = slots[i].Origin;
                if (Mathf.Abs(o.x) > 0.01f || Mathf.Abs(o.z) > 0.01f) return true;
            }
            return false;
        }
    }
}
