using System;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Actions;

public class SysctlOverrideAction : BaseAction, IAction
{
    public SysctlOverrideAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "systemd sysctl override";
    }

    public bool Execute()
    {
        if (PCTService == null || string.IsNullOrEmpty(ContainerID))
        {
            Logger.GetLogger("sysctl_override").Printf("PCT service or container ID not available");
            return false;
        }
        if (!int.TryParse(ContainerID, out int containerIDInt))
        {
            Logger.GetLogger("sysctl_override").Printf("Invalid container ID: {0}", ContainerID);
            return false;
        }

        string mkdirCmd = CLI.Files.NewFileOps().Mkdir("/etc/systemd/system/systemd-sysctl.service.d", true).ToCommand();
        var (output, exitCode) = PCTService.Execute(containerIDInt, mkdirCmd, null);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("sysctl_override").Printf("create sysctl override directory failed with exit code {0}", exitCode?.ToString() ?? "null");
            if (!string.IsNullOrEmpty(output))
            {
                var lines = output.Split('\n');
                if (lines.Length > 0)
                {
                    Logger.GetLogger("sysctl_override").Printf("create sysctl override directory output: {0}", lines[lines.Length - 1]);
                }
            }
            return false;
        }

        string overrideCmd = CLI.Files.NewFileOps().Write("/etc/systemd/system/systemd-sysctl.service.d/override.conf", "[Service]\nImportCredential=\n").ToCommand();
        (output, exitCode) = PCTService.Execute(containerIDInt, overrideCmd, null);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("sysctl_override").Printf("write sysctl override failed with exit code {0}", exitCode?.ToString() ?? "null");
            if (!string.IsNullOrEmpty(output))
            {
                var lines = output.Split('\n');
                if (lines.Length > 0)
                {
                    Logger.GetLogger("sysctl_override").Printf("write sysctl override output: {0}", lines[lines.Length - 1]);
                }
            }
            return false;
        }

        string reloadCmd = "systemctl daemon-reload && systemctl stop systemd-sysctl.service || true && systemctl start systemd-sysctl.service || true";
        (output, exitCode) = PCTService.Execute(containerIDInt, reloadCmd, null);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("sysctl_override").Printf("reload systemd-sysctl failed with exit code {0}", exitCode?.ToString() ?? "null");
            if (!string.IsNullOrEmpty(output))
            {
                var lines = output.Split('\n');
                if (lines.Length > 0)
                {
                    Logger.GetLogger("sysctl_override").Printf("reload systemd-sysctl output: {0}", lines[lines.Length - 1]);
                }
            }
            return false;
        }
        return true;
    }
}

public static class SysctlOverrideActionFactory
{
    public static IAction NewSysctlOverrideAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new SysctlOverrideAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
