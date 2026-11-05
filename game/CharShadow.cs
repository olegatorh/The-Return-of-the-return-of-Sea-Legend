using System;
using System.Collections.Generic;
using Godot;

namespace SeaDogs;

/// <summary>The triangles of a location's static models in world space on a 4 m grid: the surfaces a character's shadow can fall on (the engine's SHADOW layer).</summary>
public sealed class ReceiverGrid
{
    const float Cell = 4f;
    readonly List<Vector3> v = new();
    readonly Dictionary<long, List<int>> cells = new();
    static long Key(int cx, int cz) => ((long)cx << 32) ^ (uint)cz;

    public int Count => v.Count / 3;

    public void Add(MeshInstance3D mi)
    {
        if (mi.Mesh == null) return;
        var faces = mi.Mesh.GetFaces();
        var xf = mi.GlobalTransform;
        for (int i = 0; i + 2 < faces.Length; i += 3)
        {
            var a = xf * faces[i]; var b = xf * faces[i + 1]; var c = xf * faces[i + 2];
            int t = v.Count / 3;
            v.Add(a); v.Add(b); v.Add(c);
            int x0 = (int)MathF.Floor(Math.Min(a.X, Math.Min(b.X, c.X)) / Cell), x1 = (int)MathF.Floor(Math.Max(a.X, Math.Max(b.X, c.X)) / Cell);
            int z0 = (int)MathF.Floor(Math.Min(a.Z, Math.Min(b.Z, c.Z)) / Cell), z1 = (int)MathF.Floor(Math.Max(a.Z, Math.Max(b.Z, c.Z)) / Cell);
            if ((x1 - x0 + 1) * (z1 - z0 + 1) > 64) continue;        // a huge sheet (sky, sea bottom): not a receiver
            for (int cx = x0; cx <= x1; cx++)
                for (int cz = z0; cz <= z1; cz++)
                {
                    long k = Key(cx, cz);
                    if (!cells.TryGetValue(k, out var l)) cells[k] = l = new List<int>();
                    l.Add(t);
                }
        }
    }

    /// <summary>The triangles within <paramref name="r"/> metres (in x and z) of a point, each listed once.</summary>
    public void Query(Vector3 p, float r, List<int> result, HashSet<int> seen)
    {
        result.Clear(); seen.Clear();
        int x0 = (int)MathF.Floor((p.X - r) / Cell), x1 = (int)MathF.Floor((p.X + r) / Cell), z0 = (int)MathF.Floor((p.Z - r) / Cell), z1 = (int)MathF.Floor((p.Z + r) / Cell);
        for (int cx = x0; cx <= x1; cx++)
            for (int cz = z0; cz <= z1; cz++)
                if (cells.TryGetValue(Key(cx, cz), out var l))
                    foreach (var t in l) if (seen.Add(t)) result.Add(t);
    }

    public (Vector3 a, Vector3 b, Vector3 c) Tri(int t) => (v[t * 3], v[t * 3 + 1], v[t * 3 + 2]);
}

/// <summary>
/// The engine's "shadow" class (src/libs/shadow/shadow.cpp) for one character. Like the original: the silhouette of the model seen from the light is drawn into
/// a small texture (the engine: 128 x 128, blurred 3 x 3), then the location's polygons near the character that face the light are drawn with that texture
/// projected along the light (here a mesh rebuilt from the static models, with the projection in its UVs, exactly what the engine's AddPoly does). The light is
/// the weather sun, its slope limited to 0.6 downwards; the density is the script's Shadow.Density (0x60 of 255); the shadow fades between 8 and 16 m from the
/// camera and weakens to 0.2 while the body is not lit by the sun (ten rays from the body to the light, smoothed over a second).
/// </summary>
public sealed partial class CharShadow : Node3D
{
    public const uint CasterLayer = 1u << 19;       // silhouette copies of the meshes: drawn only by the shadow camera
    public const uint BodyLayer = 1u << 18;
    const int TexSize = 64;
    const float FarBlend = 16f;                     // shadow.cpp farBlend (nearBlend 8 is in the shader)
    const float Density = 96f / 255f;               // Shadow.Density.Foot / Head: argb(255, 96, 96, 64..96)
    const float Capture = 2.8f;                     // metres covered by the silhouette texture (the engine fits the object radius x 1.2)
    const int MaxVerts = 1024;                      // vbuff_size

