using System.Globalization;
using System.Text;

namespace StormScript.Runtime;

public enum VT : byte { Void, Int, Float, String, Attr, Slot }

/// <summary>
/// A script value. Attr = reference to an attribute node (this is what object / ref / aref variables hold,
/// and what reading "a.b.c" yields). Slot = reference to a variable ("&amp;x" on a scalar).
/// </summary>
public readonly struct V
{
    public readonly VT T;
    public readonly int I;
    public readonly float F;
    public readonly string? S;
    public readonly object? O;

    V(VT t, int i, float f, string? s, object? o) { T = t; I = i; F = f; S = s; O = o; }

    public static readonly V Void = default;
    public static V Int(int i) => new(VT.Int, i, 0, null, null);
    public static V Float(float f) => new(VT.Float, 0, f, null, null);
    public static V Str(string s) => new(VT.String, 0, 0, s, null);
    public static V A(Attr? a) => new(VT.Attr, 0, 0, null, a);
    public static V Ref(Slot s) => new(VT.Slot, 0, 0, null, s);

    public Attr? Attr => T == VT.Attr ? (Attr?)O : null;
    public Slot? Slot => T == VT.Slot ? (Slot?)O : null;

    public override string ToString() => T switch
    {
        VT.Int => I.ToString(),
        VT.Float => Conv.FloatToConcat(F),
        VT.String => S ?? "",
        VT.Attr => $"<attr {(O as Attr)?.Path}>",
        VT.Slot => $"<slot {(O as Slot)?.Name}>",
        _ => "<void>",
    };
}

/// <summary>A variable. Arrays have Arr != null (elements are Slots).</summary>
public sealed class Slot
{
    public string Name = "";
    public string Type = "int";          // int float string bool ref aref object
    public V Val;
    public Slot[]? Arr;
    public Slot(string name, string type) { Name = name; Type = type; }
}

public sealed class ScriptError(string message) : Exception(message);

public static class Conv
{
    public static bool IsAttrType(string t) => t is "ref" or "aref" or "object";

    public static string FloatToConcat(float f)
    {
        // fmt::format("{}", f): shortest round-trip, no trailing ".0"
        string s = f.ToString("R", CultureInfo.InvariantCulture);
        return s.Replace("E+", "e+").Replace("E-", "e-");
    }

    public static string FloatToGcvt(float f)
    {
        string s = ((double)f).ToString("G10", CultureInfo.InvariantCulture);
        return s.Replace("E+", "e+").Replace("E-", "e-");
    }

    public static int ParseIntPrefix(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int i = 0;
        bool neg = false;
        if (s[0] == '-') { neg = true; i++; }
        else if (s[0] == '+') i++;
        long v = 0;
        int digits = 0;
        while (i < s.Length && s[i] >= '0' && s[i] <= '9' && digits < 18) { v = v * 10 + (s[i] - '0'); i++; digits++; }
        if (digits == 0) return 0;
        v = neg ? -v : v;
        return unchecked((int)v);
    }

    public static float ParseFloatPrefix(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        int i = 0, n = s.Length;
        if (i < n && (s[i] == '-' || s[i] == '+')) i++;
        int digStart = i;
        while (i < n && char.IsAsciiDigit(s[i])) i++;
        if (i < n && s[i] == '.') { i++; while (i < n && char.IsAsciiDigit(s[i])) i++; }
        if (i == digStart) return 0;
        if (i < n && (s[i] == 'e' || s[i] == 'E'))
        {
            int j = i + 1;
            if (j < n && (s[j] == '-' || s[j] == '+')) j++;
            if (j < n && char.IsAsciiDigit(s[j])) { while (j < n && char.IsAsciiDigit(s[j])) j++; i = j; }
        }
        return float.TryParse(s.AsSpan(0, i), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 0;
    }

    /// <summary>Value as the string an attribute would hold ("Convert" semantics).</summary>
    public static string ToStr(V v) => v.T switch
    {
        VT.Int => v.I.ToString(CultureInfo.InvariantCulture),
        VT.Float => FloatToGcvt(v.F),
        VT.String => v.S ?? "",
        VT.Attr => (v.O as Attr)?.Value ?? "",
        VT.Slot => v.O is Slot { Arr: null } sl && sl.Val.T != VT.Slot ? ToStr(sl.Val) : "",       // "&variable" passed to a builtin: its value
        _ => "",
    };

    public static int ToInt(V v) => v.T switch
    {
        VT.Int => v.I,
        VT.Float => float.IsFinite(v.F) ? (int)v.F : 0,
        VT.String => ParseIntPrefix(v.S),
        VT.Attr => ParseIntPrefix((v.O as Attr)?.Value),
        VT.Slot => v.O is Slot { Arr: null } sl && sl.Val.T != VT.Slot ? ToInt(sl.Val) : 0,
        _ => 0,
    };

    public static float ToFloat(V v) => v.T switch
    {
        VT.Int => v.I,
        VT.Float => v.F,
        VT.String => ParseFloatPrefix(v.S),
        VT.Attr => ParseFloatPrefix((v.O as Attr)?.Value),
        VT.Slot => v.O is Slot { Arr: null } sl && sl.Val.T != VT.Slot ? ToFloat(sl.Val) : 0,
        _ => 0,
    };

    public static bool ToBool(V v) => v.T switch
    {
        VT.Int => v.I != 0,
        VT.Float => v.F != 0,
        VT.String => !string.IsNullOrEmpty(v.S),
        VT.Attr => !string.IsNullOrEmpty((v.O as Attr)?.Value),
        VT.Slot => true,
        _ => false,
    };

    /// <summary>Replace an attribute node by its string value (what arithmetic sees), keep everything else.</summary>
    public static V Decay(V v) => v.T == VT.Attr ? V.Str((v.O as Attr)?.Value ?? "") : v;

    public static V Coerce(V v, string type)
    {
        switch (type)
        {
            case "int": case "bool": case "dword": return v.T == VT.Int ? v : V.Int(ToInt(v));
            case "float": return v.T == VT.Float ? v : V.Float(ToFloat(v));
            case "string": return v.T == VT.String ? v : V.Str(ToStr(v));
            case "ref": case "aref": case "object":
                return v.T == VT.Attr || v.T == VT.Slot ? v : V.A(null);
            default: return v;
        }
    }

    public static string Cp(string s) => s; // placeholder for later encoding work
}
