using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using StormScript.Formats;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The engine's "location" class: static models of a location, its locators (labels of the loaded models), patches.
/// Coordinates stay in Direct3D space for the script (locator attributes); only rendering converts (z is negated).
/// </summary>
public partial class LocationView : Node3D
{
    readonly Interp it;
    public readonly Entity Ent;
    string modelsPath = "", texturesPath = "", lightPath = "", shadowPath = "";
    GmFile? lastModel;
    Node3D? lastNode;
    readonly Dictionary<string, Dictionary<string, GmFile.Label>> locators = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, float> groupRadius = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<(string, string), float> locRadius = new();
    Aabb bounds;
    bool hasBounds;
    public FlyCam? Cam;
    public PtcFile? Patch;
    static readonly HashSet<string> warned = new();

    public LocationView(Interp it, Entity ent) { this.it = it; Ent = ent; Name = "Location"; }

    static void Warn(string s) { if (warned.Add(s)) GD.Print("[location] not implemented: " + s); }

    string ModelFile(string name, string ext) => GameAssets.Resource("MODELS\\" + modelsPath.TrimEnd((char)92) + "\\" + name + ext);

    public V OnMessage(string fmt, V[] d)
    {
        int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
        string S(int i) => i < d.Length ? Conv.ToStr(d[i]) : "";
        switch (msg)
        {
            case 30140: modelsPath = S(1); return V.Int(1);
            case 30141: texturesPath = S(1); return V.Int(1);
            case 30142: lightPath = S(1).Trim((char)92, '/'); return V.Int(1);
            case 30143: shadowPath = S(1); return V.Int(1);
            case 30100: return V.Int(AddModel(S(1)) ? 1 : 0);                    // ADD_MODEL "lssl"
            case 30101: return V.Int(0);                                         // GET_MODEL: no model objects yet
            case 30110: PlaceLastModel(S(1), S(2)); return V.Int(1);             // MODEL_SET_POS
            case 30111: case 30112: case 30113: case 30114: return V.Int(1);
            case 30120:                                                          // SET_CHRS_PATCH: walkable surface
                try { Patch = PtcFile.Parse(File.ReadAllBytes(ModelFile(S(1), ".ptc"))); GD.Print($"[location] patch {S(1)}: {Patch.Tris.Length} triangles"); return V.Int(1); }
                catch (Exception e) { GD.PrintErr($"[location] patch {S(1)}: {e.Message}"); return V.Int(0); }
            case 30121: case 30123: return V.Int(1);
            case 30122: return V.Int(File.Exists(ModelFile(S(1), ".gm")) ? 1 : 0);    // SET_JMP_PATCH
            case 30125: groupRadius[S(1)] = Conv.ToFloat(d[2]); return V.Int(1);
            case 30126: locRadius[(S(1).ToLowerInvariant(), S(2).ToLowerInvariant())] = Conv.ToFloat(d[3]); return V.Int(1);
            case 30128: return V.Int(1);                                         // ADD_LIGHT
            case 30130: UpdateLocators(); return V.Int(1);
            case 30152:                                                          // SETCHRPOSITIONS: remember where everybody stands (for a save)
            {
                var store = Ent.Root.GetOrCreate("chrpos");
                foreach (var cv in CharacterView.All.Values)
                    if (cv.Placed && cv.Ent.Root.Get("index")?.Value is { } idx) store.GetOrCreate("c" + idx).Value = cv.PositionText();
                return V.Int(1);
            }
            case 30153:                                                          // TLPCHRPOSITIONS: put everybody back (after a load)
            {
                var store = Ent.Root.Get("chrpos");
                if (store != null)
                    foreach (var cv in CharacterView.All.Values)
                        if (cv.Ent.Root.Get("index")?.Value is { } idx && store.Get("c" + idx)?.Value is { } pos) cv.RestorePosition(pos);
                return V.Int(1);
            }
            case 30154: Ent.Root.Remove("chrpos"); return V.Int(1);              // CLRCHRPOSITIONS
            case 30150: case 30151: return V.Int(1);
            case 30155: return V.Int(0);                                         // EX_MSG ("AddFlys", "AddEagle", ...)
        }
        Warn($"message {msg} ({fmt})");
        return V.Int(0);
    }

