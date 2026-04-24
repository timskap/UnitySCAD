using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace SCADPlugin.Editor
{
    public static class ScadParameterParser
    {
        static readonly Regex GroupRegex = new Regex(@"^\s*/\*\s*\[(.+?)\]\s*\*/", RegexOptions.Compiled);
        static readonly Regex DescRegex = new Regex(@"^\s*//\s*(.+)$", RegexOptions.Compiled);
        static readonly Regex AssignRegex = new Regex(
            @"^\s*([A-Za-z_\$][A-Za-z0-9_]*)\s*=\s*(.+?)\s*;\s*(//\s*(.*))?\s*$",
            RegexOptions.Compiled);
        static readonly Regex ModuleRegex = new Regex(@"^\s*(module|function)\b", RegexOptions.Compiled);

        public static List<ScadParameter> Parse(string source)
        {
            var list = new List<ScadParameter>();
            string currentGroup = null;
            string pendingDesc = null;

            foreach (var rawLine in source.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');

                if (ModuleRegex.IsMatch(line)) break;

                var g = GroupRegex.Match(line);
                if (g.Success)
                {
                    currentGroup = g.Groups[1].Value.Trim();
                    pendingDesc = null;
                    continue;
                }

                var a = AssignRegex.Match(line);
                if (a.Success)
                {
                    var param = new ScadParameter
                    {
                        name = a.Groups[1].Value,
                        defaultValue = a.Groups[2].Value.Trim(),
                        group = currentGroup,
                        description = pendingDesc,
                    };
                    param.value = param.defaultValue;
                    pendingDesc = null;

                    // Skip assignments whose right-hand side is a non-literal
                    // (calculated globals like `bay_width = section_width / n;`)
                    if (!InferType(param)) continue;

                    var decorator = a.Groups[4].Success ? a.Groups[4].Value.Trim() : null;
                    if (!string.IsNullOrEmpty(decorator)) ApplyDecorator(param, decorator);
                    list.Add(param);
                    continue;
                }

                var d = DescRegex.Match(line);
                if (d.Success) { pendingDesc = d.Groups[1].Value.Trim(); continue; }

                if (string.IsNullOrWhiteSpace(line)) pendingDesc = null;
            }
            return list;
        }

        static bool InferType(ScadParameter p)
        {
            var v = p.defaultValue;
            if (v == "true" || v == "false") { p.type = ScadParameterType.Boolean; return true; }
            if (v.Length >= 2 && v[0] == '"' && v[v.Length - 1] == '"') { p.type = ScadParameterType.String; return true; }
            if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            {
                p.type = (v.Contains(".") || v.Contains("e") || v.Contains("E"))
                    ? ScadParameterType.Number
                    : ScadParameterType.Integer;
                return true;
            }
            if (IsColorVectorLiteral(v))
            {
                p.type = ScadParameterType.ColorVector;
                return true;
            }
            return false;
        }

        // Matches [n, n, n] or [n, n, n, n] where n is a numeric literal — the
        // shape used by SCAD's color(c) calls (RGB or RGBA components, 0–1 range).
        static bool IsColorVectorLiteral(string v)
        {
            var t = v.Trim();
            if (t.Length < 2 || t[0] != '[' || t[t.Length - 1] != ']') return false;
            var inside = t.Substring(1, t.Length - 2);
            var parts = inside.Split(',');
            if (parts.Length != 3 && parts.Length != 4) return false;
            foreach (var part in parts)
            {
                if (!double.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    return false;
            }
            return true;
        }

        static void ApplyDecorator(ScadParameter p, string decorator)
        {
            var d = decorator.Trim();
            if (d.Length < 2 || d[0] != '[' || d[d.Length - 1] != ']')
            {
                // Same-line comment that isn't a Customizer decorator — use as description
                if (string.IsNullOrEmpty(p.description)) p.description = d;
                return;
            }
            var inside = d.Substring(1, d.Length - 2).Trim();
            if (inside.Length == 0) return;

            if (!ContainsOutsideQuotes(inside, ','))
            {
                var parts = inside.Split(':');
                if (parts.Length == 2 &&
                    TryParseDouble(parts[0], out var mn) &&
                    TryParseDouble(parts[1], out var mx))
                {
                    p.hasRange = true;
                    p.min = mn;
                    p.max = mx;
                    p.step = (p.type == ScadParameterType.Integer) ? 1.0 : 0.0;
                    return;
                }
                if (parts.Length == 3 &&
                    TryParseDouble(parts[0], out var a) &&
                    TryParseDouble(parts[1], out var s) &&
                    TryParseDouble(parts[2], out var b))
                {
                    p.hasRange = true;
                    p.min = a;
                    p.step = s;
                    p.max = b;
                    return;
                }
            }

            var items = SplitTopLevelCommas(inside);
            if (items.Count == 0) return;

            p.choices.Clear();
            bool allNumeric = true;
            foreach (var raw in items)
            {
                var item = raw.Trim();
                string rawValue;
                string label;
                int colonIdx = FindLabelColon(item);
                if (colonIdx >= 0)
                {
                    rawValue = item.Substring(0, colonIdx).Trim();
                    label = item.Substring(colonIdx + 1).Trim();
                    if (label.Length >= 2 && label[0] == '"' && label[label.Length - 1] == '"')
                        label = label.Substring(1, label.Length - 2);
                }
                else
                {
                    rawValue = item;
                    label = item;
                    if (label.Length >= 2 && label[0] == '"' && label[label.Length - 1] == '"')
                        label = label.Substring(1, label.Length - 2);
                }

                if (rawValue.Length >= 2 && rawValue[0] == '"' && rawValue[rawValue.Length - 1] == '"')
                    allNumeric = false;
                else if (!TryParseDouble(rawValue, out _))
                    allNumeric = false;

                p.choices.Add(new ScadChoice { label = label, value = rawValue });
            }
            p.type = allNumeric ? ScadParameterType.NumberDropdown : ScadParameterType.StringDropdown;
        }

        static bool TryParseDouble(string s, out double result) =>
            double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out result);

        static bool ContainsOutsideQuotes(string s, char c)
        {
            bool inStr = false;
            for (int i = 0; i < s.Length; i++)
            {
                var ch = s[i];
                if (ch == '"') inStr = !inStr;
                else if (ch == c && !inStr) return true;
            }
            return false;
        }

        static int FindLabelColon(string item)
        {
            bool inStr = false;
            for (int i = 0; i < item.Length; i++)
            {
                var c = item[i];
                if (c == '"') inStr = !inStr;
                else if (c == ':' && !inStr) return i;
            }
            return -1;
        }

        static List<string> SplitTopLevelCommas(string s)
        {
            var result = new List<string>();
            bool inStr = false;
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                var c = s[i];
                if (c == '"') inStr = !inStr;
                else if (c == ',' && !inStr)
                {
                    result.Add(s.Substring(start, i - start));
                    start = i + 1;
                }
            }
            if (start < s.Length) result.Add(s.Substring(start));
            return result;
        }
    }
}
