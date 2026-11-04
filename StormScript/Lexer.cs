using System.Text;

namespace StormScript;

public enum TokKind { Ident, Int, Float, String, Punct, Eof }

/// <summary>A lexical token. <see cref="Via"/> is the #define name this token was substituted from, if any.</summary>
public sealed class Token
{
    public TokKind Kind;
    public string Text = "";
    public int Line;
    public string File = "";
    public string? Via;

    public bool Is(string punctOrKeyword) => (Kind == TokKind.Punct || Kind == TokKind.Ident) && Text == punctOrKeyword;
    public override string ToString() => $"{Kind}:{Text}@{Line}";
    public Token Clone() => (Token)MemberwiseClone();
}

public sealed class LexException(string message, string file, int line) : Exception($"{file}({line}): {message}")
{
    public string File { get; } = file;
    public int Line { get; } = line;
}

/// <summary>
/// Lexer for Storm Engine script files. Strings have no escape sequences: a backslash is a literal
/// character (paths such as "Voice\ALMI\ALMI001" are common).
/// </summary>
public static class Lexer
{
    static readonly string[] Ops2 =
        ["++", "--", "+=", "-=", "*=", "/=", "%=", "==", "!=", "<=", ">=", "&&", "||", "<<", ">>"];
    const string Ops1 = "+-*/%=<>!&|^~?:;,.(){}[]#";

    public static Encoding Cp1251
    {
        get
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251);
        }
    }

    public static string ReadFile(string path) => Cp1251.GetString(File.ReadAllBytes(path));

    public static List<Token> Lex(string src, string file)
    {
        var toks = new List<Token>(src.Length / 4);
        int i = 0, line = 1, n = src.Length;
        while (i < n)
        {
            char c = src[i];
            if (c == '\n') { line++; i++; continue; }
            if (char.IsWhiteSpace(c) || c == '\0' || c == '\x1a') { i++; continue; }

            if (c == '/' && i + 1 < n && src[i + 1] == '/')
            {
                while (i < n && src[i] != '\n') i++;
                continue;
            }
            if (c == '/' && i + 1 < n && src[i + 1] == '*')
            {
                int start = line;
                i += 2;
                while (i + 1 < n && !(src[i] == '*' && src[i + 1] == '/'))
                {
                    if (src[i] == '\n') line++;
                    i++;
                }
                if (i + 1 >= n) throw new LexException("unterminated block comment", file, start);
                i += 2;
                continue;
            }

            if (c == '"')
            {
                int s = ++i;
                while (i < n && src[i] != '"' && src[i] != '\n') i++;
                if (i >= n || src[i] != '"') throw new LexException("unterminated string", file, line);
                toks.Add(new Token { Kind = TokKind.String, Text = src.Substring(s, i - s), Line = line, File = file });
                i++;
                continue;
            }

            bool afterDot = toks.Count > 0 && toks[^1].Kind == TokKind.Punct && toks[^1].Text == ".";
            if (char.IsAsciiLetter(c) || c == '_' || (afterDot && char.IsAsciiDigit(c)) || (c > 127 && char.IsLetter(c)))
            {
                int s = i;
                // the original compiler accepts any cp1251 letter in names (shipped scripts contain a Cyrillic 'c' typo)
                while (i < n && (char.IsAsciiLetterOrDigit(src[i]) || src[i] == '_' || (src[i] > 127 && char.IsLetter(src[i])))) i++;
                toks.Add(new Token { Kind = TokKind.Ident, Text = src.Substring(s, i - s), Line = line, File = file });
                continue;
            }

            bool afterOperand = toks.Count > 0 &&
                (toks[^1].Kind == TokKind.Ident || toks[^1].Is(")") || toks[^1].Is("]"));
            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < n && char.IsAsciiDigit(src[i + 1]) && !afterOperand))
            {
                int s = i;
                bool isFloat = false;
                if (c == '0' && i + 1 < n && (src[i + 1] == 'x' || src[i + 1] == 'X'))
                {
                    i += 2;
                    while (i < n && Uri.IsHexDigit(src[i])) i++;
                }
                else
                {
                    while (i < n && char.IsAsciiDigit(src[i])) i++;
                    if (i < n && src[i] == '.' && !(i + 1 < n && (char.IsAsciiLetter(src[i + 1]) || src[i + 1] == '_' || src[i + 1] == '(')))
                    {
                        isFloat = true;
                        i++;
                        while (i < n && char.IsAsciiDigit(src[i])) i++;
                    }
                    if (i < n && (src[i] == 'e' || src[i] == 'E') && i + 1 < n &&
                        (char.IsAsciiDigit(src[i + 1]) || ((src[i + 1] == '+' || src[i + 1] == '-') && i + 2 < n && char.IsAsciiDigit(src[i + 2]))))
                    {
                        isFloat = true;
                        i += 2;
                        while (i < n && char.IsAsciiDigit(src[i])) i++;
                    }
                }
                string text = src.Substring(s, i - s);
                toks.Add(new Token { Kind = isFloat ? TokKind.Float : TokKind.Int, Text = text, Line = line, File = file });
                continue;
            }

            if (i + 1 < n)
            {
                string two = src.Substring(i, 2);
                if (Array.IndexOf(Ops2, two) >= 0)
                {
                    toks.Add(new Token { Kind = TokKind.Punct, Text = two, Line = line, File = file });
                    i += 2;
                    continue;
                }
            }
            if (Ops1.IndexOf(c) >= 0)
            {
                toks.Add(new Token { Kind = TokKind.Punct, Text = c.ToString(), Line = line, File = file });
                i++;
                continue;
            }

            throw new LexException($"unexpected character '{c}' (0x{(int)c:X2})", file, line);
        }
        toks.Add(new Token { Kind = TokKind.Eof, Text = "<eof>", Line = line, File = file });
        return toks;
    }
}
