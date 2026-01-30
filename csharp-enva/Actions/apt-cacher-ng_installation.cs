using Enva.Libs;
using Enva.Services;

namespace Enva.Actions;

public class InstallAptCacherAction : BaseAction, IAction
{
    public InstallAptCacherAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "apt-cacher-ng installation";
    }

    public bool Execute()
    {
        if (APTService == null)
        {
            Logger.GetLogger("install_apt_cacher").Printf("APT service not initialized");
            return false;
        }
        Logger.GetLogger("install_apt_cacher").Printf("Installing apt-cacher-ng package...");
        var (output, exitCode) = APTService.Install(new[] { "apt-cacher-ng" });
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("install_apt_cacher").Printf("apt-cacher-ng installation failed: {0}", output);
            return false;
        }
        return true;
    }
}

public static class InstallAptCacherActionFactory
{
    public static IAction NewInstallAptCacherAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallAptCacherAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
