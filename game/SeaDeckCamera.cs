using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using StormScript.Formats;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The sea cameras (SEA_AI\AICameras.c; storm-engine src/libs/sea_cameras). The control "Sea_CameraSwitch" (Tab) runs the script's SeaCameras_Switch,
/// which sends SEA_CAMERAS "ia" (the camera object SeaShipCamera / SeaDeckCamera / SeaFreeCamera, the character whose ship it shows).
/// DECK_CAMERA (deck_camera.cpp) is the first-person view from the deck. The walkable floor is the ship's "path" model (MODELS\Ships\&lt;type&gt;\&lt;type&gt;_path.gm);
/// the start is its "camera" label (eye height = label height above the floor, clamped to h_min..h_max, else h_def). The mouse turns the view by
/// Sensivity*Angle * 3 per mouse unit (pitch within MinAngleX..MaxAngleX); DeckCamera_Forward / _Backward (right / middle mouse button, here also the
/// arrow keys) walk SensivityDistance metres per ms; a step is taken only where the path has a floor within StepUp of the current height (the C++ also
/// slides along the triangle edges; here the step is retried along the x and z components). The roll of the ship reaches the view by RockingZ.
/// Ships without a path model fall back to the deck walk graph of the sailors (SEA_AI\walk\*_walk.c).
/// The script itself knows the camera from SeaCameras.Camera (deck: the shot goes to the side the camera looks at, +15 % accuracy).
/// </summary>
public partial class SeaView
{
    bool deckCam, freeCam;
    /// <summary>The first-person family (mouse captured, mouse look): the deck camera and the free camera.</summary>
    public bool DeckCamera => (deckCam || freeCam) && HasPlayerShip;
    ShipRt? deckLastShip;            // DECK_CAMERA::FindPath calls SetStartPos only when the path node changes: coming back to the same ship keeps place and view
    float deckRockX = 0.8f;
    Vector3 deckPos;                 // ship model space of the engine (x right, y up, z forward): y = floor height under the eye
    float deckYaw, deckPitch, deckEyeY = float.NaN;
    float deckSensH = 0.0015f, deckSensV = 0.0015f, deckSensDist = 0.01f, deckEye = 1.6f, deckMaxPitch = 1.5f, deckMinPitch = -1.5f,
          deckStepUp = 0.5f, deckHMin = 0.5f, deckHMax = 2.0f, deckHDef = 1.6f, deckRockZ = 0.8f;
    /// <summary>Walking by the mouse buttons (Main): +1 forward (right button), -1 back (middle button).</summary>
    public int DeckWalkMouse;
    const float DeckWalkRadius = 1.4f;
    int deckWalkTest;

    /// <summary>The walkable floor of a ship type: triangles of the path model in the hull's space, and the "camera" start label.</summary>
    sealed class DeckPath { public readonly List<(Vector3 a, Vector3 b, Vector3 c)> Tris = new(); public Vector3? Camera; }
    readonly Dictionary<string, DeckPath?> deckPaths = new(StringComparer.OrdinalIgnoreCase);

    V OnSeaCameras(V[] d)
    {
        if (d.Length < 1 || d[0].Attr is not { } camAttr) return V.Int(1);
        var ent = it.Entities.FirstOrDefault(x => !x.Deleted && ReferenceEquals(x.Root, camAttr));
        string cls = ent?.Class ?? (camAttr.Get("h_def") != null ? "DECK_CAMERA" : "SHIP_CAMERA");
        bool deck = cls.Equals("DECK_CAMERA", StringComparison.OrdinalIgnoreCase), free = cls.Equals("FREE_CAMERA", StringComparison.OrdinalIgnoreCase);
        if (free != freeCam)
        {
            freeCam = free;
            if (free) EnterFree();
            GD.Print($"[sea] camera: {cls}");
        }
        if (deck)
        {
            deckRockX = F(camAttr, "RockingX", 0.8f);
            deckSensH = F(camAttr, "SensivityAzimuthAngle", 0.0015f); deckSensV = F(camAttr, "SensivityHeightAngle", 0.0015f); deckSensDist = F(camAttr, "SensivityDistance", 0.01f);
            deckHDef = F(camAttr, "h_def", 1.6f); deckHMin = F(camAttr, "h_min", 0.5f); deckHMax = F(camAttr, "h_max", 2f);
            deckMaxPitch = F(camAttr, "MaxAngleX", 1.5f); deckMinPitch = F(camAttr, "MinAngleX", -1.5f); deckStepUp = F(camAttr, "StepUp", 0.5f); deckRockZ = F(camAttr, "RockingZ", 0.8f);
        }
        if (deck != deckCam)
        {
            deckCam = deck;
            if (deck) EnterDeck();
            GD.Print($"[sea] camera: {cls}");
        }
        return V.Int(1);
    }

