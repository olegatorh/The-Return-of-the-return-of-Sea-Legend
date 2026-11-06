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

    public static Node3D Build(GmFile g, string name, string? texturesPath, Color[]? vertexColors, string? shadowPath = null)
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
            arrays[(int)Mesh.ArrayType.Index] = m.Index;
            bool lit = false;
            if (vertexColors != null && m.FirstGlobalVertex + n <= vertexColors.Length)
            {
                var cs = new Color[n];
                for (int i = 0; i < n; i++)
                {
                    var c = vertexColors[m.FirstGlobalVertex + i];
                    cs[i] = new Color(Math.Min(c.R * LightGain, 1f), Math.Min(c.G * LightGain, 1f), Math.Min(c.B * LightGain, 1f));
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
                if (System.Environment.GetEnvironmentVariable("SEADOGS_MATLOG") != null) Godot.GD.Print($"[mat] #{index} {file} -> {rel} alpha={GameAssets.HasAlpha(rel)} lit={lit}");
            }
            else Godot.GD.PrintErr("[model] texture not found: " + file);
        }
        return mat;
    }
}
