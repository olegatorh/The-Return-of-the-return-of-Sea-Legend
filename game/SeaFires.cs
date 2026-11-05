using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// Fires on ships (the engine class "Ship", ship.dll FirePlace). The hull model carries "fireplace" labels. A ball that hits the hull reports the nearest fire place
/// and its distance in Shp_HullHit; the script decides whether the ship catches fire (bInflame) and posts ShpActFirePlace, whose handler
/// (Ship_ActivateFirePlace) starts a looped sound and sends MSG_SHIP_ACTIVATE_FIRE_PLACE "llsslf" (index, smoke system, fire system, sound id, seconds).
/// The engine then burns that place: the two particle systems of ship_fire.ini ("ship_smoke", "ship_fire") emit at the place and move with the ship;
/// when the time is over they stop and the sound is stopped. The damage is the script's (ShpFDamage events).
/// </summary>
public partial class SeaView
{
    sealed class FireRt { public ShipRt Ship = null!; public int Place, Smoke, Fire, Sound; public float End; }

    readonly List<FireRt> fires = new();

    V OnShipMessage(Entity e, V[] d)
    {
        int msg = d.Length > 0 ? Conv.ToInt(d[0]) : 0;
        var s = ShipOf(e.Root);
        switch (msg)
        {
            case 50300: return AddLocationShip(e, d);                                     // MSG_SHIP_CREATE "laa": a ship standing in a location (SeaLocShips.cs)
            case 50306:                                                                   // MSG_SHIP_GET_NUM_FIRE_PLACES "le"
                if (d.Length > 1) it.WriteRef(d[1], V.Int(s?.FirePlaces.Count ?? 0));
                return V.Int(1);
            case 50307:                                                                   // MSG_SHIP_GET_CHARACTER_INDEX "le"
                if (d.Length > 1) it.WriteRef(d[1], V.Int(s?.Index ?? (int)F(e.Root, "index", -1)));
                return V.Int(1);
            case 50304:                                                                   // MSG_SHIP_ACTIVATE_FIRE_PLACE "llsslf"
                if (s != null && d.Length > 5) ActivateFirePlace(s, Conv.ToInt(d[1]), Conv.ToStr(d[2]), Conv.ToStr(d[3]), Conv.ToInt(d[4]), Conv.ToFloat(d[5]));
                return V.Int(1);
            case 50308:                                                                   // MSG_SHIP_ADD_MOVE_IMPULSE "llffffff": inertia flag, speed x y z (world), rotate x y z
                if (s != null && d.Length > 7)
                {
                    // ship.cpp: the speed is turned into the ship's frame and added once (a non-inertia STRENGTH), knots like State.vSpeed
                    float wx = Conv.ToFloat(d[2]), wz = Conv.ToFloat(d[4]), c = MathF.Cos(s.Ay), sn = MathF.Sin(s.Ay);
                    s.Drift += wx * c - wz * sn; s.Speed += wx * sn + wz * c; s.TurnRate += Conv.ToFloat(d[6]);
                }
                return V.Int(1);
        }
        return V.Int(1);
    }

    void ActivateFirePlace(ShipRt s, int place, string smoke, string fire, int sound, float seconds)
    {
        if (place < 0 || place >= s.FirePlaces.Count || ParticleService.Instance is not { } ps) { StopFireSound(sound); return; }
        seconds = Math.Max(1f, seconds);
        if (fires.FirstOrDefault(f => ReferenceEquals(f.Ship, s) && f.Place == place) is { } burning)
        {
            burning.End = Math.Max(burning.End, clock + seconds);                         // already burning: it just burns longer
            StopFireSound(sound);
            return;
        }
        var l = s.FirePlaces[place];
        var local = new Vector3(l.X, l.Y, -l.Z);
        var at = BallStart(s, l);
        var f2 = new FireRt
        {
            Ship = s, Place = place, Sound = sound, End = clock + seconds,
            Smoke = ps.Create(smoke, at, Vector3.Up, 0f, s.Node, local, true),
            Fire = ps.Create(fire, at, Vector3.Up, 0f, s.Node, local, true),
        };
        fires.Add(f2);
        GD.Print($"[sea] fire on {s.Chr.Get("id")?.Value} at place {place} for {seconds:0} s ({smoke}, {fire})");
    }

    void StopFireSound(int sound) { if (sound > 0) it.CallTop("StopSound", V.Int(sound), V.Int(0)); }

    void TickFires()
    {
        for (int i = fires.Count - 1; i >= 0; i--)
        {
            var f = fires[i];
            bool gone = !ships.Contains(f.Ship);
            if (!gone && clock < f.End) continue;
            ParticleService.Instance?.Delete(f.Smoke, !gone);
            ParticleService.Instance?.Delete(f.Fire, !gone);
            StopFireSound(f.Sound);
            fires.RemoveAt(i);
        }
    }

    void ClearFires()
    {
        foreach (var f in fires) { ParticleService.Instance?.Delete(f.Smoke, false); ParticleService.Instance?.Delete(f.Fire, false); StopFireSound(f.Sound); }
        fires.Clear();
    }

    /// <summary>The fire place nearest to an engine-space point: (index, distance), (-1, 99) when the ship has none.</summary>
    (int index, float dist) NearestFirePlace(ShipRt s, Vector3 p)
    {
        int bi = -1; float bd = 99f;
        for (int i = 0; i < s.FirePlaces.Count; i++)
        {
            var w = BallStart(s, s.FirePlaces[i]);
            float dd = w.DistanceTo(p);
            if (dd < bd) { bd = dd; bi = i; }
        }
        return (bi, bd);
    }

    /// <summary>Test hook --shipfire@N (=me: own ship): sets three fire places of the nearest other ship on fire through the script, like a burning hit would.</summary>
    void FireTest(ShipRt o)
    {
        int n = Math.Min(3, o.FirePlaces.Count);
        GD.Print($"[sea] fire test on {o.Chr.Get("id")?.Value}: {o.FirePlaces.Count} fire places");
        for (int i = 0; i < n; i++)
        {
            int k = (i * o.FirePlaces.Count) / Math.Max(1, n);
            it.FireEvent("ShpActFirePlace", new List<V> { V.A(o.Chr), V.A(o.Chr), V.Int(k), V.Str("ship_onfire"), V.Float(40f) });
            it.FireEvent("ShpFDamage", new List<V> { V.Int(o.Index), V.Int(0), V.Int(k), V.Float(40f) });
        }
    }
}
