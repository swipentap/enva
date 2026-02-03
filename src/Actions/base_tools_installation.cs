using Enva.Libs;
using Enva.Services;

namespace Enva.Actions;

public class InstallBaseToolsAction : BaseAction, IAction
{
    public InstallBaseToolsAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "base tools installation";
    }

    public bool Execute()
    {
        if (APTService == null)
        {
            Logger.GetLogger("install_base_tools").Printf("APTService is required");
            return false;
        }
        var (output, exitCode) = APTService.Install(new[] { "ca-certificates", "curl" });
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("install_base_tools").Printf("Failed to install base tools: {0}", output);
            return false;
        }
        return true;
    }
}

public static class InstallBaseToolsActionFactory
{
    public static IAction NewInstallBaseToolsAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallBaseToolsAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
