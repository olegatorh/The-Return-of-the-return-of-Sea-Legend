using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Formats;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// "Flag" class messages for models that are not ships: the flags of towns and ports (locations_loader.c: SendMessage(&amp;Flag, "lil", MSG_FLAG_INIT, &amp;mdl, GetLocationNation(loc)))
/// and of forts at sea (AIFort.c). flag.cpp walks the labels of the model: every group "flag*" with the labels f1..f4 is one flag. The cloth itself is the ship flag
/// of RiggingFx (shader of flag.cpp). The location / fort code registers its model nodes here so the script can refer to them by their entity.
/// </summary>
public static class LandFlags
{
    static readonly Dictionary<Entity, (Node3D node, GmFile gm)> hosts = new();

    /// <summary>A model entity (location model object from GET_MODEL, "MODELR" fort) with the node that shows it and its geometry (labels).</summary>
    public static void Register(Entity e, Node3D node, GmFile gm) => hosts[e] = (node, gm);

    public static void Forget(Entity e) => hosts.Remove(e);

    /// <summary>The node of a registered model entity (the fort model for the sea's blots).</summary>
    public static bool TryGet(Entity e, out Node3D node)
    {
        if (hosts.TryGetValue(e, out var h) && GodotObject.IsInstanceValid(h.node)) { node = h.node; return true; }
        node = null!; return false;
    }

    /// <summary>The Flag entity must exist on land too: the scripts create it with the sea rigging only, but the location loader sends MSG_FLAG_INIT to it.</summary>
    public static void EnsureEntity(Interp it)
    {
        if (it.Globals.TryGetValue("Flag", out var s) && s.Val.Attr is { } a && a.Entity is not { Deleted: false }) it.CreateEntity(a, "Flag");
    }

    /// <summary>Messages of the Flag class: INIT 40200 "lil" (model, nation), DEL_GROUP 40201, TO_NEWHOST 40210.</summary>
    public static V OnMessage(V[] d)
    {
        int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
        if (msg != 40200 || d.Length < 3) return V.Int(0);
        if (d[1].Attr?.Entity is not { } e || !hosts.TryGetValue(e, out var h) || !GodotObject.IsInstanceValid(h.node)) return V.Int(0);
        int nation = Conv.ToInt(d[2]);
        int n = 0;
        foreach (var (host, flag) in RiggingFx.BuildFlags(new[] { new RiggingFx.Part(h.node, h.gm) }, nation)) { host.AddChild(flag); n++; }
        if (n > 0) GD.Print($"[flag] {n} flag(s) on {h.node.Name}, nation {nation}");
        return V.Int(n > 0 ? 1 : 0);
    }

    /// <summary>Wind of the weather and the shader time; the sea view does the same for ships.</summary>
    public static void Tick(Interp it)
    {
        float ang = 0f;
        if (it.Globals.TryGetValue("Weather", out var w) && w.Val.Attr is { } wa && wa.Find("Wind.Angle", false)?.Value is { } av) ang = (float)Conv.ParseFloatPrefix(av);
        RiggingFx.WindWorld = new Vector3(MathF.Sin(ang), 0f, -MathF.Cos(ang));
        RiggingFx.UpdateFlags((float)(Time.GetTicksMsec() / 1000.0));
    }
}
