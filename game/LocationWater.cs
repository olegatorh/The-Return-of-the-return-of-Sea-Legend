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
                ClampBelowWater(mi);
            }
    }

    /// <summary>The seabed is only for depth (colour, shore foam, refraction): vertices above the water plane are pressed down so no dry beach appears where the sea should be.</summary>
    static void ClampBelowWater(MeshInstance3D mi)
    {
        if (mi.Mesh is not ArrayMesh am || am.GetSurfaceCount() == 0) return;
        var mat = am.SurfaceGetMaterial(0);
        var arr = am.SurfaceGetArrays(0);
        var v = (Vector3[])arr[(int)Mesh.ArrayType.Vertex];
        for (int i = 0; i < v.Length; i++) if (v[i].Y > Level) v[i].Y = Level;
        arr[(int)Mesh.ArrayType.Vertex] = v;
        var nm = new ArrayMesh();
        nm.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arr);
        nm.SurfaceSetMaterial(0, mat);
        mi.Mesh = nm;
    }
    const float Level = -0.55f;
}
