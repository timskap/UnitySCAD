using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Rendering;

namespace SCADPlugin.Editor
{
    public static class StlMeshLoader
    {
        // Converts OpenSCAD's right-handed Z-up coordinates to Unity's left-handed Y-up.
        // Axis swap (x, y, z) -> (x, z, y) reverses handedness, so triangle winding
        // is flipped to keep faces front-facing under Unity's default CCW culling.

        // Quantization used when welding duplicate vertices. 1e-5 at unit scale is
        // ~0.01 mm — tighter than OpenSCAD's own CSG precision, so floats that should
        // be equal but drift slightly still collapse together.
        const float WeldEpsilon = 1e-5f;

        public struct LoadStats
        {
            public int triangleCount;
            public int rawVertexCount;
            public int weldedVertexCount;
        }

        // Background-thread-safe: no Unity Mesh allocation. Returned lists are owned
        // by the caller and can be populated into a Mesh on the main thread later.
        public static void LoadRaw(string path, float scale, bool weldVertices,
            out List<Vector3> verts, out List<Vector3> normals, out List<int> tris,
            out LoadStats stats)
        {
            var bytes = File.ReadAllBytes(path);
            if (IsBinary(bytes)) LoadBinaryRaw(bytes, scale, out verts, out normals, out tris);
            else LoadAsciiRaw(bytes, scale, out verts, out normals, out tris);

            stats = new LoadStats
            {
                triangleCount = tris.Count / 3,
                rawVertexCount = verts.Count,
                weldedVertexCount = verts.Count,
            };

            if (weldVertices)
            {
                WeldByPosition(verts, normals, tris);
                stats.weldedVertexCount = verts.Count;
            }
        }

        // Main-thread convenience: raw-load + build Mesh.
        public static Mesh Load(string path, float scale, bool weldVertices, out LoadStats stats)
        {
            LoadRaw(path, scale, weldVertices, out var verts, out var normals, out var tris, out stats);

            var mesh = new Mesh();
            if (verts.Count > 65535) mesh.indexFormat = IndexFormat.UInt32;
            mesh.SetVertices(verts);
            mesh.SetNormals(normals);
            mesh.SetTriangles(tris, 0);
            return mesh;
        }

        // Reads only the header so callers can guard against oversized meshes
        // before allocating vertex arrays. Returns -1 for ASCII / malformed files.
        public static int PeekBinaryTriangleCount(string path)
        {
            using var fs = File.OpenRead(path);
            if (fs.Length < 84) return -1;
            var header = new byte[84];
            int read = fs.Read(header, 0, 84);
            if (read < 84) return -1;
            uint tri = System.BitConverter.ToUInt32(header, 80);
            long expected = 84L + 50L * tri;
            return expected == fs.Length ? (int)tri : -1;
        }

        static bool IsBinary(byte[] bytes)
        {
            if (bytes.Length < 84) return false;
            uint triCount = System.BitConverter.ToUInt32(bytes, 80);
            long expected = 84L + 50L * triCount;
            return expected == bytes.LongLength;
        }

        static void LoadBinaryRaw(byte[] bytes, float scale,
            out List<Vector3> verts, out List<Vector3> normals, out List<int> tris)
        {
            int triCount = (int)System.BitConverter.ToUInt32(bytes, 80);
            int vertCount = triCount * 3;
            verts = new List<Vector3>(vertCount);
            normals = new List<Vector3>(vertCount);
            tris = new List<int>(vertCount);

            int offset = 84;
            for (int i = 0; i < triCount; i++)
            {
                float nx = System.BitConverter.ToSingle(bytes, offset + 0);
                float ny = System.BitConverter.ToSingle(bytes, offset + 4);
                float nz = System.BitConverter.ToSingle(bytes, offset + 8);
                var n = new Vector3(nx, nz, ny);

                var v0o = offset + 12;
                var v1o = offset + 24;
                var v2o = offset + 36;
                var v0 = ReadVertex(bytes, v0o, scale);
                var v1 = ReadVertex(bytes, v1o, scale);
                var v2 = ReadVertex(bytes, v2o, scale);

                int baseIdx = verts.Count;
                verts.Add(v0); verts.Add(v1); verts.Add(v2);
                normals.Add(n); normals.Add(n); normals.Add(n);

                // Flip winding to compensate for axis-swap mirror.
                tris.Add(baseIdx + 0);
                tris.Add(baseIdx + 2);
                tris.Add(baseIdx + 1);

                offset += 50;
            }
        }

