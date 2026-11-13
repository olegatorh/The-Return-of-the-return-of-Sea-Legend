using Godot;

namespace SeaDogs;

/// <summary>Water of land locations: the sea-bottom mesh (models.always.sb, foam = 1) is drawn under the sea so the boujie water shader sees the depth.</summary>
public static class LocationWater
{
    public static void PrepareSeabed(Node3D node)
    {
        if (!SeaSurface.Boujie || System.Environment.GetEnvironmentVariable("SEADOGS_NOSEABED") == "1") { node.Visible = false; return; }
        foreach (var c in node.GetChildren())
            if (c is MeshInstance3D mi)
            {
                mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
                var bb = mi.Mesh.GetAabb();
                var m = mi.Mesh.SurfaceGetMaterial(0) as StandardMaterial3D;
                GD.Print($"[locwater] seabed part {mi.Name}: y {bb.Position.Y:0.00}..{bb.End.Y:0.00} size {bb.Size.X:0}x{bb.Size.Z:0} tex={m?.AlbedoTexture?.GetPath()} tr={m?.Transparency}");
            }
    }
}
