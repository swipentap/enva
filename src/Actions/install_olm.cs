using System;
using System.Threading;
using Enva.Libs;
using Enva.Orchestration;
using Enva.Services;

namespace Enva.Actions;

public class InstallOLMAction : BaseAction, IAction
{
    private const string OLMVersion = "v0.28.0";

    public InstallOLMAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "install olm";
    }

    public bool Execute()
    {
        if (Cfg == null)
        {
            Logger.GetLogger("install_olm").Printf("Lab configuration is missing for InstallOLMAction.");
            return false;
        }
        if (Cfg.Kubernetes == null)
        {
            Logger.GetLogger("install_olm").Printf("Kubernetes configuration is missing. Cannot install OLM.");
            return false;
        }

        var context = Kubernetes.BuildKubernetesContext(Cfg);
        if (context == null)
        {
            Logger.GetLogger("install_olm").Printf("Failed to build Kubernetes context.");
            return false;
        }
        if (context.Control.Count == 0)
        {
            Logger.GetLogger("install_olm").Printf("No Kubernetes control node found.");
            return false;
        }

        var controlConfig = context.Control[0];
        string lxcHost = context.LXCHost();
        int controlID = controlConfig.ID;

        var lxcService = new LXCService(lxcHost, Cfg.SSH);
        if (!lxcService.Connect())
        {
            return false;
        }

        try
        {
            var pctService = new PCTService(lxcService);
            var logger = Logger.GetLogger("install_olm");

            // Check if OLM is already installed
            logger.Printf("Checking if OLM is already installed...");
            string checkCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get namespace olm --ignore-not-found -o name";
            (string checkOutput, _) = pctService.Execute(controlID, checkCmd, 30);
            if (checkOutput.Contains("namespace/olm"))
            {
                logger.Printf("OLM is already installed, skipping.");
                return true;
            }

            logger.Printf("Installing OLM {0}...", OLMVersion);
            string installCmd = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && curl -sL https://github.com/operator-framework/operator-lifecycle-manager/releases/download/{OLMVersion}/install.sh | bash -s {OLMVersion}";
            (string installOutput, int? installExit) = pctService.Execute(controlID, installCmd, 300);
            if (installExit.HasValue && installExit.Value != 0)
            {
                logger.Printf("OLM installation failed: {0}", installOutput);
                return false;
            }

            // Wait for OLM pods to be ready
            logger.Printf("Waiting for OLM pods to be ready...");
            int maxWait = 180;
            int waited = 0;
            while (waited < maxWait)
            {
                string readyCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods -n olm --field-selector=status.phase=Running --no-headers | wc -l";
                (string readyCount, _) = pctService.Execute(controlID, readyCmd, 30);
                if (int.TryParse(readyCount.Trim(), out int running) && running >= 2)
                {
                    logger.Printf("OLM is running ({0} pods ready).", running);
                    break;
                }
                if (waited % 30 == 0)
                {
                    logger.Printf("Waiting for OLM pods (waited {0}/{1} seconds)...", waited, maxWait);
                }
                Thread.Sleep(10000);
                waited += 10;
            }

            logger.Printf("OLM {0} installed successfully.", OLMVersion);
            return true;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }
}

public static class InstallOLMActionFactory
{
    public static IAction NewInstallOLMAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallOLMAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
