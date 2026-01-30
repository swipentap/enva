using System.Linq;
using Enva.Libs;
using Enva.Services;

namespace Enva.Actions;

public class DisableSystemdResolvedAction : BaseAction, IAction
{
    public DisableSystemdResolvedAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "disable systemd resolved";
    }

    public bool Execute()
    {
        if (SSHService == null)
        {
            Logger.GetLogger("disable_systemd_resolved").Printf("SSH service not initialized");
            return false;
        }
        string command = "systemctl stop systemd-resolved || true && systemctl disable systemd-resolved || true && systemctl mask systemd-resolved || true";
        var (output, exitCode) = SSHService.Execute(command, null);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("disable_systemd_resolved").Printf("disable systemd-resolved failed with exit code {0}", exitCode?.ToString() ?? "null");
            if (!string.IsNullOrEmpty(output))
            {
                var lines = output.Split('\n');
                if (lines.Length > 0)
                {
                    Logger.GetLogger("disable_systemd_resolved").Printf("disable systemd-resolved output: {0}", lines[lines.Length - 1]);
                }
            }
            return false;
        }
        return true;
    }
}

public static class DisableSystemdResolvedActionFactory
{
    public static IAction NewDisableSystemdResolvedAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new DisableSystemdResolvedAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
