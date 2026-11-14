using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Rendering;
using UnityEngine;

namespace MultiViewExperiment
{
    /// <summary>
    /// Extra view regions (in cells) that every culling system should treat as visible, in addition to
    /// the main camera. Written by ExperimentController.Update, read by the patches below in LateUpdate.
    /// </summary>
    internal static class MultiViewRegistry
    {
        public static bool Enabled;        // config "union"
        public static bool Suspended;      // temporarily off (off_nounion A/B shot)
        public static readonly List<RectInt> Regions = new List<RectInt>(); // [xMin,xMax) x [yMin,yMax)

        public static bool Active => Enabled && !Suspended && Regions.Count > 0;

        /// <summary>While set, GridVisibleArea.GetVisibleAreaExtended returns this instead of the main view.</summary>
        public static GridArea? GridAreaOverride;

        public static RectInt ClampToGrid(RectInt r)
        {
            int x0 = Mathf.Clamp(r.xMin, 0, Grid.WidthInCells), x1 = Mathf.Clamp(r.xMax, 0, Grid.WidthInCells);
            int y0 = Mathf.Clamp(r.yMin, 0, Grid.HeightInCells), y1 = Mathf.Clamp(r.yMax, 0, Grid.HeightInCells);
            return new RectInt(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
        }

        public static GridArea ToGridArea(RectInt r)
        {
            r = ClampToGrid(r);
            var a = new GridArea();
            a.SetExtents(r.xMin, r.yMin, r.xMax, r.yMax);
            return a;
        }
    }

    /// <summary>Per-frame cost of the patched systems (ms), averaged between two reads.</summary>
    internal static class Perf
    {
        private static readonly Dictionary<string, (double sum, int n)> acc = new Dictionary<string, (double, int)>();

        public static void Add(string key, long ticks)
        {
            double ms = ticks * 1000.0 / Stopwatch.Frequency;
            acc.TryGetValue(key, out var v);
            acc[key] = (v.sum + ms, v.n + 1);
        }

        public static string ReadAndReset()
        {
            var parts = new List<string>();
            foreach (var kv in acc)
                parts.Add($"{kv.Key}={(kv.Value.n > 0 ? kv.Value.sum / kv.Value.n : 0):F2}ms");
            acc.Clear();
            return string.Join(" ", parts);
        }
    }

    // ------------------------------------------------------------------ ground tiles (+ KAnim batch submit)

    /// <summary>World.LateUpdate draws ground chunks for the main view and submits KAnim batches;
    /// after it, draw ground chunks for each extra region too.</summary>
    [HarmonyPatch(typeof(World), "LateUpdate")]
    internal static class World_LateUpdate_Patch
    {
        private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void Postfix(World __instance, long __state)
        {
            if (MultiViewRegistry.Active && !Game.IsQuitting() && !GameUtil.IsCapturingTimeLapse()
                && __instance.groundRenderer != null)
            {
                foreach (RectInt r in MultiViewRegistry.Regions)
                {
                    GridArea a = MultiViewRegistry.ToGridArea(r);
                    __instance.groundRenderer.Render(a.Min, a.Max);
                }
            }
            long dt = Stopwatch.GetTimestamp() - __state;
            ExperimentController.LastWorldLateUpdateTicks = dt;
            Perf.Add("World.LateUpdate", dt);
        }
    }

    // ------------------------------------------------------------------ building tiles

    [HarmonyPatch(typeof(GridVisibleArea), nameof(GridVisibleArea.GetVisibleAreaExtended))]
    internal static class GridVisibleArea_Override_Patch
    {
        private static bool Prefix(ref GridArea __result)
        {
            if (MultiViewRegistry.GridAreaOverride is GridArea a)
            {
                __result = a;
                return false;
            }
            return true;
        }
    }

    /// <summary>BlockTileRenderer.Render() reads GridVisibleArea; run it again once per extra region
    /// with the visible area overridden to that region.</summary>
    [HarmonyPatch(typeof(BlockTileRenderer), nameof(BlockTileRenderer.LateUpdate))]
    internal static class BlockTileRenderer_LateUpdate_Patch
    {
        private static readonly MethodInfo RenderMethod = AccessTools.Method(typeof(BlockTileRenderer), "Render");

        private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void Postfix(BlockTileRenderer __instance, long __state)
        {
            if (MultiViewRegistry.Active && !GameUtil.IsCapturingTimeLapse())
            {
                try
                {
                    foreach (RectInt r in MultiViewRegistry.Regions)
                    {
                        MultiViewRegistry.GridAreaOverride = MultiViewRegistry.ToGridArea(r);
                        RenderMethod.Invoke(__instance, null);
                    }
                }
                finally
                {
                    MultiViewRegistry.GridAreaOverride = null;
                }
            }
            long dt = Stopwatch.GetTimestamp() - __state;
            ExperimentController.LastBlockTileLateUpdateTicks = dt;
            Perf.Add("BlockTile.LateUpdate", dt);
        }
    }

