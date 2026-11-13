using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using StormScript.Formats;

namespace SeaDogs;

/// <summary>
/// A skinned character: body (and head) .gm models on a skeleton from a .an animation file, actions from the .ani file.
/// The .an stores bind-pose joint offsets and per-frame quaternions in Direct3D space; z is negated on the way to Godot,
/// which turns a quaternion (x,y,z,w) into (-x,-y,z,w).
/// </summary>
public partial class CharacterRig : Node3D
{
    static readonly Dictionary<string, (AnFile an, AniActions ani)?> animCache = new(StringComparer.OrdinalIgnoreCase);

    readonly Skeleton3D skel = new() { Name = "Skeleton" };
    readonly Skin skin = new();
    AnFile an = null!;
    AniActions ani = null!;
    AniActions.Action? cur;
    float time;
    string curName = "";
    bool loop = true;
    public float Fps => an.Fps;
    public float SpeedMul = 1f;         // the character speed factor k (character.cpp Move: animation speed x k)
    public float TimeScale = 1f;        // 0 while a dialog is open (the script calls SetTimeScale)

    /// <summary>"Walk speed" (m/s) of an action from the .ani file.</summary>
    public float ActionSpeed(string action, float fallback)
        => ani.Actions.TryGetValue(action, out var a) && a.Data.TryGetValue("Walk speed", out var v)
           && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? Math.Abs(f) : fallback;

    public float ActionTurnSpeed(string action, float fallback)
        => ani.Actions.TryGetValue(action, out var a) && a.Data.TryGetValue("Turn speed", out var v)
           && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : fallback;
    public string ActionName => curName;

    /// <summary>False for a one-shot action ("loop = false" in the .ani): it stops on its last frame and the engine reports its end.</summary>
    public bool ActionLoops(string action) => !ani.Actions.TryGetValue(action, out var a) || a.Loop;

    /// <summary>Play time (seconds) of an action and the moment of its strike: the "Attack*" event of the .ani (frame - start) / (file fps * speed). Null if unknown.</summary>
    public (float hit, float duration)? SwingTiming(string action)
    {
        if (!ani.Actions.TryGetValue(action, out var a) || a.End <= a.Start) return null;
        float rate = an.Fps * Math.Abs(a.Speed <= 0 ? 1f : a.Speed);
        float dur = (a.End - a.Start) / rate, hit = dur * 0.4f;
        foreach (var (name, frame, rev) in a.Events)
            if (!rev && name.StartsWith("Attack", StringComparison.OrdinalIgnoreCase)) { hit = (frame - a.Start) / rate; break; }
        return (hit, dur);
    }

    /// <summary>Real seconds the CURRENT playback (with the live speed factor and time scale) still needs to reach animation time <paramref name="animFrames"/>
    /// (frames from the action start). Negative once passed; a stopped clock (dialog) gives a large value.</summary>
    float SecondsTo(float animFrames)
    {
        float rate = TimeScale * SpeedMul * an.Fps * Math.Abs(cur == null || cur.Speed <= 0 ? 1f : cur.Speed);
        return rate > 1e-4f ? (animFrames - time) / rate : 999f;
    }

    /// <summary>Seconds until the strike frame (the .ani event "Attack*") of the playing action; null if the action has none.</summary>
    public float? SecondsToStrike()
    {
        if (cur == null) return null;
        foreach (var (name, frame, rev) in cur.Events)
            if (!rev && name.StartsWith("Attack", StringComparison.OrdinalIgnoreCase)) return SecondsTo(frame - cur.Start);
        return SecondsTo(Math.Max(cur.End - cur.Start, 1) * 0.4f);
    }

    /// <summary>Seconds until the playing action has reached its last frame.</summary>
    public float SecondsToEnd() => cur == null ? 0f : Math.Max(0f, SecondsTo(Math.Max(cur.End - cur.Start, 1)));

    /// <summary>Playback position of the current action in frames from its start (for the melee log).</summary>
    public float AnimFrame => time;

