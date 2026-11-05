using Godot;

namespace SeaDogs;

/// <summary>
/// Land combat feedback added by the port (the original land interface shows health only for the hero and the officers): after a blow of the hero the target gets a
/// health bar above its head for a few seconds and the damage dealt rises from it as a number ("блок" when the blow was blocked).
/// </summary>
public static partial class HitFx
{
    public static void Show(Node3D target, float damage, bool blocked, float hp, float hpMax)
    {
        var bar = target.GetNodeOrNull<HitBar>("HitBar");
        if (bar == null) { bar = new HitBar { Name = "HitBar" }; target.AddChild(bar); }
        bar.Set(hp / hpMax);
        var l = new Label3D
        {
            Text = blocked ? "блок" : damage > 0.5f ? "-" + Mathf.RoundToInt(damage) : "0",
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, PixelSize = 0.006f, FontSize = 48, OutlineSize = 12, NoDepthTest = true,
            Modulate = blocked ? new Color(0.7f, 0.85f, 1f) : new Color(1f, 0.35f, 0.25f), Position = new Vector3(0, 2.15f, 0), RenderPriority = 5, OutlineRenderPriority = 4,
        };
        target.AddChild(l);
        var tw = l.CreateTween();
        tw.SetParallel(true);
        tw.TweenProperty(l, "position:y", 3.0f, 1.1f);
        tw.TweenProperty(l, "modulate:a", 0f, 1.1f).SetDelay(0.4f);
        tw.Chain().TweenCallback(Callable.From(l.QueueFree));
    }

    public sealed partial class HitBar : Node3D
    {
        const float W = 0.9f, H = 0.1f;
        readonly MeshInstance3D back, fill;
        float left;

        public HitBar()
        {
            Position = new Vector3(0, 2.0f, 0);
            back = Quad(new Color(0.05f, 0.05f, 0.05f, 0.85f), 1);
            fill = Quad(new Color(0.85f, 0.12f, 0.1f), 2);
            AddChild(back); AddChild(fill);
        }

        static MeshInstance3D Quad(Color c, int prio) => new()
        {
            Mesh = new QuadMesh { Size = new Vector2(W, H) },
            MaterialOverride = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled, AlbedoColor = c, NoDepthTest = true, RenderPriority = prio, Transparency = BaseMaterial3D.TransparencyEnum.Alpha },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };

        public void Set(float k)
        {
            k = Mathf.Clamp(k, 0f, 1f);
            var q = new QuadMesh { Size = new Vector2(Mathf.Max(0.001f, W * k), H * 0.7f), CenterOffset = new Vector3(-(W - W * k) / 2f, 0, 0) };
            fill.Mesh = q;
            left = 3.5f;
            Visible = true;
        }

        public override void _Process(double delta)
        {
            left -= (float)delta;
            if (left <= 0f) Visible = false;
        }
    }
}
