using System;
using System.Collections.Generic;
using System.Linq;

namespace Enva.CLI;

public class Curl
{
    private bool failSilently = true;
    private bool silent = true;
    private bool showErrors = true;
    private bool location;
    private string? output;
    private string url = "";

    public static Curl NewCurl()
    {
        return new Curl();
    }

    public Curl FailSilently(bool value)
    {
        failSilently = value;
        return this;
    }

    public Curl Silent(bool value)
    {
        silent = value;
        return this;
    }

    public Curl ShowErrors(bool value)
    {
        showErrors = value;
        return this;
    }

    public Curl Location(bool value)
    {
        location = value;
        return this;
    }

    public Curl Output(string path)
    {
        output = path;
        return this;
    }

    public Curl URL(string urlValue)
    {
        url = urlValue;
        return this;
    }

    public string Download()
    {
        if (string.IsNullOrEmpty(url))
        {
            throw new InvalidOperationException("URL must be set for curl download");
        }
        var flags = new List<string>();
        if (failSilently)
        {
            flags.Add("-f");
        }
        if (silent)
        {
            flags.Add("-s");
        }
        if (showErrors)
        {
            flags.Add("-S");
        }
        if (location)
        {
            flags.Add("-L");
        }
        if (!string.IsNullOrEmpty(output))
        {
            flags.Add($"-o {Quote(output)}");
        }
        string flagStr = flags.Any() ? string.Join(" ", flags) : "";
        return $"curl {flagStr} {Quote(url)}";
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