        static Vector3 ReadVertex(byte[] bytes, int offset, float scale)
        {
            float x = System.BitConverter.ToSingle(bytes, offset + 0);
            float y = System.BitConverter.ToSingle(bytes, offset + 4);
            float z = System.BitConverter.ToSingle(bytes, offset + 8);
            return new Vector3(x, z, y) * scale;
        }

        static void LoadAsciiRaw(byte[] bytes, float scale,
            out List<Vector3> verts, out List<Vector3> normals, out List<int> tris)
        {
            var text = Encoding.ASCII.GetString(bytes);
            verts = new List<Vector3>();
            normals = new List<Vector3>();
            tris = new List<int>();
            var ci = CultureInfo.InvariantCulture;
            var splitChars = new[] { ' ', '\t' };
            var splitOpts = System.StringSplitOptions.RemoveEmptyEntries;

            Vector3 curNormal = Vector3.up;

            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.Length == 0) continue;

                if (line.StartsWith("facet normal ", System.StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Substring("facet normal ".Length).Split(splitChars, splitOpts);
                    if (parts.Length >= 3 &&
                        float.TryParse(parts[0], NumberStyles.Float, ci, out var nx) &&
                        float.TryParse(parts[1], NumberStyles.Float, ci, out var ny) &&
                        float.TryParse(parts[2], NumberStyles.Float, ci, out var nz))
                    {
                        curNormal = new Vector3(nx, nz, ny);
                    }
                }
                else if (line.StartsWith("vertex ", System.StringComparison.OrdinalIgnoreCase))
                {
                    var parts = line.Substring("vertex ".Length).Split(splitChars, splitOpts);
                    if (parts.Length >= 3 &&
                        float.TryParse(parts[0], NumberStyles.Float, ci, out var x) &&
                        float.TryParse(parts[1], NumberStyles.Float, ci, out var y) &&
                        float.TryParse(parts[2], NumberStyles.Float, ci, out var z))
                    {
                        verts.Add(new Vector3(x, z, y) * scale);
                        normals.Add(curNormal);
                    }
                }
                else if (line.Equals("endloop", System.StringComparison.OrdinalIgnoreCase))
                {
                    int n = verts.Count;
                    if (n >= 3)
                    {
                        tris.Add(n - 3);
                        tris.Add(n - 1);
                        tris.Add(n - 2);
                    }
                }
            }
        }

        // Merges vertices at the same quantized position into a single entry,
        // remapping the triangle index buffer. Normals for duplicate positions
        // are dropped — callers should follow up with Mesh.RecalculateNormals to
        // get proper averaged smooth shading across the collapsed verts.
        static void WeldByPosition(List<Vector3> verts, List<Vector3> normals, List<int> tris)
        {
            int n = verts.Count;
            var map = new Dictionary<Vector3Int, int>(n);
            var remap = new int[n];
            var newVerts = new List<Vector3>(n);
            var newNormals = new List<Vector3>(n);

            for (int i = 0; i < n; i++)
            {
                var v = verts[i];
                var key = new Vector3Int(
                    Mathf.RoundToInt(v.x / WeldEpsilon),
                    Mathf.RoundToInt(v.y / WeldEpsilon),
                    Mathf.RoundToInt(v.z / WeldEpsilon));

                if (map.TryGetValue(key, out var idx))
                {
                    remap[i] = idx;
                }
                else
                {
                    idx = newVerts.Count;
                    map[key] = idx;
                    newVerts.Add(v);
                    newNormals.Add(normals[i]);
                    remap[i] = idx;
                }
            }

            for (int i = 0; i < tris.Count; i++) tris[i] = remap[tris[i]];

            verts.Clear(); verts.AddRange(newVerts);
            normals.Clear(); normals.AddRange(newNormals);
        }
    }
}
