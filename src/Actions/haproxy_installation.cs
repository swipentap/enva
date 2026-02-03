using Enva.Libs;
using Enva.Services;

namespace Enva.Actions;

public class InstallHaproxyAction : BaseAction, IAction
{
    public InstallHaproxyAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "haproxy installation";
    }

    public bool Execute()
    {
        if (APTService == null)
        {
            Logger.GetLogger("install_haproxy").Printf("APT service not initialized");
            return false;
        }
        Logger.GetLogger("install_haproxy").Printf("Installing haproxy package...");
        var (output, exitCode) = APTService.Install(new[] { "haproxy" });
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("install_haproxy").Printf("haproxy installation failed: {0}", output);
            return false;
        }
        return true;
    }
}

public static class InstallHaproxyActionFactory
{
    public static IAction NewInstallHaproxyAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallHaproxyAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
