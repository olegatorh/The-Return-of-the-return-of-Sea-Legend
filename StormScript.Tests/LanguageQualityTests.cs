using System.Text;
using System.Text.RegularExpressions;
using StormScript.Runtime;
using Xunit;

namespace StormScript.Tests;

/// <summary>
/// Regression checks of the Ukrainian layer (python twins: tools/scan_lang.py, tools/scan_texts.py): every Cyrillic string literal of the
/// original scripts is looked up the way the interpreter does it; the shown text must not keep Russian-only letters (ы э ъ ё), known
/// Russianisms or whole untranslated sentences. Thresholds only go down.
/// </summary>
public sealed class LanguageQualityTests
{
    static readonly Regex Lit = new("\"([^\"\\r\\n]*)\"", RegexOptions.Compiled);
    static readonly Regex Cyr = new("[А-Яа-яЁё]", RegexOptions.Compiled);
    static readonly Regex RuLetters = new("[ыэъёЫЭЪЁ]", RegexOptions.Compiled);
    static readonly Regex Word = new("[А-Яа-яЁёІіЇїЄєҐґ]+", RegexOptions.Compiled);
    // Russian words that must never appear in Ukrainian text (not valid Ukrainian forms)
    static readonly HashSet<string> Russianisms = new()
    {
        "спроси", "красавица", "пойдите", "пойди", "красавчик", "деньги", "хорошо", "спасибо", "пожалуйста", "корабль", "капитан",
        "сейчас", "теперь", "потом", "чтобы", "потому", "почему", "нужно", "здесь", "очень", "только", "тебя", "меня", "мне"
    };

    static string LangDir => Path.Combine(Paths.Port, "lang");

    static IEnumerable<(string file, string ru)> Literals()
    {
        var seen = new HashSet<string>();
        foreach (var path in Directory.EnumerateFiles(Paths.Merged, "*.*", SearchOption.AllDirectories))
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".c" && ext != ".h") continue;
            string file = Path.GetFileNameWithoutExtension(path);
            string text = Regex.Replace(Lexer.ReadFile(path), @"/\*.*?\*/", "", RegexOptions.Singleline);
            foreach (var line in text.Split('\n'))
            {
                string code = line;
                int c = line.IndexOf("//", StringComparison.Ordinal);
                if (c >= 0 && line.Count(ch => ch == '"') % 2 == 0 && !Regex.IsMatch(line, "\"[^\"]*//[^\"]*\"")) code = line[..c];
                foreach (Match m in Lit.Matches(code))
                {
                    string s = m.Groups[1].Value;
                    if (Cyr.IsMatch(s) && seen.Add(file + "\0" + s)) yield return (file, s);
                }
            }
        }
    }

    [Fact]
    public void ShownScriptStringsKeepNoRussianLettersRussianismsOrWholeSentences()
    {
        if (!Paths.HasMerged || !Directory.Exists(Path.Combine(LangDir, "ukrainian", "strings"))) return;
        var lang = new LanguageService(Paths.Game, LangDir, "Ukrainian");
        int letters = 0, russianisms = 0, missingSentences = 0, total = 0;
        var samples = new List<string>();
        foreach (var (file, ru) in Literals())
        {
            total++;
            if (ru.StartsWith("voice", StringComparison.OrdinalIgnoreCase)) continue;      // sound file names
            string? t = lang.LiteralText(file, ru);
            if (t == null)
            {
                if (Word.Matches(ru).Count >= 3) { missingSentences++; if (samples.Count < 8) samples.Add("MISSING " + file + ": " + ru); }
                continue;
            }
            if (RuLetters.IsMatch(t)) { letters++; if (samples.Count < 8) samples.Add("RU-LETTER " + file + ": " + t); }
            if (Word.Matches(t).Any(w => Russianisms.Contains(w.Value.ToLowerInvariant()))) { russianisms++; if (samples.Count < 8) samples.Add("RUSSIANISM " + file + ": " + t); }
        }
        Assert.True(total > 10000, "literals found: " + total);
        string info = string.Join("\n", samples);
        Assert.True(letters <= 0, $"{letters} shown strings keep Russian-only letters\n{info}");
        Assert.True(russianisms <= 3, $"{russianisms} shown strings keep Russian words\n{info}");
        Assert.True(missingSentences <= 5, $"{missingSentences} Russian sentences without translation\n{info}");
    }

    [Fact]
    public void TextFileLayerHasNoRussianOnlyLetters()
    {
        string dir = Path.Combine(LangDir, "ukrainian", "texts");
        if (!Directory.Exists(dir)) return;
        int bad = 0; var samples = new List<string>();
        foreach (var f in Directory.EnumerateFiles(dir))
            foreach (var line in File.ReadAllLines(f, new UTF8Encoding(false)))
                if (RuLetters.IsMatch(line) && !line.TrimStart().StartsWith(';')) { bad++; if (samples.Count < 5) samples.Add(Path.GetFileName(f) + ": " + line); }
        Assert.True(bad <= 0, $"{bad} lines of lang/ukrainian/texts keep Russian-only letters\n" + string.Join("\n", samples));
    }
}
