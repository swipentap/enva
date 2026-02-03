using Enva.Libs;
using Enva.Services;

namespace Enva.Actions;

public class SystemUpgradeAction : BaseAction, IAction
{
    public SystemUpgradeAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "system upgrade";
    }

    public bool Execute()
    {
        if (APTService == null)
        {
            Logger.GetLogger("system_upgrade").Printf("APT service not initialized");
            return false;
        }

        // Run apt update
        var (output, exitCode) = APTService.Update();
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("system_upgrade").Printf("apt update failed");
            return false;
        }

        // Run distribution upgrade
        (output, exitCode) = APTService.DistUpgrade();
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            string outputLower = output.ToLower();
            bool successIndicators = outputLower.Contains("setting up") ||
                outputLower.Contains("processing triggers") ||
                outputLower.Contains("created symlink") ||
                outputLower.Contains("0 upgraded") ||
                outputLower.Contains("0 newly installed");
            if (!successIndicators)
            {
                Logger.GetLogger("system_upgrade").Printf("distribution upgrade failed: {0}", output);
                return false;
            }
        }
        return true;
    }
}

public static class SystemUpgradeActionFactory
{
    public static IAction NewSystemUpgradeAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new SystemUpgradeAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