    // ------------------------------------------------------------------ KAnim visibility

    /// <summary>
    /// Replacement for KBatchedAnimUpdater.UpdateVisibility that marks chunks visible for the main view
    /// AND every extra region (a set of rectangles, not their bounding box), so controllers there are
    /// visible (numFrames != 0) and keep animating.
    /// </summary>
    [HarmonyPatch(typeof(KBatchedAnimUpdater), "UpdateVisibility")]
    internal static class KBatchedAnimUpdater_UpdateVisibility_Patch
    {
        private const int Chunk = KAnimBatchManager.CHUNK_SIZE;
        private const int Padding = 4; // same padding Grid.GetVisibleCellRangeInActiveWorld uses

        private static readonly AccessTools.FieldRef<KBatchedAnimUpdater, Dictionary<int, KBatchedAnimController>[,]> ControllerGrid =
            AccessTools.FieldRefAccess<KBatchedAnimUpdater, Dictionary<int, KBatchedAnimController>[,]>("controllerGrid");
        private static readonly AccessTools.FieldRef<KBatchedAnimUpdater, bool[,]> VisibleGrid =
            AccessTools.FieldRefAccess<KBatchedAnimUpdater, bool[,]>("visibleChunkGrid");
        private static readonly AccessTools.FieldRef<KBatchedAnimUpdater, bool[,]> PrevVisibleGrid =
            AccessTools.FieldRefAccess<KBatchedAnimUpdater, bool[,]>("previouslyVisibleChunkGrid");
        private static readonly AccessTools.FieldRef<KBatchedAnimUpdater, List<Vector2I>> VisibleChunks =
            AccessTools.FieldRefAccess<KBatchedAnimUpdater, List<Vector2I>>("visibleChunks");
        private static readonly AccessTools.FieldRef<KBatchedAnimUpdater, List<Vector2I>> PrevVisibleChunks =
            AccessTools.FieldRefAccess<KBatchedAnimUpdater, List<Vector2I>>("previouslyVisibleChunks");
        internal static readonly AccessTools.FieldRef<KBatchedAnimUpdater, Vector2I> VisMin =
            AccessTools.FieldRefAccess<KBatchedAnimUpdater, Vector2I>("vis_chunk_min");
        internal static readonly AccessTools.FieldRef<KBatchedAnimUpdater, Vector2I> VisMax =
            AccessTools.FieldRefAccess<KBatchedAnimUpdater, Vector2I>("vis_chunk_max");

        private static bool Prefix(KBatchedAnimUpdater __instance)
        {
            if (!MultiViewRegistry.Active)
                return true; // original

            var grid = ControllerGrid(__instance);
            if (grid == null || Camera.main == null)
                return false; // original's DoGridProcessing() == false

            int gw = grid.GetLength(0), gh = grid.GetLength(1);

            // Main view, exactly as the original computes it.
            Grid.GetVisibleCellRangeInActiveWorld(out Vector2I min, out Vector2I max);
            Vector2I vmin = new Vector2I(min.x / Chunk, min.y / Chunk);
            Vector2I vmax = new Vector2I(Math.Min(max.x / Chunk, gw - 1), Math.Min(max.y / Chunk, gh - 1));
            VisMin(__instance) = vmin;
            VisMax(__instance) = vmax;

            // Swap current/previous buffers like the original.
            bool[,] tmpGrid = PrevVisibleGrid(__instance);
            PrevVisibleGrid(__instance) = VisibleGrid(__instance);
            VisibleGrid(__instance) = tmpGrid;
            Array.Clear(tmpGrid, 0, tmpGrid.Length);
            List<Vector2I> tmpList = PrevVisibleChunks(__instance);
            PrevVisibleChunks(__instance) = VisibleChunks(__instance);
            VisibleChunks(__instance) = tmpList;
            tmpList.Clear();

            bool[,] vis = VisibleGrid(__instance), prev = PrevVisibleGrid(__instance);
            List<Vector2I> visList = VisibleChunks(__instance), prevList = PrevVisibleChunks(__instance);

            void Mark(int x0, int y0, int x1, int y1)
            {
                x0 = Math.Max(0, x0); y0 = Math.Max(0, y0);
                x1 = Math.Min(gw - 1, x1); y1 = Math.Min(gh - 1, y1);
                for (int y = y0; y <= y1; y++)
                    for (int x = x0; x <= x1; x++)
                    {
                        if (vis[x, y]) continue;
                        vis[x, y] = true;
                        visList.Add(new Vector2I(x, y));
                        if (prev[x, y]) continue;
                        foreach (var kv in grid[x, y])
                            if (kv.Value != null) kv.Value.SetVisiblity(true);
                    }
            }

            Mark(vmin.x, vmin.y, vmax.x, vmax.y);
            foreach (RectInt r in MultiViewRegistry.Regions)
            {
                Mark((r.xMin - Padding) / Chunk, (r.yMin - Padding) / Chunk,
                     (r.xMax + Padding) / Chunk, (r.yMax + Padding) / Chunk);
            }

            foreach (Vector2I c in prevList)
            {
                if (vis[c.x, c.y]) continue;
                foreach (var kv in grid[c.x, c.y])
                    if (kv.Value != null) kv.Value.SetVisiblity(false);
            }
            return false;
        }
    }

