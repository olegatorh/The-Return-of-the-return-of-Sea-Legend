namespace StormScript.Runtime;

/// <summary>
/// The engine's control service (PCS_CONTROLS, src/libs/pcs_controls): a table of named user controls that the script creates (CreateControl), binds to a
/// system code (MapControl: a Windows virtual key, a mouse button or an axis), flags (SetControlFlags), locks (LockControl) and reads. Once per frame the core raises
/// "Control Activation" / "Control Deactivation" for every control whose state became ACTIVATED / INACTIVATED (CoreImpl::ProcessControls).
/// Pure logic: the host feeds the keyboard (<see cref="IsKeyDown"/>) and the mouse (<see cref="AddMouse"/>, <see cref="AddWheel"/>).
/// </summary>
public sealed class ControlTable
{
    // flags (CONTROLS/controls.c)
    public const int AxisAsButton = 1, AxisAsInverseButton = 2, InverseControl = 4;
    // system codes above the 256 virtual keys
    public const int MouseXAxis = 256, MouseYAxis = 257, MouseWheelUp = 258, MouseWheelDown = 259, SystemCodes = 260;
    public const int VkLButton = 1, VkRButton = 2, VkMButton = 4;

    public enum State { Inactive, Active, Inactivated, Activated, Forced = 0x7fffffff }

    public sealed class Control
    {
        public string Name = "";
        public int SystemCode = -1;          // UNASSIGNED
        public int Flags;
        public bool Locked;
        public State UserState = State.Inactive;     // PCS_CONTROLS::pUserControls[].state: only LockControl (FORCE_DWORD) and SetControlState change it
        public float Threshold;
    }

    public struct Reading
    {
        public State State;
        public int L;
        public float F;
        public bool Active => State is State.Active or State.Activated;
    }

    struct Sys { public State State; public int L; public float F; public long Frame; }

    readonly List<Control> controls = new();
    readonly Dictionary<string, int> byName = new(StringComparer.OrdinalIgnoreCase);
    readonly Sys[] tab = new Sys[SystemCodes];
    long frame;
    int dx, dy, wheel;

    /// <summary>Is the virtual key / mouse button down right now (supplied by the host).</summary>
    public Func<int, bool> IsKeyDown = _ => false;
    public bool LockAll;
    public float SensX = 1f, SensY = 1f;
    /// <summary>The raw mouse movement of the last <see cref="Update"/> (pixels).</summary>
    public int LastDx { get; private set; }
    public int LastDy { get; private set; }

    public IReadOnlyList<Control> All => controls;
    public int Count => controls.Count;

    public int Create(string name)
    {
        if (string.IsNullOrEmpty(name)) return -1;
        if (byName.TryGetValue(name, out int n)) return n;
        controls.Add(new Control { Name = name });
        byName[name] = controls.Count - 1;
        return controls.Count - 1;
    }

    public int Find(string name) => byName.TryGetValue(name, out int n) ? n : -1;
    public Control? Get(string name) => byName.TryGetValue(name, out int n) ? controls[n] : null;

    /// <summary>MapControl(code, systemCode): an out-of-range control code is ignored (the original writes through a bad index; the intent is "unassigned").</summary>
    public void Map(int code, int systemCode)
    {
        if (code < 0 || code >= controls.Count) return;
        controls[code].SystemCode = systemCode;
    }

    public void SetFlags(int code, int flags) { if (code >= 0 && code < controls.Count) controls[code].Flags = flags; }
    public void SetThreshold(int code, float v) { if (code >= 0 && code < controls.Count) controls[code].Threshold = v; }

    /// <summary>LockControl("", mode) locks everything; LockControl(name, mode) one control and puts its user state to FORCE_DWORD for good (as the engine does).</summary>
    public void Lock(string name, bool mode)
    {
        if (string.IsNullOrEmpty(name)) { LockAll = mode; return; }
        if (byName.TryGetValue(name, out int n)) { controls[n].Locked = mode; controls[n].UserState = State.Forced; }
    }

    /// <summary>XI_ControlMakeInvert(name, state): state != 0 clears the flags, 0 sets INVERSE_CONTROL (str_service.cpp _ControlMakeInvert).</summary>
    public void MakeInvert(string name, int state)
    {
        int n = Create(name);
        controls[n].Flags = state != 0 ? 0 : InverseControl;
    }

    public void Delete(int code)
    {
        if (code < 0 || code >= controls.Count) return;
        controls[code].SystemCode = -1;
        controls[code].Locked = true;
    }

