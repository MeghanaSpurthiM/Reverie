using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using GaussianSplatting.Runtime;
using UnityEngine;

/// <summary>
/// Builds a <see cref="GaussianSplatAsset"/> from a 3DGS .ply at runtime: in the Editor
/// and in compiled players (aras-p's own converter is Editor-only).
///
/// Produces the package's "Very High" layout (Float32 position/scale/colour/SH, no
/// chunks), byte-for-byte what GaussianSplatAssetCreator writes for that quality level in
/// UnityGaussianSplatting v1.1.x (asset format 2023_10_20): Morton-ordered splats,
/// smallest-3 packed rotations, 2048-wide Morton-tiled colour texture.
///
/// Memory: ~236 bytes per splat (about 470 MB for 2M splats), the same as importing at
/// Very High in the Editor. Heavy CPU work runs on a worker thread; only the final
/// TextAsset/ScriptableObject creation touches Unity on the main thread.
/// </summary>
public static class RuntimeSplatLoader
{
    // Layout this loader writes. static readonly (not const) so the version check below is a real
    // runtime check rather than a compile-time constant the compiler flags as unreachable.
    static readonly int kSupportedFormat = 2023_10_20;
    const int kTextureWidth = 2048;   // GaussianSplatAsset.kTextureWidth
    const int kOtherStride = 16;      // 4 (rotation, Norm10) + 12 (scale, Float32)
    const int kShStride = 16 * 12;    // SHTableItemFloat32: 15 float3 + 1 float3 padding

    /// Converted splat data, ready to wrap into Unity objects on the main thread.
    sealed class SplatData
    {
        public int count;
        public Vector3 boundsMin, boundsMax;
        public byte[] pos, other, color, sh;
    }

    /// Loads <paramref name="plyPath"/>. Call from the main thread; the conversion runs on a worker.
    /// The returned asset and its TextAssets are runtime objects: release them with <see cref="Destroy"/>.
    public static async Task<GaussianSplatAsset> LoadAsync(string plyPath, IProgress<float> progress = null)
    {
        if (GaussianSplatAsset.kCurrentVersion != kSupportedFormat)
            Debug.LogWarning($"[RuntimeSplatLoader] Written for asset format {kSupportedFormat}; package reports " +
                             $"{GaussianSplatAsset.kCurrentVersion}. Check the layout if splats render wrong.");

        SplatData d = await Task.Run(() => Convert(plyPath, progress));

        var asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
        asset.name = Path.GetFileNameWithoutExtension(plyPath);
        asset.Initialize(d.count,
            GaussianSplatAsset.VectorFormat.Float32, GaussianSplatAsset.VectorFormat.Float32,
            GaussianSplatAsset.ColorFormat.Float32x4, GaussianSplatAsset.SHFormat.Float32,
            d.boundsMin, d.boundsMax, null);
        var info = new FileInfo(plyPath);
        asset.SetDataHash(Hash128.Compute($"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{d.count}"));
        asset.SetAssetFiles(null,
            new TextAsset(d.pos)   { name = asset.name + "_pos" },
            new TextAsset(d.other) { name = asset.name + "_oth" },
            new TextAsset(d.color) { name = asset.name + "_col" },
            new TextAsset(d.sh)    { name = asset.name + "_shs" });
        progress?.Report(1f);
        return asset;
    }

    // ── Prebaked: an Editor-imported asset copied as plain files (e.g. into StreamingAssets) ──

    /// Finds a GaussianSplatAsset .asset file in <paramref name="folder"/> whose data files
    /// (*_pos/_oth/_col/_shs[/_chk].bytes) sit next to it. Null if there is none.
    public static string FindPrebaked(string folder)
    {
        if (!Directory.Exists(folder)) return null;
        foreach (string asset in Directory.GetFiles(folder, "*.asset", SearchOption.TopDirectoryOnly))
        {
            string b = Path.Combine(folder, Path.GetFileNameWithoutExtension(asset));
            if (File.Exists(b + "_pos.bytes") && File.Exists(b + "_oth.bytes") &&
                File.Exists(b + "_col.bytes") && File.Exists(b + "_shs.bytes") &&
                File.ReadAllText(asset).Contains("m_SplatCount:"))
                return asset;
        }
        return null;
    }

