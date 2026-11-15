using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SeaDogs;

/// <summary>One gun of the player's ship as the navigator ring shows it: one cell = one gun.</summary>
public struct GunCell
{
    public byte State;          // 0 = loaded (ready), 1 = loading / just fired, 2 = destroyed or disabled
    public float Progress;      // loading progress 0..1 (State 1)
}

public partial class SeaView
{
    static string lastCellLog = "";

    /// <summary>
    /// The state of every gun of the player's ship, by side (index 0 bow, 1 starboard, 2 stern, 3 port as in HudData), ordered along the arc of the ring: the arcs of the
    /// original run clockwise from "up", so the port arc (215..325 degrees) and the starboard arc (145..35, reversed) start at the stern and end at the bow, and the bow and stern arcs
    /// run from the port to the starboard side. An empty array = the ship has no guns on that side.
    /// </summary>
    GunCell[][] HudGunCells(ShipRt p)
    {
        var res = new GunCell[4][];
        var all = GunsOf(p);
        for (int i = 0; i < 4; i++)
        {
            string? bort = HudBort(p, i);
            if (bort == null) { res[i] = Array.Empty<GunCell>(); continue; }
            var gs = all.Where(g => g.Bort == bort);
            // along the ship (stern -> bow) for the beams, across it (port -> starboard) for the bow and the stern guns
            gs = i is 1 or 3 ? gs.OrderBy(g => g.Pos.Z) : gs.OrderBy(g => g.Pos.X);
            res[i] = gs.Select(g => new GunCell
            {
                State = (byte)(g.Damaged ? 2 : g.Ready ? 0 : 1),
                Progress = g.Damaged ? 0f : g.Ready ? 1f : g.Percent,
            }).ToArray();
        }
        string log = $"cells: port {res[3].Length} starboard {res[1].Length} bow {res[0].Length} stern {res[2].Length}";
        if (log != lastCellLog) { lastCellLog = log; GD.Print("[hud] " + log + $" ({p.Chr.Get("id")?.Value})"); }
        return res;
    }
}
