using System.Text;

namespace StormScript.Runtime;

/// <summary>Engine-object stubs and segment hooks. The stubs record what the scripts ask from the engine.</summary>
public sealed partial class Interp
{
    /// <summary>Texts and translations; null = scripts see "English" and untranslated text.</summary>
    public LanguageService? Language;

    /// <summary>SaveEngineState(name) / LoadEngineState(name): implemented by the game window (files, rebuilding the world).</summary>
    public Func<string, bool>? SaveHook, LoadHook;
    public Func<string, bool>? SegmentLoader;
    public Action<string>? SegmentUnloader;
    public Func<string, bool>? SegmentIsLoaded;

    /// <summary>Game-time multiplier set by the script's SetTimeScale (0 while a dialog is open).</summary>
    public float TimeScale = 1f;

    int nextEntityId = 1;
    public readonly List<Entity> Entities = new();

    /// <summary>Count of (entity class, first message id) pairs received. Key: "class | format | msg".</summary>
    public readonly Dictionary<string, int> MessageStats = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Optional real implementations of entity classes: class name -> message handler.</summary>
    public readonly Dictionary<string, Func<Entity, string, V[], V>> EntityHandlers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when the script deletes an engine object (DeleteClass / DeleteEntities).</summary>
    public Action<Entity>? EntityDeleted;

    public bool CreateEntity(Attr? root, string cls)
    {
        if (root == null) { Error($"CreateEntity({cls}): uninitialised reference"); return false; }
        var e = new Entity { Id = nextEntityId++, Class = cls, Root = root };
        root.Entity = e;
        Entities.Add(e);
        return true;
    }

    public void DeleteEntity(Attr? root)
    {
        if (root?.Entity is { } e) { e.Deleted = true; root.Entity = null; EntityDeleted?.Invoke(e); }
    }

    public void DeleteAllEntities(string? cls)
    {
        foreach (var e in Entities)
            if (!e.Deleted && (cls == null || string.Equals(e.Class, cls, StringComparison.OrdinalIgnoreCase)))
            { e.Deleted = true; e.Root.Entity = null; EntityDeleted?.Invoke(e); }
    }

    public V SendMessage(Attr? target, string fmt, V[] data)
    {
        var e = target?.Entity;
        string cls = e?.Class ?? "<no entity>";
        string msg = data.Length > 0 ? CatStr(Conv.Decay(data[0])) : "";
        if (data.Length > 1 && Conv.Decay(data[1]).T == VT.String && Conv.Decay(data[0]).T == VT.Int) msg += " " + CatStr(Conv.Decay(data[1]));
        string key = $"{cls} | {fmt} | {msg}";
        MessageStats[key] = MessageStats.GetValueOrDefault(key) + 1;
        if (e != null && EntityHandlers.TryGetValue(e.Class, out var h)) return h(e, fmt, data);
        return V.Int(0);
    }

    /// <summary>For message formats with 'e' (output reference): store a value into the caller's variable.</summary>
    public void WriteRef(V target, V value)
    {
        if (target.Slot is { } s) SetSlot(s, value, null);
    }

    /// <summary>Host-side helper: assign a global variable from text (converted to the variable's type).</summary>
    public void SetGlobal(string name, string value)
    {
        if (!Globals.TryGetValue(name, out var s)) { Error($"set: unknown global {name}"); return; }
        SetSlot(s, V.Str(value), null);
    }

    /// <summary>Host-side helper: log the attribute tree under "Global.path.to.node".</summary>
    public void DumpPath(string path)
    {
        int dot = path.IndexOf('.');
        string g = dot < 0 ? path : path.Substring(0, dot);
        int idx = -1;
        int br = g.IndexOf('[');
        if (br >= 0) { idx = Conv.ParseIntPrefix(g.Substring(br + 1)); g = g.Substring(0, br); }
        if (!Globals.TryGetValue(g, out var s)) { Log($"dump: {path}: unknown global"); return; }
        if (idx >= 0 && s.Arr != null && idx < s.Arr.Length) s = s.Arr[idx];
        if (s.Val.Attr is not { } root) { Log($"dump: {path}: not an object"); return; }
        var node = dot < 0 ? root : root.Find(path.Substring(dot + 1), false);
        if (node == null) { Log($"dump: {path}: no such attribute"); return; }
        var sb = new StringBuilder();
        node.Dump(sb);
        Log(sb.ToString());
    }

    /// <summary>For every global array of objects: how many elements are populated (have an id or any attribute).</summary>
    public Dictionary<string, string> DescribeObjectArrays()
    {
        var r = new Dictionary<string, string>();
        foreach (var (name, s) in Globals)
        {
            if (s.Arr == null || s.Type != "object") continue;
            int withId = s.Arr.Count(e => e.Val.Attr?.Get("id")?.Value is { Length: > 0 });
            int used = s.Arr.Count(e => e.Val.Attr is { Count: > 0 });
            r[name] = $"{used}/{s.Arr.Length} populated, {withId} with id";
        }
        return r;
    }
}
