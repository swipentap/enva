using System;

namespace Enva.CLI;

public class Find
{
    private string? directory;
    private int? maxdepth;
    private string? fileType;
    private string? name;

    public static Find NewFind()
    {
        return new Find();
    }

    public Find Directory(string path)
    {
        directory = path;
        return this;
    }

    public Find Maxdepth(int depth)
    {
        maxdepth = depth;
        return this;
    }

    public Find Type(string fileTypeValue)
    {
        fileType = fileTypeValue;
        return this;
    }

    public Find Name(string pattern)
    {
        name = pattern;
        return this;
    }

    public string Delete()
    {
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("Directory must be set");
        }
        string cmd = $"find {Quote(directory)}";
        if (maxdepth.HasValue)
        {
            cmd += $" -maxdepth {maxdepth.Value}";
        }
        if (!string.IsNullOrEmpty(fileType))
        {
            cmd += $" -type {fileType}";
        }
        if (!string.IsNullOrEmpty(name))
        {
            string patternEscaped = EscapeSingleQuotes(name);
            cmd += $" -name '{patternEscaped}'";
        }
        cmd += " -delete || true";
        return cmd;
    }

    public string Count()
    {
        if (string.IsNullOrEmpty(directory))
        {
            throw new InvalidOperationException("Directory must be set");
        }
        string cmd = $"find {Quote(directory)}";
        if (maxdepth.HasValue)
        {
            cmd += $" -maxdepth {maxdepth.Value}";
        }
        if (!string.IsNullOrEmpty(fileType))
        {
            cmd += $" -type {fileType}";
        }
        if (!string.IsNullOrEmpty(name))
        {
            string patternEscaped = EscapeSingleQuotes(name);
            cmd += $" -name '{patternEscaped}'";
        }
        cmd += " -print | wc -l";
        return cmd;
    }

    private string Quote(string s)
    {
        if (s.Contains(" ") || s.Contains("$") || s.Contains("'"))
        {
            return $"'{s.Replace("'", "'\"'\"'")}'";
        }
        return s;
    }

    private string EscapeSingleQuotes(string value)
    {
        return value.Replace("'", "'\"'\"'");
    }
}
