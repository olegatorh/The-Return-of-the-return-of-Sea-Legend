using System.Text;

namespace StormScript.Runtime;

public delegate V BuiltinFn(Interp it, V[] a);

/// <summary>Script-visible functions implemented by the host. Names are case-insensitive.</summary>
public static class Builtins
{
    public static readonly Dictionary<string, BuiltinFn> Table = new(StringComparer.OrdinalIgnoreCase);

    static readonly System.Text.RegularExpressions.Regex AssembleRx = new(@"#([sdfSDF])(?:\.(\d))?([^#\s]+)#");

    /// <summary>Fills the "#sName#", "#dName#", "#f.2Name#" markers of a text from the attributes of <paramref name="db"/>.</summary>
    public static string AssembleString(string fmt, Attr? db)
        => AssembleRx.Replace(fmt, m =>
        {
            string v = db?.Find(m.Groups[3].Value, false)?.Value ?? "";
            switch (char.ToLowerInvariant(m.Groups[1].Value[0]))
            {
                case 'd': return Conv.ToInt(V.Str(v)).ToString();
                case 'f':
                    int dec = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 2;
                    return Conv.ToFloat(V.Str(v)).ToString("F" + dec, System.Globalization.CultureInfo.InvariantCulture);
                default: return v;
            }
        });

    static V Arg(V[] a, int i) => i < a.Length ? a[i] : V.Void;
    static string S(V[] a, int i) => Conv.ToStr(Arg(a, i));
    static int I(V[] a, int i) => Conv.ToInt(Arg(a, i));
    static float F(V[] a, int i) => Conv.ToFloat(Arg(a, i));
    static void Add(string name, BuiltinFn fn) => Table[name] = fn;
    static void Nop(params string[] names) { foreach (var n in names) Table[n] = (_, _) => V.Void; }
    static void Zero(params string[] names) { foreach (var n in names) Table[n] = (_, _) => V.Int(0); }

    static V Num(V v, Func<int, int> fi, Func<float, float> ff)
    {
        v = Conv.Decay(v);
        return v.T == VT.Float ? V.Float(ff(v.F)) : V.Int(fi(Conv.ToInt(v)));
    }

