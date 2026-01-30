using System;
using System.Linq;

namespace Enva.CLI;

public class Dpkg
{
    private bool all;
    private string? logFile;
    private bool suppressErrors = true;

    public static Dpkg NewDpkg()
    {
        return new Dpkg();
    }

    public Dpkg All(bool value)
    {
        all = value;
        return this;
    }

    public Dpkg LogFile(string path)
    {
        logFile = path;
        return this;
    }

    public Dpkg SuppressErrors(bool value)
    {
        suppressErrors = value;
        return this;
    }

    public string Configure()
    {
        var parts = new System.Collections.Generic.List<string> { "dpkg" };
        if (all)
        {
            parts.Add("--configure");
            parts.Add("-a");
        }
        else
        {
            parts.Add("--configure");
        }
        if (!string.IsNullOrEmpty(logFile))
        {
            parts.Add($">{Quote(logFile)}");
        }
        return string.Join(" ", parts);
    }

    public string Divert(string path, bool quiet, bool local, bool rename, string action)
    {
        var parts = new System.Collections.Generic.List<string> { "dpkg-divert" };
        if (quiet)
        {
            parts.Add("--quiet");
        }
        if (local)
        {
            parts.Add("--local");
        }
        if (rename)
        {
            parts.Add("--rename");
        }
        parts.Add(action);
        parts.Add(Quote(path));
        return string.Join(" ", parts);
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