    /// Loads an Editor-created GaussianSplatAsset from its .asset text + .bytes files without the
    /// AssetDatabase, so prebaked themes in StreamingAssets work in builds. Any quality level.
    public static async Task<GaussianSplatAsset> LoadPrebakedAsync(string assetPath, IProgress<float> progress = null)
    {
        string yaml = File.ReadAllText(assetPath);
        int Int(string key) => int.Parse(Field(yaml, key));
        Vector3 Vec(string key)
        {
            var m = System.Text.RegularExpressions.Regex.Match(Field(yaml, key),
                @"x:\s*([^,]+),\s*y:\s*([^,]+),\s*z:\s*([^}]+)");
            float P(int g) => float.Parse(m.Groups[g].Value.Trim(), System.Globalization.CultureInfo.InvariantCulture);
            return new Vector3(P(1), P(2), P(3));
        }

        int version = Int("m_FormatVersion");
        if (version != GaussianSplatAsset.kCurrentVersion)
            throw new IOException($"{assetPath}: asset format {version}, package expects {GaussianSplatAsset.kCurrentVersion}. Re-import the .ply.");
        int count = Int("m_SplatCount");
        var posFmt   = (GaussianSplatAsset.VectorFormat)Int("m_PosFormat");
        var sclFmt   = (GaussianSplatAsset.VectorFormat)Int("m_ScaleFormat");
        var shFmt    = (GaussianSplatAsset.SHFormat)Int("m_SHFormat");
        var colorFmt = (GaussianSplatAsset.ColorFormat)Int("m_ColorFormat");

        string b = Path.Combine(Path.GetDirectoryName(assetPath), Path.GetFileNameWithoutExtension(assetPath));
        bool chunked = posFmt != GaussianSplatAsset.VectorFormat.Float32 || sclFmt != GaussianSplatAsset.VectorFormat.Float32 ||
                       colorFmt != GaussianSplatAsset.ColorFormat.Float32x4 || shFmt != GaussianSplatAsset.SHFormat.Float32;

        var files = await Task.Run(() =>
        {
            byte[] Read(string suffix, long expected, int step)
            {
                string p = b + suffix;
                var data = File.ReadAllBytes(p);
                // Position/other files are padded to 8 bytes by the importer; others are exact.
                if (data.Length < expected || data.Length > expected + 8)
                    throw new IOException($"{Path.GetFileName(p)} is {data.Length:N0} bytes, expected {expected:N0}.");
                progress?.Report(step / 5f);
                return data;
            }
            return (
                chk: chunked ? Read("_chk.bytes", GaussianSplatAsset.CalcChunkDataSize(count), 1) : null,
                pos: Read("_pos.bytes", GaussianSplatAsset.CalcPosDataSize(count, posFmt), 2),
                oth: Read("_oth.bytes", GaussianSplatAsset.CalcOtherDataSize(count, sclFmt) +
                                        (shFmt >= GaussianSplatAsset.SHFormat.Cluster64k ? count * 2L : 0), 3),
                col: Read("_col.bytes", GaussianSplatAsset.CalcColorDataSize(count, colorFmt), 4),
                // Clustered SH: the file holds only the table (Float16 items, 96 B); the per-splat
                // indices live in _oth, although CalcSHDataSize counts them here too.
                shs: Read("_shs.bytes", shFmt >= GaussianSplatAsset.SHFormat.Cluster64k
                                            ? GaussianSplatAsset.GetSHCount(shFmt, count) * 96L
                                            : GaussianSplatAsset.CalcSHDataSize(count, shFmt), 5));
        });

        var asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
        asset.name = Path.GetFileNameWithoutExtension(assetPath);
        asset.Initialize(count, posFmt, sclFmt, colorFmt, shFmt, Vec("m_BoundsMin"), Vec("m_BoundsMax"), null);
        string hash = Field(yaml, "Hash", required: false);
        asset.SetDataHash(!string.IsNullOrEmpty(hash) ? Hash128.Parse(hash) : Hash128.Compute(assetPath));
        asset.SetAssetFiles(
            files.chk != null ? new TextAsset(files.chk) { name = asset.name + "_chk" } : null,
            new TextAsset(files.pos) { name = asset.name + "_pos" },
            new TextAsset(files.oth) { name = asset.name + "_oth" },
            new TextAsset(files.col) { name = asset.name + "_col" },
            new TextAsset(files.shs) { name = asset.name + "_shs" });
        progress?.Report(1f);
        return asset;
    }

