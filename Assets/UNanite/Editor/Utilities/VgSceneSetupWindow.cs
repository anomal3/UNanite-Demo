using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace UNanite.Editor
{
    /// <summary>
    /// M14 Scene Setup window (Tools > UNanite > Scene Setup): audit of the open scenes (VgSceneAudit) -
    /// what is virtual geometry, what can be converted and why the rest stays a regular renderer, the path
    /// each material takes - with batch convert / revert (undo) and Shader Graph variant generation.
    /// </summary>
    public sealed class VgSceneSetupWindow : EditorWindow
    {
        enum Filter { All, Convertible, VirtualGeometry, Skipped }
        enum Sort { Name, Status, Triangles, Kind }

        const float k_Row = 18f;
        List<VgAuditEntry> m_Entries = new List<VgAuditEntry>();
        List<VgAuditEntry> m_View = new List<VgAuditEntry>();
        readonly HashSet<VgAuditEntry> m_Selected = new HashSet<VgAuditEntry>();
        Filter m_Filter = Filter.All;
        Sort m_Sort = Sort.Triangles;
        bool m_Descending = true;
        string m_Search = "";
        int m_MinTriangles;
        bool m_SkipTransparent = true;
        Vector2 m_Scroll;
        string m_Message;
        bool m_Dirty = true;

        [MenuItem("Tools/UNanite/Scene Setup", priority = 5)]
        public static void Open() => GetWindow<VgSceneSetupWindow>("UNanite Scene Setup");

        void OnEnable()
        {
            EditorApplication.hierarchyChanged += MarkDirty;
            UnityEditor.SceneManagement.EditorSceneManager.sceneOpened += OnSceneOpened;
            m_MinTriangles = EditorPrefs.GetInt("UNanite.SceneSetup.MinTriangles", 0);
            m_SkipTransparent = EditorPrefs.GetBool("UNanite.SceneSetup.SkipTransparent", true);
        }

        void OnDisable()
        {
            EditorApplication.hierarchyChanged -= MarkDirty;
            UnityEditor.SceneManagement.EditorSceneManager.sceneOpened -= OnSceneOpened;
        }

        void OnSceneOpened(UnityEngine.SceneManagement.Scene s, UnityEditor.SceneManagement.OpenSceneMode m) => MarkDirty();

        void MarkDirty()
        {
            m_Dirty = true;
            Repaint();
        }

        public void Rescan()
        {
            m_Entries = VgSceneAudit.Scan(m_MinTriangles);
            m_Selected.RemoveWhere(e => !m_Entries.Contains(e));
            m_Dirty = false;
            ApplyView();
        }

        void ApplyView()
        {
            IEnumerable<VgAuditEntry> q = m_Entries;
            if (m_Filter != Filter.All)
            {
                var status = m_Filter == Filter.Convertible ? VgAuditStatus.Convertible : m_Filter == Filter.VirtualGeometry ? VgAuditStatus.VirtualGeometry : VgAuditStatus.Skipped;
                q = q.Where(e => e.status == status);
            }
            if (!string.IsNullOrEmpty(m_Search))
                q = q.Where(e => e.scenePath.IndexOf(m_Search, System.StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 (e.reason ?? "").IndexOf(m_Search, System.StringComparison.OrdinalIgnoreCase) >= 0);
            q = m_Sort switch
            {
                Sort.Name => m_Descending ? q.OrderByDescending(e => e.scenePath) : q.OrderBy(e => e.scenePath),
                Sort.Status => m_Descending ? q.OrderByDescending(e => e.status) : q.OrderBy(e => e.status),
                Sort.Kind => m_Descending ? q.OrderByDescending(e => e.kind) : q.OrderBy(e => e.kind),
                _ => m_Descending ? q.OrderByDescending(e => e.triangles) : q.OrderBy(e => e.triangles),
            };
            m_View = q.ToList();
        }

        void OnGUI()
        {
            if (m_Dirty)
                Rescan();

            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Rescan", EditorStyles.toolbarButton, GUILayout.Width(60)))
                    Rescan();
                EditorGUI.BeginChangeCheck();
                m_Filter = (Filter)EditorGUILayout.EnumPopup(m_Filter, EditorStyles.toolbarPopup, GUILayout.Width(110));
                m_Search = EditorGUILayout.TextField(m_Search, EditorStyles.toolbarSearchField, GUILayout.MinWidth(120));
                if (EditorGUI.EndChangeCheck())
                    ApplyView();
                GUILayout.Label("Min triangles", GUILayout.Width(80));
                EditorGUI.BeginChangeCheck();
                m_MinTriangles = Mathf.Max(0, EditorGUILayout.DelayedIntField(m_MinTriangles, EditorStyles.toolbarTextField, GUILayout.Width(60)));
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetInt("UNanite.SceneSetup.MinTriangles", m_MinTriangles);
                    Rescan();
                }
            }

            DrawSummary();
            DrawHeader();
            DrawRows();
            DrawActions();
        }

        void DrawSummary()
        {
            int vg = 0, conv = 0, skip = 0;
            long vgTris = 0, convTris = 0, allTris = 0;
            foreach (var e in m_Entries)
            {
                allTris += e.triangles;
                if (e.status == VgAuditStatus.VirtualGeometry) { vg++; vgTris += e.triangles; }
                else if (e.status == VgAuditStatus.Convertible) { conv++; convTris += e.triangles; }
                else skip++;
            }
            EditorGUILayout.LabelField(
                $"{m_Entries.Count} objects: {vg} virtual geometry ({Tris(vgTris)} triangles), {conv} convertible ({Tris(convTris)}), {skip} skipped. " +
                $"Source triangles in the scenes: {Tris(allTris)}.", EditorStyles.wordWrappedMiniLabel);
            if (!string.IsNullOrEmpty(m_Message))
                EditorGUILayout.HelpBox(m_Message, MessageType.Info);
        }

        static string Tris(long n) => n >= 1_000_000 ? $"{n / 1e6:F1} M" : n >= 1000 ? $"{n / 1e3:F0} K" : n.ToString();

        static readonly float[] s_Widths = { 18f, 0f, 90f, 105f, 70f, 70f, 0f };

        void ColumnRects(Rect r, out Rect sel, out Rect name, out Rect kind, out Rect status, out Rect tris, out Rect shadow, out Rect info)
        {
            float fixedW = s_Widths[0] + s_Widths[2] + s_Widths[3] + s_Widths[4] + s_Widths[5];
            float flex = Mathf.Max(100f, r.width - fixedW);
            float x = r.x;
            sel = new Rect(x, r.y, s_Widths[0], r.height); x += s_Widths[0];
            name = new Rect(x, r.y, flex * 0.45f, r.height); x += flex * 0.45f;
            kind = new Rect(x, r.y, s_Widths[2], r.height); x += s_Widths[2];
            status = new Rect(x, r.y, s_Widths[3], r.height); x += s_Widths[3];
            tris = new Rect(x, r.y, s_Widths[4], r.height); x += s_Widths[4];
            shadow = new Rect(x, r.y, s_Widths[5], r.height); x += s_Widths[5];
            info = new Rect(x, r.y, flex * 0.55f, r.height);
        }

        void DrawHeader()
        {
            var r = GUILayoutUtility.GetRect(position.width, k_Row);
            EditorGUI.DrawRect(r, new Color(0f, 0f, 0f, 0.15f));
            ColumnRects(r, out var sel, out var name, out var kind, out var status, out var tris, out var shadow, out var info);
            bool all = m_View.Count > 0 && m_View.All(m_Selected.Contains);
            bool now = EditorGUI.Toggle(sel, all);
            if (now != all)
            {
                if (now) m_Selected.UnionWith(m_View);
                else m_Selected.ExceptWith(m_View);
            }
            SortButton(name, "Object", Sort.Name);
            SortButton(kind, "Kind", Sort.Kind);
            SortButton(status, "Status", Sort.Status);
            SortButton(tris, "Triangles", Sort.Triangles);
            GUI.Label(shadow, new GUIContent("Shadow", "Every opaque material can use the vertex-pulled shadow raster (fastest shadows)."), EditorStyles.miniBoldLabel);
            GUI.Label(info, "Materials / notes", EditorStyles.miniBoldLabel);
        }

        void SortButton(Rect r, string label, Sort sort)
        {
            string arrow = m_Sort == sort ? (m_Descending ? " ▼" : " ▲") : "";
            if (GUI.Button(r, label + arrow, EditorStyles.miniBoldLabel))
            {
                m_Descending = m_Sort == sort ? !m_Descending : sort == Sort.Triangles;
                m_Sort = sort;
                ApplyView();
            }
        }

        void DrawRows()
        {
            var area = GUILayoutUtility.GetRect(position.width, 100f, GUILayout.ExpandHeight(true));
            var content = new Rect(0, 0, area.width - 16f, m_View.Count * k_Row);
            m_Scroll = GUI.BeginScrollView(area, m_Scroll, content);
            int first = Mathf.Max(0, (int)(m_Scroll.y / k_Row));
            int last = Mathf.Min(m_View.Count, first + (int)(area.height / k_Row) + 2);
            for (int i = first; i < last; ++i)
            {
                var e = m_View[i];
                var r = new Rect(0, i * k_Row, content.width, k_Row);
                if (i % 2 == 1)
                    EditorGUI.DrawRect(r, new Color(0.5f, 0.5f, 0.5f, 0.06f));
                ColumnRects(r, out var sel, out var name, out var kind, out var status, out var tris, out var shadow, out var info);
                bool on = m_Selected.Contains(e);
                if (EditorGUI.Toggle(sel, on) != on)
                {
                    if (on) m_Selected.Remove(e);
                    else m_Selected.Add(e);
                }
                if (GUI.Button(name, new GUIContent(e.name, e.scenePath), EditorStyles.label) && e.target != null)
                {
                    var go = e.target is Component c ? c.gameObject : null;
                    Selection.activeObject = go;
                    EditorGUIUtility.PingObject(go);
                }
                GUI.Label(kind, e.kind, EditorStyles.miniLabel);
                var prev = GUI.contentColor;
                GUI.contentColor = e.status == VgAuditStatus.VirtualGeometry ? new Color(0.4f, 0.85f, 0.4f) :
                                   e.status == VgAuditStatus.Convertible ? new Color(0.95f, 0.8f, 0.3f) : new Color(0.7f, 0.7f, 0.7f);
                GUI.Label(status, e.status == VgAuditStatus.VirtualGeometry ? "virtual geometry" : e.status == VgAuditStatus.Convertible ? "convertible" : "skipped", EditorStyles.miniLabel);
                GUI.contentColor = prev;
                GUI.Label(tris, Tris(e.triangles), EditorStyles.miniLabel);
                GUI.Label(shadow, e.kind == "Mesh" ? (e.shadowRaster ? "raster" : "expansion") : "", EditorStyles.miniLabel);
                GUI.Label(info, new GUIContent(InfoText(e), InfoTooltip(e)), EditorStyles.miniLabel);
            }
            GUI.EndScrollView();
        }

        static string InfoText(VgAuditEntry e)
        {
            string mats = e.paths.Length == 0 ? "" : string.Join(", ", e.paths.GroupBy(p => p).Select(g => g.Count() > 1 ? $"{VgSceneAudit.Describe(g.Key)} x{g.Count()}" : VgSceneAudit.Describe(g.Key)));
            return string.IsNullOrEmpty(e.reason) ? mats : string.IsNullOrEmpty(mats) ? e.reason : $"{e.reason} | {mats}";
        }

        static string InfoTooltip(VgAuditEntry e)
        {
            var lines = new List<string>();
            for (int i = 0; i < e.materials.Length; ++i)
            {
                var m = e.materials[i];
                lines.Add($"{(m != null ? m.name + " (" + m.shader.name + ")" : "<none>")}: {VgSceneAudit.Describe(i < e.paths.Length ? e.paths[i] : VgMaterialPath.None)}");
            }
            if (!string.IsNullOrEmpty(e.reason))
                lines.Add(e.reason);
            return string.Join("\n", lines);
        }

        void DrawActions()
        {
            var selected = m_Selected.ToList();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUI.enabled = selected.Any(e => e.status == VgAuditStatus.Convertible);
                if (GUILayout.Button($"Convert selected ({selected.Count(e => e.status == VgAuditStatus.Convertible)})"))
                    Convert(selected.Where(e => e.status == VgAuditStatus.Convertible));
                GUI.enabled = m_View.Any(e => e.status == VgAuditStatus.Convertible);
                if (GUILayout.Button($"Convert all shown ({m_View.Count(e => e.status == VgAuditStatus.Convertible)})"))
                    Convert(m_View.Where(e => e.status == VgAuditStatus.Convertible));
                GUI.enabled = selected.Any(e => e.status == VgAuditStatus.VirtualGeometry);
                if (GUILayout.Button($"Revert selected ({selected.Count(e => e.status == VgAuditStatus.VirtualGeometry)})"))
                    Revert(selected.Where(e => e.status == VgAuditStatus.VirtualGeometry));
                GUI.enabled = true;
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                m_SkipTransparent = GUILayout.Toggle(m_SkipTransparent, new GUIContent("Skip transparent-only", "Renderers whose materials are all transparent stay regular renderers."));
                if (EditorGUI.EndChangeCheck())
                    EditorPrefs.SetBool("UNanite.SceneSetup.SkipTransparent", m_SkipTransparent);
                GUILayout.FlexibleSpace();
                var missing = m_Entries.SelectMany(e => e.materials.Zip(e.paths, (m, p) => (m, p))).Where(x => x.p == VgMaterialPath.GraphVariantMissing && x.m != null)
                                       .Select(x => x.m.shader).Distinct().ToList();
                GUI.enabled = missing.Count > 0;
                if (GUILayout.Button($"Generate Shader Graph variants ({missing.Count})"))
                {
                    int ok = 0;
                    foreach (var shader in missing)
                        if (VgShaderVariantGenerator.GetOrCreate(shader, out _) != null)
                            ok++;
                    m_Message = $"Generated {ok} of {missing.Count} Shader Graph variants.";
                    Rescan();
                }
                GUI.enabled = selected.Count > 0;
                if (GUILayout.Button("Select in Hierarchy"))
                    Selection.objects = selected.Select(e => e.target is Component c ? c.gameObject : null).Where(g => g != null).Distinct().ToArray();
                GUI.enabled = true;
            }
        }

        void Convert(IEnumerable<VgAuditEntry> entries)
        {
            var list = entries.ToList();
            var meshes = list.Where(e => e.kind == "Mesh").Select(e => e.target as MeshRenderer).Where(r => r != null).ToList();
            var skipped = new List<string>();
            int converted = meshes.Count > 0 ? VirtualGeometryConverter.ConvertRenderers(meshes, out skipped, m_SkipTransparent) : 0;
            if (skipped.Count > 0)
                Debug.Log("UNanite Scene Setup: skipped " + string.Join(", ", skipped.Distinct()));
            var problems = new List<string>();
            int terrains = 0;
            foreach (var e in list.Where(e => e.IsTerrain))
            {
                var t = (Terrain)e.target;
                if (e.kind == "Terrain" && VirtualGeometryTerrainConverter.Convert(t) != null) terrains++;
                else if (e.kind == "Terrain trees" && VirtualGeometryTerrainConverter.ConvertTrees(t, problems) != null) terrains++;
                else if (e.kind == "Terrain details" && VirtualGeometryTerrainConverter.ConvertDetails(t, problems) != null) terrains++;
            }
            m_Message = $"Converted {converted} renderer(s)" + (terrains > 0 ? $" and {terrains} terrain part(s)" : "") + "." +
                        (meshes.Count > converted ? $" {meshes.Count - converted} skipped (see the console)." : "") +
                        (problems.Count > 0 ? " " + string.Join(" ", problems.Take(3)) : "");
            Rescan();
        }

        void Revert(IEnumerable<VgAuditEntry> entries)
        {
            var list = entries.ToList();
            var vgs = list.Where(e => e.kind == "Mesh").Select(e => (e.target as Component)?.GetComponent<VirtualGeometryRenderer>()).Where(v => v != null).ToList();
            int reverted = VirtualGeometryConverter.RevertRenderers(vgs);
            int terrains = 0;
            foreach (var t in list.Where(e => e.IsTerrain).Select(e => (Terrain)e.target).Distinct())
            {
                VirtualGeometryTerrainConverter.Revert(t);
                terrains++;
            }
            m_Message = $"Reverted {reverted} renderer(s)" + (terrains > 0 ? $" and {terrains} terrain(s)" : "") + ".";
            Rescan();
        }
    }
}
