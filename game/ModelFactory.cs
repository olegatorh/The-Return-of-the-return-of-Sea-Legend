using System;
using System.Collections.Generic;
using Godot;
using StormScript.Formats;

namespace SeaDogs;

/// <summary>Turns a .gm file into Godot meshes. Direct3D is left-handed: z is negated on the way.</summary>
public static class ModelFactory
{
    public static double TotalBuildMs; public static int BuildCount;       // SEADOGS_PROF: all ModelFactory.Build calls so far
    public static float LightGain = 2.0f;   // the original modulates vertex lighting x2 (128 = neutral)

    public static Node3D Build(GmFile g, string name, string? texturesPath, Color[]? vertexColors, string? shadowPath = null, float relief = 0f)
    {
        long tB = System.Diagnostics.Stopwatch.GetTimestamp();
        var root = new Node3D { Name = name };
        PrefetchTextures(g, texturesPath, shadowPath);
        var matCache = new Dictionary<int, StandardMaterial3D>();
        for (int oi = 0; oi < g.Objects.Count; oi++)
        {
            var o = g.Objects[oi];
            var m = g.ExtractObject(o);
            if (m == null) continue;
            int n = m.Pos.Length / 3;
            var verts = new Vector3[n];
            var norms = new Vector3[n];
            var uvs = new Vector2[n];
            for (int i = 0; i < n; i++)
            {
                verts[i] = new Vector3(m.Pos[i * 3], m.Pos[i * 3 + 1], -m.Pos[i * 3 + 2]);
                norms[i] = new Vector3(m.Normal[i * 3], m.Normal[i * 3 + 1], -m.Normal[i * 3 + 2]);
                uvs[i] = new Vector2(m.Uv[i * 2], m.Uv[i * 2 + 1]);
            }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = verts;
            arrays[(int)Mesh.ArrayType.Normal] = norms;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            var uv2 = new Vector2[n];                                                       // detail layer: one repeat per DetailMetres of ground (engine: second texture "bump_land")
            for (int i = 0; i < n; i++) uv2[i] = new Vector2(verts[i].X, verts[i].Z) / DetailMetres;
            arrays[(int)Mesh.ArrayType.TexUV2] = uv2;
            arrays[(int)Mesh.ArrayType.Index] = m.Index;
            bool lit = false;
            if (vertexColors != null && m.FirstGlobalVertex + n <= vertexColors.Length)
            {
                var cs = new Color[n];
                for (int i = 0; i < n; i++)
                {
                    var c = vertexColors[m.FirstGlobalVertex + i];
                    float k = relief > 0f ? 1f - relief * 0.5f + relief * Math.Max(0f, norms[i].Normalized().Dot(ReliefSun)) : 1f;     // slopes facing the light are brighter, the others fall into shade: volume where the baked light is flat
                    cs[i] = new Color(Math.Min(c.R * LightGain * k, 1f), Math.Min(c.G * LightGain * k, 1f), Math.Min(c.B * LightGain * k, 1f));
                }
                arrays[(int)Mesh.ArrayType.Color] = cs;
                lit = true;
            }
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            if (!matCache.TryGetValue(o.Material, out var mat))
                matCache[o.Material] = mat = MakeMaterial(g, o.Material, texturesPath, lit, shadowPath);
            mesh.SurfaceSetMaterial(0, mat);
            var mi = new MeshInstance3D { Mesh = mesh, Name = string.IsNullOrEmpty(o.Name) ? $"obj{oi}" : o.Name };
            if (System.Environment.GetEnvironmentVariable("SEADOGS_NOSHADOWHIDE") == null && IsShadowMaterial(g, o.Material) && string.IsNullOrEmpty(shadowPath)) mi.Visible = false;
            root.AddChild(mi);
        }
        if (GameAssets.Prof) { TotalBuildMs += (System.Diagnostics.Stopwatch.GetTimestamp() - tB) * 1000.0 / System.Diagnostics.Stopwatch.Frequency; BuildCount++; }
        return root;
    }

    /// <summary>Bounding box of all mesh children (in the node's parent space).</summary>
    public static Aabb Bounds(Node3D n)
    {
        bool any = false;
        Aabb r = default;
        foreach (var c in n.GetChildren())
            if (c is MeshInstance3D mi)
            {
                var b = mi.Transform * mi.Mesh.GetAabb();
                r = any ? r.Merge(b) : b;
                any = true;
            }
        return r;
    }