    bool AddModel(string name)
    {
        string file = ModelFile(name, ".gm");
        if (!File.Exists(file)) { GD.PrintErr("[location] model not found: " + file); return false; }
        GmFile g;
        try { g = GmFile.Parse(File.ReadAllBytes(file)); }
        catch (Exception e) { GD.PrintErr($"[location] {name}: {e.Message}"); return false; }

        Color[]? cols = null;
        if (lightPath.Length > 0)
        {
            string colFile = ModelFile(name + "_" + lightPath, ".col");
            if (File.Exists(colFile))
            {
                var b = File.ReadAllBytes(colFile);
                cols = new Color[b.Length / 4];
                for (int i = 0; i < cols.Length; i++) cols[i] = new Color(b[i * 4 + 2] / 255f, b[i * 4 + 1] / 255f, b[i * 4] / 255f);
            }
        }
        GD.Print($"[location] model {name}: {g.Objects.Count} objects, {g.Labels.Count} labels, {g.TotalVertices} vertices, light={(cols != null)}");
        GD.Print($"[location] paths: textures='{texturesPath}' light='{lightPath}' shadow='{shadowPath}'");
        var node = ModelFactory.Build(g, name, texturesPath, cols, shadowPath.Length > 0 ? "lighting" + (char)92 + shadowPath.Trim((char)92) + (char)92 + modelsPath.Trim((char)92) : null);
        if (IsFoamModel(name)) node.Visible = false;       // shore/sea-bottom mesh for the sea shader's foam: not drawn on its own
        AddChild(node);
        // collision for the follow camera (it must not look through walls): triangle meshes of the visible parts, no characters
        if (node.Visible && !name.EndsWith("_l", StringComparison.OrdinalIgnoreCase))
            foreach (var c in node.GetChildren())
                if (c is MeshInstance3D { Visible: true } mi && mi.Mesh != null && mi.Mesh.GetSurfaceCount() > 0) mi.CreateTrimeshCollision();
        lastModel = g;
        lastNode = node;

        foreach (var l in g.Labels)
        {
            if (!locators.TryGetValue(l.Group, out var grp)) locators[l.Group] = grp = new(StringComparer.OrdinalIgnoreCase);
            grp[l.Name] = l;
        }
        var bb = ModelFactory.Bounds(node);
        if (bb.Size != Vector3.Zero) { bounds = hasBounds ? bounds.Merge(bb) : bb; hasBounds = true; }
        return true;
    }