    static string Field(string yaml, string key, bool required = true)
    {
        var m = System.Text.RegularExpressions.Regex.Match(yaml, @"^\s*" + key + @":\s*(.+?)\s*$",
                                                           System.Text.RegularExpressions.RegexOptions.Multiline);
        if (m.Success) return m.Groups[1].Value;
        if (required) throw new IOException($"Splat asset is missing '{key}'.");
        return null;
    }

    /// Frees an asset created by <see cref="LoadAsync"/> (no-op for project assets).
    public static void Destroy(GaussianSplatAsset asset)
    {
        if (asset == null || IsPersistent(asset)) return;
        foreach (var t in new[] { asset.posData, asset.otherData, asset.colorData, asset.shData, asset.chunkData })
            if (t != null) UnityEngine.Object.Destroy(t);
        UnityEngine.Object.Destroy(asset);
    }

    static bool IsPersistent(UnityEngine.Object o)
    {
#if UNITY_EDITOR
        return UnityEditor.EditorUtility.IsPersistent(o);
#else
        return false;
#endif
    }

    // ── PLY header ────────────────────────────────────────────────────────────

    sealed class PlyLayout
    {
        public int vertexCount, vertexStride;
        public long dataStart;            // byte offset of the first vertex
        public long bytesBeforeVertices;  // other elements stored before 'vertex'
        public readonly Dictionary<string, int> floatOffset = new();
    }

    static PlyLayout ReadHeader(Stream s, string path)
    {
        var layout = new PlyLayout();
        bool binaryLE = false, inVertex = false, vertexSeen = false;
        int curCount = 0, curStride = 0;

        for (int line = 0; line < 9000; line++)
        {
            string l = ReadLine(s);
            if (l == null) throw new IOException($"{path}: unexpected end of PLY header.");
            if (l == "end_header") break;
            string[] t = l.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (t.Length == 0) continue;

            if (t[0] == "format") binaryLE = t.Length >= 2 && t[1] == "binary_little_endian";
            else if (t[0] == "element")
            {
                if (!inVertex && !vertexSeen) layout.bytesBeforeVertices += (long)curCount * curStride;
                inVertex = t[1] == "vertex";
                curCount = int.Parse(t[2]);
                curStride = 0;
                if (inVertex) { layout.vertexCount = curCount; vertexSeen = true; }
            }
            else if (t[0] == "property")
            {
                if (t[1] == "list") throw new IOException($"{path}: list properties are not supported.");
                int size = TypeSize(t[1], path);
                if (inVertex && (t[1] == "float" || t[1] == "float32"))
                    layout.floatOffset[t[2]] = curStride;
                curStride += size;
                if (inVertex) layout.vertexStride = curStride;
            }
        }
        if (!binaryLE) throw new IOException($"{path}: only binary_little_endian PLY is supported.");
        if (!vertexSeen || layout.vertexCount <= 0) throw new IOException($"{path}: no vertex element.");
        if (layout.vertexCount > GaussianSplatAsset.kMaxSplats)
            throw new IOException($"{path}: {layout.vertexCount:N0} splats exceeds the package limit of {GaussianSplatAsset.kMaxSplats:N0}.");

        var missing = new List<string>();
        foreach (var req in new[] { "x", "y", "z", "f_dc_0", "f_dc_1", "f_dc_2", "opacity",
                                    "scale_0", "scale_1", "scale_2", "rot_0", "rot_1", "rot_2", "rot_3" })
            if (!layout.floatOffset.ContainsKey(req)) missing.Add(req);
        if (missing.Count > 0)
            throw new IOException($"{path} is not a Gaussian Splat PLY; missing float properties: {string.Join(", ", missing)}");

        layout.dataStart = s.Position + layout.bytesBeforeVertices;
        return layout;
    }