    static Builtins()
    {
        // ---- conversion / random / math
        Add("sti", (_, a) => V.Int(I(a, 0)));
        Add("makeint", (_, a) => V.Int(I(a, 0)));
        Add("stf", (_, a) => V.Float(F(a, 0)));
        Add("makefloat", (_, a) => V.Float(F(a, 0)));
        Add("rand", (it, a) =>
        {
            int n = I(a, 0);
            if (n == 0) return V.Int(0);
            int r = it.Rng.Next(0, Math.Abs(n) + 1);
            return V.Int(n < 0 ? -r : r);
        });
        Add("frnd", (it, _) => V.Float((float)it.Rng.NextDouble()));
        Add("abs", (_, a) => Num(Arg(a, 0), Math.Abs, MathF.Abs));
        Add("sqr", (_, a) => Num(Arg(a, 0), x => x * x, x => x * x));
        Add("sqrt", (_, a) => Num(Arg(a, 0), x => (int)MathF.Sqrt(Math.Max(0, x)), x => MathF.Sqrt(Math.Max(0, x))));
        Add("sin", (_, a) => V.Float(MathF.Sin(F(a, 0))));
        Add("cos", (_, a) => V.Float(MathF.Cos(F(a, 0))));
        Add("tan", (_, a) => V.Float(MathF.Tan(F(a, 0))));
        Add("atan", (_, a) => V.Float(MathF.Atan(F(a, 0))));
        Add("asin", (_, a) => V.Float(MathF.Asin(Math.Clamp(F(a, 0), -1f, 1f))));
        Add("acos", (_, a) => V.Float(MathF.Acos(Math.Clamp(F(a, 0), -1f, 1f))));
        Add("atan2", (_, a) => V.Float(MathF.Atan2(F(a, 0), F(a, 1))));
        Add("pow", (_, a) => V.Float(MathF.Pow(F(a, 0), F(a, 1))));
        Add("degree2radian", (_, a) => V.Float(F(a, 0) * MathF.PI / 180f));
        Add("clampf", (_, a) => V.Float(Math.Clamp(F(a, 0), 0f, 1f)));
        Add("bring2range", (_, a) =>
        {
            float min1 = F(a, 0), max1 = F(a, 1), min2 = F(a, 2), max2 = F(a, 3), v = F(a, 4);
            if (v < min1) v = min1;
            if (v > max1) v = max1;
            return V.Float(max1 == min1 ? min2 : min2 + (v - min1) * (max2 - min2) / (max1 - min1));
        });
        Add("bring2rangenocheck", (_, a) =>
        {
            float min1 = F(a, 0), max1 = F(a, 1), min2 = F(a, 2), max2 = F(a, 3), v = F(a, 4);
            return V.Float(max1 == min1 ? min2 : min2 + (v - min1) * (max2 - min2) / (max1 - min1));
        });
        Add("argb", (_, a) => V.Int(unchecked((I(a, 0) << 24) | (I(a, 1) << 16) | (I(a, 2) << 8) | I(a, 3))));
        Add("shl", (_, a) => V.Int(I(a, 0) << I(a, 1)));
        Add("shr", (_, a) => V.Int(I(a, 0) >> I(a, 1)));
        Add("and", (_, a) => V.Int(I(a, 0) & I(a, 1)));
        Add("or", (_, a) => V.Int(I(a, 0) | I(a, 1)));
        Zero("rdtsc_b", "rdtsc_e");

        // ---- strings
        Add("strlen", (_, a) => V.Int(S(a, 0).Length));
        Add("strcut", (_, a) =>
        {
            string s = S(a, 0);
            int st = I(a, 1), en = I(a, 2);
            if (st > en || st < 0 || en >= s.Length) return V.Str("");
            return V.Str(s.Substring(st, en - st + 1));
        });
        Add("findsubstr", (_, a) =>
        {
            string s = S(a, 0), sub = S(a, 1);
            int st = Math.Max(0, I(a, 2));
            if (sub.Length > s.Length || st > s.Length) return V.Int(-1);
            return V.Int(s.IndexOf(sub, st, StringComparison.OrdinalIgnoreCase));
        });
        Add("getsymbol", (_, a) =>
        {
            string s = S(a, 0);
            int i = I(a, 1);
            return V.Str(i >= 0 && i < s.Length ? s[i].ToString() : "");
        });
        Add("isdigit", (_, a) =>
        {
            string s = S(a, 0);
            int i = I(a, 1);
            return V.Int(i >= 0 && i < s.Length && char.IsAsciiDigit(s[i]) ? 1 : 0);
        });
        Add("fts", (_, a) => V.Str(((double)F(a, 0)).ToString("G" + Math.Clamp(I(a, 1), 1, 17), System.Globalization.CultureInfo.InvariantCulture)));
        Add("trace", (it, a) => { it.Log("TRACE " + S(a, 0)); return V.Void; });
        Add("vartype", (_, a) => V.Str(Arg(a, 0).T switch { VT.Int => "int", VT.Float => "float", VT.String => "string", VT.Attr => "aref", _ => "unknown" }));

        // ---- attributes
        Add("checkattribute", (_, a) =>
        {
            var o = Arg(a, 0).Attr;
            return V.Int(o != null && o.Find(S(a, 1), false) != null ? 1 : 0);
        });
        Add("deleteattribute", (it, a) =>
        {
            var o = Arg(a, 0).Attr;
            if (o == null) return V.Void;
            string path = S(a, 1);
            if (path.Length == 0) { o.Clear(); return V.Void; }
            int dot = path.LastIndexOf('.');
            var parent = dot < 0 ? o : o.Find(path.Substring(0, dot), false);
            parent?.Remove(dot < 0 ? path : path.Substring(dot + 1));
            return V.Void;
        });
        Add("getattributesnum", (_, a) => V.Int(Arg(a, 0).Attr?.Count ?? 0));
        Add("getattributen", (_, a) => V.A(Arg(a, 0).Attr?.ChildAt(I(a, 1))));
        Add("getattributename", (_, a) => V.Str(Arg(a, 0).Attr?.Name ?? ""));
        Add("getattributevalue", (_, a) => V.Str(Arg(a, 0).Attr?.Value ?? ""));
        Add("copyattributes", (_, a) =>
        {
            var d = Arg(a, 0).Attr; var s = Arg(a, 1).Attr;
            if (d != null && s != null) d.CopyFrom(s);
            return V.Void;
        });
        Add("dumpattributes", (it, a) =>
        {
            var sb = new StringBuilder();
            Arg(a, 0).Attr?.Dump(sb);
            it.Log("DUMP\n" + sb);
            return V.Void;
        });
        Add("testref", (_, a) => V.Int(Arg(a, 0).Attr != null ? 1 : 0));

        // ---- events
        Add("seteventhandler", (it, a) => { it.SetHandler(S(a, 0), S(a, 1), I(a, 2)); return V.Void; });
        Add("deleventhandler", (it, a) => { it.DelHandler(S(a, 0), S(a, 1)); return V.Void; });
        Add("geteventdata", (it, _) => it.NextEventData());
        Add("event", (it, a) => it.FireEvent(S(a, 0), a.Skip(2).ToList()));
        Add("postevent", (it, a) => { it.PostEventInternal(S(a, 0), I(a, 1), a.Skip(3).ToList()); return V.Void; });
        Add("clearevents", (it, _) => { it.ClearDynamicHandlers(); return V.Void; });
        Add("clearpostevents", (it, _) => { it.ClearPosted(); return V.Void; });
        Nop("eventsbreak", "breakpoint", "stop");

        // ---- segments / program
        Add("randphrasesimple", (it, a) => V.Str(S(a, new Random().Next(2))));        // one of two phrases at random
        Zero("getentity", "getentitynext");          // entity enumeration of a layer: nothing to find (used to look for the screenshot object)
        Add("getentityname", (_, _) => V.Str(""));
        Add("saveenginestate", (it, a) => V.Int(it.SaveHook?.Invoke(S(a, 0)) == true ? 1 : 0));
        Add("loadenginestate", (it, a) => V.Int(it.LoadHook?.Invoke(S(a, 0)) == true ? 1 : 0));
        Add("loadsegment", (it, a) => V.Int(it.SegmentLoader?.Invoke(S(a, 0)) == true ? 1 : 0));
        Add("unloadsegment", (it, a) => { it.SegmentUnloader?.Invoke(S(a, 0)); return V.Void; });
        Add("segmentisloaded", (it, a) => V.Int(it.SegmentIsLoaded?.Invoke(S(a, 0)) == true ? 1 : 0));
        Add("exitprogram", (it, _) => { it.Exit = true; return V.Void; });
        Add("gettargetplatform", (_, _) => V.Str("pc"));
        Add("getdeltatime", (_, _) => V.Int(16));
        Add("getengineversion", (_, _) => V.Int(1));

        // ---- entities (engine objects are stubs for now; they record what scripts ask of them)
        Add("createentity", (it, a) => V.Int(it.CreateEntity(Arg(a, 0).Attr, S(a, 1)) ? 1 : 0));
        Add("deleteclass", (it, a) => { it.DeleteEntity(Arg(a, 0).Attr); return V.Void; });
        Add("isentity", (_, a) => V.Int(Arg(a, 0).Attr?.Entity is { Deleted: false } ? 1 : 0));
        Add("sendmessage", (it, a) => it.SendMessage(Arg(a, 0).Attr, S(a, 1), a.Skip(2).ToArray()));
        Add("deleteentities", (it, _) => { it.DeleteAllEntities(null); return V.Void; });
        Add("deleteentitiesbytype", (it, a) => { it.DeleteAllEntities(S(a, 0)); return V.Void; });
        Nop("entityupdate", "layercreate", "layeraddobject", "layerdelobject", "layerfreeze", "layersetrealize", "layersetexecute",
            "layerdeletecontent", "layersetmessages", "settimescale", "reloadprogressstart", "reloadprogressupdate",
            "reloadprogressend", "setreloadnexttipsimage", "setreloadprogressbackimage", "executetechnique",
            "lockcontrol", "mapcontrol", "setcontrolflags", "setcontroltreshold", "deletecontrol");
        Zero("createcontrol");

        Add("settimescale", (it, a) => { it.TimeScale = F(a, 0); return V.Void; });

        // ---- texts (language service)
        Add("languagegetlanguage", (it, _) => V.Str(it.Language?.Name ?? "English"));
        Add("languageopenfile", (it, a) => V.Int(it.Language?.Open(S(a, 0)) ?? -1));
        Add("languageclosefile", (it, a) => { it.Language?.Close(I(a, 0)); return V.Void; });
        Add("languageconvertstring", (it, a) => V.Str(it.Language?.Convert(I(a, 0), S(a, 1)) ?? S(a, 1)));
        // native string GetAssembledString(string format, aref data): "#sName#" -> data.Name as text, "#dName#" -> integer, "#f.2Name#" -> number with 2 decimals
        Add("getassembledstring", (it, a) => V.Str(AssembleString(S(a, 0), Arg(a, 1).Attr)));
        Add("xi_convertstring", (it, a) => V.Str(it.Language?.Global(S(a, 0)) ?? S(a, 0)));
        Nop("xi_controlmakeinvert", "xi_setcolorcorrection", "xi_setmousesensitivity");

        // ---- engine natives used by the scripts: lookup by "id" attribute in an array of objects
        Add("nativefindcharacter", (it, a) => V.Int(FindById(a, "NativeFindCharacter")));
        Add("nativefindlocation", (it, a) => V.Int(FindById(a, "NativeFindLocation")));
        Add("nativefindloadcharacter", (it, _) =>
        {
            // index of the entry of Locations[] that currently has a live "location" entity (the loaded location), else -1
            if (it.Globals.TryGetValue("Locations", out var locs) && locs.Arr != null)
                for (int i = 0; i < locs.Arr.Length; i++)
                    if (locs.Arr[i].Val.Attr?.Entity is { Deleted: false } e && string.Equals(e.Class, "location", StringComparison.OrdinalIgnoreCase)) return V.Int(i);
            return V.Int(-1);
        });
    }

    static readonly Dictionary<Slot, Dictionary<string, int>> idCache = new();

    static int FindById(V[] a, string who)
    {
        var arr = Arg(a, 0).Slot;
        if (arr != null && arr.Arr == null && arr.Val.Slot is { Arr: not null } inner) arr = inner;
        if (arr?.Arr == null) return -1;
        string id = S(a, 1);
        if (!idCache.TryGetValue(arr, out var map)) idCache[arr] = map = new(StringComparer.OrdinalIgnoreCase);
        if (map.TryGetValue(id, out int hit) && hit < arr.Arr.Length &&
            string.Equals(arr.Arr[hit].Val.Attr?.Get("id")?.Value, id, StringComparison.OrdinalIgnoreCase)) return hit;
        for (int i = 0; i < arr.Arr.Length; i++)
        {
            string? v = arr.Arr[i].Val.Attr?.Get("id")?.Value;
            if (v != null) map[v] = i;
            if (string.Equals(v, id, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }
}