    /// <summary>models.always.sb = "..._sb" with .foam = "1": the sea-bottom mesh of a harbour, drawn by the engine under the transparent sea - hidden here (our sea is opaque).</summary>
    bool IsFoamModel(string name)
    {
        var models = Ent.Root.Find("models", false);
        if (models == null) return false;
        foreach (var grp in new[] { "always", "day", "night", "back" })
        {
            var g = models.Find(grp, false);
            if (g == null) continue;
            for (int i = 0; i < g.Count; i++)
                if (g.ChildAt(i) is { } n && string.Equals(n.Value, name, StringComparison.OrdinalIgnoreCase) && n.Find("foam", false)?.Value == "1" && n.Name.Equals("sb", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public GmFile.Label? FindLocator(string group, string name)
        => locators.TryGetValue(group, out var g) && g.TryGetValue(name, out var l) ? l : null;

    void PlaceLastModel(string group, string name)
    {
        if (lastNode == null || !locators.TryGetValue(group, out var grp) || !grp.TryGetValue(name, out var l)) return;
        lastNode.Position = new Vector3(l.X, l.Y, -l.Z);
    }

    /// <summary>Copies all locators into the location's attributes: locators.group.name.{radius,x,y,z,vx,vy,vz}.</summary>
    void UpdateLocators()
    {
        var root = Ent.Root;
        root.Remove("locators");
        var loc = root.GetOrCreate("locators");
        string F(float v) => Conv.ToStr(V.Float(v));
        foreach (var (gname, grp) in locators)
        {
            var g = loc.GetOrCreate(gname);
            float gr = groupRadius.TryGetValue(gname, out var r0) ? r0 : 1f;
            foreach (var (lname, l) in grp)
            {
                var n = g.GetOrCreate(lname);
                float r = locRadius.TryGetValue((gname.ToLowerInvariant(), lname.ToLowerInvariant()), out var r1) ? r1 : gr;
                n.GetOrCreate("radius").Value = F(r);
                n.GetOrCreate("x").Value = F(l.M[12]);
                n.GetOrCreate("y").Value = F(l.M[13]);
                n.GetOrCreate("z").Value = F(l.M[14]);
                foreach (var (an, o) in new[] { ("vx", 0), ("vy", 4), ("vz", 8) })
                {
                    var v = n.GetOrCreate(an);
                    v.GetOrCreate("x").Value = F(l.M[o]);
                    v.GetOrCreate("y").Value = F(l.M[o + 1]);
                    v.GetOrCreate("z").Value = F(l.M[o + 2]);
                }
            }
        }
        GD.Print($"[location] {locators.Sum(g => g.Value.Count)} locators in {locators.Count} groups");
        FitCamera();
    }

    void FitCamera()
    {
        if (!hasBounds || Cam != null) return;
        Cam = new FlyCam { Name = "FlyCam", Far = 5000, Near = 0.05f };
        AddChild(Cam);
        var c = bounds.GetCenter();
        float dist = Math.Max(bounds.Size.Length() * 0.45f, 3f);
        Cam.Position = c + new Vector3(0.5f, 0.45f, 0.9f).Normalized() * dist;
        Cam.LookAt(c);
        Cam.MakeCurrent();
    }
}

/// <summary>Debug camera: WASD/QE move, hold right mouse button to look, Shift = fast.</summary>
public partial class FlyCam : Camera3D
{
    float yaw, pitch;
    bool look;
    public bool Enabled = true;     // off while the follow camera is in charge

    public override void _Ready()
    {
        var e = Rotation;
        yaw = e.Y; pitch = e.X;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (!Enabled) return;
        if (e is InputEventMouseButton { ButtonIndex: MouseButton.Right } mb) look = mb.Pressed;
        else if (e is InputEventMouseMotion mm && look)
        {
            yaw -= mm.Relative.X * 0.004f;
            pitch = Math.Clamp(pitch - mm.Relative.Y * 0.004f, -1.5f, 1.5f);
            Rotation = new Vector3(pitch, yaw, 0);
        }
    }

    public override void _Process(double delta)
    {
        if (!Enabled) return;
        var dir = Vector3.Zero;
        if (Input.IsPhysicalKeyPressed(Key.W)) dir -= GlobalTransform.Basis.Z;
        if (Input.IsPhysicalKeyPressed(Key.S)) dir += GlobalTransform.Basis.Z;
        if (Input.IsPhysicalKeyPressed(Key.A)) dir -= GlobalTransform.Basis.X;
        if (Input.IsPhysicalKeyPressed(Key.D)) dir += GlobalTransform.Basis.X;
        if (Input.IsPhysicalKeyPressed(Key.E)) dir += Vector3.Up;
        if (Input.IsPhysicalKeyPressed(Key.Q)) dir -= Vector3.Up;
        float speed = Input.IsPhysicalKeyPressed(Key.Shift) ? 20f : 5f;
        GlobalPosition += dir * speed * (float)delta;
    }
}
