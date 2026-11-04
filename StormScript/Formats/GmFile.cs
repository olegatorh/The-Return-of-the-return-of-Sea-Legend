using System.Buffers.Binary;
using System.Text;

namespace StormScript.Formats;

/// <summary>
/// Reader for Storm Engine .gm geometry files (RDF version '1.05', bytes "50.1"). Names in the file are byte offsets
/// into the names blob. Coordinates are left-handed Direct3D; conversion is up to the caller.
/// </summary>
public sealed class GmFile
{
    public sealed class Material
    {
        public string Group = "", Name = "";
        public float Diffuse, Specular, Gloss, SelfIllum;
        public string?[] Textures = new string?[4];     // resolved texture file names per slot (null = none)
    }

    public sealed class Label
    {
        public string Group = "", Name = "";
        public int Flags;
        public int Bone;                                // skeleton bone this label is attached to (characters)
        public float[] M = new float[16];               // row-major D3D matrix: vx, vy, vz, pos
        public float X => M[12];
        public float Y => M[13];
        public float Z => M[14];
    }

    public sealed class Obj
    {
        public string Group = "", Name = "";
        public int Flags;
        public float[] Center = new float[3];
        public float Radius;
        public int VertexBuff, NTriangles, STriangle, NVertices, SVertex, Material;
    }

    public sealed class VertexBuffer
    {
        public int Type, Size, Stride, FirstVertex;     // FirstVertex: index of its first vertex over all buffers
        public byte[] Data = [];
        public int Count => Stride == 0 ? 0 : Size / Stride;
    }

    public const int Version = 0x312E3035;
    public int Flags;
    public float[] BBoxSize = new float[3], BBoxCenter = new float[3];
    public float Radius;
    public List<string> Textures = new();
    public List<Material> Materials = new();
    public List<Label> Labels = new();
    public List<Obj> Objects = new();
    public ushort[] Triangles = [];
    public List<VertexBuffer> Buffers = new();
    public int TotalVertices => Buffers.Sum(b => b.Count);

    static int StrideOf(int type) => type switch { 0 => 36, 1 => 44, 4 => 44, _ => 0 };

