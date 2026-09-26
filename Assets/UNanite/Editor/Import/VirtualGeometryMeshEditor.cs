using UnityEditor;
using UnityEngine;

namespace UNanite.Editor
{
    [CustomEditor(typeof(VirtualGeometryMesh))]
    sealed class VirtualGeometryMeshEditor : UnityEditor.Editor
    {
        bool m_ShowLevels = true;

        public override void OnInspectorGUI()
        {
            var vg = (VirtualGeometryMesh)target;
            if (!vg.IsValid)
            {
                EditorGUILayout.HelpBox("No virtual geometry data (build failed?). Check the console.", MessageType.Error);
                return;
            }

            var r = vg.Report;
            EditorGUILayout.LabelField("Source", $"{r.sourceTriangles:N0} triangles, {r.sourceVertices:N0} vertices, {vg.MaterialCount} material slot(s)");
            EditorGUILayout.LabelField("DAG", $"{r.levelCount} levels, {r.clusterCount:N0} clusters, {r.groupCount:N0} groups, {r.nodeCount:N0} nodes");
            EditorGUILayout.LabelField("All levels", $"{r.totalTriangles:N0} triangles ({(float)r.totalTriangles / Mathf.Max(1, r.sourceTriangles):F2}x), {r.totalVertices:N0} vertices");
            EditorGUILayout.LabelField("Size", $"{r.blobBytes / 1048576.0:F2} MB in {r.pageCount} pages — {r.BytesPerSourceTriangle:F1} B/source tri, {r.BytesPerStoredTriangle:F1} B/stored tri");
            EditorGUILayout.LabelField("Build time", $"{r.msTotal:F0} ms (prepare {r.msPrepare:F0}, DAG {r.msDag:F0}, encode {r.msEncode:F0}) on {r.threadsUsed} threads");
            if (r.stuckGroups > 0)
                EditorGUILayout.HelpBox($"{r.stuckGroups} group(s) could not be simplified further and stop the DAG early (common for disconnected or heavily seamed geometry).", MessageType.Info);

            var reader = vg.Reader;
            m_ShowLevels = EditorGUILayout.Foldout(m_ShowLevels, "Levels", true);
            if (m_ShowLevels && reader != null)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    for (int l = 0; l < reader.Levels.Length; ++l)
                    {
                        var lvl = reader.Levels[l];
                        EditorGUILayout.LabelField($"LOD {l}", $"{lvl.triangles,10:N0} tris  {lvl.clusters,7:N0} clusters  {lvl.groups,5:N0} groups");
                    }
                }
            }
        }
    }
}
