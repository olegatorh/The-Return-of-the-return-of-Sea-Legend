using System;
using Godot;

namespace SeaDogs;

/// <summary>
/// The follow camera of a location (location/src/camera_follow.cpp + location_camera.cpp): the camera hangs <c>Radius</c> metres behind and above the point
/// "character position + 0.8 x height", the radius shortens when a wall or a prop is in the way (the engine clips a bounding volume against the location
/// polygons; here five probe rays around the boom do the same job) and recovers slowly, the height never drops below half of the look height.
/// </summary>
public sealed class LandCamera
{
    public const float LookHeight = 0.8f;       // LocationCamera::lookHeight: the camera looks at 0.8 of the character's height
    public const float ProbeRadius = 0.25f;     // LCF_RADIUS
    public const float TeleportDist = 12f;      // LocationCamera::teleport_dist: farther than this the camera is born anew
    public const float RotInertia = 5f;         // LocationCamera::rotInertia
    public const float PitchMin = -1.0f, PitchMax = 0.75f;      // axmin / axmax

    public float Radius;                        // the current boom length (camradius)
    public bool Born = true;                    // the next Place() puts the camera straight to the wanted spot
    public Vector3 LookTo, Position;
    public float PitchVel;                      // vAx: the vertical angle has some inertia too

    /// <summary>The nearest hit of the five probe rays along the boom: the largest radius that still keeps the camera body (a ball of ProbeRadius) free.</summary>
    static float FreeRadius(PhysicsDirectSpaceState3D space, Vector3 look, Vector3 dir, float maxR, out float centreFree)
    {
        var up = Math.Abs(dir.Y) > 0.98f ? Vector3.Right : Vector3.Up;
        var left = dir.Cross(up).Normalized();
        var upv = left.Cross(dir).Normalized();
        float best = maxR;
        centreFree = maxR;
        Vector3[] offs = { Vector3.Zero, left * ProbeRadius, -left * ProbeRadius, upv * ProbeRadius, -upv * ProbeRadius };
        for (int i = 0; i < offs.Length; i++)
        {
            var from = look + offs[i];
            var hit = space.IntersectRay(PhysicsRayQueryParameters3D.Create(from, from + dir * (maxR + ProbeRadius)));
            if (hit.Count == 0) continue;
            float d = from.DistanceTo((Vector3)hit["position"]) - ProbeRadius;
            if (i == 0) centreFree = Math.Min(centreFree, d);
            if (d < best) best = d;
        }
        return Math.Max(0.1f, best);
    }

    /// <summary>One frame: lookTo is the point looked at, groundY the character's feet, yaw/pitch the direction FROM the target TO the camera (pitch above the horizon).</summary>
    public void Place(Camera3D cam, Vector3 lookTo, float groundY, float charHeight, float yaw, float pitch, float maxRadius, float dt)
    {
        if (!Born && Position.DistanceSquaredTo(lookTo) > TeleportDist * TeleportDist) Born = true;
        var dir = new Vector3(MathF.Sin(yaw) * MathF.Cos(pitch), MathF.Sin(pitch), MathF.Cos(yaw) * MathF.Cos(pitch));
        float want = maxRadius, centre = maxRadius;
        var space = cam.GetWorld3D()?.DirectSpaceState;
        if (space != null) want = FreeRadius(space, lookTo, dir, maxRadius, out centre);
        if (Born) { Radius = want; Born = false; }
        else
        {
            // ChangeRadius: shrinks quickly (k = 10 dt), grows 30% slower
            want = Math.Clamp(want, 0f, maxRadius);
            float k = Math.Min(dt * 10f, 0.99f);
            if (want > Radius) k *= 0.7f;
            Radius += (want - Radius) * k;
            if (Radius > maxRadius) Radius = maxRadius;
        }
        // the smoothing may never put the camera behind a wall: the centre ray is the hard limit
        float r = Math.Min(Radius, Math.Max(0.1f, centre - ProbeRadius));
        var pos = lookTo + dir * r;
        float minY = groundY + charHeight * LookHeight * 0.5f;       // lc->camPos.y < pos.y + lheight * 0.5
        if (pos.Y < minY) pos.Y = minY;
        Position = pos; LookTo = lookTo;
        cam.GlobalPosition = pos;
        cam.LookAt(lookTo);
    }

    /// <summary>The vertical angle: input is the wanted angular speed (rad/s); it is smoothed (vAx -= 8 dt vAx) and clamped to the camera's range.</summary>
    public float Pitch(float pitch, float wantedSpeed, float dt)
    {
        PitchVel += (wantedSpeed - PitchVel) * Math.Min(dt * 8f, 1f);
        pitch += PitchVel * dt;
        if (pitch < PitchMin) { pitch = PitchMin; PitchVel = 0f; }
        if (pitch > PitchMax) { pitch = PitchMax; PitchVel = 0f; }
        return pitch;
    }
}
