using System;
using Godot;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The player's controls of the ship camera (sea_cameras/src/ship_camera.cpp SHIP_CAMERA::Move; the constants come from SEA_AI/AICameras.c): the mouse always turns the
/// camera round the ship (ShipCamera_Turn_H / _V with angle inertia 10 / 12), the middle / right button (ShipCamera_Forward / _Backward) move it closer / away at
/// SensivityDistance metres per second with distance inertia 15, between MinDistance and MaxDistance. The angle stays where the player left it.
/// </summary>
public partial class SeaView
{
    float camYawDlt, camPitchDlt, camDistDlt;       // fAngleYDlt, fAngleXDlt, fDistanceDlt

    void ShipCameraControls(float dt, float dMin, float dMax, Attr? cam)
    {
        float sensDist = F(cam, "SensivityDistance", 30f), sensAz = F(cam, "SensivityAzimuthAngle", 0.04f), sensH = F(cam, "SensivityHeightAngle", 0.02f);
        float minX = F(cam, "MinAngleX", -1.4f), maxX = F(cam, "MaxAngleX", 0.2f), invX = F(cam, "InvertMouseX", 1f), invY = F(cam, "InvertMouseY", -1f);
        // distance
        float target = 0f;
        if (Ctl.Down("ShipCamera_Forward")) target -= sensDist;
        if (Ctl.Down("ShipCamera_Backward")) target += sensDist;
        camDistDlt += (target - camDistDlt) * Math.Clamp(15f * dt, 0f, 1f);
        camDist = Math.Clamp(camDist + dt * camDistDlt, dMin, dMax);
        // rotation: the mouse (fValue = pixels * sensitivity, negated by the control's flags: ControlsMakeInvert)
        float hv = invX * 2f * Ctl.T.Read("ShipCamera_Turn_H").F * sensAz;
        camYawDlt += (hv - camYawDlt) * Math.Clamp(10f * dt, 0f, 1f);
        camYawOff += dt * camYawDlt;                      // vAng.y: the camera sits at angle vAng.y round the ship; the port keeps it relative to "behind"
        float vv = invY * 3f * Ctl.T.Read("ShipCamera_Turn_V").F * sensH;
        camPitchDlt += (vv - camPitchDlt) * Math.Clamp(12f * dt, 0f, 1f);
        // vAng.x is the angle below the horizon: -vAng.x is the camera's elevation; MinAngleX / MaxAngleX limit it (above 0 the camera slides along the cylinder: level with the ship)
        camPitch = Math.Clamp(camPitch - dt * camPitchDlt, Math.Max(0.02f, -maxX), -minX);
    }
}
