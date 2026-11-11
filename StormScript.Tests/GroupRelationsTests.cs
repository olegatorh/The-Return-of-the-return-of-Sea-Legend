using StormScript.Runtime;
using Xunit;

namespace StormScript.Tests;

/// <summary>Relations, alarms and attacker memory of the engine's CharactersGroups (game/Groups.cs delegates to this class).</summary>
public class GroupRelationsTests
{
    [Fact]
    public void SameGroupIsFriendAndUnknownPairIsNeutral()
    {
        var r = new GroupRelations();
        Assert.Equal("friend", r.Relation("Pirates", "pirates"));
        Assert.Equal("neitral", r.Relation("a", "b"));
    }

    [Fact]
    public void RelationIsSymmetricAndCaseInsensitive()
    {
        var r = new GroupRelations();
        r.SetRelation("Player", "Pirates", "enemy");
        Assert.Equal("enemy", r.Relation("player", "PIRATES"));
        Assert.Equal("enemy", r.Relation("pirates", "player"));
    }

    [Fact]
    public void AlarmReactionOverridesRelationOnlyWhileAlarmed()
    {
        var r = new GroupRelations();
        r.SetRelation("player", "guards", "friend");
        r.SetReaction("player", "guards", "enemy", "friend");
        Assert.Equal("friend", r.Relation("player", "guards"));
        r.SetAlarm("player", "guards", 0.5f);
        Assert.Equal("enemy", r.Relation("player", "guards"));
        r.SetAlarm("player", "guards", 0f);
        Assert.Equal("friend", r.Relation("player", "guards"));
    }

    [Fact]
    public void SetAlarmClampsToUnitRange()
    {
        var r = new GroupRelations();
        r.SetAlarm("a", "b", 5f);
        Assert.Equal(1f, r.Alarm("a", "b"));
        r.SetAlarm("a", "b", -2f);
        Assert.Equal(0f, r.Alarm("a", "b"));
    }

    [Fact]
    public void RelationBecomesActiveAtMaxAndStaysUntilMin()
    {
        var r = new GroupRelations();
        r.SetAlarm("a", "b", 0.8f);
        Assert.True(r.IsActive("a", "b"));
        r.SetAlarm("a", "b", 0.5f);   // between the thresholds: hysteresis keeps it active
        Assert.True(r.IsActive("a", "b"));
        r.SetAlarm("a", "b", 0.3f);
        Assert.False(r.IsActive("a", "b"));
        r.SetAlarm("a", "b", 0.5f);   // below the max again: stays inactive
        Assert.False(r.IsActive("a", "b"));
    }

    [Fact]
    public void AlarmFadesAtTheDefaultAndAtTheConfiguredSpeed()
    {
        var r = new GroupRelations();
        r.SetAlarm("a", "b", 1f);
        r.SetAlarm("c", "d", 1f);
        r.SetAlarmDown("c", "d", 0.5f);
        r.DecayAlarms(10f);
        Assert.Equal(0.8f, r.Alarm("a", "b"), 3);   // default 0.02 per second
        Assert.Equal(0f, r.Alarm("c", "d"));        // 0.5 per second, never below zero
        Assert.False(r.IsActive("c", "d"));
    }

    [Fact]
    public void AttackRaisesBothDirectionsAndTurnsTheVictimsFriendsHostile()
    {
        var r = new GroupRelations();
        r.G("player"); r.G("guards"); r.G("citizens"); r.G("pirates");
        r.SetRelation("guards", "citizens", "friend");
        r.SetReaction("player", "citizens", "enemy", "neitral");
        r.Attack("player", "guards");
        Assert.Equal(1f, r.Alarm("player", "guards"));
        Assert.Equal(1f, r.Alarm("guards", "player"));
        Assert.True(r.IsActive("player", "guards"));
        Assert.Equal(1f, r.Alarm("player", "citizens"));   // friend of the victim
        Assert.Equal(1f, r.Alarm("citizens", "player"));
        Assert.Equal(0f, r.Alarm("player", "pirates"));    // unrelated group
        Assert.Equal("enemy", r.Relation("player", "citizens"));
    }

    [Fact]
    public void AttackSkipsFriendsOfTheVictimThatAreNotRegistered()
    {
        var r = new GroupRelations();
        r.SetRelation("guards", "citizens", "friend");   // "citizens" was never registered as a group
        r.Attack("player", "guards");
        Assert.Equal(0f, r.Alarm("player", "citizens"));
    }

    [Fact]
    public void RememberedAttackerExpiresAndIsListedByLeastTimeLeft()
    {
        var r = new GroupRelations();
        r.AddTarget(1, 10, 5f);
        r.AddTarget(1, 11, 2f);
        Assert.True(r.IsRemembered(1, 10));
        Assert.False(r.IsRemembered(2, 10));
        Assert.Equal(new[] { 11, 10 }, r.Remembered(1));
        r.ExpireTargets(3f);
        Assert.False(r.IsRemembered(1, 11));
        Assert.True(r.IsRemembered(1, 10));
        r.ExpireTargets(2f);                              // exactly used up: removed
        Assert.False(r.IsRemembered(1, 10));
    }

    [Fact]
    public void AddingTheSameAttackerAgainRefreshesItsTime()
    {
        var r = new GroupRelations();
        r.AddTarget(1, 10, 1f);
        r.AddTarget(1, 10, 9f);
        r.ExpireTargets(5f);
        Assert.True(r.IsRemembered(1, 10));
        Assert.Single(r.Remembered(1));
    }

    [Fact]
    public void ClearTargetsForgetsEverybody()
    {
        var r = new GroupRelations();
        r.AddTarget(1, 10, 9f);
        r.ClearTargets();
        Assert.Empty(r.Remembered(1));
    }

    [Fact]
    public void GroupDefaultsMatchTheEngine()
    {
        var g = new GroupRelations().G("x");
        Assert.Equal(15f, g.Look);
        Assert.Equal(2.5f, g.Hear);
        Assert.Equal(20f, g.Say);
        Assert.Equal(10, g.Priority);
    }
}
