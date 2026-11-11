using System.Collections.Generic;
using System.IO;
using StormScript;

namespace SeaDogs;

/// <summary>Storm .ini files: [SECTION] headers and "key = value" lines (keys may repeat), ';' starts a comment line.</summary>
public sealed class IniFile
{
    public sealed class Section(string name)
    {
        public string Name = name;
        public List<(string Key, string Value)> Lines = new();
        public string? Get(string key)
        {
            foreach (var (k, v) in Lines) if (string.Equals(k, key, System.StringComparison.OrdinalIgnoreCase)) return v;
            return null;
        }
        public bool Has(string key) => Get(key) != null;
        public IEnumerable<string> All(string key)
        {
            foreach (var (k, v) in Lines) if (string.Equals(k, key, System.StringComparison.OrdinalIgnoreCase)) yield return v;
        }
    }

    public readonly Dictionary<string, Section> Sections = new(System.StringComparer.OrdinalIgnoreCase);

    /// <param name="firstWins">the original ini reader finds the FIRST section of a name; a duplicate later in the file is ignored (default: the last wins)</param>
    public static IniFile Load(string path, bool firstWins = false)
    {
        var ini = new IniFile();
        Section? cur = null;
        foreach (var raw in Lexer.ReadFile(path).Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] == ';') continue;
            if (line[0] == '[' && line.EndsWith(']'))
            {
                cur = new Section(line.Substring(1, line.Length - 2));
                if (firstWins && ini.Sections.ContainsKey(cur.Name)) { cur = new Section(cur.Name); continue; }   // orphan: its lines go nowhere
                ini.Sections[cur.Name] = cur;
                continue;
            }
            if (cur == null) continue;
            int eq = line.IndexOf('=');
            if (eq < 0) cur.Lines.Add((line, ""));
            else cur.Lines.Add((line.Substring(0, eq).Trim(), line.Substring(eq + 1).Trim()));
        }
        return ini;
    }

    public Section? Get(string name) => Sections.TryGetValue(name, out var s) ? s : null;
}