    /// <summary>Row-major D3D matrix applied to an engine-space point.</summary>
    static Vector3 Mul(float[] m, Vector3 p) => new(p.X * m[0] + p.Y * m[4] + p.Z * m[8] + m[12], p.X * m[1] + p.Y * m[5] + p.Z * m[9] + m[13], p.X * m[2] + p.Y * m[6] + p.Z * m[10] + m[14]);

    DeckPath? PathOf(ShipRt s)
    {
        if (deckPaths.TryGetValue(s.TypeName, out var cached)) return cached;
        DeckPath? dp = null;
        try
        {
            string dir = GameAssets.Resource("MODELS\\Ships\\" + s.TypeName), file = Path.Combine(dir, s.TypeName + "_path.gm"), hullFile = Path.Combine(dir, s.TypeName + ".gm");
            if (File.Exists(file))
            {
                var g = GmFile.Parse(File.ReadAllBytes(file));
                var hull = File.Exists(hullFile) ? GmFile.Parse(File.ReadAllBytes(hullFile)) : null;
                // the path model hangs on the hull's "geometry/path" label like the masts do (identity when there is none)
                var at = hull?.Labels.FirstOrDefault(l => l.Group.Equals("geometry", StringComparison.OrdinalIgnoreCase) && l.Name.Equals("path", StringComparison.OrdinalIgnoreCase))?.M;
                dp = new DeckPath();
                foreach (var o in g.Objects)
                {
                    if (g.ExtractObject(o) is not { } md) continue;
                    Vector3 P(int i) { var v = new Vector3(md.Pos[i * 3], md.Pos[i * 3 + 1], md.Pos[i * 3 + 2]); return at != null ? Mul(at, v) : v; }
                    for (int t = 0; t + 2 < md.Index.Length; t += 3) dp.Tris.Add((P(md.Index[t]), P(md.Index[t + 1]), P(md.Index[t + 2])));
                }
                var cam = hull?.Labels.FirstOrDefault(l => l.Name.StartsWith("camera", StringComparison.OrdinalIgnoreCase)) ?? g.Labels.FirstOrDefault(l => l.Name.StartsWith("camera", StringComparison.OrdinalIgnoreCase));
                if (cam != null) dp.Camera = new Vector3(cam.M[12], cam.M[13], cam.M[14]);
                if (dp.Tris.Count == 0) dp = null;
                GD.Print($"[sea] deck path of {s.TypeName}: {dp?.Tris.Count ?? 0} triangles, camera label {(dp?.Camera is { } c ? c.ToString() : "none")}");
            }
        }
        catch (Exception ex) { GD.PrintErr("[sea] deck path " + s.TypeName + ": " + ex.Message); dp = null; }
        deckPaths[s.TypeName] = dp;
        return dp;
    }

    /// <summary>The floor of the path under (x, z) nearest to the height <paramref name="baseY"/> within StepUp (DECK_CAMERA::MultiTrace), or null.</summary>
    float? FloorAt(DeckPath dp, float x, float z, float baseY)
    {
        float? best = null; float bd = deckStepUp;
        foreach (var (a, b, c) in dp.Tris)
        {
            float d = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
            if (MathF.Abs(d) < 1e-6f) continue;
            float u = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / d, v = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / d, w = 1f - u - v;
            if (u < -1e-4f || v < -1e-4f || w < -1e-4f) continue;
            float y = u * a.Y + v * b.Y + w * c.Y, dy = MathF.Abs(y - baseY);
            if (dy <= bd) { bd = dy; best = y; }
        }
        return best;
    }

