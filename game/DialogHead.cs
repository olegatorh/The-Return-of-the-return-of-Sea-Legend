using System;
using System.Linq;
using Godot;

namespace SeaDogs;

/// <summary>
/// The talking head of the dialog window (legacy_dialog.cpp UpdateHeadModel / DrawHeadModel / SetAction): the model MODELS\Heads\&lt;headModel&gt;.gm with its own animation
/// animation\Heads\&lt;headModel&gt;.ani, drawn in a 115 x 157 window (at 31, 28 of the 640 x 480 dialog) by a camera 4 units away with a very narrow view angle
/// (PI/2 - 1.49 rad, nearly orthographic) and one directional light (-1, -1, 2). The head turns slightly to the side (rotation x 0.1, y PI - 0.1) and sits 0.025 above its centre.
/// The action is "dialog_idle", "dialog_all" while the NPC's voice plays; "&lt;action&gt;_&lt;mood&gt;" when the mood is not "normal" (smile, angry, afraid).
/// It lives in a SubViewport with its own World3D, so the location behind the dialog never lights or hides it.
/// </summary>
public sealed partial class DialogHead : Control
{
    public const float W = 115f, H = 157f;       // size of the head window in dialog units (the original's 115 x 157 pixels at 640 x 480)
    const float Scale = 4f;                      // render resolution of the head view per dialog unit (the dialog is scaled up with the window)
    const float ViewAngle = 1.5707964f - 1.49f;  // PId2 - 1.49: the projection angle of the original (radians)

    readonly SubViewport vp;
    readonly Camera3D cam;
    readonly TextureRect view;
    CharacterRig? rig;
    string model = "", mood = "normal", action = "";
    bool speaking;

    public bool HasHead => rig != null;
    public string Action => rig?.ActionName ?? "";

    public DialogHead()
    {
        Name = "Head";
        Size = new Vector2(W, H);
        MouseFilter = MouseFilterEnum.Ignore;
        ClipContents = true;

        vp = new SubViewport
        {
            Size = new Vector2I((int)(W * Scale), (int)(H * Scale)), TransparentBg = true, OwnWorld3D = true, Msaa3D = Viewport.Msaa.Msaa4X,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always, HandleInputLocally = false, GuiDisableInput = true, AudioListenerEnable3D = false,
        };
        AddChild(vp);

        var env = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(0, 0, 0, 0), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = new Color(0.62f, 0.6f, 0.58f), AmbientLightEnergy = 1f };
        vp.AddChild(new WorldEnvironment { Environment = env });

        // camera at the origin looking at the head 4 units away; the model space is mirrored in z (see ModelFactory), so the head sits at z = -4
        cam = new Camera3D { Fov = Mathf.RadToDeg(ViewAngle) * 1.12f, Near = 1f, Far = 10f, Current = true, KeepAspect = Camera3D.KeepAspectEnum.Width };
        vp.AddChild(cam);
        // DirectionalLight (-1, -1, 2) of the original, z mirrored as the models are
        var sun = new DirectionalLight3D { LightColor = new Color(1f, 0.97f, 0.92f), LightEnergy = 0.9f, ShadowEnabled = false };
        vp.AddChild(sun);
        sun.LookAt(sun.Position + new Vector3(-1f, -1f, -2f), Vector3.Up);

        view = new TextureRect { Size = new Vector2(W, H), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.Scale, MouseFilter = MouseFilterEnum.Ignore, TextureFilter = TextureFilterEnum.Linear };
        AddChild(view);
        view.Texture = vp.GetTexture();
    }

    /// <summary>The head model of the speaker (the script's Dialog.headModel, "" = none). The same name again keeps the running head.</summary>
    public void SetHead(string? name)
    {
        name = (name ?? "").Trim();
        if (name.Length > 0 && name.EndsWith(".gm", StringComparison.OrdinalIgnoreCase)) name = name[..^3];
        if (string.Equals(name, model, StringComparison.OrdinalIgnoreCase) && (rig != null || name.Length == 0)) return;
        model = name;
        rig?.QueueFree(); rig = null;
        action = "";
        view.Visible = false;
        if (name.Length == 0) return;
        var r = CharacterRig.Create(name, "Heads\\" + name, null, "Heads");
        if (r == null) { GD.Print($"[dialog] no head model '{name}'"); return; }
        rig = r;
        PoseHead(r);
        vp.AddChild(r);
        MakeLit(r);
        view.Visible = true;
        Apply();
        GD.Print($"[dialog] head '{name}' action {rig.ActionName}");
    }

    /// <summary>mtx = translate(0, 0.025, 0) * rotation(0.1, PI - 0.1, 0) * translate(0, 0, 4) of the original, in this port's z-mirrored space.</summary>
    static void PoseHead(CharacterRig r)
    {
        var rot = new Basis(Vector3.Up, -(Mathf.Pi - 0.1f)) * new Basis(Vector3.Right, -0.1f);
        r.Basis = rot;
        r.Position = rot * new Vector3(0f, 0.025f, 0f) + new Vector3(0f, 0f, -4f);
    }

    /// <summary>The character shader takes the weather's light: in the dialog window the head gets its own light instead (lit standard materials of this viewport).</summary>
    static void MakeLit(Node root)
    {
        foreach (var n in root.FindChildren("*", "MeshInstance3D", true, false))
        {
            var mi = (MeshInstance3D)n;
            if (mi.Mesh == null) continue;
            for (int s = 0; s < mi.Mesh.GetSurfaceCount(); s++)
            {
                if (mi.Mesh.SurfaceGetMaterial(s) is not ShaderMaterial sm) continue;
                var std = new StandardMaterial3D { CullMode = BaseMaterial3D.CullModeEnum.Disabled, Roughness = 1f, MetallicSpecular = 0.15f, TextureFilter = BaseMaterial3D.TextureFilterEnum.LinearWithMipmapsAnisotropic };
                if (sm.GetShaderParameter("tex").Obj is Texture2D t) std.AlbedoTexture = t;
                if ((float)sm.GetShaderParameter("scissor") >= 0f) { std.Transparency = BaseMaterial3D.TransparencyEnum.AlphaScissor; std.AlphaScissorThreshold = 0.5f; }
                mi.SetSurfaceOverrideMaterial(s, std);
            }
        }
    }

    public void SetMood(string? m) { m = string.IsNullOrWhiteSpace(m) ? "normal" : m.Trim().ToLowerInvariant(); if (m != mood) { mood = m; Apply(); } }

    /// <summary>The NPC's voice plays: "dialog_all" (talking); when it ends back to "dialog_idle".</summary>
    public void SetSpeaking(bool on) { if (on != speaking) { speaking = on; Apply(); } }

    void Apply()
    {
        if (rig == null) return;
        string baseName = speaking ? "dialog_all" : "dialog_idle";
        string want = mood == "normal" ? baseName : baseName + "_" + mood;
        if (want == action) return;
        if (!rig.Play(want, true) && !rig.Play(baseName, true)) return;
        action = want;
    }

    public override void _Process(double delta)
    {
        // the head's own frame: keep the camera aspect equal to the window's (115 : 157)
        cam.Fov = Mathf.RadToDeg(ViewAngle) * 1.12f;
    }
}
