using System.Collections.Generic;
using UnityEngine;

namespace UNanite.Samples.QuarryScene
{
    /// <summary>
    /// Switches the loaded scenes between virtual geometry and Unity's own renderers: converted renderers
    /// get their MeshRenderer (and their LODGroup with its coarser LODs) back, VG terrains let Unity draw
    /// the heightmap, trees and details again. The converter keeps every original renderer, so this is exact.
    /// </summary>
    public static class SampleVgToggle
    {
        public static bool Enabled { get; private set; } = true;

        public static void Set(bool virtualGeometry)
        {
            Enabled = virtualGeometry;
            var groups = new HashSet<LODGroup>();
            foreach (var vgr in Object.FindObjectsByType<VirtualGeometryRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                var mr = vgr.GetComponent<MeshRenderer>();
                if (mr == null)
                    continue; // nothing to fall back to
                vgr.enabled = virtualGeometry;
                mr.enabled = !virtualGeometry;
                var group = mr.GetComponentInParent<LODGroup>(true);
                if (group != null)
                    groups.Add(group);
            }
            foreach (var group in groups)
            {
                group.enabled = !virtualGeometry;
                var lods = group.GetLODs();
                for (int l = 1; l < lods.Length; ++l)
                    foreach (var r in lods[l].renderers)
                        if (r != null)
                            r.enabled = !virtualGeometry;
            }
            foreach (var t in Object.FindObjectsByType<VirtualGeometryTerrain>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                t.enabled = virtualGeometry;
            foreach (var t in Object.FindObjectsByType<VirtualGeometryTerrainTrees>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                t.enabled = virtualGeometry;
            foreach (var t in Object.FindObjectsByType<VirtualGeometryTerrainDetails>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                t.enabled = virtualGeometry;
        }
    }
}
