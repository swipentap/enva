using System;

namespace Enva.CLI;

public class Process
{
    private int signal = 9;
    private bool fullMatch;
    private bool suppressErrors = true;

    public static Process NewProcess()
    {
        return new Process();
    }

    public Process Signal(int value)
    {
        signal = value;
        return this;
    }

    public Process FullMatch(bool value)
    {
        fullMatch = value;
        return this;
    }

    public Process SuppressErrors(bool value)
    {
        suppressErrors = value;
        return this;
    }

    public string Pkill(string pattern)
    {
        string flags = $"-{signal}";
        if (fullMatch)
        {
            flags += " -f";
        }
        string cmd = $"pkill {flags} {Quote(pattern)}";
        if (suppressErrors)
        {
            cmd += " || true";
        }
        return cmd;
    }

    public string LsofFile(string filePath)
    {
        string cmd = $"lsof -t {Quote(filePath)}";
        cmd += " | head -1";
        return cmd;
    }

    public string FuserFile(string filePath)
    {
        string cmd = $"fuser {Quote(filePath)}";
        cmd += " | grep -oE '[0-9]+' | head -1";
        return cmd;
    }

    public string CheckPID(int pid)
    {
        string cmd = $"kill -0 {pid}";
        cmd += " && echo exists || echo not_found";
        return cmd;
    }

    public string GetProcessName(int pid)
    {
        string cmd = $"ps -p {pid} -o comm=";
        cmd += " || echo unknown";
        return cmd;
    }

    public string Kill(int pid)
    {
        string cmd = $"kill -{signal} {pid}";
        if (suppressErrors)
        {
            cmd += " || true";
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
