using StormScript.Runtime;
using Xunit;

namespace StormScript.Tests;

/// <summary>Pure helpers behind game/Characters.cs: turning, vector limit, step sound alias, saved position text.</summary>
public class CharacterMathTests
{
    [Fact]
    public void TurnTowardStopsAtTheTargetWhenInReach()
        => Assert.Equal(0.5f, CharacterMath.TurnToward(0.2f, 0.5f, 1f), 5);

    [Fact]
    public void TurnTowardIsLimitedByTheStep()
    {
        Assert.Equal(0.3f, CharacterMath.TurnToward(0.2f, 2f, 0.1f), 5);
        Assert.Equal(0.1f, CharacterMath.TurnToward(0.2f, -2f, 0.1f), 5);
    }

    [Fact]
    public void TurnTowardTakesTheShortWayAcrossPi()
    {
        // from 3.0 to -3.0 rad the short way is forward (through pi), about 0.28 rad
        float r = CharacterMath.TurnToward(3.0f, -3.0f, 0.1f);
        Assert.Equal(3.1f, r, 5);
    }

    [Fact]
    public void LimitLengthShortensOnlyLongVectors()
    {
        float ax = 3f, az = 4f;
        CharacterMath.LimitLength(ref ax, ref az);
        Assert.Equal(0.6f, ax, 5);
        Assert.Equal(0.8f, az, 5);
        float bx = 0.3f, bz = 0.4f;
        CharacterMath.LimitLength(ref bx, ref bz);
        Assert.Equal(0.3f, bx);
        Assert.Equal(0.4f, bz);
    }

    [Theory]
    [InlineData("snd_grass", "step_grass")]
    [InlineData("add_snd_stone", "step_stone")]
    [InlineData("GrexitT_snd_gras", "step_grass")]      // truncated name
    [InlineData("snd_srairway", "step_stairway")]       // typo in some patches
    [InlineData("snd_WOOD", "step_wood")]
    [InlineData("lava", "step_ground")]
    [InlineData("", "step_ground")]
    [InlineData("x", "step_ground")]                    // too short to match anything
    public void StepAliasPicksTheStepSound(string material, string expected)
        => Assert.Equal(expected, CharacterMath.StepAlias(material));

    [Fact]
    public void PositionTextRoundTrips()
    {
        string t = CharacterMath.FormatPosition(1.5f, -2.25f, 0.1f, 3.14159f);
        Assert.True(CharacterMath.TryParsePosition(t, out var x, out var y, out var z, out var ay));
        Assert.Equal((1.5f, -2.25f, 0.1f, 3.14159f), (x, y, z, ay));
    }

    [Fact]
    public void PositionTextUsesInvariantCulture()
    {
        var old = System.Threading.Thread.CurrentThread.CurrentCulture;
        try
        {
            System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("uk-UA");
            Assert.Equal("1.5,2,3,4", CharacterMath.FormatPosition(1.5f, 2f, 3f, 4f));
            Assert.True(CharacterMath.TryParsePosition("1.5,2,3,4", out var x, out _, out _, out _));
            Assert.Equal(1.5f, x);
        }
        finally { System.Threading.Thread.CurrentThread.CurrentCulture = old; }
    }

    [Fact]
    public void ShortPositionTextIsRejected()
        => Assert.False(CharacterMath.TryParsePosition("1,2,3", out _, out _, out _, out _));
}
