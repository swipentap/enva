using System.Collections.Generic;

namespace Enva.CLI;

public class CloudInit
{
    private string? logFile;
    private bool suppressOutput;
    private bool cleanLogs;
    private bool cleanMachineId;
    private bool cleanSeed;
    private bool isWaitCommand;

    public static CloudInit NewCloudInit()
    {
        return new CloudInit();
    }

    public CloudInit LogFile(string file)
    {
        logFile = file;
        return this;
    }

    public CloudInit SuppressOutput(bool suppress)
    {
        suppressOutput = suppress;
        return this;
    }

    public CloudInit Clean(bool logs, bool machineId, bool seed)
    {
        cleanLogs = logs;
        cleanMachineId = machineId;
        cleanSeed = seed;
        isWaitCommand = false;
        return this;
    }

    public string Wait(int? waitSeconds)
    {
        isWaitCommand = true;
        string cmd = "cloud-init status --wait";
        if (waitSeconds.HasValue)
        {
            cmd += $" --wait-timeout {waitSeconds.Value}";
        }
        if (!string.IsNullOrEmpty(logFile))
        {
            cmd += $" --log-file {Quote(logFile)}";
        }
        return cmd;
    }

    public string ToCommand()
    {
        if (isWaitCommand)
        {
            string cmd = "cloud-init status --wait";
            if (!string.IsNullOrEmpty(logFile))
            {
                cmd += $" >{Quote(logFile)}";
            }
            return cmd;
        }
        if (cleanLogs || cleanMachineId || cleanSeed)
        {
            string cmd = "cloud-init clean";
            List<string> flags = new List<string>();
            if (cleanLogs) flags.Add("--logs");
            if (cleanSeed) flags.Add("--seed");
            if (cleanMachineId) flags.Add("--machine-id");
            if (flags.Count > 0)
            {
                cmd += " " + string.Join(" ", flags);
            }
            return cmd;
        }
        throw new Exception("Invalid CloudInit configuration");
    }

    private string Quote(string s)
    {
        if (s.Contains(" ") || s.Contains("$"))
        {
            return $"'{s.Replace("'", "'\"'\"'")}'";
        }
        return s;
    }
}
