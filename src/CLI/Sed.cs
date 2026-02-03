using System.Text.RegularExpressions;

namespace Enva.CLI;

public class Sed
{
    private string delimiter = "/";
    private string flags = "g";

    public static Sed NewSed()
    {
        return new Sed();
    }

    public Sed Delimiter(string value)
    {
        delimiter = value;
        return this;
    }

    public Sed Flags(string f)
    {
        flags = f;
        return this;
    }

    public string Replace(string path, string search, string replacement)
    {
        string escapedSearch = EscapeDelimiter(EscapeSingleQuotes(search), delimiter);
        string escapedReplacement = EscapeDelimiter(EscapeSingleQuotes(replacement), delimiter);
        string expression = $"s{delimiter}{escapedSearch}{delimiter}{escapedReplacement}{delimiter}{flags}";
        return $"sed -i '{expression}' {Quote(path)}";
    }

    private string EscapeDelimiter(string value, string delim)
    {
        return value.Replace(delim, "\\" + delim);
    }

    private string EscapeSingleQuotes(string value)
    {
        // Go: strings.ReplaceAll(value, "'", "'\"'\"'")
        return value.Replace("'", "'\"'\"'");
    }

    private string Quote(string s)
    {
        if (s.Contains(" ") || s.Contains("$") || s.Contains("'"))
        {
            return $"'{s.Replace("'", "'\"'\"'")}'";
        }
        return s;
    }
}
