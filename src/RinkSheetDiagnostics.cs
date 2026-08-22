// RinkSheetDiagnostics.cs - Dump what a cloned sheet actually IS, versus the rink it
// was copied from.
//
// This exists because two rounds of reasoning about why sheet physics and lighting come
// out wrong produced two plausible fixes and no improvement. Guessing from the outside
// has had its turn; this walks the clone and the original side by side and reports every
// place they differ, so the next change is aimed at something real.
//
// Run it with /rinkdiag while standing anywhere, with a sheet open.

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace MaxPractice
{
    internal static class RinkSheetDiagnostics
    {
        private const int MaxReportedMismatches = 40;

        internal static string WriteReport()
        {
            var sb = new StringBuilder();
            sb.AppendLine("=== MaxPractice rink sheet diagnostic ===");

            try
            {
                ReportLayout(sb);
                ReportLighting(sb);

                foreach (int index in RinkSheets.DiagnosticSheetIndices())
                {
                    RinkSheetSlot slot = RinkSheets.SlotAt(index);
                    if (slot == null) continue;

                    ReportServerClone(sb, slot);
                    ReportClientClone(sb, slot);
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("diagnostic failed: " + ex);
            }

            string report = sb.ToString();
            // One Debug.Log per line: the game's logger truncates long single messages,
            // and a truncated diagnostic is worse than none.
            foreach (string line in report.Split('\n'))
            {
                if (!string.IsNullOrEmpty(line.Trim())) Debug.Log("[MaxPractice][diag] " + line.TrimEnd());
            }

            return report;
        }

        // ------------------------------------------------------------------

        private static void ReportLayout(StringBuilder sb)
        {
            var slots = RinkSheets.Slots;
            sb.AppendLine($"layout: {slots.Count} slot(s), chunkSync={RinkSheets.ChunkSyncRequired}, openClones={RinkSheets.OpenCloneCount}");
            for (int i = 0; i < slots.Count; i++)
                sb.AppendLine($"  slot[{i}] {slots[i].Id} origin={Fmt(slots[i].Origin)}");

            Transform levelRoot = RinkCloneBuilder.DiagnosticLevelRoot();
            if (levelRoot == null)
            {
                sb.AppendLine("level root: NOT FOUND");
                return;
            }

            sb.AppendLine($"level root: '{levelRoot.name}' pos={Fmt(levelRoot.position)} " +
                          $"localScale={Fmt(levelRoot.localScale)} lossyScale={Fmt(levelRoot.lossyScale)}");
            sb.AppendLine($"ice surface Y (measured): {RinkCloneBuilder.IceSurfaceY:F3}");
            sb.AppendLine($"arena scale: {Fmt(RinkSheets.ArenaScale)} (1,1,1 = stock; CompetitiveAdjustments resizes this)");

            float bx0, bx1, bz0, bz1;
            RinkSheets.IceBoundsAt(Vector3.zero, out bx0, out bx1, out bz0, out bz1);
            sb.AppendLine($"playable bounds on the arena rink: x[{bx0:F1},{bx1:F1}] z[{bz0:F1},{bz1:F1}]");

            // The whole layout rests on staying inside this box, and the box is NOT a cube:
            // the wire's X half-range is half its Z. Taking max(|x|,|z|) against a single
            // limit tested the wider axis against the looser bound, so it never fired for
            // the axis that actually runs out first.
            sb.AppendLine($"  wire limits: x ±{SheetLayout.SyncRangeX:F0} y ±{SheetLayout.SyncRangeY:F0} z ±{SheetLayout.SyncRangeZ:F0} " +
                          "(positions outside these saturate at the boundary for remote clients)");

            // Measured against the RAW rink, not the bounds above: IceBoundsAt now clips to
            // the wire, so reading the clipped figure back could never report a problem.
            Vector3 arena = RinkSheets.ArenaScale;
            WarnIfPastWire(sb, "x", 26f * arena.x, SheetLayout.SyncRangeX);
            WarnIfPastWire(sb, "z", 42f * arena.z, SheetLayout.SyncRangeZ);
        }

        /// <summary>
        /// Flag an axis whose playable ice reaches past what the wire can carry. The strip
        /// beyond the limit is still skateable — it is only positions SENT to other clients
        /// that saturate — so this is about where things may be spawned, not where the
        /// boards are.
        /// </summary>
        private static void WarnIfPastWire(StringBuilder sb, string axis, float halfExtent, float limit)
        {
            if (halfExtent <= limit) return;
            sb.AppendLine($"  WARNING: the arena's playable {axis} reaches ±{halfExtent:F1} m, past the ±{limit:F0} m " +
                          $"position-encoding limit — the outer {halfExtent - limit:F1} m of {axis} cannot be " +
                          "represented, so anything spawned out there sits on the limit for everyone else. " +
                          "Spawn bounds are clipped to compensate.");
        }

        private static void ReportLighting(StringBuilder sb)
        {
            sb.AppendLine("--- lighting ---");
            sb.AppendLine($"ambient: mode={RenderSettings.ambientMode} intensity={RenderSettings.ambientIntensity:F2} " +
                          $"light={RenderSettings.ambientLight} reflectionIntensity={RenderSettings.reflectionIntensity:F2}");
            sb.AppendLine($"lightmaps loaded: {(LightmapSettings.lightmaps != null ? LightmapSettings.lightmaps.Length : 0)}");

            Light[] lights = UnityEngine.Object.FindObjectsByType<Light>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            int near = 0;
            sb.AppendLine($"lights in scene: {lights.Length}");
            for (int i = 0; i < lights.Length; i++)
            {
                Light l = lights[i];
                if (l == null) continue;
                bool isClone = l.transform.root != null && l.transform.root.name.StartsWith("MaxPractice_", StringComparison.Ordinal);
                if (isClone) continue;

                near++;
                if (near <= 24)
                {
                    sb.AppendLine($"  '{l.name}' type={l.type} pos={Fmt(l.transform.position)} intensity={l.intensity:F2} " +
                                  $"range={l.range:F1} enabled={l.enabled} baked={l.bakingOutput.isBaked} " +
                                  $"shadows={l.shadows}");
                }
            }

            ReflectionProbe[] probes = UnityEngine.Object.FindObjectsByType<ReflectionProbe>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            sb.AppendLine($"reflection probes in scene: {probes.Length}");
            for (int i = 0; i < probes.Length && i < 12; i++)
            {
                ReflectionProbe p = probes[i];
                if (p == null) continue;
                sb.AppendLine($"  '{p.name}' pos={Fmt(p.transform.position)} mode={p.mode} size={Fmt(p.size)} " +
                              $"texture={(p.texture != null ? p.texture.name : "null")} " +
                              $"baked={(p.bakedTexture != null ? p.bakedTexture.name : "null")} " +
                              $"intensity={p.intensity:F2} boxProjection={p.boxProjection}");
            }
        }

        // ------------------------------------------------------------------

        private static void ReportServerClone(StringBuilder sb, RinkSheetSlot slot)
        {
            GameObject root = RinkSheets.DiagnosticServerRoot(slot.Index);
            sb.AppendLine($"--- server clone {slot.Id} ---");
            if (root == null)
            {
                sb.AppendLine("  (not built on this process)");
                return;
            }

            Transform levelRoot = RinkCloneBuilder.DiagnosticLevelRoot();
            List<Transform> templates = RinkCloneBuilder.DiagnosticTemplates(levelRoot);
            sb.AppendLine($"  templates cloned: {templates.Count}");

            int cloneColliders = 0, originalColliders = 0, mismatches = 0, reported = 0;
            int cloneRb = 0, cloneDynamicRb = 0, originalRb = 0, originalDynamicRb = 0;

            for (int t = 0; t < templates.Count; t++)
            {
                Transform src = templates[t];
                Transform dst = FindCloneOf(root.transform, src, slot);
                if (dst == null)
                {
                    sb.AppendLine($"  template '{src.name}': NO MATCHING CLONE");
                    continue;
                }

                sb.AppendLine($"  '{src.name}': original lossyScale={Fmt(src.lossyScale)} clone lossyScale={Fmt(dst.lossyScale)}" +
                              (Approximately(src.lossyScale, dst.lossyScale) ? "" : "   <-- SCALE MISMATCH"));

                var srcByPath = new Dictionary<string, Collider>(StringComparer.Ordinal);
                foreach (Collider c in src.GetComponentsInChildren<Collider>(true))
                {
                    if (c == null) continue;
                    originalColliders++;
                    srcByPath[PathOf(src, c.transform) + "#" + c.GetType().Name] = c;
                }

                foreach (Rigidbody rb in src.GetComponentsInChildren<Rigidbody>(true))
                {
                    if (rb == null) continue;
                    originalRb++;
                    if (!rb.isKinematic) originalDynamicRb++;
                }

                foreach (Rigidbody rb in dst.GetComponentsInChildren<Rigidbody>(true))
                {
                    if (rb == null) continue;
                    cloneRb++;
                    if (!rb.isKinematic) cloneDynamicRb++;
                }

                foreach (Collider c in dst.GetComponentsInChildren<Collider>(true))
                {
                    if (c == null) continue;
                    cloneColliders++;

                    string key = StripPrefix(PathOf(dst, c.transform)) + "#" + c.GetType().Name;
                    if (!srcByPath.TryGetValue(key, out Collider original))
                    {
                        mismatches++;
                        if (reported++ < MaxReportedMismatches)
                            sb.AppendLine($"    EXTRA collider in clone: {key} enabled={c.enabled} trigger={c.isTrigger} layer={LayerMask.LayerToName(c.gameObject.layer)}");
                        continue;
                    }

                    string diff = DescribeColliderDiff(original, c, slot.Origin);
                    if (diff == null) continue;

                    mismatches++;
                    if (reported++ < MaxReportedMismatches)
                        sb.AppendLine($"    DIFF {key}: {diff}");
                }
            }

            sb.AppendLine($"  colliders: original={originalColliders} clone={cloneColliders} mismatches={mismatches}" +
                          (reported > MaxReportedMismatches ? $" (showing first {MaxReportedMismatches})" : ""));
            sb.AppendLine($"  rigidbodies: original={originalRb} (dynamic {originalDynamicRb}) clone={cloneRb} (dynamic {cloneDynamicRb})" +
                          (cloneDynamicRb > originalDynamicRb ? "   <-- CLONE HAS LOOSE RIGIDBODIES" : ""));

            ReportDuplicateColliders(sb, root.transform);
        }

        /// <summary>
        /// Two colliders in the same place is the classic way to launch a rigidbody that
        /// touches them, so count how many clone colliders share a position.
        /// </summary>
        private static void ReportDuplicateColliders(StringBuilder sb, Transform root)
        {
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            int duplicates = 0;

            foreach (Collider c in root.GetComponentsInChildren<Collider>(true))
            {
                if (c == null || c.isTrigger) continue;
                Vector3 p = c.bounds.center;
                string key = c.GetType().Name + ":" +
                             Mathf.RoundToInt(p.x * 20f) + "," +
                             Mathf.RoundToInt(p.y * 20f) + "," +
                             Mathf.RoundToInt(p.z * 20f);

                if (seen.ContainsKey(key)) { seen[key]++; duplicates++; }
                else seen[key] = 1;
            }

            sb.AppendLine($"  overlapping colliders in clone: {duplicates}" +
                          (duplicates > 0 ? "   <-- OVERLAP" : ""));
        }

        private static string DescribeColliderDiff(Collider original, Collider clone, Vector3 sheetOrigin)
        {
            var parts = new List<string>();

            // The check that matters most for "my stick jumps to rink one's height": is
            // this collider's collision geometry actually where the clone appears to be?
            // A mesh whose vertices were baked in world space does not move with its
            // transform, so the visual can sit on the sheet while the thing you collide
            // with stays up at the original rink.
            Vector3 expectedCenter = original.bounds.center + sheetOrigin;
            Vector3 actualCenter = clone.bounds.center;
            if ((expectedCenter - actualCenter).sqrMagnitude > 0.25f)
                parts.Add($"WORLD POSITION expected={Fmt(expectedCenter)} actual={Fmt(actualCenter)} " +
                          $"off by {Fmt(actualCenter - expectedCenter)}");

            if (original.enabled != clone.enabled)
                parts.Add($"enabled {original.enabled}->{clone.enabled}");
            if (original.isTrigger != clone.isTrigger)
                parts.Add($"trigger {original.isTrigger}->{clone.isTrigger}");
            if (original.gameObject.layer != clone.gameObject.layer)
                parts.Add($"layer {LayerMask.LayerToName(original.gameObject.layer)}->{LayerMask.LayerToName(clone.gameObject.layer)}");
            if (original.gameObject.activeInHierarchy != clone.gameObject.activeInHierarchy)
                parts.Add($"active {original.gameObject.activeInHierarchy}->{clone.gameObject.activeInHierarchy}");

            PhysicsMaterial om = original.sharedMaterial, cm = clone.sharedMaterial;
            if ((om == null) != (cm == null))
                parts.Add($"material {(om != null ? om.name : "null")}->{(cm != null ? cm.name : "null")}");
            else if (om != null && cm != null &&
                     (!Mathf.Approximately(om.dynamicFriction, cm.dynamicFriction) ||
                      !Mathf.Approximately(om.bounciness, cm.bounciness)))
                parts.Add($"material {om.name}(f{om.dynamicFriction:F2},b{om.bounciness:F2})->{cm.name}(f{cm.dynamicFriction:F2},b{cm.bounciness:F2})");

            var omc = original as MeshCollider;
            var cmc = clone as MeshCollider;
            if (omc != null && cmc != null)
            {
                if (omc.convex != cmc.convex) parts.Add($"convex {omc.convex}->{cmc.convex}");
                string on = omc.sharedMesh != null ? omc.sharedMesh.name : "null";
                string cn = cmc.sharedMesh != null ? cmc.sharedMesh.name : "null";
                if (on != cn) parts.Add($"mesh {on}->{cn}");
            }

            if (!Approximately(original.transform.lossyScale, clone.transform.lossyScale))
                parts.Add($"scale {Fmt(original.transform.lossyScale)}->{Fmt(clone.transform.lossyScale)}");

            Vector3 expected = original.bounds.size;
            Vector3 actual = clone.bounds.size;
            if (!Approximately(expected, actual))
                parts.Add($"boundsSize {Fmt(expected)}->{Fmt(actual)}");

            return parts.Count == 0 ? null : string.Join(", ", parts.ToArray());
        }

        // ------------------------------------------------------------------

        private static void ReportClientClone(StringBuilder sb, RinkSheetSlot slot)
        {
            GameObject root = RinkSheets.DiagnosticClientRoot(slot.Index);
            sb.AppendLine($"--- client clone {slot.Id} ---");
            if (root == null)
            {
                sb.AppendLine("  (not built on this process)");
                return;
            }

            int total = 0, enabledReal = 0, disabled = 0, lightmapped = 0, noMaterial = 0;
            foreach (Renderer r in root.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null) continue;
                total++;
                if (r.sharedMaterial == null) noMaterial++;
                if (r.enabled) enabledReal++; else disabled++;
                if (r.lightmapIndex >= 0 && r.lightmapIndex < 65534) lightmapped++;
            }

            sb.AppendLine($"  renderers: total={total} drawingThemselves={enabledReal} handedToProxy={disabled} " +
                          $"lightmapped={lightmapped} noMaterial={noMaterial}");
            sb.AppendLine($"  proxy draw entries: {RinkCloneVisuals.DrawCount}");
            sb.AppendLine($"  cloned lights under sheet: {root.GetComponentsInChildren<Light>(true).Length}, " +
                          $"cloned probes: {root.GetComponentsInChildren<ReflectionProbe>(true).Length}");

            // What the original looks like, for comparison.
            Transform levelRoot = RinkCloneBuilder.DiagnosticLevelRoot();
            List<Transform> templates = RinkCloneBuilder.DiagnosticTemplates(levelRoot);
            int srcTotal = 0, srcBatched = 0, srcLightmapped = 0;
            for (int t = 0; t < templates.Count; t++)
            {
                foreach (Renderer r in templates[t].GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    srcTotal++;
                    var mr = r as MeshRenderer;
                    if (mr != null && mr.isPartOfStaticBatch) srcBatched++;
                    if (r.lightmapIndex >= 0 && r.lightmapIndex < 65534) srcLightmapped++;
                }
            }
            sb.AppendLine($"  original for comparison: total={srcTotal} staticBatched={srcBatched} lightmapped={srcLightmapped}");
        }

        // ------------------------------------------------------------------

        /// <summary>
        /// Match a template to its clone under the sheet root.
        ///
        /// Must accept the "MP " prefix as well as the bare name. RinkCloneBuilder renames
        /// every object inside a finished clone so other mods can't find it by name, and
        /// that runs before anyone gets to look at it — so testing only the bare name meant
        /// this returned null for every template and the whole per-template comparison
        /// below reported "NO MATCHING CLONE". LogPlacement already tested both; this was
        /// the copy that got missed.
        /// </summary>
        private static Transform FindCloneOf(Transform cloneRoot, Transform template, RinkSheetSlot slot)
        {
            string expected = template.name.Replace(' ', '_') + "_" + slot.Id;
            for (int i = 0; i < cloneRoot.childCount; i++)
            {
                Transform child = cloneRoot.GetChild(i);
                if (string.Equals(child.name, expected, StringComparison.Ordinal)) return child;
                if (string.Equals(child.name, RinkCloneBuilder.ClonePrefix + expected, StringComparison.Ordinal))
                    return child;
            }
            return null;
        }

        private static string PathOf(Transform root, Transform node)
        {
            if (root == null || node == null || node == root) return string.Empty;
            var parts = new List<string>();
            Transform current = node;
            while (current != null && current != root)
            {
                parts.Add(current.name);
                current = current.parent;
            }
            parts.Reverse();
            return string.Join("/", parts.ToArray());
        }

        /// <summary>Clone children are name-prefixed; strip it so paths line up with the original.</summary>
        private static string StripPrefix(string path)
        {
            return path.Replace(RinkCloneBuilder.ClonePrefix, string.Empty);
        }

        private static bool Approximately(Vector3 a, Vector3 b)
        {
            return (a - b).sqrMagnitude < 0.0004f;
        }

        private static string Fmt(Vector3 v) => $"({v.x:F2},{v.y:F2},{v.z:F2})";
    }
}
