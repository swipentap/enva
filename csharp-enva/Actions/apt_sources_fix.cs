using System;
using System.Linq;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Actions;

public class FixAptSourcesAction : BaseAction, IAction
{
    public FixAptSourcesAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        SSHService = sshService;
        APTService = aptService;
        PCTService = pctService;
        ContainerID = containerID;
        Cfg = cfg;
        ContainerCfg = containerCfg;
    }

    public override string Description()
    {
        return "apt sources fix";
    }

    public bool Execute()
    {
        if (PCTService == null || string.IsNullOrEmpty(ContainerID))
        {
            Logger.GetLogger("fix_apt_sources").Printf("PCT service or container ID not available");
            return false;
        }
        if (!int.TryParse(ContainerID, out int containerIDInt))
        {
            Logger.GetLogger("fix_apt_sources").Printf("Invalid container ID: {0}", ContainerID);
            return false;
        }
        string[] sedCmds = {
            CLI.Sed.NewSed().Replace("/etc/apt/sources.list", "oracular", "plucky"),
            CLI.Sed.NewSed().Replace("/etc/apt/sources.list", "old-releases.ubuntu.com", "archive.ubuntu.com"),
        };
        bool allSucceeded = true;
        foreach (string sedCmd in sedCmds)
        {
            var (output, exitCode) = PCTService.Execute(containerIDInt, sedCmd, null);
            if (exitCode.HasValue && exitCode.Value != 0)
            {
                if (output.Contains("No such file or directory") || output.Contains("can't read"))
                {
                    Logger.GetLogger("fix_apt_sources").Printf("sources.list not found (may use sources.list.d), skipping fix");
                }
                else
                {
                    Logger.GetLogger("fix_apt_sources").Printf("Failed to fix apt sources: {0}", output);
                    allSucceeded = false;
                }
            }
        }
        return allSucceeded;
    }
}

public static class FixAptSourcesActionFactory
{
    public static IAction NewFixAptSourcesAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new FixAptSourcesAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
