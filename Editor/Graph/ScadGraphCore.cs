using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SCADPlugin.Editor.Graph
{
    public enum ScadPortType
    {
        Number,
        Vector2,
        Vector3,
        Boolean,
        String,
        Color,
        Solid,
        Shape,
        Any,
    }

    [Serializable]
    public class ScadPort
    {
        public string id;
        public string label;
        public ScadPortType type;
        public bool isInput;
        public string defaultLiteral;

        public static ScadPort In(string id, string label, ScadPortType type, string defaultLiteral = null) =>
            new ScadPort { id = id, label = label, type = type, isInput = true, defaultLiteral = defaultLiteral ?? ScadLiteral.DefaultFor(type) };

        public static ScadPort Out(string id, string label, ScadPortType type) =>
            new ScadPort { id = id, label = label, type = type, isInput = false };
    }

    [Serializable]
    public class ScadConnection
    {
        public string fromNodeId;
        public string fromPortId;
        public string toNodeId;
        public string toPortId;
    }

    [Serializable]
    public class ScadExposedParameter
    {
        public string id;
        public string label;
        public ScadPortType type;
        public string defaultLiteral;
        public bool hasRange;
        public double min;
        public double max;
    }

    [AttributeUsage(AttributeTargets.Class)]
    public sealed class ScadNodeAttribute : Attribute
    {
        public string DisplayName { get; }
        public string Category { get; }
        public string Tooltip { get; set; }
        public bool Hidden { get; set; }

        public ScadNodeAttribute(string displayName, string category)
        {
            DisplayName = displayName;
            Category = category;
        }
    }

    // Centralised formatting/parsing of SCAD literal strings used as port
    // defaults. Keeping this one place means node code never has to worry
    // about culture-sensitive float formatting.
    public static class ScadLiteral
    {
        static readonly CultureInfo Ci = CultureInfo.InvariantCulture;

        public static string DefaultFor(ScadPortType type) => type switch
        {
            ScadPortType.Number => "0",
            ScadPortType.Vector2 => "[0, 0]",
            ScadPortType.Vector3 => "[0, 0, 0]",
            ScadPortType.Boolean => "false",
            ScadPortType.String => "\"\"",
            ScadPortType.Color => "[1, 1, 1, 1]",
            _ => "undef",
        };

        public static string Number(double v) => v.ToString("R", Ci);
        public static string Number(float v) => v.ToString("R", Ci);
        public static string Number(int v) => v.ToString(Ci);

        public static string Vector2(Vector2 v) =>
            $"[{Number(v.x)}, {Number(v.y)}]";
        public static string Vector3(Vector3 v) =>
            $"[{Number(v.x)}, {Number(v.y)}, {Number(v.z)}]";
        public static string Color(Color c) => Mathf.Approximately(c.a, 1f)
            ? $"[{Number(c.r)}, {Number(c.g)}, {Number(c.b)}]"
            : $"[{Number(c.r)}, {Number(c.g)}, {Number(c.b)}, {Number(c.a)}]";
        public static string Bool(bool v) => v ? "true" : "false";
        public static string String(string s) => "\"" + (s ?? string.Empty).Replace("\"", "\\\"") + "\"";

        public static bool TryParseNumber(string s, out double v)
        {
            v = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            return double.TryParse(s.Trim(), NumberStyles.Float, Ci, out v);
        }

        public static bool TryParseVector3(string s, out Vector3 v)
        {
            v = UnityEngine.Vector3.zero;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var t = s.Trim();
            if (t.Length < 2 || t[0] != '[' || t[t.Length - 1] != ']') return false;
            var parts = t.Substring(1, t.Length - 2).Split(',');
            if (parts.Length < 3) return false;
            if (!TryParseNumber(parts[0], out var x)) return false;
            if (!TryParseNumber(parts[1], out var y)) return false;
            if (!TryParseNumber(parts[2], out var z)) return false;
            v = new Vector3((float)x, (float)y, (float)z);
            return true;
        }

        public static bool TryParseVector2(string s, out Vector2 v)
        {
            v = UnityEngine.Vector2.zero;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var t = s.Trim();
            if (t.Length < 2 || t[0] != '[' || t[t.Length - 1] != ']') return false;
            var parts = t.Substring(1, t.Length - 2).Split(',');
            if (parts.Length < 2) return false;
            if (!TryParseNumber(parts[0], out var x)) return false;
            if (!TryParseNumber(parts[1], out var y)) return false;
            v = new Vector2((float)x, (float)y);
            return true;
        }

        public static bool TryParseBool(string s, out bool v)
        {
            v = false;
            if (string.IsNullOrWhiteSpace(s)) return false;
            var t = s.Trim().ToLowerInvariant();
            if (t == "true") { v = true; return true; }
            if (t == "false") { v = false; return true; }
            return false;
        }

        // Whether an output of `from` can drive an input of `to`. Allows Any
        // on either side and broadcasts Number into Vector2/Vector3 when the
        // target port opts in via allowBroadcast.
        public static bool Compatible(ScadPortType from, ScadPortType to, bool allowBroadcast = true)
        {
            if (from == ScadPortType.Any || to == ScadPortType.Any) return true;
            if (from == to) return true;
            if (allowBroadcast && from == ScadPortType.Number &&
                (to == ScadPortType.Vector2 || to == ScadPortType.Vector3)) return true;
            return false;
        }
    }

    // Small helper collection used by node implementations.
    internal static class ScadCollections
    {
        public static IEnumerable<T> WhereNotNull<T>(this IEnumerable<T> src) where T : class
        {
            foreach (var x in src) if (x != null) yield return x;
        }
    }
}
