using Enva.Libs;
using Enva.Services;

namespace Enva.Actions;

public class InstallDockerAction : BaseAction, IAction
{
    public InstallDockerAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "docker installation";
    }

    public bool Execute()
    {
        if (APTService == null)
        {
            Logger.GetLogger("install_docker").Printf("APT service not initialized");
            return false;
        }
        Logger.GetLogger("install_docker").Printf("Installing docker package...");
        var (output, exitCode) = APTService.Install(new[] { "docker.io", "docker-compose" });
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("install_docker").Printf("docker installation failed: {0}", output);
            return false;
        }
        return true;
    }
}

public static class InstallDockerActionFactory
{
    public static IAction NewInstallDockerAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallDockerAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