    /// <summary>DECK_CAMERA::SetStartPos: at the "camera" label, the eye at the label's height above the floor; otherwise h_def above the floor in the middle.</summary>
    void EnterDeck()
    {
        var p = ships.FirstOrDefault(s => s.IsPlayer);
        if (p != null && p == deckLastShip && System.Environment.GetEnvironmentVariable("SEADOGS_DECKCAM") == null) { deckEyeY = float.NaN; return; }   // same ship as last time: place and view are kept
        deckLastShip = p;
        deckYaw = 0f; deckPitch = 0f; deckEyeY = float.NaN; deckEye = deckHDef;
        if (System.Environment.GetEnvironmentVariable("SEADOGS_DECKCAM") is { } dc)          // test hook: "yaw,pitch" of the view (radians, 0 = ahead)
        {
            var v = dc.Split(',');
            float.TryParse(v[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out deckYaw);
            if (v.Length > 1) float.TryParse(v[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out deckPitch);
            if (v.Length > 2) int.TryParse(v[2], out deckWalkTest);                              // third value: frames to walk forward
        }
        if (p == null) return;
        if (PathOf(p) is { } dp)
        {
            var start = dp.Camera ?? Vector3.Zero;
            float? floor = FloorAt(dp, start.X, start.Z, start.Y);
            if (floor == null)                                           // the label is higher than StepUp above the floor: take the highest floor below it
                foreach (var t in new[] { 1f, 2f, 4f, 8f, 20f }) { floor = FloorAt(dp, start.X, start.Z, start.Y - t); if (floor != null) break; }
            if (floor != null)
            {
                deckEye = dp.Camera != null ? Math.Clamp(start.Y - floor.Value, deckHMin, deckHMax) : deckHDef;
                deckPos = new Vector3(start.X, floor.Value, start.Z);
                return;
            }
            GD.Print("[sea] WARNING: bad camera locator on ship " + p.TypeName);
        }
        var w = decks.FirstOrDefault(x => x.Ship == p)?.Walk;
        if (w == null) { deckPos = new Vector3(0f, p.Height * 0.25f, -p.Length * 0.25f); return; }
        var want = new Vector3(0f, 0f, -p.Length * 0.25f);
        int best = 0; float bd = float.MaxValue;
        for (int i = 0; i < w.P.Length; i++)
        {
            if (w.Next[i].Count == 0) continue;
            float dd = new Vector2(w.P[i].X - want.X, w.P[i].Z - want.Z).LengthSquared();
            if (dd < bd) { bd = dd; best = i; }
        }
        deckPos = w.P[best];
    }

    /// <summary>Mouse look on the deck (DeckCamera_Turn_H / _V: Sensivity*Angle * 3 per mouse unit).</summary>
    void DeckLook(float dx, float dy)
    {
        if (freeCam) { freeYaw -= dx * 0.0015f * 3f; freePitch = Math.Clamp(freePitch - dy * 0.0015f * 3f, -1.55f, 1.55f); return; }   // FreeCamera_Turn_H / _V
        deckYaw += dx * deckSensH * 3f;
        deckPitch = Math.Clamp(deckPitch - dy * deckSensV * 3f, deckMinPitch, deckMaxPitch);
    }

    /// <summary>Nearest point of the deck walk graph (edges, x/z plane) to the engine-space point; y = the deck height there.</summary>
    static Vector3 NearestOnWalk(DeckWalk w, Vector3 at)
    {
        Vector3 best = at; float bd = float.MaxValue;
        var q = new Vector2(at.X, at.Z);
        for (int i = 0; i < w.P.Length; i++)
            foreach (int j in w.Next[i])
            {
                if (j < i) continue;
                Vector2 a = new(w.P[i].X, w.P[i].Z), b = new(w.P[j].X, w.P[j].Z), ab = b - a;
                float t = ab.LengthSquared() < 1e-6f ? 0f : Math.Clamp((q - a).Dot(ab) / ab.LengthSquared(), 0f, 1f);
                var c = a + ab * t;
                float dd = (q - c).LengthSquared();
                if (dd < bd) { bd = dd; best = new Vector3(c.X, Mathf.Lerp(w.P[i].Y, w.P[j].Y, t), c.Y); }
            }
        return best;
    }

    /// <summary>
    /// One walking step on the path model; false when there is no floor within StepUp there. The C++ slides along the triangle edge it meets; here the step is
    /// retried turned away from the wall by growing angles (cos-scaled), which slides along straight rails and corners alike.
    /// </summary>
    bool StepOnPath(DeckPath dp, Vector3 shift)
    {
        if (shift.LengthSquared() < 1e-10f) return false;
        foreach (float a in new[] { 0f, 0.35f, -0.35f, 0.75f, -0.75f, 1.2f, -1.2f, 1.5f, -1.5f })
        {
            float c = MathF.Cos(a), s = MathF.Sin(a);
            var v = new Vector3(shift.X * c + shift.Z * s, 0f, -shift.X * s + shift.Z * c) * Math.Max(0f, c);
            if (v.LengthSquared() < 1e-10f) continue;
            if (FloorAt(dp, deckPos.X + v.X, deckPos.Z + v.Z, deckPos.Y) is { } y) { deckPos = new Vector3(deckPos.X + v.X, y, deckPos.Z + v.Z); return true; }
        }
        return false;
    }

    /// <summary>Places the camera on the deck of the player's ship (walking with the keys / mouse buttons); the telescope zooms from the same eye.</summary>
    void UpdateDeckCamera(ShipRt p, float dt, bool inputEnabled)
    {
        if (inputEnabled)
        {
            int walk = DeckWalkMouse;
            if (deckWalkTest > 0) { deckWalkTest--; walk = 1; }
            if (walk != 0)
            {
                float dist = dt * 1000f * deckSensDist * MathF.Cos(deckPitch);   // SensivityDistance metres per ms along (cos pitch * sin yaw, 0, cos pitch * cos yaw): looking up or down slows the walk
                var shift = new Vector3(MathF.Sin(deckYaw), 0f, MathF.Cos(deckYaw)) * (Math.Sign(walk) * dist);
                if (PathOf(p) is { } dp)
                {
                    int n = Math.Max(1, (int)MathF.Ceiling(dist / 0.25f));     // short steps: thin floor pieces are not jumped over
                    for (int i = 0; i < n; i++) if (!StepOnPath(dp, shift / n)) break;
                }
                else
                {
                    var to = deckPos + shift;
                    if (decks.FirstOrDefault(x => x.Ship == p)?.Walk is { } w)
                    {
                        var on = NearestOnWalk(w, to);
                        var off = new Vector2(to.X - on.X, to.Z - on.Z);
                        if (off.Length() > DeckWalkRadius) { off = off.Normalized() * DeckWalkRadius; to = new Vector3(on.X + off.X, to.Y, on.Z + off.Y); }
                        to.Y = on.Y;
                    }
                    else { to.X = Math.Clamp(to.X, -p.Width * 0.35f, p.Width * 0.35f); to.Z = Math.Clamp(to.Z, -p.Length * 0.4f, p.Length * 0.4f); }
                    deckPos = to;
                }
            }
        }
        // the eye follows the floor height smoothly (steps up to the quarterdeck); the ship's rocking moves it with the hull
        deckEyeY = float.IsNaN(deckEyeY) ? deckPos.Y : Mathf.Lerp(deckEyeY, deckPos.Y, Math.Min(1f, dt * 8f));
        var xf = p.Node.GlobalTransform;
        var eye = xf * new Vector3(deckPos.X, deckEyeY + deckEye, -deckPos.Z);
        // DECK_CAMERA::Move: heading = ship heading + view heading; pitch = view pitch + RockingX of the ship's pitch; roll = RockingZ of the ship's roll
        var se = xf.Basis.Orthonormalized().GetEuler(EulerOrder.Yxz);
        var cb = Basis.FromEuler(new Vector3(deckPitch + deckRockX * se.X, se.Y - deckYaw, Math.Clamp(deckRockZ, 0f, 1f) * se.Z), EulerOrder.Yxz);
        Cam.Near = 0.1f;
        Cam.GlobalTransform = new Transform3D(cb, eye);
        // ISPYGLASS::UpdateCamera: the perspective (tangent of the half angle) goes linearly from the normal to normal / zoom during the activation time
        float tanHalf = MathF.Tan(Mathf.DegToRad(62f) * 0.5f) * Mathf.Lerp(1f, 1f / scopeZoom, scopeT);
        Cam.Fov = Mathf.RadToDeg(2f * MathF.Atan(tanHalf));
        Cam.Current = true;
    }

    // ---- FREE_CAMERA (free_camera.cpp): a flying camera for the debug menu (locCameraEnableFree); Tab goes deck -> free -> ship
    float freeYaw, freePitch;
    Vector3 freePos;

    /// <summary>FREE_CAMERA::Execute copies the render camera into its own position and angles each frame, so it starts where the previous camera was.</summary>
    void EnterFree()
    {
        freePos = Cam.GlobalPosition;
        var e = Cam.GlobalTransform.Basis.Orthonormalized().GetEuler(EulerOrder.Yxz);
        freeYaw = e.Y; freePitch = e.X;
    }

    /// <summary>FREE_CAMERA::Move: the mouse turns (FreeCamera_Turn_H / _V, DeckLook), the left button flies forward along the view, the right one back; Shift x4, Ctrl x8; 5 m/s.</summary>
    void UpdateFreeCamera(float dt, bool inputEnabled)
    {
        if (inputEnabled)
        {
            float speed = 5f * dt;
            if (Input.IsKeyPressed(Key.Shift)) speed *= 4f;
            if (Input.IsKeyPressed(Key.Ctrl)) speed *= 8f;
            var fwd = new Vector3(-MathF.Sin(freeYaw) * MathF.Cos(freePitch), MathF.Sin(freePitch), -MathF.Cos(freeYaw) * MathF.Cos(freePitch));
            int dir = (Input.IsMouseButtonPressed(MouseButton.Left) ? 1 : 0) - (Input.IsMouseButtonPressed(MouseButton.Right) ? 1 : 0);
            if (deckWalkTest > 0) { deckWalkTest--; dir = 1; }
            freePos += fwd * (speed * dir);
        }
        Cam.Near = 0.3f;
        Cam.GlobalTransform = new Transform3D(Basis.FromEuler(new Vector3(freePitch, freeYaw, 0f), EulerOrder.Yxz), freePos);
        Cam.Fov = 62f;
        Cam.Current = true;
    }
}