    public static GmFile Parse(byte[] d)
    {
        int I(int o) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o));
        float F(int o) => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o));

        if (d.Length < 72 || I(0) != Version) throw new InvalidDataException("not a .gm 1.05 file");
        var g = new GmFile { Flags = I(4) };
        int nameSize = I(8), nNames = I(12), nTex = I(16), nMat = I(20), nLight = I(24), nLabel = I(28), nObj = I(32), nTri = I(36), nVb = I(40);
        for (int i = 0; i < 3; i++) { g.BBoxSize[i] = F(44 + 4 * i); g.BBoxCenter[i] = F(56 + 4 * i); }
        g.Radius = F(68);
        int p = 72;
        int blobStart = p;
        string Name(int off)
        {
            if (off < 0 || off >= nameSize) return "";
            int a = blobStart + off, e = a;
            while (e < blobStart + nameSize && d[e] != 0) e++;
            return Encoding.GetEncoding(1251).GetString(d, a, e - a);
        }
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        p += nameSize + 4 * nNames;                      // names blob + offsets table (names are addressed by byte offset)

        for (int i = 0; i < nTex; i++, p += 4) g.Textures.Add(Name(I(p)));

        for (int i = 0; i < nMat; i++, p += 56)
        {
            var m = new Material { Group = Name(I(p)), Name = Name(I(p + 4)), Diffuse = F(p + 8), Specular = F(p + 12), Gloss = F(p + 16), SelfIllum = F(p + 20) };
            for (int t = 0; t < 4; t++)
            {
                int type = I(p + 24 + 4 * t), idx = I(p + 40 + 4 * t);
                m.Textures[t] = type != 0 && idx >= 0 && idx < g.Textures.Count && g.Textures[idx].Length > 0 ? g.Textures[idx] : null;
            }
            g.Materials.Add(m);
        }
        p += 80 * nLight;

        for (int i = 0; i < nLabel; i++, p += 108)
        {
            var l = new Label { Group = Name(I(p)), Name = Name(I(p + 4)), Flags = I(p + 8), Bone = I(p + 76) };
            for (int k = 0; k < 16; k++) l.M[k] = F(p + 12 + 4 * k);
            g.Labels.Add(l);
        }

        for (int i = 0; i < nObj; i++, p += 104)
        {
            var o = new Obj
            {
                Group = Name(I(p)), Name = Name(I(p + 4)), Flags = I(p + 8),
                Radius = F(p + 24), VertexBuff = I(p + 28), NTriangles = I(p + 32), STriangle = I(p + 36),
                NVertices = I(p + 40), SVertex = I(p + 44), Material = I(p + 48),
            };
            for (int k = 0; k < 3; k++) o.Center[k] = F(p + 12 + 4 * k);
            g.Objects.Add(o);
        }

        g.Triangles = new ushort[nTri * 3];
        for (int i = 0; i < nTri * 3; i++) g.Triangles[i] = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(p + 2 * i));
        p += 6 * nTri;

        var vbs = new List<VertexBuffer>();
        for (int i = 0; i < nVb; i++, p += 8)
        {
            int type = I(p), size = I(p + 4);
            vbs.Add(new VertexBuffer { Type = type, Size = size, Stride = StrideOf(type) });
        }
        int first = 0;
        foreach (var vb in vbs)
        {
            vb.Data = d.AsSpan(p, vb.Size).ToArray();
            p += vb.Size;
            vb.FirstVertex = first;
            first += vb.Count;
        }
        g.Buffers = vbs;
        return g;
    }

    /// <summary>Geometry of one object in D3D (left-handed) space, indices local to the returned vertex range.</summary>
    public sealed record MeshData(float[] Pos, float[] Normal, float[] Uv, int[] Index, int FirstGlobalVertex)
    {
        /// <summary>Skinned vertices (type 4): two bone indices and weights per vertex; null for static geometry.</summary>
        public int[]? Bones;
        public float[]? Weights;
    }

    public MeshData? ExtractObject(Obj o)
    {
        if (o.VertexBuff < 0 || o.VertexBuff >= Buffers.Count || o.NTriangles <= 0) return null;
        var vb = Buffers[o.VertexBuff];
        if (vb.Stride == 0) return null;
        int lo = o.SVertex, n = Math.Min(o.NVertices, vb.Count - lo);
        if (n <= 0) return null;
        var pos = new float[n * 3];
        var nrm = new float[n * 3];
        var uv = new float[n * 2];
        int[]? bones = vb.Type == 4 ? new int[n * 2] : null;
        float[]? weights = vb.Type == 4 ? new float[n * 2] : null;
        var data = vb.Data;
        for (int i = 0; i < n; i++)
        {
            int b = (lo + i) * vb.Stride;
            float F(int k) => BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(b + k));
            if (vb.Type == 4)   // pos, weight, boneid, norm, color, uv
            {
                pos[i * 3] = F(0); pos[i * 3 + 1] = F(4); pos[i * 3 + 2] = F(8);
                nrm[i * 3] = F(20); nrm[i * 3 + 1] = F(24); nrm[i * 3 + 2] = F(28);
                uv[i * 2] = F(36); uv[i * 2 + 1] = F(40);
                uint bid = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(b + 16));
                float w0 = F(12);
                bones![i * 2] = (int)(bid & 255); bones[i * 2 + 1] = (int)((bid >> 8) & 255);
                weights![i * 2] = w0; weights[i * 2 + 1] = 1f - w0;
            }
            else                // pos, norm, color, uv0 [, uv1]
            {
                pos[i * 3] = F(0); pos[i * 3 + 1] = F(4); pos[i * 3 + 2] = F(8);
                nrm[i * 3] = F(12); nrm[i * 3 + 1] = F(16); nrm[i * 3 + 2] = F(20);
                uv[i * 2] = F(28); uv[i * 2 + 1] = F(32);
            }
        }
        var idx = new int[o.NTriangles * 3];
        for (int i = 0; i < idx.Length; i++)
        {
            int v = Triangles[o.STriangle * 3 + i];
            idx[i] = v >= n ? 0 : v;
        }
        return new MeshData(pos, nrm, uv, idx, vb.FirstVertex + lo) { Bones = bones, Weights = weights };
    }
}
