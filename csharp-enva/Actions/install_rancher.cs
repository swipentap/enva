using Enva.Libs;
using Enva.Orchestration;
using Enva.Services;

namespace Enva.Actions;

public class InstallRancherAction : BaseAction, IAction
{
    public InstallRancherAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "install rancher";
    }

    public bool Execute()
    {
        if (Cfg == null)
        {
            Logger.GetLogger("install_rancher").Printf("Lab configuration is missing for InstallRancherAction.");
            return false;
        }
        if (Cfg.Kubernetes == null)
        {
            Logger.GetLogger("install_rancher").Printf("Kubernetes configuration is missing. Cannot install Rancher.");
            return false;
        }
        Logger.GetLogger("install_rancher").Printf("Installing Rancher on Kubernetes cluster...");
        var context = Kubernetes.BuildKubernetesContext(Cfg);
        if (context == null)
        {
            Logger.GetLogger("install_rancher").Printf("Failed to build Kubernetes context.");
            return false;
        }
        if (context.Control.Count == 0)
        {
            Logger.GetLogger("install_rancher").Printf("No Kubernetes control node found.");
            return false;
        }
        var controlConfig = context.Control[0];
        
        // Use the InstallRancher method from Kubernetes orchestration
        if (!Kubernetes.InstallRancher(context, controlConfig))
        {
            Logger.GetLogger("install_rancher").Printf("Rancher installation failed.");
            return false;
        }
        
        Logger.GetLogger("install_rancher").Printf("Rancher installed successfully.");
        return true;
    }
}

public static class InstallRancherActionFactory
{
    public static IAction NewInstallRancherAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallRancherAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
