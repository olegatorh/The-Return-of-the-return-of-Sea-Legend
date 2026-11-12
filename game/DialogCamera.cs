using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SeaDogs;

/// <summary>
/// The camera of a dialog on land. While the hero talks the camera swings round behind his shoulder so that the interlocutor is in the middle of the picture
/// (the look point is between the two heads, a little closer to the NPC), the vertical angle drops to eye level; when the dialog ends the pitch goes back and the ordinary follow camera
/// (behind the hero's heading) takes over again.
/// </summary>
public sealed class DialogCamera
{
    const float Rate = 3.5f;                    // 1/s: how fast the yaw / pitch ease toward the dialog framing
    const float Shoulder = 0.5f;                // rad: the camera stands beside the line hero -> NPC so both faces are visible
    const float TalkPitch = 0.22f;              // rad above the horizon

    bool active, returning;
    float savedYaw, savedPitch;
    public static readonly bool Log = System.Environment.GetEnvironmentVariable("SEADOGS_DIALOGLOG") != null;

    public bool Active => active;

    static float Wrap(float a) { while (a > MathF.PI) a -= MathF.Tau; while (a < -MathF.PI) a += MathF.Tau; return a; }

    /// <summary>Adjusts yaw / pitch / look point for this frame; returns true while the dialog framing is in charge.</summary>
    public bool Update(IEnumerable<CharacterView> all, CharacterView player, bool dialogOn, float dt, ref float yaw, ref float pitch, ref Vector3 target)
    {
        CharacterView? npc = dialogOn ? all.FirstOrDefault(c => !ReferenceEquals(c, player) && c.InDialog && c.Placed) : null;
        float k = Math.Min(dt * Rate, 1f);
        if (npc != null)
        {
            if (!active) { active = true; returning = false; savedYaw = yaw; savedPitch = pitch; if (Log) GD.Print($"[dialogcam] focus on {npc.Id}: yaw {yaw:0.00}, pitch {pitch:0.00}"); }
            float dx = npc.Position.X - player.Position.X, dz = player.Position.Z - npc.Position.Z;        // Direct3D z is mirrored
            float toNpc = MathF.Atan2(dx, dz);
            // the shoulder side is the one the camera already is on (no swing across the hero)
            float want = -toNpc + (Wrap(savedYaw - (-toNpc)) >= 0f ? Shoulder : -Shoulder);
            yaw += Wrap(want - yaw) * k;
            pitch += (TalkPitch - pitch) * k;
            var chest = new Vector3(0, CharacterView.CharHeight * LandCamera.LookHeight, 0);
            target = player.Position + chest + (npc.Position + chest - player.Position - chest) * 0.55f;
            return true;
        }
        if (active)
        {
            active = false; returning = true;
            if (Log) GD.Print($"[dialogcam] dialog over: back to yaw {savedYaw:0.00}, pitch {savedPitch:0.00}");
        }
        if (returning)
        {
            // only the pitch goes back: the yaw belongs to the follow camera again (it swings behind the hero's heading, which has changed during the talk).
            // Pulling the yaw toward the old angle as well fought the follow camera and left it stuck half-way, side-on to the hero.
            pitch += (savedPitch - pitch) * k;
            if (MathF.Abs(savedPitch - pitch) < 0.01f) returning = false;
        }
        return false;
    }
}