    static float Fl(string? v, float def) => v != null && float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f) ? f : def;

    /// <summary>The engine's <c>UpdateActionMoveData</c>: signed "Walk speed" (0 if absent), "Blend time" (0.2), "Turn speed" (the file's, 2.0 if absent).
    /// Null if the action does not exist.</summary>
    public (float speed, float blend, float turn)? MoveData(string action)
    {
        if (!ani.Actions.TryGetValue(action, out var a)) return null;
        float fileTurn = Fl(ani.GlobalData.GetValueOrDefault("Turn speed"), 2f);
        a.Data.TryGetValue("Walk speed", out var ws); a.Data.TryGetValue("Blend time", out var bt); a.Data.TryGetValue("Turn speed", out var ts);
        return (Fl(ws, 0f), Math.Max(0f, Fl(bt, 0.2f)), Fl(ts, fileTurn));
    }

    /// <summary>The file-wide "Turn speed" (turning in place, idle).</summary>
    public float IdleTurnSpeed => Fl(ani.GlobalData.GetValueOrDefault("Turn speed"), 2f);

    static (AnFile, AniActions)? LoadAnim(string anim)
    {
        if (animCache.TryGetValue(anim, out var c)) return c;
        (AnFile, AniActions)? res = null;
        try
        {
            string aniPath = GameAssets.Resource("animation\\" + anim + ".ani");
            AniActions ani = File.Exists(aniPath) ? AniActions.Parse(StormScript.Lexer.ReadFile(aniPath)) : new AniActions();
            string anName = ani.AnimationFile.Length > 0 ? ani.AnimationFile : anim + ".an";
            string anPath = GameAssets.Resource("animation\\" + anName);
            if (File.Exists(anPath)) res = (AnFile.Parse(File.ReadAllBytes(anPath)), ani);
        }
        catch (Exception e) { GD.PrintErr($"[rig] animation {anim}: {e.Message}"); }
        animCache[anim] = res;
        return res;
    }

    /// <summary>Returns null (like the engine returning 0 for SETMODEL) if the model or animation cannot be loaded.</summary>
    /// <param name="folder">model folder under MODELS (the sailors on deck use the low-poly "Lowcharacters")</param>
    public static CharacterRig? Create(string model, string anim, string? head, string folder = "Characters")
    {
        string body = GameAssets.Resource("MODELS\\" + folder + "\\" + model + ".gm");
        if (!File.Exists(body)) { GD.PrintErr("[rig] model not found: " + body); return null; }
        var a = LoadAnim(anim);
        if (a == null) { GD.PrintErr("[rig] animation not found: " + anim); return null; }
        var rig = new CharacterRig { Name = "Rig" };
        rig.an = a.Value.Item1;
        rig.ani = a.Value.Item2;
        rig.BuildSkeleton();
        try
        {
            var gmBody = GmFile.Parse(File.ReadAllBytes(body));
            rig.labels = gmBody.Labels;
            rig.AddMesh(gmBody, model);
            if (System.Environment.GetEnvironmentVariable("SEADOGS_RIGLOG") != null)
                foreach (var l in gmBody.Labels) GD.Print($"[rig] {model} label '{l.Group}/{l.Name}' bone {l.Bone} at ({l.X:0.00},{l.Y:0.00},{l.Z:0.00})");
            // the head models (Heads\h_*.gm) are modelled around the head joint and belong to the dialog window's portrait; the body model has its own head
            if (!string.IsNullOrEmpty(head) && System.Environment.GetEnvironmentVariable("SEADOGS_WORLDHEADS") != null)
            {
                string hp = GameAssets.Resource("MODELS\\Heads\\" + head + ".gm");
                if (File.Exists(hp)) rig.AddMesh(GmFile.Parse(File.ReadAllBytes(hp)), head);
            }
        }
        catch (Exception e) { GD.PrintErr($"[rig] {model}: {e.Message}"); return null; }
        rig.Play(rig.ani.Actions.ContainsKey("idle_2") ? "idle_2" : "idle_1", true);
        return rig;
    }

    List<GmFile.Label> labels = new();
    Transform3D[] restGlobal = [];
    readonly List<Node3D> equipment = new();

    /// <summary>
    /// Weapons on the body: the model carries labels (Saber_hand, Saber_belt, Gun_Hand, Gun_Belt) with the bone they hang on and a matrix in the model's rest pose.
    /// The sword is in the hand while the fight mode is on and on the belt otherwise; the pistol stays on the belt.
    /// </summary>
    public void Equip(string? blade, string? gun, bool drawn)
    {
        foreach (var n in equipment) n.QueueFree();
        equipment.Clear();
        if (!string.IsNullOrEmpty(blade)) Attach(blade, drawn ? "Saber_hand" : "Saber_belt");
        if (!string.IsNullOrEmpty(gun)) Attach(gun, "Gun_Belt");
    }

    BoneAttachment3D? Attach(string itemModel, string label)
    {
        var l = labels.Find(x => x.Name.Equals(label, StringComparison.OrdinalIgnoreCase));
        string path = GameAssets.Resource("MODELS\\Items\\" + itemModel + ".gm");
        if (l == null || l.Bone < 0 || l.Bone >= restGlobal.Length || !File.Exists(path)) return null;
        try
        {
            var item = ModelFactory.Build(GmFile.Parse(File.ReadAllBytes(path)), itemModel, "items", null);
            // label matrix (D3D, row vectors, left-handed) -> Godot: mirror z
            var m = l.M;
            var basis = new Basis(new Vector3(m[0], m[1], -m[2]), new Vector3(m[4], m[5], -m[6]), new Vector3(-m[8], -m[9], m[10]));
            var world = new Transform3D(basis.Orthonormalized(), new Vector3(m[12], m[13], -m[14]));
            var att = new BoneAttachment3D { Name = "Eq_" + label };
            skel.AddChild(att);
            att.BoneIdx = l.Bone;
            item.Transform = restGlobal[l.Bone].AffineInverse() * world;
            att.AddChild(item);
            equipment.Add(att);
            return att;
        }
        catch (Exception e) { GD.PrintErr($"[rig] item {itemModel}: {e.Message}"); return null; }
    }

    BoneAttachment3D? prop;
    /// <summary>A held item (the potion bottle while drinking) on the first of the labels the model has.</summary>
    public void ShowProp(string itemModel, params string[] labelNames)
    {
        HideProp();
        foreach (var n in labelNames) if ((prop = Attach(itemModel, n)) != null) return;
    }
    public void HideProp()
    {
        if (prop != null && GodotObject.IsInstanceValid(prop)) { equipment.Remove(prop); prop.QueueFree(); }
        prop = null;
    }

    void BuildSkeleton()
    {
        int n = an.NJoints;
        for (int j = 0; j < n; j++) skel.AddBone("b" + j);
        var global = new Transform3D[n];
        for (int j = 0; j < n; j++)
        {
            skel.SetBoneParent(j, an.Parents[j]);
            // The meshes are modelled in the pose of frame 0 (verified numerically: bone positions of frame 0 match the vertex
            // clusters, a zero-rotation skeleton would be a T-pose). Rest = frame 0, skin binds = its inverse.
            int a0 = (j * an.NFrames) * 4;
            var q0 = new Quaternion(-an.Quat[a0], -an.Quat[a0 + 1], an.Quat[a0 + 2], an.Quat[a0 + 3]).Normalized();
            var local = new Transform3D(new Basis(q0), new Vector3(an.JointPos[j * 3], an.JointPos[j * 3 + 1], -an.JointPos[j * 3 + 2]));
            skel.SetBoneRest(j, local);
            global[j] = an.Parents[j] >= 0 ? global[an.Parents[j]] * local : local;
            skin.AddBind(j, global[j].AffineInverse());
        }
        skel.ResetBonePoses();
        restGlobal = global;
        AddChild(skel);
    }

    void AddMesh(GmFile g, string name)
    {
        var matCache = new Dictionary<int, ShaderMaterial>();
        for (int oi = 0; oi < g.Objects.Count; oi++)
        {
            var m = g.ExtractObject(g.Objects[oi]);
            if (m == null || m.Bones == null) continue;
            int n = m.Pos.Length / 3;
            var verts = new Vector3[n];
            var norms = new Vector3[n];
            var uvs = new Vector2[n];
            var bones = new int[n * 4];
            var weights = new float[n * 4];
            for (int i = 0; i < n; i++)
            {
                verts[i] = new Vector3(m.Pos[i * 3], m.Pos[i * 3 + 1], -m.Pos[i * 3 + 2]);
                norms[i] = new Vector3(m.Normal[i * 3], m.Normal[i * 3 + 1], -m.Normal[i * 3 + 2]);
                uvs[i] = new Vector2(m.Uv[i * 2], m.Uv[i * 2 + 1]);
                bones[i * 4] = Math.Min(m.Bones[i * 2], an.NJoints - 1);
                bones[i * 4 + 1] = Math.Min(m.Bones[i * 2 + 1], an.NJoints - 1);
                weights[i * 4] = m.Weights![i * 2];
                weights[i * 4 + 1] = m.Weights[i * 2 + 1];
            }
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = verts;
            arrays[(int)Mesh.ArrayType.Normal] = norms;
            arrays[(int)Mesh.ArrayType.TexUV] = uvs;
            arrays[(int)Mesh.ArrayType.Bones] = bones;
            arrays[(int)Mesh.ArrayType.Weights] = weights;
            arrays[(int)Mesh.ArrayType.Index] = m.Index;
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            int mi = g.Objects[oi].Material;
            if (!matCache.TryGetValue(mi, out var mat))
            {
                matCache[mi] = mat = CharShading.Make(ModelFactory.MakeMaterial(g, mi, "characters", false));
                Shading.Add(mat);
            }
            mesh.SurfaceSetMaterial(0, mat);
            if (System.Environment.GetEnvironmentVariable("SEADOGS_RIGLOG") != null)
            {
                var bb = mesh.GetAabb();
                GD.Print($"[rig] {name}#{oi} verts={n} mat={mi} tex={(mi < g.Materials.Count ? g.Materials[mi].Textures[0] : "?")} aabb y {bb.Position.Y:0.00}..{bb.End.Y:0.00} x {bb.Position.X:0.00}..{bb.End.X:0.00} z {bb.Position.Z:0.00}..{bb.End.Z:0.00}");
            }
            var inst = new MeshInstance3D { Mesh = mesh, Name = name + "_" + oi, Skin = skin };
            skel.AddChild(inst);
            inst.Skeleton = new NodePath("..");
        }
    }

    /// <summary>
    /// The silhouette of the model for the character's shadow: every mesh gets a twin on the same skeleton that only the shadow camera sees (flat black),
    /// the real meshes move to <paramref name="bodyLayer"/>. Returns the number of twins.
    /// </summary>
    public int MakeShadowCasters(uint casterLayer, uint bodyLayer)
    {
        var black = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = Colors.Black, CullMode = BaseMaterial3D.CullModeEnum.Disabled };
        int n = 0;
        foreach (var c in skel.GetChildren())
        {
            if (c is not MeshInstance3D { Mesh: { } mesh } mi || mi.Layers == casterLayer) continue;
            mi.Layers = bodyLayer;
            var twin = new MeshInstance3D { Mesh = mesh, Skin = skin, Layers = casterLayer, MaterialOverride = black, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off, Name = mi.Name + "_sil" };
            skel.AddChild(twin);
            twin.Skeleton = new NodePath("..");
            n++;
        }
        return n;
    }

    /// <summary>True when a non-looping action has played to its end.</summary>
    public bool Finished => cur != null && !loop && time >= Math.Max(cur.End - cur.Start, 1);

    /// <summary>A one-shot character action (draw, blow, reaction, shot) is still playing: the character does not move or turn meanwhile.</summary>
    public bool ActionLocked(bool busyState) => busyState && !Finished;

    // Blend into a fight action (Character::SetAction with the action's "Blend time", 0.2 s in man.ani): the new action plays from its first frame at once while the
    // pose of the previous action fades out, so a wind-up is seen as a motion instead of a jump to the first frame.
    Quaternion[]? lastQ; Vector3 lastRoot; bool havePose;
    Quaternion[]? fromQ; Vector3 fromRoot; float blendLeft, blendDur;
    static bool IsFightAction(string n) => n.StartsWith("attack", StringComparison.OrdinalIgnoreCase) || n.StartsWith("hit", StringComparison.OrdinalIgnoreCase)
        || n.StartsWith("block", StringComparison.OrdinalIgnoreCase) || n.StartsWith("recoil", StringComparison.OrdinalIgnoreCase) || n.StartsWith("shot", StringComparison.OrdinalIgnoreCase);

    public bool Play(string action, bool looping, bool restart = false)
    {
        if (!ani.Actions.TryGetValue(action, out var a) || a.End <= a.Start) return false;
        if (cur == a && !restart) return true;
        float bt = IsFightAction(action) && havePose && IsVisibleInTree() ? Math.Max(0f, Fl(a.Data.GetValueOrDefault("Blend time"), 0.2f)) : 0f;
        if (bt > 0.01f) { fromQ = (Quaternion[])lastQ!.Clone(); fromRoot = lastRoot; blendLeft = blendDur = bt; } else { fromQ = null; blendLeft = 0f; }
        cur = a; curName = action; loop = looping; time = 0;
        Pose(a.Start);
        return true;
    }

    /// <summary>Per-character lighting (weather sun/ambient + the nearest location lights), see LocationLights.cs.</summary>
    public readonly CharShading.State Shading = new();

    public override void _Process(double delta)
    {
        if (IsVisibleInTree()) Shading.Update(GlobalPosition);
        if (cur == null) return;
        float len = Math.Max(cur.End - cur.Start, 1);
        float prev = time;
        time += (float)delta * TimeScale * SpeedMul * an.Fps * Math.Abs(cur.Speed <= 0 ? 1f : cur.Speed);
        bool wrapped = false;
        if (time >= len)
        {
            if (loop) { time %= len; wrapped = true; } else time = len;
        }
        if (cur.Events.Count > 0 && time != prev)
            foreach (var (name, frame, rev) in cur.Events)
            {
                if (rev != cur.Reverse) continue;
                float at = cur.Reverse ? cur.End - frame : frame - cur.Start;      // the event's place on the playback time line
                if ((wrapped ? at > prev || at <= time : at > prev && at <= time) && Events.Count < 16) Events.Add(name);
            }
        if (blendLeft > 0f) blendLeft = Math.Max(0f, blendLeft - (float)delta * TimeScale);
        Pose(cur.Reverse ? cur.End - time : cur.Start + time);
    }

    /// <summary>Animation events (.ani "event = ...") passed since the owner last drained the list.</summary>
    public readonly List<string> Events = new();

    static Quaternion Unit(Quaternion q) => q.LengthSquared() > 1e-12f ? q.Normalized() : Quaternion.Identity;

    void Pose(float frame)
    {
        frame = Math.Clamp(frame, 0, an.NFrames - 1);
        int f0 = (int)frame, f1 = Math.Min(f0 + 1, an.NFrames - 1);
        float k = frame - f0;
        int nf = an.NFrames;
        lastQ ??= new Quaternion[an.NJoints];
        float bk = blendLeft > 0f && fromQ != null ? 1f - blendLeft / blendDur : 1f;
        bk = bk * bk * (3f - 2f * bk);
        for (int j = 0; j < an.NJoints; j++)
        {
            int a = (j * nf + f0) * 4, b = (j * nf + f1) * 4;
            var q0 = Unit(new Quaternion(-an.Quat[a], -an.Quat[a + 1], an.Quat[a + 2], an.Quat[a + 3]));       // Rat.an stores quaternions that are not exactly unit length
            var q1 = Unit(new Quaternion(-an.Quat[b], -an.Quat[b + 1], an.Quat[b + 2], an.Quat[b + 3]));
            var q = q0.Slerp(q1, k).Normalized();
            if (bk < 1f) q = fromQ![j].Slerp(q, bk).Normalized();
            lastQ[j] = q;
            skel.SetBonePoseRotation(j, q);
            if (an.Parents[j] < 0)
            {
                var p0 = new Vector3(an.RootPos[f0 * 3], an.RootPos[f0 * 3 + 1], -an.RootPos[f0 * 3 + 2]);
                var p1 = new Vector3(an.RootPos[f1 * 3], an.RootPos[f1 * 3 + 1], -an.RootPos[f1 * 3 + 2]);
                var p = p0.Lerp(p1, k);
                if (bk < 1f) p = fromRoot.Lerp(p, bk);
                lastRoot = p;
                skel.SetBonePosePosition(j, p);
            }
        }
        havePose = true;
    }
}
