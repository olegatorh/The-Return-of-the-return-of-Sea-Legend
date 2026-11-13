using System;

namespace SeaDogs;

/// <summary>
/// Comfort for the hero's fight on land (no new controls): a soft aim assist - standing still with the sword out, he eases round to the nearest enemy
/// in front of him (enemies only, a narrow cone) - and a short input buffer for the recoil step (blows are buffered by queuedSwing).
/// </summary>
public partial class CharacterView
{
    public const float InputBufferSeconds = 0.25f;
    float recoilBuffered;

    /// <summary>The hero idles with the sword drawn: turn slowly to the enemy in the cone (cos &gt; 0.7, 3.5 m).</summary>
    void SoftLockOn(float dt)
    {
        if (!IsPlayer || !fightMode || !IsAlive || Stunned || Busy || Blocking) return;
        var e = NearestInFront(3.5f);
        if (e == null) return;
        float want = Angle(e.x - x, e.z - z);
        float d = MathF.Abs(TurnToward(ay, want, 10f) - ay);
        if (d > 0.8f) return;                               // too far off the heading: the player is looking elsewhere
        ay = TurnToward(ay, want, 3f * dt); nay = ay; hasNay = false;
    }

    /// <summary>A step back asked for while the previous action ends within the buffer time is carried out right after it.</summary>
    bool BufferRecoil()
    {
        if (!IsPlayer || !IsAlive || Stunned || atkCooldown > InputBufferSeconds) return false;
        recoilBuffered = InputBufferSeconds;
        return true;
    }

    void TickRecoilBuffer(float dt)
    {
        if (recoilBuffered <= 0f) return;
        recoilBuffered -= dt;
        if (atkCooldown <= 0f && atkHitIn < 0f && !Busy) { recoilBuffered = 0f; Recoil(); }
    }
}
