using System.Threading;
using Enva.Libs;
using Enva.Orchestration;
using Enva.Services;
using Enva.Verification;

namespace Enva.Actions;

public class SetupKubernetesAction : BaseAction, IAction
{
    public SetupKubernetesAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "setup kubernetes";
    }

    public bool Execute()
    {
        if (Cfg == null)
        {
            Logger.GetLogger("setup_kubernetes").Printf("Lab configuration is missing for SetupKubernetesAction.");
            return false;
        }
        Logger.GetLogger("setup_kubernetes").Printf("Deploying Kubernetes (k3s) cluster...");
        if (!Kubernetes.DeployKubernetes(Cfg))
        {
            Logger.GetLogger("setup_kubernetes").Printf("Kubernetes deployment failed.");
            return false;
        }
        Logger.GetLogger("setup_kubernetes").Printf("Kubernetes deployment completed successfully.");

        // Verify cluster health after deployment
        if (PCTService != null)
        {
            Logger.GetLogger("setup_kubernetes").Printf("Verifying k3s cluster health...");
            Thread.Sleep(10000); // Give services time to stabilize
            if (!Enva.Verification.Verification.VerifyKubernetesCluster(Cfg, PCTService))
            {
                Logger.GetLogger("setup_kubernetes").Printf("⚠ Cluster health verification found issues, but deployment completed");
                // Don't fail deployment, just warn
            }
        }
        return true;
    }
}

public static class SetupKubernetesActionFactory
{
    public static IAction NewSetupKubernetesAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new SetupKubernetesAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}