    public static Func<ReceiverGrid?>? ReceiversOf;
    static readonly bool Debug = System.Environment.GetEnvironmentVariable("SEADOGS_SHADOWDEBUG") != null;
    static readonly bool Off = System.Environment.GetEnvironmentVariable("SEADOGS_NOSHADOW") != null;
    public static int Active, Polys;                // shadows drawn this frame / polygons they use (statistics)
    static ulong frameCounter;

    const string ShaderCode = @"
shader_type spatial;
render_mode unshaded, depth_draw_never, cull_disabled, blend_mix;
uniform sampler2D tex : filter_linear, repeat_disable;
uniform float density = 0.376;
varying float view_d;
void vertex() {
    vec4 v = VIEW_MATRIX * vec4(VERTEX, 1.0);
    v.xyz -= normalize(v.xyz) * 0.04;
    view_d = length(v.xyz);
    POSITION = PROJECTION_MATRIX * v;
}
void fragment() {
    float a = (UV.x < 0.0 || UV.x > 1.0 || UV.y < 0.0 || UV.y > 1.0) ? 0.0 : texture(tex, UV).a;
    ALBEDO = vec3(0.0);
    ALPHA = a * density * (1.0 - smoothstep(8.0, 16.0, view_d));
}";
    static Shader? shader;

    readonly CharacterView owner;
    readonly SubViewport vp;
    readonly Camera3D cam;
    readonly MeshInstance3D receivers;
    readonly ShaderMaterial mat;
    readonly ArrayMesh mesh = new();
    readonly List<int> tris = new();
    readonly HashSet<int> seen = new();
    float shading = 1f;
    int tick;
    Vector3 builtAt = new(1e9f, 0, 0), builtDir;
    bool hasSurface;

    public static CharShadow? Create(CharacterView owner, CharacterRig rig)
    {
        if (Off) return null;
        if (rig.MakeShadowCasters(CasterLayer, BodyLayer) == 0) return null;
        return new CharShadow(owner);
    }