    static int TypeSize(string type, string path) => type switch
    {
        "char" or "uchar" or "int8" or "uint8" => 1,
        "short" or "ushort" or "int16" or "uint16" => 2,
        "int" or "uint" or "float" or "int32" or "uint32" or "float32" => 4,
        "double" or "float64" => 8,
        _ => throw new IOException($"{path}: unsupported PLY property type '{type}'.")
    };

    static string ReadLine(Stream s)
    {
        var sb = new StringBuilder();
        while (true)
        {
            int b = s.ReadByte();
            if (b < 0) return sb.Length > 0 ? sb.ToString() : null;
            if (b == '\n') break;
            if (b != '\r') sb.Append((char)b);
        }
        return sb.ToString();
    }

    // ── Conversion (worker thread) ────────────────────────────────────────────

    static SplatData Convert(string path, IProgress<float> progress)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        PlyLayout L = ReadHeader(fs, path);
        int n = L.vertexCount, stride = L.vertexStride;
        int ox = L.floatOffset["x"], oy = L.floatOffset["y"], oz = L.floatOffset["z"];

        // SH rest coefficients: PLY stores them channel-major (all R, then G, then B).
        int restCount = 0;
        while (L.floatOffset.ContainsKey($"f_rest_{restCount}")) restCount++;
        int coeffs = Math.Min(15, restCount / 3);
        var restOffset = new int[restCount];
        for (int i = 0; i < restCount; i++) restOffset[i] = L.floatOffset[$"f_rest_{i}"];

        var buf = new byte[stride * 8192];

        // Pass 1: positions -> bounds -> Morton order.
        var px = new float[n]; var py = new float[n]; var pz = new float[n];
        Vector3 bmin = Vector3.positiveInfinity, bmax = Vector3.negativeInfinity;
        ForEachVertex(fs, L, buf, (v, b, o) =>
        {
            float x = F(b, o + ox), y = F(b, o + oy), z = F(b, o + oz);
            px[v] = x; py[v] = y; pz[v] = z;
            bmin = Vector3.Min(bmin, new Vector3(x, y, z));
            bmax = Vector3.Max(bmax, new Vector3(x, y, z));
        }, p => progress?.Report(p * 0.2f));

        var keys = new ulong[n];
        var order = new int[n];
        const float kScaler = (1 << 21) - 1;
        Vector3 inv = new(1f / (bmax.x - bmin.x), 1f / (bmax.y - bmin.y), 1f / (bmax.z - bmin.z));
        for (int i = 0; i < n; i++)
        {
            uint ix = (uint)((px[i] - bmin.x) * inv.x * kScaler);
            uint iy = (uint)((py[i] - bmin.y) * inv.y * kScaler);
            uint iz = (uint)((pz[i] - bmin.z) * inv.z * kScaler);
            keys[i] = (Part1By2(iz) << 2) | (Part1By2(iy) << 1) | Part1By2(ix);
            order[i] = i;
        }
        px = py = pz = null;
        // Same ordering as the Editor importer: by Morton code, ties by original index.
        Array.Sort(order, (a, b) => keys[a] != keys[b] ? keys[a].CompareTo(keys[b]) : a.CompareTo(b));
        keys = null;
        var dstOf = new int[n];
        for (int dst = 0; dst < n; dst++) dstOf[order[dst]] = dst;
        order = null;
        progress?.Report(0.3f);

