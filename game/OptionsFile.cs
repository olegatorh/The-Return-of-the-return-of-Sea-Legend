using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using StormScript.Runtime;

namespace SeaDogs;

/// <summary>
/// The engine's game options file (MSG_INTERFACE_SAVEOPTIONS / LOADOPTIONS, INTERFACE\option_sl.c): the script hands over an attribute tree (sound volumes, options of the
/// game, mouse sensitivity, invert, the key bindings of every control group ...) and gets it back at the next start. Stored as "path TAB value" lines in port/saves/options.txt.
/// </summary>
public static class OptionsFile
{
    static string FilePath(string name) => Path.Combine(SaveFiles.Dir.Length > 0 ? SaveFiles.Dir : ".", name + ".txt");

    public static void Save(string name, Attr root)
    {
        var sb = new StringBuilder();
        Walk(root, "", sb);
        try { Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath(name)))!); File.WriteAllText(FilePath(name), sb.ToString(), new UTF8Encoding(false)); }
        catch (Exception e) { Godot.GD.PrintErr("[options] cannot save: " + e.Message); }
    }

    static void Walk(Attr a, string prefix, StringBuilder sb)
    {
        foreach (var k in a.Children)
        {
            string p = prefix.Length == 0 ? k.Name : prefix + "." + k.Name;
            if (k.Value != null) sb.Append(p).Append('\t').Append(k.Value.Replace("\n", "\n")).Append('\n');
            Walk(k, p, sb);
        }
    }

    /// <summary>Fills <paramref name="root"/> from the file (existing attributes are overwritten); false when there is no file.</summary>
    public static bool Load(string name, Attr root)
    {
        if (!File.Exists(FilePath(name))) return false;
        foreach (var line in File.ReadAllLines(FilePath(name)))
        {
            int t = line.IndexOf('\t');
            if (t <= 0 || line.Contains(".Port_")) continue;        // controls of an earlier port version: only the original controls exist
            root.Find(line.Substring(0, t), true)!.Value = line.Substring(t + 1).Replace("\n", "\n");
        }
        return true;
    }
}
