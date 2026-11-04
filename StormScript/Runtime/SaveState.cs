using System.IO.Compression;
using System.Text.Json;

namespace StormScript.Runtime;

/// <summary>
/// Snapshot of the whole script world (SaveEngineState / LoadEngineState): every global variable and attribute tree, the run-time event handlers
/// and the posted events. Entities (3D objects, interface, sound) are not stored: after a restore the host rebuilds the world by re-entering the
/// saved location through the scripts. The file is GZip-compressed JSON.
/// </summary>
public sealed partial class Interp
{
    public sealed record SaveInfo(string Description, long Now, string[] Segments);

    // ------------------------------------------------------------------ writing
    public void SaveState(string path, IEnumerable<string> segments, string description)
    {
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        using var w = new Utf8JsonWriter(gz);
        w.WriteStartObject();
        w.WriteString("description", description);
        w.WriteNumber("now", Now);
        w.WriteNumber("timeScale", TimeScale);
        w.WriteStartArray("segments");
        foreach (var s in segments) w.WriteStringValue(s);
        w.WriteEndArray();

        w.WriteStartObject("globals");
        foreach (var (name, slot) in Globals)
        {
            w.WritePropertyName(name);
            WriteSlot(w, slot);
        }
        w.WriteEndObject();

        w.WriteStartArray("handlers");
        foreach (var (evt, list) in Handlers)
            foreach (var (fn, prio) in list)
                if (!staticHandlers.Contains((evt.ToLowerInvariant(), fn.ToLowerInvariant())))
                {
                    w.WriteStartArray(); w.WriteStringValue(evt); w.WriteStringValue(fn); w.WriteNumberValue(prio); w.WriteEndArray();
                }
        w.WriteEndArray();

        w.WriteStartArray("posted");
        foreach (var p in postQueue)
        {
            w.WriteStartObject();
            w.WriteNumber("due", p.Due);
            w.WriteString("name", p.Name);
            w.WriteStartArray("data");
            foreach (var v in p.Data) WriteValue(w, v, false);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }

    void WriteSlot(Utf8JsonWriter w, Slot s)
    {
        w.WriteStartObject();
        w.WriteString("t", s.Type);
        if (s.Arr != null)
        {
            w.WriteStartArray("a");
            foreach (var e in s.Arr) WriteValue(w, e.Val, e.Type == "object");
            w.WriteEndArray();
        }
        else
        {
            w.WritePropertyName("v");
            WriteValue(w, s.Val, s.Type == "object");
        }
        w.WriteEndObject();
    }

    void WriteValue(Utf8JsonWriter w, V v, bool fullTree)
    {
        w.WriteStartObject();
        switch (v.T)
        {
            case VT.Int: w.WriteNumber("i", v.I); break;
            case VT.Float: w.WriteNumber("f", v.F); break;
            case VT.String: w.WriteString("s", v.S ?? ""); break;
            case VT.Attr:
                if (v.Attr == null) w.WriteNull("p");
                else if (fullTree) { w.WritePropertyName("tree"); WriteTree(w, v.Attr); }
                else w.WriteString("p", v.Attr.Path);
                break;
            case VT.Slot:
                w.WriteString("sl", v.Slot?.Name ?? "");
                break;
            default: w.WriteNumber("z", 0); break;
        }
        w.WriteEndObject();
    }

    static void WriteTree(Utf8JsonWriter w, Attr a)
    {
        w.WriteStartObject();
        w.WriteString("n", a.Name);
        if (a.Value != null) w.WriteString("v", a.Value);
        if (a.Count > 0)
        {
            w.WriteStartArray("c");
            foreach (var k in a.Children) WriteTree(w, k);
            w.WriteEndArray();
        }
        w.WriteEndObject();
    }

    // ------------------------------------------------------------------ reading
    public static SaveInfo ReadInfo(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var doc = JsonDocument.Parse(gz);
        var r = doc.RootElement;
        return new SaveInfo(r.GetProperty("description").GetString() ?? "", r.GetProperty("now").GetInt64(),
            r.GetProperty("segments").EnumerateArray().Select(x => x.GetString() ?? "").ToArray());
    }

    /// <summary>Restores variables, attribute trees, run-time handlers and posted events from a snapshot. Loaded segments must already be in place.</summary>
    public void RestoreState(string path)
    {
        using var fs = File.OpenRead(path);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        using var doc = JsonDocument.Parse(gz);
        var root = doc.RootElement;
        var fixups = new List<(Slot slot, string path)>();

        foreach (var g in root.GetProperty("globals").EnumerateObject())
        {
            var j = g.Value;
            string type = j.GetProperty("t").GetString() ?? "int";
            if (!Globals.TryGetValue(g.Name, out var slot) || slot.Type != type) { slot = new Slot(g.Name, type); Globals[g.Name] = slot; }
            if (j.TryGetProperty("a", out var arr))
            {
                int n = arr.GetArrayLength();
                if (slot.Arr == null || slot.Arr.Length != n)
                {
                    slot.Arr = new Slot[n];
                    for (int i = 0; i < n; i++) slot.Arr[i] = new Slot($"{g.Name}[{i}]", type) { Val = DefaultValue(type, $"{g.Name}[{i}]") };
                }
                int k = 0;
                foreach (var e in arr.EnumerateArray()) ReadValue(slot.Arr[k++], e, fixups);
            }
            else
            {
                slot.Arr = null;
                ReadValue(slot, j.GetProperty("v"), fixups);
            }
        }
        // references (aref/ref variables) can only be resolved once every tree exists
        foreach (var (slot, p) in fixups) slot.Val = V.A(ResolvePath(p));

        // run-time handlers and posted events
        ClearDynamicHandlers();
        foreach (var h in root.GetProperty("handlers").EnumerateArray())
            SetHandler(h[0].GetString()!, h[1].GetString()!, h[2].GetInt32());
        postQueue.Clear();
        Now = root.GetProperty("now").GetInt64();
        TimeScale = (float)root.GetProperty("timeScale").GetDouble();
        foreach (var p in root.GetProperty("posted").EnumerateArray())
        {
            var data = new List<V>();
            foreach (var d in p.GetProperty("data").EnumerateArray())
            {
                var tmp = new Slot("tmp", "ref");
                var fx = new List<(Slot, string)>();
                ReadValue(tmp, d, fx);
                foreach (var (s2, pp) in fx) tmp.Val = V.A(ResolvePath(pp));
                data.Add(tmp.Val);
            }
            postQueue.Add(new Posted(p.GetProperty("due").GetInt64(), postSeq++, p.GetProperty("name").GetString()!, data));
        }
    }

    void ReadValue(Slot slot, JsonElement e, List<(Slot slot, string path)> fixups)
    {
        if (e.TryGetProperty("i", out var i)) slot.Val = V.Int(i.GetInt32());
        else if (e.TryGetProperty("f", out var f)) slot.Val = V.Float((float)f.GetDouble());
        else if (e.TryGetProperty("s", out var s)) slot.Val = V.Str(s.GetString() ?? "");
        else if (e.TryGetProperty("tree", out var tree))
        {
            var root = slot.Val.Attr ?? new Attr(slot.Name);
            root.Clear();
            ReadTree(root, tree, true);
            slot.Val = V.A(root);
        }
        else if (e.TryGetProperty("p", out var p))
        {
            if (p.ValueKind == JsonValueKind.Null) slot.Val = V.A(null);
            else fixups.Add((slot, p.GetString() ?? ""));
        }
        else if (e.TryGetProperty("sl", out var sl))
        {
            string n = sl.GetString() ?? "";
            slot.Val = Globals.TryGetValue(n, out var target) ? V.Ref(target) : V.A(null);
        }
        else slot.Val = V.Void;
    }

    static void ReadTree(Attr a, JsonElement j, bool isRoot)
    {
        if (j.TryGetProperty("v", out var v)) a.Value = v.GetString();
        if (j.TryGetProperty("c", out var c))
            foreach (var k in c.EnumerateArray())
            {
                string name = k.GetProperty("n").GetString() ?? "";
                var child = a.GetOrCreate(name);
                ReadTree(child, k, false);
            }
    }

    Attr? ResolvePath(string path)
    {
        if (path.Length == 0) return null;
        int dot = path.IndexOf('.');
        string rootName = dot < 0 ? path : path.Substring(0, dot);
        Attr? root = null;
        int br = rootName.IndexOf('[');
        if (br > 0 && rootName.EndsWith(']') && int.TryParse(rootName.AsSpan(br + 1, rootName.Length - br - 2), out int idx))
        {
            if (Globals.TryGetValue(rootName.Substring(0, br), out var arr) && arr.Arr != null && idx >= 0 && idx < arr.Arr.Length) root = arr.Arr[idx].Val.Attr;
        }
        else if (Globals.TryGetValue(rootName, out var g)) root = g.Val.Attr;
        if (root == null) return null;
        return dot < 0 ? root : root.Find(path.Substring(dot + 1), true);
    }
}
