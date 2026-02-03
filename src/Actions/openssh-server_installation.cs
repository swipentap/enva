using Enva.Libs;
using Enva.Services;

namespace Enva.Actions;

public class InstallOpensshServerAction : BaseAction, IAction
{
    public InstallOpensshServerAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "openssh-server installation";
    }

    public bool Execute()
    {
        if (APTService == null)
        {
            Logger.GetLogger("install_openssh_server").Printf("APT service not initialized");
            return false;
        }
        Logger.GetLogger("install_openssh_server").Printf("Installing openssh-server package...");
        var (output, exitCode) = APTService.Install(new[] { "openssh-server" });
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("install_openssh_server").Printf("openssh-server installation failed: {0}", output);
            return false;
        }
        return true;
    }
}

public static class InstallOpensshServerActionFactory
{
    public static IAction NewInstallOpensshServerAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallOpensshServerAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
