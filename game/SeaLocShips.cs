using System;
using System.Collections.Generic;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// Ships that stand in a location (the town port: the player's ship and quest ships at the pier, the boat). Locations/locations_loader.c LocLoadShips creates one "ship" entity
/// per ship, puts Ship.Pos / Ship.Ang.y of the character on a "ships" / "ships_other" locator, calls Ship_PrepareShipForLocation (stopped, sails furled) and sends
/// MSG_SHIP_CREATE 50300 "laa" (character, ship type) (ship.cpp: SHIP::Create). The ship is built with the same model code as at sea (hull, masts, yards, sails, ropes),
/// stands still on the water of the location (rocking on the waves) and is removed with the entity (DeleteClass(locShips[n]) when the location is left).
/// The nodes live beside the SeaView (not inside it): the sea world is hidden while a location is loaded.
/// </summary>
public partial class SeaView
{
    sealed partial class LocShipRoot : Node3D
    {
        public Action<float>? Tick;
        public override void _Process(double delta) => Tick?.Invoke((float)delta);
    }

    sealed class LocShipRt { public Entity Ent = null!; public ShipRt Rt = null!; }

    readonly List<LocShipRt> locShips = new();
    LocShipRoot? locShipRoot;

    /// <summary>MSG_SHIP_CREATE of a ship in a location.</summary>
    V AddLocationShip(Entity e, V[] d)
    {
        if (d.Length < 3 || d[1].Attr is not { } chr || d[2].Attr is not { } type) return V.Int(0);
        foreach (var o in locShips.FindAll(x => ReferenceEquals(x.Ent, e) || ReferenceEquals(x.Rt.Chr, chr))) RemoveLocShip(o);
        string name = type.Get("Name")?.Value ?? "";
        if (name.Length == 0) return V.Int(0);
        var model = LoadShipModel(name, (int)F(chr, "Nation", 3));
        var rt = new ShipRt
        {
            Chr = chr, Index = (int)F(chr, "index", -1), TypeName = name, Node = model.Root, Length = model.Length, Height = model.Height, Width = model.Width,
            Sails = model.Sails, Masts = model.Masts, Cannons = model.Cannons, FirePlaces = model.FirePlaces, Ropes = model.Ropes, YardNodes = model.YardNodes, FixedRopes = model.FixedRopes, YardRopes = model.YardRopes,
            X = F(chr, "Ship.Pos.x"), Z = F(chr, "Ship.Pos.z"), Ay = F(chr, "Ship.Ang.y"), Sail = 0f,
        };
        if (locShipRoot == null || !GodotObject.IsInstanceValid(locShipRoot))
        {
            locShipRoot = new LocShipRoot { Name = "LocationShips", Tick = TickLocShips };
            (GetParent() ?? this).AddChild(locShipRoot);
        }
        locShipRoot.AddChild(rt.Node);
        foreach (var sl in rt.Sails) sl.Roll = 1f;                                      // stopped ships have their sails furled (Ship.Speed.z = 0)
        ApplySailTextures(rt);
        ApplyRiggingState(rt);
        foreach (var sl in rt.Sails) if (!sl.Gone) { sl.SetRoll = 1f; sl.Mat.SetShaderParameter("roll", 1f); }
        rt.Node.Position = new Vector3(rt.X, SeaWaves.Height(rt.X, -rt.Z), -rt.Z);
        rt.Node.Rotation = new Vector3(0f, -rt.Ay, 0f);
        locShips.Add(new LocShipRt { Ent = e, Rt = rt });
        GD.Print($"[sea] location ship {chr.Get("id")?.Value} type={name} at ({rt.X:0.0},{rt.Z:0.0}) ay={rt.Ay:0.00} length={rt.Length:0.0}");
        return V.Int(1);
    }

    void RemoveLocShip(LocShipRt o)
    {
        if (GodotObject.IsInstanceValid(o.Rt.Node)) o.Rt.Node.QueueFree();
        locShips.Remove(o);
    }

    void TickLocShips(float dt)
    {
        foreach (var o in locShips.ToArray())
        {
            if (o.Ent.Deleted) { RemoveLocShip(o); continue; }
            if (!o.Rt.Node.IsInsideTree()) continue;
            rockDt = dt;
            ApplyRocking(o.Rt);
        }
    }
}
