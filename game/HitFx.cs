using Godot;

namespace SeaDogs;

/// <summary>
/// Land combat feedback of the original game (owner's photos of the original): after a blow of the hero a line "damage/health" rises above the target's head in
/// green, e.g. "4/356" (damage dealt, 0 when the enemy blocked it, then the health the enemy has left); every new blow adds a new line, the older ones float up and
/// fade. When the enemy has only a couple of blows left (health within about two and a half blows) the health turns red.
/// </summary>
public static class HitFx
{
    /// <summary>The hero was struck: "-damage" in red rises above his head ("блок" in blue when the guard held and the script took nothing).</summary>
    public static void ShowHero(Node3D hero, float damage, bool blocked)
    {
        int dmg = Mathf.Max(0, Mathf.RoundToInt(damage));
        var l = new Label3D
        {
            Text = dmg > 0 ? $"-{dmg}" : (blocked ? "блок" : "0"),
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, PixelSize = 0.0022f, FontSize = 44, OutlineSize = 6, NoDepthTest = true,
            Modulate = dmg > 0 ? new Color(1f, 0.2f, 0.15f) : new Color(0.6f, 0.8f, 1f), OutlineModulate = new Color(0, 0, 0, 0.85f), Position = new Vector3(0, 2.1f, 0), RenderPriority = 5, OutlineRenderPriority = 4,
        };
        hero.AddChild(l);
        var tw = l.CreateTween();
        tw.SetParallel(true);
        tw.TweenProperty(l, "position:y", 2.9f, 0.9f);
        tw.TweenProperty(l, "modulate:a", 0f, 0.5f).SetDelay(0.4f);
        tw.TweenProperty(l, "outline_modulate:a", 0f, 0.5f).SetDelay(0.4f);
        tw.Chain().TweenCallback(Callable.From(l.QueueFree));
    }

    /// <summary>"+N" in green above the hero: the potion's health arriving.</summary>
    public static void ShowHeal(Node3D hero, float gained)
    {
        var l = new Label3D
        {
            Text = $"+{Mathf.RoundToInt(gained)}", Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, PixelSize = 0.0022f, FontSize = 44, OutlineSize = 6, NoDepthTest = true,
            Modulate = new Color(0.3f, 1f, 0.35f), OutlineModulate = new Color(0, 0, 0, 0.85f), Position = new Vector3(0.25f, 2.0f, 0), RenderPriority = 5, OutlineRenderPriority = 4,
        };
        hero.AddChild(l);
        var tw = l.CreateTween();
        tw.SetParallel(true);
        tw.TweenProperty(l, "position:y", 2.8f, 0.8f);
        tw.TweenProperty(l, "modulate:a", 0f, 0.4f).SetDelay(0.4f);
        tw.TweenProperty(l, "outline_modulate:a", 0f, 0.4f).SetDelay(0.4f);
        tw.Chain().TweenCallback(Callable.From(l.QueueFree));
    }

    /// <summary>Sparks where blade meets blade (the game's particle ini has no spark effect, so a small burst of glowing points): between the two fighters at chest height.</summary>
    public static void Sparks(Node3D victim, Node3D attacker, int count)
    {
        if (!GodotObject.IsInstanceValid(victim) || victim.GetParent() is not Node parent || !victim.IsInsideTree() || !attacker.IsInsideTree()) return;
        var toward = attacker.GlobalPosition - victim.GlobalPosition; toward.Y = 0;
        toward = toward.LengthSquared() > 1e-4f ? toward.Normalized() : Vector3.Forward;
        var mat = new StandardMaterial3D { ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded, AlbedoColor = new Color(1f, 0.85f, 0.4f), BillboardMode = BaseMaterial3D.BillboardModeEnum.Enabled };
        var ps = new CpuParticles3D
        {
            Amount = count, Lifetime = 0.4, OneShot = true, Explosiveness = 1f, Emitting = true, LocalCoords = false,
            Mesh = new QuadMesh { Size = new Vector2(0.035f, 0.035f), Material = mat },
            Direction = (toward + Vector3.Up * 0.6f).Normalized(), Spread = 70f, InitialVelocityMin = 2.0f, InitialVelocityMax = 5.0f, Gravity = new Vector3(0, -9f, 0),
            ScaleAmountMin = 0.6f, ScaleAmountMax = 1.4f,
        };
        parent.AddChild(ps);
        ps.GlobalPosition = victim.GlobalPosition + toward * 0.6f + new Vector3(0, 1.3f, 0);
        ps.GetTree().CreateTimer(0.9).Timeout += () => { if (GodotObject.IsInstanceValid(ps)) ps.QueueFree(); };
    }

    public static void Show(Node3D target, float damage, bool blocked, float hp, float hpMax)
    {
        int dmg = blocked ? 0 : Mathf.Max(0, Mathf.RoundToInt(damage));
        float last = target.HasMeta("hitfx_last") ? (float)target.GetMeta("hitfx_last") : 6f;
        if (dmg > 0) { last = dmg; target.SetMeta("hitfx_last", last); }
        bool low = hp <= 2.5f * last;
        var color = low ? new Color(1f, 0.15f, 0.12f) : new Color(0.35f, 1f, 0.35f);
        var l = new Label3D
        {
            Text = $"{dmg}/{Mathf.Max(0, Mathf.RoundToInt(hp))}",
            Billboard = BaseMaterial3D.BillboardModeEnum.Enabled, PixelSize = 0.0022f, FontSize = 40, OutlineSize = 6, NoDepthTest = true,
            Modulate = color, OutlineModulate = new Color(0, 0, 0, 0.85f), Position = new Vector3(0, 2.1f, 0), RenderPriority = 5, OutlineRenderPriority = 4,
        };
        target.AddChild(l);
        var tw = l.CreateTween();
        tw.SetParallel(true);
        tw.TweenProperty(l, "position:y", 3.0f, 1.0f);
        tw.TweenProperty(l, "modulate:a", 0f, 0.55f).SetDelay(0.45f);
        tw.TweenProperty(l, "outline_modulate:a", 0f, 0.55f).SetDelay(0.45f);
        tw.Chain().TweenCallback(Callable.From(l.QueueFree));
    }
}