    CharShadow(CharacterView owner)
    {
        this.owner = owner;
        Name = "CharShadow";
        TopLevel = true;
        vp = new SubViewport
        {
            Size = new Vector2I(TexSize, TexSize), TransparentBg = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            Msaa3D = System.Environment.GetEnvironmentVariable("SEADOGS_SHADOWMSAA") != null ? Viewport.Msaa.Msaa4X : Viewport.Msaa.Disabled, GuiDisableInput = true,
        };
        cam = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = Capture, Near = 0.1f, Far = 12f, CullMask = CasterLayer, Environment = new Godot.Environment() };
        vp.AddChild(cam);
        AddChild(vp);
        shader ??= new Shader { Code = ShaderCode };
        mat = new ShaderMaterial { Shader = shader, RenderPriority = 1 };
        receivers = new MeshInstance3D { Mesh = mesh, MaterialOverride = mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, TopLevel = true, ExtraCullMargin = 100f, Name = "ShadowPolys" };
        AddChild(receivers);
    }

    public override void _Ready()
    {
        mat.SetShaderParameter("tex", vp.GetTexture());         // a viewport texture needs the viewport in the tree
        receivers.GlobalTransform = Transform3D.Identity;
    }

    public override void _Process(double delta)
    {
        if (frameCounter != Engine.GetProcessFrames()) { frameCounter = Engine.GetProcessFrames(); Active = 0; Polys = 0; }
        var camera = GetViewport().GetCamera3D();
        bool show = owner.Placed && owner.IsVisibleInTree() && camera != null && owner.Visible;
        receivers.Visible = show;
        if (!show) return;
        var feet = owner.GlobalPosition;
        var center = feet + new Vector3(0f, CharacterView.CharHeight * 0.5f, 0f);
        float dist = camera!.GlobalPosition.DistanceTo(center);
        if (dist > FarBlend + 1f) { receivers.Visible = false; return; }
        Active++;

        // the sun's ray, flattened no more than the engine allows: light_pos.y > -0.6 -> -0.6
        var l = -LocationLights.SunDir;
        if (l.Y > -0.6f) l.Y = -0.6f;
        l = l.Normalized();

        // the silhouette camera looks along the light: xc to the right, yc up in the picture, zc back to the light
        var zc = -l;
        var xc = Vector3.Up.Cross(zc);
        if (xc.LengthSquared() < 1e-6f) xc = Vector3.Right;
        xc = xc.Normalized();
        var yc = zc.Cross(xc).Normalized();
        cam.GlobalTransform = new Transform3D(new Basis(xc, yc, zc), center + zc * 5f);

        tick++;
        int interval = dist < 5f ? 1 : dist < 11f ? 2 : 3;
        if (tick % interval == 0) vp.RenderTargetUpdateMode = SubViewport.UpdateMode.Once;
        if (tick % 6 == 0) UpdateShading(feet, l, (float)delta * 6f);
        mat.SetShaderParameter("density", Debug ? 1f : Density * shading);

        // the polygons that receive the shadow: rebuilt when the character or the light moved
        if (builtAt.DistanceSquaredTo(feet) > 0.0004f || builtDir.DistanceSquaredTo(l) > 1e-5f || !hasSurface)
        {
            builtAt = feet; builtDir = l;
            Rebuild(center, l, xc, yc, camera.GlobalPosition);
        }
        Polys += mesh.GetSurfaceCount() > 0 ? mesh.SurfaceGetArrayLen(0) / 3 : 0;
    }

    /// <summary>shadow.cpp AddPoly: polygons facing the light (the camera and the light on the same side of the plane, the object on the light's side), inside the projector box.</summary>
    void Rebuild(Vector3 center, Vector3 l, Vector3 xc, Vector3 yc, Vector3 camPos)
    {
        mesh.ClearSurfaces();
        hasSurface = true;
        var grid = ReceiversOf?.Invoke();
        if (grid == null) return;
        var lightPos = center - l * 100f;
        grid.Query(center, Capture * 0.5f + 2.4f, tris, seen);
        var verts = new List<Vector3>(256);
        var uvs = new List<Vector2>(256);
        float half = Capture * 0.5f;
        var p = new Vector3[3];
        var uv = new Vector2[3];
        foreach (int t in tris)
        {
            if (verts.Count + 3 > MaxVerts) break;
            var (a, b, c) = grid.Tri(t);
            var n = (b - a).Cross(c - a);
            if (n.LengthSquared() < 1e-12f) continue;
            n = n.Normalized();
            float d = n.Dot(a);
            float dc = n.Dot(camPos) - d, dl = n.Dot(lightPos) - d, d0 = n.Dot(center) - d;
            if (dc * dl < 0f) continue;                       // seen from behind
            if (d0 * dl < 0f) continue;                       // the object is under it: a roof, not a floor
            // the projector box: x and y of the light view within +-half, depth along the light not beyond 6 m
            p[0] = a; p[1] = b; p[2] = c;
            int outX0 = 0, outX1 = 0, outY0 = 0, outY1 = 0, far = 0;
            for (int i = 0; i < 3; i++)
            {
                var r = p[i] - center;
                float px = r.Dot(xc), py = r.Dot(yc), depth = r.Dot(l);
                uv[i] = new Vector2(px / Capture + 0.5f, 0.5f - py / Capture);
                if (px < -half) outX0++; else if (px > half) outX1++;
                if (py < -half) outY0++; else if (py > half) outY1++;
                if (depth > 6f) far++;
            }
            if (outX0 == 3 || outX1 == 3 || outY0 == 3 || outY1 == 3 || far == 3) continue;
            for (int i = 0; i < 3; i++) { verts.Add(p[i]); uvs.Add(uv[i]); }
        }
        if (verts.Count == 0) return;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = verts.ToArray();
        arrays[(int)Mesh.ArrayType.TexUV] = uvs.ToArray();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
    }

    /// <summary>Ten rays from the body to the light: the part of the body in the sun; in the shade the shadow weakens to 0.2 (shading moves by one per second).</summary>
    void UpdateShading(Vector3 feet, Vector3 l, float dt)
    {
        var space = GetWorld3D()?.DirectSpaceState;
        if (space == null) return;
        float lit = 0f;
        for (int i = 0; i < 10; i++)
        {
            var from = feet + new Vector3(0f, CharacterView.CharHeight * 0.1f * i + 0.05f, 0f);
            var q = PhysicsRayQueryParameters3D.Create(from, from - l * 100f);
            q.HitBackFaces = false;                      // the walk patch lies a little under the drawn floor: its underside must not count
            if (space.IntersectRay(q).Count == 0) lit += 0.1f;
        }
        shading += (lit <= 0.5f ? -1f : 1f) * dt;
        shading = Math.Max(0.2f, Math.Max(lit, Math.Min(shading, 1f)));
    }
}
