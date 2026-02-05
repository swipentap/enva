using System;
using System.Linq;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Actions;

public class EnableSshServiceAction : BaseAction, IAction
{
    public EnableSshServiceAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "SSH service enablement";
    }

    public bool Execute()
    {
        if (PCTService == null || string.IsNullOrEmpty(ContainerID))
        {
            Logger.GetLogger("enable_ssh_service").Printf("PCT service or container ID not available");
            return false;
        }
        if (!int.TryParse(ContainerID, out int containerIDInt))
        {
            Logger.GetLogger("enable_ssh_service").Printf("Invalid container ID: {0}", ContainerID);
            return false;
        }
        string enableCmd = CLI.SystemCtl.NewSystemCtl().Service("ssh").Enable();
        var (output, exitCode) = PCTService.Execute(containerIDInt, enableCmd, null);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("enable_ssh_service").Printf("Failed to enable SSH service: {0}", output);
            return false;
        }
        string startCmd = CLI.SystemCtl.NewSystemCtl().Service("ssh").Start();
        (output, exitCode) = PCTService.Execute(containerIDInt, startCmd, null);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("enable_ssh_service").Printf("Failed to start SSH service: {0}", output);
            return false;
        }
        return true;
    }
}

public static class EnableSshServiceActionFactory
{
    public static IAction NewEnableSshServiceAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new EnableSshServiceAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