    /// <summary>The texture of a material (same lookup as MakeMaterial).</summary>
    static string? TextureOf(GmFile g, int index, string? texturesPath, string? shadowPath)
    {
        string? file = index >= 0 && index < g.Materials.Count ? g.Materials[index].Textures[0] : null;
        if (file == null) return null;
        if (file.Equals("shadow.tga", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(shadowPath))
        {
            string cand = shadowPath.TrimEnd((char)92) + (char)92 + file;
            if (System.IO.File.Exists(GameAssets.Resource("Textures" + (char)92 + cand + ".tx"))) return cand;
        }
        return GameAssets.FindTexture(file, string.IsNullOrEmpty(texturesPath) ? null : texturesPath);
    }

    static void PrefetchTextures(GmFile g, string? texturesPath, string? shadowPath)
    {
        var names = new List<string>();
        var seen = new HashSet<int>();
        foreach (var o in g.Objects)
            if (seen.Add(o.Material) && TextureOf(g, o.Material, texturesPath, shadowPath) is { } rel) names.Add(rel);
        GameAssets.Prefetch(names);
    }

    /// <summary>"shadow.tga" materials are the engine's shadow-blob layer (drawn blended by a special technique): not shown as opaque geometry.</summary>
    static bool IsShadowMaterial(GmFile g, int index)
        => index >= 0 && index < g.Materials.Count && string.Equals(g.Materials[index].Textures[0], "shadow.tga", StringComparison.OrdinalIgnoreCase);

    static readonly Vector3 ReliefSun = new Vector3(-0.45f, 0.75f, 0.35f).Normalized();
    const float DetailMetres = 14f;
    static readonly bool NoDetail = System.Environment.GetEnvironmentVariable("SEADOGS_NODETAIL") != null;
    static readonly Dictionary<string, ImageTexture?> detailTex = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The land materials carry a grey detail map as second texture: multiplied over the base texture (normalised to a mean of 1) so that close ground stays sharp.</summary>
    static ImageTexture? DetailTexture(string file)
    {
        if (detailTex.TryGetValue(file, out var t)) return t;
        ImageTexture? r = null;
        string? rel = GameAssets.FindTexture(file, null);
        if (rel != null && GameAssets.Texture(rel) is { } src && src.GetImage() is { } img)
        {
            img = (Image)img.Duplicate();
            if (img.IsCompressed()) img.Decompress();
            img.ClearMipmaps();
            img.Convert(Image.Format.Rgba8);
            var d = img.GetData(); long sum = 0; int cnt = d.Length / 4;
            for (int i = 0; i < d.Length; i += 4) sum += d[i] + d[i + 1] + d[i + 2];
            float mean = Math.Max(1f, sum / (3f * cnt)), k = 255f / mean;
            for (int i = 0; i < d.Length; i += 4) for (int c = 0; c < 3; c++) d[i + c] = (byte)Math.Min(255f, d[i + c] * k);
            var n = Image.CreateFromData(img.GetWidth(), img.GetHeight(), false, Image.Format.Rgba8, d);
            n.GenerateMipmaps();
            r = ImageTexture.CreateFromImage(n);
        }
        detailTex[file] = r;
        return r;
    }

    public static StandardMaterial3D MakeMaterial(GmFile g, int index, string? texturesPath, bool lit, string? shadowPath = null)
    {
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            CullMode = BaseMaterial3D.CullModeEnum.Disabled,
            VertexColorUseAsAlbedo = lit,
            TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic,
        };
        string? file = index >= 0 && index < g.Materials.Count ? g.Materials[index].Textures[0] : null;
        if (file != null)
        {
            string? rel = null;
            if (file.Equals("shadow.tga", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(shadowPath))
            {
                // the baked ground/lightmap texture of a location lives in the lighting folder (shadowPath = "lighting\day\<models path>")
                string cand = shadowPath.TrimEnd((char)92) + (char)92 + file;
                if (System.IO.File.Exists(GameAssets.Resource("Textures" + (char)92 + cand + ".tx"))) rel = cand;
                else Godot.GD.Print("[model] shadow texture candidate missing: " + cand);
            }
            rel ??= GameAssets.FindTexture(file, string.IsNullOrEmpty(texturesPath) ? null : texturesPath);
            if (rel != null)
            {
                mat.AlbedoTexture = GameAssets.Texture(rel);
                if (GameAssets.HasAlpha(rel)) mat.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor;
                if (System.Environment.GetEnvironmentVariable("SEADOGS_MATLOG") != null) Godot.GD.Print($"[mat] #{index} {file} -> {rel} alpha={GameAssets.HasAlpha(rel)} lit={lit} t1={g.Materials[index].Textures[1]} t2={g.Materials[index].Textures[2]} t3={g.Materials[index].Textures[3]}");
            }
            else Godot.GD.PrintErr("[model] texture not found: " + file);
            if (!NoDetail && lit && index >= 0 && index < g.Materials.Count && g.Materials[index].Textures[1] is { } dn && dn.StartsWith("bump_land", StringComparison.OrdinalIgnoreCase)
                && !(file.Equals("shadow.tga", StringComparison.OrdinalIgnoreCase)) && DetailTexture(dn) is { } dt)
            {
                mat.DetailEnabled = true; mat.DetailAlbedo = dt; mat.DetailBlendMode = BaseMaterial3D.BlendModeEnum.Mul; mat.DetailUVLayer = BaseMaterial3D.DetailUV.UV2;
            }
        }
        return mat;
    }
}
