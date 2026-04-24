using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SCADPlugin.Editor
{
    public static class ScadColorExtractor
    {
        // Matches color(...) where the first argument is one of:
        //   - a string literal:  "#C3F9BC"  or  "red"
        //   - an RGB/RGBA vector: [0.55, 0.38, 0.24]
        //   - an identifier:     wall_color
        // RGB-array forms are now supported by pairing with an override preamble
        // that uses `==` comparison, which works for vectors in OpenSCAD.
        static readonly Regex ColorCallRegex = new Regex(
            @"\bcolor\s*\(\s*(""[^""]+""|\[[^\]]+\]|[A-Za-z_][A-Za-z0-9_]*)\s*[,)]",
            RegexOptions.Compiled);

        // Returns SCAD literals ready to be passed verbatim as a `-D` value.
        // Strings keep their surrounding quotes; vectors keep their brackets.
        // The importer decides how to turn each literal into a Unity Color.
        public static List<string> Extract(string source, IEnumerable<ScadParameter> parameters)
        {
            var varLookup = new Dictionary<string, string>();
            foreach (var p in parameters)
            {
                if (p == null) continue;
                var raw = string.IsNullOrEmpty(p.value) ? p.defaultValue : p.value;
                if (string.IsNullOrEmpty(raw)) continue;

                if (p.type == ScadParameterType.String || p.type == ScadParameterType.ColorVector)
                {
                    varLookup[p.name] = raw.Trim();
                }
            }

            var seen = new HashSet<string>();
            var result = new List<string>();

            foreach (Match m in ColorCallRegex.Matches(source))
            {
                var arg = m.Groups[1].Value.Trim();
                string literal;

                if (arg.Length > 0 && (arg[0] == '"' || arg[0] == '['))
                {
                    literal = arg;
                }
                else if (varLookup.TryGetValue(arg, out var resolved))
                {
                    literal = resolved;
                }
                else
                {
                    continue;
                }

                if (seen.Add(literal)) result.Add(literal);
            }

            return result;
        }
    }
}
