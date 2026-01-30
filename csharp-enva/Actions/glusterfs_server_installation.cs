using Enva.Libs;
using Enva.Services;

namespace Enva.Actions;

public class InstallGlusterfsAction : BaseAction, IAction
{
    public InstallGlusterfsAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "glusterfs server installation";
    }

    public bool Execute()
    {
        if (APTService == null)
        {
            Logger.GetLogger("install_glusterfs").Printf("APT service not initialized");
            return false;
        }
        Logger.GetLogger("install_glusterfs").Printf("Installing glusterfs-server package...");
        var (output, exitCode) = APTService.Install(new[] { "glusterfs-server" });
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("install_glusterfs").Printf("glusterfs-server installation failed: {0}", output);
            return false;
        }
        return true;
    }
}

public static class InstallGlusterfsActionFactory
{
    public static IAction NewInstallGlusterfsAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallGlusterfsAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