        // Pass 2: convert every vertex straight into its sorted slot in the output buffers.
        var (texW, texH) = (kTextureWidth, Math.Max(1, (n + kTextureWidth - 1) / kTextureWidth));
        texH = (texH + 15) / 16 * 16;
        var d = new SplatData
        {
            count = n, boundsMin = bmin, boundsMax = bmax,
            pos   = new byte[NextMultipleOf(n * 12, 8)],
            other = new byte[NextMultipleOf(n * kOtherStride, 8)],
            color = new byte[texW * texH * 16],
            sh    = new byte[n * kShStride],
        };
        int oDc0 = L.floatOffset["f_dc_0"], oDc1 = L.floatOffset["f_dc_1"], oDc2 = L.floatOffset["f_dc_2"];
        int oOp = L.floatOffset["opacity"];
        int oS0 = L.floatOffset["scale_0"], oS1 = L.floatOffset["scale_1"], oS2 = L.floatOffset["scale_2"];
        int oR0 = L.floatOffset["rot_0"], oR1 = L.floatOffset["rot_1"], oR2 = L.floatOffset["rot_2"], oR3 = L.floatOffset["rot_3"];
        const float kSH_C0 = 0.2820948f;

        ForEachVertex(fs, L, buf, (v, b, o) =>
        {
            int dst = dstOf[v];

            // Position (Float32)
            int p = dst * 12;
            W(d.pos, p, F(b, o + ox)); W(d.pos, p + 4, F(b, o + oy)); W(d.pos, p + 8, F(b, o + oz));

            // Rotation: PLY is (w,x,y,z) -> normalize, swizzle to (x,y,z,w), pack smallest-3, Norm10
            float rw = F(b, o + oR0), rx = F(b, o + oR1), ry = F(b, o + oR2), rz = F(b, o + oR3);
            float len = MathF.Sqrt(rw * rw + rx * rx + ry * ry + rz * rz);
            if (len > 0) { rw /= len; rx /= len; ry /= len; rz /= len; }
            int q = dst * kOtherStride;
            WU(d.other, q, EncodeQuatToNorm10(PackSmallest3(rx, ry, rz, rw)));

            // Scale: linear (abs(exp)), Float32
            W(d.other, q + 4, MathF.Abs(MathF.Exp(F(b, o + oS0))));
            W(d.other, q + 8, MathF.Abs(MathF.Exp(F(b, o + oS1))));
            W(d.other, q + 12, MathF.Abs(MathF.Exp(F(b, o + oS2))));

            // Colour + opacity -> Morton-tiled Float32x4 texture
            int c = SplatIndexToTextureIndex((uint)dst) * 16;
            W(d.color, c,      F(b, o + oDc0) * kSH_C0 + 0.5f);
            W(d.color, c + 4,  F(b, o + oDc1) * kSH_C0 + 0.5f);
            W(d.color, c + 8,  F(b, o + oDc2) * kSH_C0 + 0.5f);
            W(d.color, c + 12, 1f / (1f + MathF.Exp(-F(b, o + oOp))));

            // SH: coefficient k = (R_k, G_k, B_k); missing higher orders stay zero.
            int s = dst * kShStride;
            for (int k = 0; k < coeffs; k++)
            {
                W(d.sh, s + k * 12,     F(b, o + restOffset[k]));
                W(d.sh, s + k * 12 + 4, F(b, o + restOffset[k + coeffs]));
                W(d.sh, s + k * 12 + 8, F(b, o + restOffset[k + coeffs * 2]));
            }
        }, p => progress?.Report(0.3f + p * 0.65f));

