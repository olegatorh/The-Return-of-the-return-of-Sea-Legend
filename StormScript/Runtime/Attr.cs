using System.Text;

namespace StormScript.Runtime;

/// <summary>
/// Node of an attribute tree. Script objects are such trees: every node has a name, an optional string value and
/// ordered children (child order is observable through GetAttributeN). Names are case-insensitive.
/// </summary>
public sealed class Attr
{
    public string Name;
    string? value;
    /// <summary>Optional engine hook: called after the script has written this node's value (the engine's "SPI" attributes such as worldMap.encounter.cur).</summary>
    public Action<Attr>? OnWrite;
    public string? Value { get => value; set { this.value = value; OnWrite?.Invoke(this); } }
    public Attr? Parent;
    public Entity? Entity;      // set when this node is the root of an engine entity (CreateEntity)
    readonly List<Attr> kids = new();
    Dictionary<string, Attr>? index;

    public Attr(string name, Attr? parent = null) { Name = name; Parent = parent; }

    public IReadOnlyList<Attr> Children => kids;
    public int Count => kids.Count;

    public string Path
    {
        get
        {
            var parts = new List<string>();
            for (var a = this; a != null; a = a.Parent) parts.Add(a.Name);
            parts.Reverse();
            return string.Join(".", parts);
        }
    }

    public Attr? Get(string name)
    {
        if (index == null)
        {
            // small nodes: linear scan is faster than building a dictionary
            if (kids.Count <= 6)
            {
                foreach (var k in kids) if (string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase)) return k;
                return null;
            }
            index = new Dictionary<string, Attr>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in kids) index.TryAdd(k.Name, k);
        }
        return index.TryGetValue(name, out var r) ? r : null;
    }

    public Attr GetOrCreate(string name)
    {
        var a = Get(name);
        if (a != null) return a;
        a = new Attr(name, this);
        kids.Add(a);
        index?.TryAdd(name, a);
        return a;
    }

    /// <summary>Resolve a dotted path ("a.b.c"), optionally creating missing nodes.</summary>
    public Attr? Find(string path, bool create)
    {
        if (path.Length == 0) return this;
        var cur = this;
        int start = 0;
        while (cur != null)
        {
            int dot = path.IndexOf('.', start);
            string part = dot < 0 ? path.Substring(start) : path.Substring(start, dot - start);
            cur = create ? cur.GetOrCreate(part) : cur.Get(part);
            if (dot < 0) break;
            start = dot + 1;
        }
        return cur;
    }

    public bool Remove(string name)
    {
        var a = Get(name);
        if (a == null) return false;
        kids.Remove(a);
        index?.Remove(name);
        a.Parent = null;
        return true;
    }

    public void Clear()
    {
        foreach (var k in kids) k.Parent = null;
        kids.Clear();
        index = null;
        Value = null;
    }

    public Attr? ChildAt(int i) => i >= 0 && i < kids.Count ? kids[i] : null;

    /// <summary>Deep copy of value and children into this node (existing children with the same name are overwritten).</summary>
    public void CopyFrom(Attr src)
    {
        if (ReferenceEquals(src, this)) return;
        Value = src.Value;
        foreach (var k in src.kids) GetOrCreate(k.Name).CopyFrom(k);
    }

    public void Dump(StringBuilder sb, int depth = 0)
    {
        sb.Append(' ', depth * 2).Append(Name);
        if (Value != null) sb.Append(" = \"").Append(Value).Append('"');
        sb.AppendLine();
        foreach (var k in kids) k.Dump(sb, depth + 1);
    }
}

/// <summary>Stand-in for a C++ engine object created with CreateEntity.</summary>
public sealed class Entity
{
    public int Id;
    public string Class = "";
    public Attr Root = null!;
    public bool Deleted;
    public int Layer;
    /// <summary>Layers this object was added to with LayerAddObject (lower case), for GetEntity enumeration.</summary>
    public readonly HashSet<string> Layers = new();
}
