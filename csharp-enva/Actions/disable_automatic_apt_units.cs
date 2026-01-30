using System.Linq;
using Enva.Libs;
using Enva.Services;

namespace Enva.Actions;

public class DisableAptUnitsAction : BaseAction, IAction
{
    public DisableAptUnitsAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "disable automatic apt units";
    }

    public bool Execute()
    {
        if (SSHService == null)
        {
            Logger.GetLogger("disable_apt_units").Printf("SSH service not initialized");
            return false;
        }

        string command = "for unit in apt-daily.service apt-daily.timer apt-daily-upgrade.service apt-daily-upgrade.timer; do systemctl stop \"$unit\" || true; systemctl disable \"$unit\" || true; systemctl mask \"$unit\" || true; done";
        var (output, exitCode) = SSHService.Execute(command, null);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("disable_apt_units").Printf("disable automatic apt units failed with exit code {0}", exitCode?.ToString() ?? "null");
            if (!string.IsNullOrEmpty(output))
            {
                var lines = output.Split('\n');
                if (lines.Length > 0)
                {
                    Logger.GetLogger("disable_apt_units").Printf("disable automatic apt units output: {0}", lines[lines.Length - 1]);
                }
            }
            return false;
        }
        return true;
    }
}

public static class DisableAptUnitsActionFactory
{
    public static IAction NewDisableAptUnitsAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new DisableAptUnitsAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