        return d;
    }

    static void ForEachVertex(FileStream fs, PlyLayout L, byte[] buf, Action<int, byte[], int> perVertex, Action<float> progress)
    {
        fs.Position = L.dataStart;
        int stride = L.vertexStride, perBatch = buf.Length / stride;
        for (int v = 0; v < L.vertexCount;)
        {
            int batch = Math.Min(perBatch, L.vertexCount - v);
            int need = batch * stride, got = 0;
            while (got < need)
            {
                int r = fs.Read(buf, got, need - got);
                if (r <= 0) throw new IOException($"PLY data ended early at vertex {v:N0} of {L.vertexCount:N0}.");
                got += r;
            }
            for (int i = 0; i < batch; i++) perVertex(v + i, buf, i * stride);
            v += batch;
            progress((float)v / L.vertexCount);
        }
    }

    // ── Encoding helpers (mirror GaussianUtils / GaussianSplatAssetCreator) ───

    static float F(byte[] b, int o) => BitConverter.ToSingle(b, o);
    static void W(byte[] dst, int o, float v) => BitConverter.TryWriteBytes(new Span<byte>(dst, o, 4), v);
    static void WU(byte[] dst, int o, uint v) => BitConverter.TryWriteBytes(new Span<byte>(dst, o, 4), v);
    static int NextMultipleOf(int size, int m) => (size + m - 1) / m * m;

    static ulong Part1By2(ulong x)
    {
        x &= 0x1fffff;
        x = (x ^ (x << 32)) & 0x1f00000000ffffUL;
        x = (x ^ (x << 16)) & 0x1f0000ff0000ffUL;
        x = (x ^ (x << 8)) & 0x100f00f00f00f00fUL;
        x = (x ^ (x << 4)) & 0x10c30c30c30c30c3UL;
        x = (x ^ (x << 2)) & 0x1249249249249249UL;
        return x;
    }

    static (uint x, uint y) DecodeMorton2D_16x16(uint t)
    {
        t = (t & 0xFF) | ((t & 0xFE) << 7);
        t &= 0x5555;
        t = (t ^ (t >> 1)) & 0x3333;
        t = (t ^ (t >> 2)) & 0x0f0f;
        return (t & 0xF, t >> 8);
    }

    static int SplatIndexToTextureIndex(uint idx)
    {
        var (bx, by) = DecodeMorton2D_16x16(idx);
        uint width = kTextureWidth / 16;
        idx >>= 8;
        uint x = (idx % width) * 16 + bx;
        uint y = (idx / width) * 16 + by;
        return (int)(y * kTextureWidth + x);
    }

    // q = (x,y,z,w), normalized. Returns smallest three in 0..1 and index/3 of the largest in w.
    static (float x, float y, float z, float w) PackSmallest3(float x, float y, float z, float w)
    {
        float ax = MathF.Abs(x), ay = MathF.Abs(y), az = MathF.Abs(z), aw = MathF.Abs(w);
        int index = 0; float maxV = ax;
        if (ay > maxV) { index = 1; maxV = ay; }
        if (az > maxV) { index = 2; maxV = az; }
        if (aw > maxV) { index = 3; }

        float a, b, c, big;
        switch (index)
        {
            case 0:  a = y; b = z; c = w; big = x; break;   // yzwx
            case 1:  a = x; b = z; c = w; big = y; break;   // xzwy
            case 2:  a = x; b = y; c = w; big = z; break;   // xywz
            default: a = x; b = y; c = z; big = w; break;
        }
        float sign = big >= 0 ? 1f : -1f;
        const float kSqrt2 = 1.41421356f;
        return (a * sign * kSqrt2 * 0.5f + 0.5f, b * sign * kSqrt2 * 0.5f + 0.5f,
                c * sign * kSqrt2 * 0.5f + 0.5f, index / 3f);
    }

    static uint EncodeQuatToNorm10((float x, float y, float z, float w) v) =>
        (uint)(v.x * 1023.5f) | ((uint)(v.y * 1023.5f) << 10) | ((uint)(v.z * 1023.5f) << 20) | ((uint)(v.w * 3.5f) << 30);
}
