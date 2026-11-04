using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;

namespace SeaDogs;

/// <summary>Save games live in port/saves (never in the original SAVE folder): "&lt;name&gt;.sdsave" (the script world) + "&lt;name&gt;.desc" (the description line).</summary>
public static class SaveFiles
{
    public static string Dir = "";

    static string Clean(string name) => Path.GetFileName(name.Replace('\\', '/').TrimEnd('/'));

    public static string PathFor(string name)
    {
        Directory.CreateDirectory(Dir);
        return Path.Combine(Dir, Clean(name) + ".sdsave");
    }

    public static bool Exists(string name) => File.Exists(PathFor(name));

    /// <summary>Saves, newest first.</summary>
    public static List<(string name, long size)> List()
    {
        Directory.CreateDirectory(Dir);
        return new DirectoryInfo(Dir).GetFiles("*.sdsave").OrderByDescending(f => f.LastWriteTimeUtc)
            .Select(f => (Path.GetFileNameWithoutExtension(f.Name), f.Length)).ToList();
    }

    public static void Delete(string name)
    {
        foreach (var ext in new[] { ".sdsave", ".desc" })
        {
            string p = Path.Combine(Dir, Clean(name) + ext);
            if (File.Exists(p)) File.Delete(p);
        }
    }

    public static void WriteDescription(string name, string text)
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(Path.Combine(Dir, Clean(name) + ".desc"), text);
    }

    public static string ReadDescription(string name)
    {
        string p = Path.Combine(Dir, Clean(name) + ".desc");
        return File.Exists(p) ? File.ReadAllText(p) : "";
    }
}
