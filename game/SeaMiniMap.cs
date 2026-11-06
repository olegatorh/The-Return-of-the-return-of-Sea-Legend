using System;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// What the navigator draws beyond the ring: the island picture of the minimap disc (BATTLE_NAVIGATOR::SetIsland renders the island model once from above
/// into a texture, the sea colour behind it; UpdateMiniMap moves the texture coordinates of the disc with the ship) and the data of the fire-zone fans
/// (UpdateFireRangeBuffer: Ships types' Cannons.Borts.cannon{f,r,b,l}.FireZone / FireDir in radians relative to the ship, the character's
/// Ship.Cannons.Borts.*.MaxFireDistance in metres).
/// </summary>
public partial class SeaView
{
    const int IslandMapLayer = 19;                  // the island meshes are also drawn on this visual layer; only the minimap camera looks at it
    const int IslandMapPixels = 256;
    SubViewport? islandMapVp;
    Node3D? islandMapFor;
    Vector2 islandMapCentre;
    float islandMapSize;

    void FillNavigatorExtras(HudData d, ShipRt p)
    {
        var sh = p.Chr.Find("Ship", false);
        string[] borts = { "cannonf", "cannonr", "cannonb", "cannonl" };
        int ti = (int)F(sh, "Type", -1);
        Attr? type = it.Globals.TryGetValue("ShipsTypes", out var st) && st.Arr != null && ti >= 0 && ti < st.Arr.Length ? st.Arr[ti].Val.Attr : null;
        for (int i = 0; i < 4; i++)
        {
            d.FireZone[i] = F(type, $"Cannons.Borts.{borts[i]}.FireZone");
            d.FireDir[i] = F(type, $"Cannons.Borts.{borts[i]}.FireDir");
            d.FireRange[i] = F(sh, $"Cannons.Borts.{borts[i]}.MaxFireDistance");
        }
        d.ShipPos = new Vector2(p.X, p.Z);
        if (island == null && islandMapVp != null) ClearIslandMap();
        if (island != null && !ReferenceEquals(islandMapFor, island)) BuildIslandMap();
        if (islandMapVp != null && ReferenceEquals(islandMapFor, island))
        {
            d.IslandMap = islandMapVp.GetTexture();
            if (System.Environment.GetEnvironmentVariable("SEADOGS_ISLANDMAP") is { Length: > 0 } dump && Engine.GetProcessFrames() % 600 == 0) islandMapVp.GetTexture().GetImage()?.SavePng(dump);
            d.IslandCentre = islandMapCentre; d.IslandSize = islandMapSize;
        }
    }

    void BuildIslandMap()
    {
        islandMapVp?.QueueFree(); islandMapVp = null;
        islandMapFor = island;
        if (island == null) return;
        var bb = ModelFactory.Bounds(island);
        if (bb.Size.X < 1f || bb.Size.Z < 1f) return;
        // engine coordinates: x east, z north (Godot z is the opposite)
        islandMapCentre = new Vector2(bb.Position.X + bb.Size.X / 2f, -(bb.Position.Z + bb.Size.Z / 2f));
        islandMapSize = Math.Max(bb.Size.X, bb.Size.Z) * 1.1f;
        foreach (var mi in island.GetChildren().OfType<MeshInstance3D>()) mi.Layers |= 1u << IslandMapLayer;
        var vp = new SubViewport
        {
            Name = "IslandMap", Size = new Vector2I(IslandMapPixels, IslandMapPixels), RenderTargetUpdateMode = SubViewport.UpdateMode.Once,
            DebugDraw = Viewport.DebugDrawEnum.Unshaded, TransparentBg = false, Msaa3D = Viewport.Msaa.Disabled, HandleInputLocally = false, AudioListenerEnable3D = false,
        };
        var env = new Godot.Environment { BackgroundMode = Godot.Environment.BGMode.Color, BackgroundColor = new Color(10 / 255f, 30 / 255f, 80 / 255f), AmbientLightSource = Godot.Environment.AmbientSource.Color, AmbientLightColor = Colors.White };
        var cam = new Camera3D { Projection = Camera3D.ProjectionType.Orthogonal, Size = islandMapSize, Near = 1f, Far = bb.Size.Y + 600f, CullMask = 1u << IslandMapLayer, Environment = env };
        vp.AddChild(cam);
        AddChild(vp);
        var centre = new Vector3(bb.Position.X + bb.Size.X / 2f, bb.End.Y + 100f, bb.Position.Z + bb.Size.Z / 2f);
        cam.LookAtFromPosition(centre, new Vector3(centre.X, 0f, centre.Z), new Vector3(0f, 0f, -1f));      // up on the picture = north
        islandMapVp = vp;
        GD.Print($"[sea] navigator island map: centre {islandMapCentre}, size {islandMapSize:0}");
    }

    void ClearIslandMap() { islandMapVp?.QueueFree(); islandMapVp = null; islandMapFor = null; }
}
