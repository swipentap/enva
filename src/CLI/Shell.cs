using System;
using System.Collections.Generic;
using System.Linq;

namespace Enva.CLI;

public class Shell
{
    private string shell = "sh";
    private string? scriptPath;
    private List<string> args = new List<string>();

    public static Shell NewShell()
    {
        return new Shell();
    }

    public Shell SetShell(string shellPath)
    {
        shell = shellPath;
        return this;
    }

    public Shell Script(string path)
    {
        scriptPath = path;
        return this;
    }

    public Shell Args(IEnumerable<string> arguments)
    {
        args = arguments.ToList();
        return this;
    }

    public string Execute()
    {
        if (string.IsNullOrEmpty(scriptPath))
        {
            throw new InvalidOperationException("Script path must be set for shell execution");
        }
        string scriptQuoted = Quote(scriptPath);
        string argsQuoted = "";
        if (args.Any())
        {
            var quotedArgs = args.Select(arg => Quote(arg));
            argsQuoted = string.Join(" ", quotedArgs);
        }
        string cmd = $"{shell} {scriptQuoted}";
        if (!string.IsNullOrEmpty(argsQuoted))
        {
            cmd += " " + argsQuoted;
        }
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
}
