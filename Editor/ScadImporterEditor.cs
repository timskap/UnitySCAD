using System.Globalization;
using UnityEditor;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace SCADPlugin.Editor
{
    [CustomEditor(typeof(ScadImporter))]
    public class ScadImporterEditor : ScriptedImporterEditor
    {
        public override void OnInspectorGUI()
        {
            var importer = (ScadImporter)target;
            serializedObject.Update();

            EditorGUILayout.HelpBox(
                "Clicking Apply runs OpenSCAD synchronously. Unity will freeze for a few seconds to a few minutes on complex models. Enable \"Skip Compile\" to edit parameters without triggering a rebuild.",
                MessageType.None);

            EditorGUILayout.LabelField("Mesh Import", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(serializedObject.FindProperty("scale"));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("weldVertices"),
                new GUIContent("Weld Vertices", "Merge coincident vertices (STL exports every triangle with its own 3 verts). Disable for faithful flat shading at the cost of 2–3× more vertices."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("recalculateNormals"),
                new GUIContent("Smooth Normals", "Recompute per-vertex normals after welding. Gives smooth shading on curves; disable to keep flat shading from the STL."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("perColorSubmeshes"),
                new GUIContent("Per-Color Submeshes",
                    "Run OpenSCAD once per color() used in the file, then assemble a multi-material mesh. Only works for files where color() wrappers don't mix with CSG — e.g. separate top-level colored parts. Nested color() calls and RGB-array colors are dropped. Turn off to get a single mesh with one tinted material."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("maxParallelCompiles"),
                new GUIContent("Max Parallel Compiles",
                    "Number of OpenSCAD processes to run concurrently when Per-Color Submeshes is on. 0 = auto (logical core count). Lower this if heavy models push your machine into swap — each process holds the model in RAM."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("skipCompile"),
                new GUIContent("Skip Compile", "Do not invoke OpenSCAD on import. Useful while tweaking parameters on heavy models."));
            EditorGUILayout.PropertyField(serializedObject.FindProperty("compileTimeoutSeconds"),
                new GUIContent("Compile Timeout (s)"));

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Parameters", EditorStyles.boldLabel);

            var paramsProp = serializedObject.FindProperty("parameters");
            if (paramsProp.arraySize == 0)
            {
                EditorGUILayout.HelpBox(
                    "No parameters detected. Declare top-level variables in your .scad file — e.g. `width = 20; // [5:100]`.",
                    MessageType.Info);
            }

            string currentGroup = null;
            for (int i = 0; i < paramsProp.arraySize; i++)
            {
                var prop = paramsProp.GetArrayElementAtIndex(i);
                var param = importer.parameters[i];
                if (param == null) continue;

                if (!Equals(param.group, currentGroup))
                {
                    currentGroup = param.group;
                    if (!string.IsNullOrEmpty(currentGroup))
                    {
                        EditorGUILayout.Space(6);
                        EditorGUILayout.LabelField(currentGroup, EditorStyles.boldLabel);
                    }
                }
                DrawParameter(prop, param);
            }

            serializedObject.ApplyModifiedProperties();

            EditorGUILayout.Space();
            ApplyRevertGUI();
        }

        internal static void DrawParameter(SerializedProperty prop, ScadParameter param)
        {
            var valueProp = prop.FindPropertyRelative("value");
            var label = new GUIContent(
                ObjectNames.NicifyVariableName(param.name),
                string.IsNullOrEmpty(param.description) ? param.name : param.description);
            var ci = CultureInfo.InvariantCulture;

            switch (param.type)
            {
                case ScadParameterType.Boolean:
                {
                    bool cur = valueProp.stringValue == "true";
                    bool next = EditorGUILayout.Toggle(label, cur);
                    if (next != cur) valueProp.stringValue = next ? "true" : "false";
                    break;
                }

                case ScadParameterType.Integer:
                {
                    int.TryParse(valueProp.stringValue, NumberStyles.Integer, ci, out var cur);
                    int next = param.hasRange
                        ? EditorGUILayout.IntSlider(label, cur, (int)param.min, (int)param.max)
                        : EditorGUILayout.IntField(label, cur);
                    if (next != cur) valueProp.stringValue = next.ToString(ci);
                    break;
                }

                case ScadParameterType.Number:
                {
                    double.TryParse(valueProp.stringValue, NumberStyles.Float, ci, out var curD);
                    float cur = (float)curD;
                    float next = param.hasRange
                        ? EditorGUILayout.Slider(label, cur, (float)param.min, (float)param.max)
                        : EditorGUILayout.FloatField(label, cur);
                    if (param.hasRange && param.step > 0.0)
                    {
                        var step = (float)param.step;
                        next = Mathf.Round(next / step) * step;
                    }
                    if (!Mathf.Approximately(next, cur))
                        valueProp.stringValue = next.ToString("R", ci);
                    break;
                }

                case ScadParameterType.String:
                {
                    var raw = valueProp.stringValue ?? string.Empty;
                    var shown = raw;
                    if (shown.Length >= 2 && shown[0] == '"' && shown[shown.Length - 1] == '"')
                        shown = shown.Substring(1, shown.Length - 2);

                    if (IsHexColor(shown))
                    {
                        var cur = HexToColor(shown);
                        var nextColor = EditorGUILayout.ColorField(label, cur);
                        if (nextColor != cur)
                            valueProp.stringValue = "\"" + ColorToHex(nextColor) + "\"";
                    }
                    else
                    {
                        var next = EditorGUILayout.TextField(label, shown);
                        if (next != shown) valueProp.stringValue = "\"" + next + "\"";
                    }
                    break;
                }

                case ScadParameterType.ColorVector:
                {
                    var raw = valueProp.stringValue ?? string.Empty;
                    var cur = VectorToColor(raw);
                    var next = EditorGUILayout.ColorField(label, cur);
                    if (next != cur) valueProp.stringValue = ColorToVector(next);
                    break;
                }

                case ScadParameterType.NumberDropdown:
                case ScadParameterType.StringDropdown:
                {
                    int count = param.choices?.Count ?? 0;
                    if (count == 0) break;
                    var labels = new string[count];
                    var values = new string[count];
                    int cur = 0;
                    for (int i = 0; i < count; i++)
                    {
                        labels[i] = param.choices[i].label;
                        values[i] = param.choices[i].value;
                        if (values[i] == valueProp.stringValue) cur = i;
                    }
                    int next = EditorGUILayout.Popup(label, cur, labels);
                    if (next != cur) valueProp.stringValue = values[next];
                    break;
                }
            }
        }

        static Color VectorToColor(string literal)
        {
            if (string.IsNullOrEmpty(literal)) return Color.white;
            var t = literal.Trim();
            if (t.Length < 2 || t[0] != '[' || t[t.Length - 1] != ']') return Color.white;
            var parts = t.Substring(1, t.Length - 2).Split(',');
            var ci = CultureInfo.InvariantCulture;
            var ns = NumberStyles.Float;
            float r = 1, g = 1, b = 1, a = 1;
            if (parts.Length >= 1) float.TryParse(parts[0].Trim(), ns, ci, out r);
            if (parts.Length >= 2) float.TryParse(parts[1].Trim(), ns, ci, out g);
            if (parts.Length >= 3) float.TryParse(parts[2].Trim(), ns, ci, out b);
            if (parts.Length >= 4) float.TryParse(parts[3].Trim(), ns, ci, out a);
            return new Color(r, g, b, a);
        }

        static string ColorToVector(Color c)
        {
            var ci = CultureInfo.InvariantCulture;
            return Mathf.Approximately(c.a, 1f)
                ? $"[{c.r.ToString("F3", ci)}, {c.g.ToString("F3", ci)}, {c.b.ToString("F3", ci)}]"
                : $"[{c.r.ToString("F3", ci)}, {c.g.ToString("F3", ci)}, {c.b.ToString("F3", ci)}, {c.a.ToString("F3", ci)}]";
        }

        internal static bool IsHexColor(string s)
        {
            if (string.IsNullOrEmpty(s) || s[0] != '#') return false;
            int len = s.Length - 1;
            if (len != 3 && len != 4 && len != 6 && len != 8) return false;
            return ColorUtility.TryParseHtmlString(s, out _);
        }

        internal static Color HexToColor(string hex)
        {
            return ColorUtility.TryParseHtmlString(hex, out var c) ? c : Color.white;
        }

        internal static string ColorToHex(Color c)
        {
            return Mathf.Approximately(c.a, 1f)
                ? "#" + ColorUtility.ToHtmlStringRGB(c)
                : "#" + ColorUtility.ToHtmlStringRGBA(c);
        }
    }
}
