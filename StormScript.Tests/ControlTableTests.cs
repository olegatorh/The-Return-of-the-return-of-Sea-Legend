using StormScript.Runtime;
using Xunit;

namespace StormScript.Tests;

/// <summary>The control service (pcs_controls) and the script's control table (CONTROLS/init_pc.c, controls.c).</summary>
public sealed class ControlTableTests
{
    [Fact]
    public void KeyControlActivatesOnceAndInactivates()
    {
        var t = new ControlTable();
        bool down = false;
        t.IsKeyDown = vk => vk == 65 && down;
        t.Map(t.Create("Ship_TurnLeft"), 65);
        t.Update(); Assert.Empty(t.Changes());
        down = true;
        t.Update(); Assert.Equal(new[] { ("Ship_TurnLeft", true) }, t.Changes());
        t.Update(); Assert.Empty(t.Changes());
        Assert.True(t.Down("Ship_TurnLeft"));
        down = false;
        t.Update(); Assert.Equal(new[] { ("Ship_TurnLeft", false) }, t.Changes());
        Assert.False(t.Down("Ship_TurnLeft"));
    }

    [Fact]
    public void InverseControlIsActiveWhileTheKeyIsReleased()
    {
        var t = new ControlTable();
        bool down = false;
        t.IsKeyDown = vk => vk == 17 && down;
        int c = t.Create("ChrBlock_Unlock");
        t.Map(c, 17); t.SetFlags(c, ControlTable.InverseControl);
        t.Update(); Assert.Empty(t.Changes());
        down = true;
        t.Update(); t.Changes();
        Assert.False(t.Down("ChrBlock_Unlock"));
        down = false;
        t.Update(); Assert.Equal(new[] { ("ChrBlock_Unlock", true) }, t.Changes());   // the key went up: the unlock fires
    }

    [Fact]
    public void LockedControlIsSilentAndMouseAxisUsesSensitivityForFloatsOnly()
    {
        var t = new ControlTable();
        t.SensX = 2f;
        int a = t.Create("ChrTurnH"); t.Map(a, ControlTable.MouseXAxis);
        t.AddMouse(7, 0); t.Update();
        var r = t.Read("ChrTurnH");
        Assert.Equal(7, r.L); Assert.Equal(14f, r.F);                 // lValue is the raw pixels, fValue is scaled by XI_SetMouseSensitivity
        t.Lock("ChrTurnH", true);
        t.AddMouse(7, 0); t.Update();
        Assert.Equal(0, t.Read("ChrTurnH").L);
    }

    [Fact]
    public void MakeInvertSetsTheInverseFlagWhenTheStateIsZero()
    {
        var t = new ControlTable();
        int c = t.Create("ChrCamTurnV"); t.Map(c, ControlTable.MouseYAxis);
        t.MakeInvert("ChrCamTurnV", 0);                                // _ControlMakeInvert: state 0 -> INVERSE_CONTROL
        t.AddMouse(0, 5); t.Update();
        Assert.Equal(-5, t.Read("ChrCamTurnV").L);
        t.MakeInvert("ChrCamTurnV", 1);
        t.AddMouse(0, 5); t.Update();
        Assert.Equal(5, t.Read("ChrCamTurnV").L);
    }

    [Fact]
    public void ScriptBuildsTheOriginalDefaultControlTable()
    {
        if (!Paths.HasMerged) return;
        var log = new List<string>();
        var host = new ScriptHost(Paths.Merged);
        host.It.Log = s => { lock (log) log.Add(s); };
        EngineStubs.Install(host.It);
        Assert.True(host.LoadFile("seadogs.c", "<main>"), string.Join("\n", log.Take(20)));
        host.It.CallTop("Main");
        for (int i = 0; i < 50 && !host.It.Exit; i++) host.It.Advance(100);
        host.It.CallTop("ControlsInit", V.Str("pc"), V.Int(0));          // what Main_Start does on the first frame
        var c = host.It.Controls;
        // CONTROLS/init_pc.c: defaults of the land, sea, world map and interface groups
        Assert.Equal(ControlTable.VkLButton, c.Bound("ChrAction"));
        Assert.Equal(ControlTable.VkRButton, c.Bound("ChrForward"));
        Assert.Equal(ControlTable.VkMButton, c.Bound("ChrBackward"));
        Assert.Equal(16, c.Bound("ChrRun"));
        Assert.Equal('E', c.Bound("ChrFightMode"));
        Assert.Equal('A', c.Bound("Ship_TurnLeft"));
        Assert.Equal('D', c.Bound("Ship_TurnRight"));
        Assert.Equal('W', c.Bound("Ship_SailUp"));
        Assert.Equal('S', c.Bound("Ship_SailDown"));
        Assert.Equal(ControlTable.VkMButton, c.Bound("ShipCamera_Forward"));
        Assert.Equal(ControlTable.VkRButton, c.Bound("ShipCamera_Backward"));
        Assert.Equal(17, c.Bound("TelescopeIn"));
        Assert.Equal(13, c.Bound("BICommandsActivate"));
        Assert.Equal(32, c.Bound("BIFastCommand"));
        Assert.Equal(115, c.Bound("Quick_Save"));
        Assert.Equal(117, c.Bound("Quick_Load"));
        Assert.Equal(ControlTable.MouseXAxis, c.Bound("ChrTurnH"));
        Assert.Equal(ControlTable.MouseYAxis, c.Bound("ChrCamTurnV"));
        Assert.True((c.Get("ChrBlock_Unlock")!.Flags & ControlTable.InverseControl) != 0);
        Assert.True(c.Count > 100, "controls: " + c.Count);
        // the camera's vertical axis is inverted by default (ControlsMakeInvert is called when the options are applied)
        Assert.DoesNotContain(host.It.UnresolvedFuncs.Keys, k => k.Contains("control", StringComparison.OrdinalIgnoreCase));
    }
}