    /// <summary>KAnimBatchManager.UpdateActiveArea only takes one rectangle of chunks; widen it to the
    /// bounding box of main + extra regions. (Patched here rather than on the tiny
    /// KBatchedAnimUpdater.GetVisibleArea, which the JIT may inline past a Harmony detour.)
    /// Callers that pass 0..9999 (timelapse, front end) are unaffected.</summary>
    [HarmonyPatch(typeof(KAnimBatchManager), nameof(KAnimBatchManager.UpdateActiveArea))]
    internal static class KAnimBatchManager_UpdateActiveArea_Patch
    {
        private static void Prefix(ref Vector2I vis_chunk_min, ref Vector2I vis_chunk_max)
        {
            if (!MultiViewRegistry.Active) return;
            const int C = KAnimBatchManager.CHUNK_SIZE;
            foreach (RectInt r in MultiViewRegistry.Regions)
            {
                vis_chunk_min = new Vector2I(Math.Min(vis_chunk_min.x, Math.Max(0, r.xMin - 4) / C),
                                             Math.Min(vis_chunk_min.y, Math.Max(0, r.yMin - 4) / C));
                vis_chunk_max = new Vector2I(Math.Max(vis_chunk_max.x, (r.xMax + 4) / C),
                                             Math.Max(vis_chunk_max.y, (r.yMax + 4) / C));
            }
        }
    }

    [HarmonyPatch(typeof(KBatchedAnimUpdater), nameof(KBatchedAnimUpdater.LateUpdate))]
    internal static class KBatchedAnimUpdater_LateUpdate_Timer
    {
        private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();
        private static void Postfix(long __state) => Perf.Add("KAnimUpdater.LateUpdate", Stopwatch.GetTimestamp() - __state);
    }

    // ------------------------------------------------------------------ property textures

    /// <summary>PropertyTextures only refreshes CPU-side textures (fog of war, gas colour, dig amount,
    /// temperature, ...) around Camera.main. Refresh them for each extra region as well.</summary>
    [HarmonyPatch(typeof(PropertyTextures), "LateUpdate")]
    internal static class PropertyTextures_LateUpdate_Patch
    {
        private const int Padding = 16; // same as PropertyTextures.GetVisibleCellRange

        private static readonly Type PropType = AccessTools.Inner(typeof(PropertyTextures), "TextureProperties");
        private static readonly FieldInfo UpdatedExternally = AccessTools.Field(PropType, "updatedExternally");
        private static readonly FieldInfo AllProps = AccessTools.Field(typeof(PropertyTextures), "allTextureProperties");
        private static readonly MethodInfo UpdateProperty = AccessTools.Method(typeof(PropertyTextures), "UpdateProperty");
        private static readonly object[] Args = new object[5];

        private static void Prefix(out long __state) => __state = Stopwatch.GetTimestamp();

        private static void Postfix(PropertyTextures __instance, long __state)
        {
            if (MultiViewRegistry.Active && Grid.IsInitialized() && Game.Instance != null && !Game.Instance.IsLoading())
            {
                var props = (IList)AllProps.GetValue(__instance);
                foreach (RectInt r in MultiViewRegistry.Regions)
                {
                    int x0 = Mathf.Clamp(r.xMin - Padding, 0, Grid.WidthInCells - 1);
                    int y0 = Mathf.Clamp(r.yMin - Padding, 0, Grid.HeightInCells - 1);
                    int x1 = Mathf.Clamp(r.xMax + Padding, 0, Grid.WidthInCells - 1);
                    int y1 = Mathf.Clamp(r.yMax + Padding, 0, Grid.HeightInCells - 1);
                    for (int i = 0; i < props.Count; i++)
                    {
                        object p = props[i];
                        if ((bool)UpdatedExternally.GetValue(p)) continue;
                        Args[0] = p; Args[1] = x0; Args[2] = y0; Args[3] = x1; Args[4] = y1;
                        UpdateProperty.Invoke(__instance, Args);
                    }
                }
            }
            Perf.Add("PropertyTextures.LateUpdate", Stopwatch.GetTimestamp() - __state);
        }
    }
}
