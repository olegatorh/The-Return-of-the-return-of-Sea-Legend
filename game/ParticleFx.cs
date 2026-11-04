using System;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// Stand-ins for the engine's script-driven particle systems (CreateParticleSystem(name, x, y, z, ...), class "particles" message 10000) and the sea's BallSplash:
/// short bursts of smoke, fire and water built from CPU particles with a soft round sprite. Positions are engine coordinates (Godot z = -z).
/// </summary>
public static class ParticleFx
{
    static GradientTexture2D? soft;
    static GradientTexture2D Soft() => soft ??= new GradientTexture2D
    {
        Width = 64, Height = 64, Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(0.5f, 0.5f), FillTo = new Vector2(1f, 0.5f),
        Gradient = new Gradient { Colors = new[] { new Color(1, 1, 1, 1), new Color(1, 1, 1, 0) }, Offsets = new[] { 0f, 1f } },
    };

    /// <summary>name: blast, cancloud, bomb_smoke, ship_onfire, ... (script names); anything unknown becomes a small grey puff.</summary>
    public static void Spawn(Node parent, string name, Vector3 enginePos)
    {
        var n = name.ToLowerInvariant();
        Color c0, c1; int amount; float life, speed, size, up, spread; bool fire = false;
        switch (n)
        {
            case "blast": case "blast_inv": c0 = new Color(1f, 0.75f, 0.3f, 0.9f); c1 = new Color(0.25f, 0.22f, 0.2f, 0f); amount = 18; life = 1.4f; speed = 9f; size = 4.5f; up = 4f; spread = 70f; fire = true; break;
            case "cancloud": c0 = new Color(0.95f, 0.95f, 0.95f, 0.7f); c1 = new Color(0.8f, 0.8f, 0.8f, 0f); amount = 10; life = 2.4f; speed = 3f; size = 4f; up = 1.5f; spread = 40f; break;
            case "bomb_smoke": c0 = new Color(0.25f, 0.25f, 0.25f, 0.6f); c1 = new Color(0.2f, 0.2f, 0.2f, 0f); amount = 6; life = 1.6f; speed = 1f; size = 1.2f; up = 0.5f; spread = 30f; break;
            case "ship_onfire": c0 = new Color(1f, 0.55f, 0.1f, 0.9f); c1 = new Color(0.2f, 0.18f, 0.16f, 0f); amount = 14; life = 1.8f; speed = 2f; size = 3f; up = 5f; spread = 25f; fire = true; break;
            default: c0 = new Color(0.7f, 0.7f, 0.7f, 0.5f); c1 = new Color(0.6f, 0.6f, 0.6f, 0f); amount = 6; life = 1.2f; speed = 2f; size = 2f; up = 1f; spread = 60f; break;
        }
        Burst(parent, enginePos, c0, c1, amount, life, speed, size, up, spread, fire);
    }

    /// <summary>The white column a cannon ball throws up in the water.</summary>
    public static void Splash(Node parent, Vector3 enginePos)
        => Burst(parent, enginePos, new Color(1, 1, 1, 0.85f), new Color(0.8f, 0.9f, 1f, 0f), 22, 1.3f, 8f, 1.6f, 9f, 18f, false);

    static void Burst(Node parent, Vector3 p, Color c0, Color c1, int amount, float life, float speed, float size, float up, float spread, bool additive)
    {
        var ps = new CpuParticles3D
        {
            Amount = amount, Lifetime = life, OneShot = true, Explosiveness = 0.95f, Emitting = true,
            Direction = Vector3.Up, Spread = spread, InitialVelocityMin = speed * 0.5f, InitialVelocityMax = speed,
            Gravity = new Vector3(0, up > 5f ? -9f : 0.6f, 0), DampingMin = 0.4f, DampingMax = 0.8f, ScaleAmountMin = size * 0.6f, ScaleAmountMax = size,
            Position = new Vector3(p.X, p.Y, -p.Z),
        };
        var grad = new Gradient { Colors = new[] { c0, c1 }, Offsets = new[] { 0f, 1f } };
        ps.ColorRamp = grad;
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, Transparency = BaseMaterial3D.TransparencyEnum.Alpha, BillboardMode = BaseMaterial3D.BillboardModeEnum.Particles,
            AlbedoTexture = Soft(), VertexColorUseAsAlbedo = true, BlendMode = additive ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix, NoDepthTest = false,
        };
        ps.Mesh = new QuadMesh { Size = Vector2.One, Material = mat };
        parent.AddChild(ps);
        var timer = parent.GetTree().CreateTimer(life + 0.5f);
        timer.Timeout += () => { if (GodotObject.IsInstanceValid(ps)) ps.QueueFree(); };
    }
}