    public void AddMouse(int ddx, int ddy) { dx += ddx; dy += ddy; }
    public void AddWheel(int ticks) { wheel += ticks; }

    /// <summary>Frame start (PCS_CONTROLS::Update): the mouse axes and the wheel for this frame.</summary>
    public void Update()
    {
        frame++;
        LastDx = dx; LastDy = dy;
        tab[MouseXAxis] = new Sys { L = dx, F = dx * SensX, Frame = frame };
        tab[MouseYAxis] = new Sys { L = dy, F = dy * SensY, Frame = frame };
        dx = dy = 0;
        Wheel(MouseWheelUp, wheel > 0, wheel);
        Wheel(MouseWheelDown, wheel < 0, -wheel);
        wheel = 0;
    }

    void Wheel(int code, bool on, int amount)
    {
        ref var s = ref tab[code];
        if (on)
        {
            s.L = amount; s.F = amount;
            s.State = s.State is State.Inactive or State.Inactivated ? State.Activated : State.Active;
        }
        else
        {
            s.L = 0; s.F = 0;
            s.State = s.State is State.Active or State.Activated ? State.Inactivated : State.Inactive;
        }
        s.Frame = frame;
    }

    /// <summary>GetControlState(code): the state of the bound key / axis with the control's flags applied (PCS_CONTROLS::GetControlState).</summary>
    public Reading Read(int code)
    {
        var r = new Reading();
        if (code < 0 || code >= controls.Count) return r;
        var c = controls[code];
        if (c.Locked || LockAll) return r;
        int sc = c.SystemCode;
        if (sc < 0 || sc >= SystemCodes) return r;
        ref var s = ref tab[sc];
        if (s.Frame != frame)
        {
            s.Frame = frame;
            if (sc >= MouseXAxis) { }
            else if (IsKeyDown(sc))
            {
                s.L = 1; s.F = 1f;
                s.State = s.State is State.Inactive or State.Inactivated ? State.Activated : State.Active;
            }
            else
            {
                s.L = 0; s.F = 0f;
                s.State = s.State is State.Active or State.Activated ? State.Inactivated : State.Inactive;
            }
        }
        r.State = s.State; r.L = s.L; r.F = s.F;
        if ((c.Flags & InverseControl) != 0)
        {
            if ((c.Flags & AxisAsButton) != 0) { r.L = 1 - r.L; r.F = 1f - r.F; }
            else { r.L = -r.L; r.F = -r.F; }
            r.State = r.State switch { State.Active => State.Inactive, State.Inactive => State.Active, State.Activated => State.Inactivated, State.Inactivated => State.Activated, _ => r.State };
        }
        if ((c.Flags & AxisAsButton) != 0 && c.UserState != State.Forced)
        {
            if (r.L > 0)
            {
                r.State = c.UserState is State.Inactive or State.Inactivated ? State.Activated : State.Active;
                r.L = 1; r.F = 1f;
            }
            else
            {
                r.State = c.UserState is State.Active or State.Inactive ? State.Inactivated : State.Inactive;
                r.L = 0; r.F = 0f;
            }
        }
        return r;
    }

    public Reading Read(string name) => Read(Find(name));

    /// <summary>True while the control is down (an unlocked control with a non-zero value).</summary>
    public bool Down(string name) { var r = Read(name); return (r.State is State.Active or State.Activated) && r.L != 0; }

    /// <summary>
    /// What the key itself does regardless of locks (the port routes some actions itself and must not depend on the script's group switching):
    /// a key-bound control is down when its system code is pressed (or released for INVERSE_CONTROL).
    /// </summary>
    public bool DownIgnoringLocks(string name)
    {
        var c = Get(name);
        if (c == null || c.SystemCode < 0 || c.SystemCode >= MouseXAxis) return false;
        bool d = IsKeyDown(c.SystemCode);
        return (c.Flags & InverseControl) != 0 ? !d : d;
    }

    /// <summary>The frame's changes, like CoreImpl::ProcessControls: (name, true) = activated, (name, false) = inactivated. Call once per frame after <see cref="Update"/>.</summary>
    public List<(string Name, bool Activated)> Changes()
    {
        var res = new List<(string, bool)>();
        for (int n = 0; n < controls.Count; n++)
        {
            var r = Read(n);
            if (r.State == State.Activated) res.Add((controls[n].Name, true));
            else if (r.State == State.Inactivated) res.Add((controls[n].Name, false));
        }
        return res;
    }

    /// <summary>The system code a control is bound to, or -1.</summary>
    public int Bound(string name) => Get(name)?.SystemCode ?? -1;
}
