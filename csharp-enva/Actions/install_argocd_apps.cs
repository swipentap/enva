using System;
using System.Threading;
using Enva.Libs;
using Enva.Orchestration;
using Enva.Services;

namespace Enva.Actions;

public class InstallArgoCDAppsAction : BaseAction, IAction
{
    public InstallArgoCDAppsAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "install argocd apps";
    }

    public bool Execute()
    {
        if (Cfg == null)
        {
            Logger.GetLogger("install_argocd_apps").Printf("Lab configuration is missing for InstallArgoCDAppsAction.");
            return false;
        }
        if (Cfg.Kubernetes == null)
        {
            Logger.GetLogger("install_argocd_apps").Printf("Kubernetes configuration is missing. Cannot install ArgoCD applications.");
            return false;
        }

        // Get action properties from Kubernetes config
        string actionName = "install argocd apps";
        var properties = Cfg.Kubernetes.GetActionProperties(actionName);
        
        // Get repo URL and path from properties
        string repoUrl = properties != null && properties.TryGetValue("repo_url", out object? repoUrlObj) 
            ? repoUrlObj?.ToString() ?? "" 
            : "";
        string path = properties != null && properties.TryGetValue("path", out object? pathObj)
            ? pathObj?.ToString() ?? "applications"
            : "applications";
        string targetRevision = properties != null && properties.TryGetValue("target_revision", out object? revisionObj)
            ? revisionObj?.ToString() ?? "main"
            : "main";

        if (string.IsNullOrEmpty(repoUrl))
        {
            Logger.GetLogger("install_argocd_apps").Printf("Repository URL not specified in action properties. Skipping ArgoCD applications installation.");
            return true; // Not an error, just skip
        }

        Logger.GetLogger("install_argocd_apps").Printf("Installing ArgoCD applications from {0} (path: {1}, revision: {2})...", repoUrl, path, targetRevision);
        
        var context = Kubernetes.BuildKubernetesContext(Cfg);
        if (context == null)
        {
            Logger.GetLogger("install_argocd_apps").Printf("Failed to build Kubernetes context.");
            return false;
        }
        if (context.Control.Count == 0)
        {
            Logger.GetLogger("install_argocd_apps").Printf("No Kubernetes control node found.");
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
            var logger = Logger.GetLogger("install_argocd_apps");

            // Check if kubectl is available
            logger.Printf("Checking kubectl...");
            string kubectlCheckCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && command -v kubectl && echo installed || echo not_installed";
            (string kubectlCheck, _) = pctService.Execute(controlID, kubectlCheckCmd, null);
            if (kubectlCheck.Contains("not_installed"))
            {
                logger.Printf("kubectl not found, cannot install ArgoCD applications");
                return false;
            }

            // Wait for ArgoCD to be ready
            logger.Printf("Waiting for ArgoCD to be ready...");
            int maxWait = 300;
            int waitTime = 0;
            bool argocdReady = false;
            while (waitTime < maxWait)
            {
                string checkCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods -n argocd -l app.kubernetes.io/name=argocd-server -o jsonpath='{.items[*].status.phase}' 2>&1";
                (string podStatus, _) = pctService.Execute(controlID, checkCmd, 30);
                if (podStatus.Contains("Running"))
                {
                    string readyCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods -n argocd -l app.kubernetes.io/name=argocd-server -o jsonpath='{.items[*].status.conditions[?(@.type==\"Ready\")].status}' 2>&1";
                    (string readyStatus, _) = pctService.Execute(controlID, readyCmd, 30);
                    if (readyStatus.Contains("True"))
                    {
                        argocdReady = true;
                        break;
                    }
                }
                if (waitTime % 30 == 0)
                {
                    logger.Printf("Waiting for ArgoCD to be ready (waited {0}/{1} seconds)...", waitTime, maxWait);
                }
                Thread.Sleep(5000);
                waitTime += 5;
            }

            if (!argocdReady)
            {
                logger.Printf("ArgoCD not ready after {0} seconds, checking pod status...", maxWait);
                // Check pod status and logs for errors
                string podStatusCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods -n argocd -o wide 2>&1";
                (string podStatus, _) = pctService.Execute(controlID, podStatusCmd, 30);
                logger.Printf("ArgoCD pod status: {0}", podStatus);
                
                // Get pod logs if available
                string getPodCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods -n argocd -l app.kubernetes.io/name=argocd-server -o jsonpath='{.items[0].metadata.name}' 2>&1";
                (string podName, _) = pctService.Execute(controlID, getPodCmd, 30);
                if (!string.IsNullOrEmpty(podName) && !podName.Contains("error"))
                {
                    podName = podName.Trim();
                    string logsCmd = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl logs -n argocd {podName} --tail=50 2>&1";
                    (string logs, _) = pctService.Execute(controlID, logsCmd, 30);
                    logger.Printf("ArgoCD server pod logs (last 50 lines): {0}", logs);
                }
                
                logger.Printf("ArgoCD installation failed - ArgoCD is not ready after {0} seconds", maxWait);
                return false;
            }

            // Create root Application manifest
            logger.Printf("Creating root Application manifest...");
            string rootAppName = "root-apps";
            string rootAppYaml = $@"apiVersion: argoproj.io/v1alpha1
kind: Application
metadata:
  name: {rootAppName}
  namespace: argocd
  finalizers:
    - resources-finalizer.argocd.argoproj.io
spec:
  project: default
  source:
    repoURL: {repoUrl}
    targetRevision: {targetRevision}
    path: {path}
    directory:
      recurse: true
  destination:
    server: https://kubernetes.default.svc
    namespace: argocd
  syncPolicy:
    automated:
      prune: true
      selfHeal: true
    syncOptions:
    - CreateNamespace=true
    refresh:
      time: 0s
";

            // Write manifest to temporary file
            string tempYamlFile = "/tmp/root-apps.yaml";
            string writeYamlCmd = $"cat > {tempYamlFile} << 'EOF'\n{rootAppYaml}EOF";
            (string writeOutput, int? writeExit) = pctService.Execute(controlID, writeYamlCmd, 30);
            if (writeExit.HasValue && writeExit.Value != 0)
            {
                logger.Printf("Failed to write root Application manifest: {0}", writeOutput);
                return false;
            }

            // Apply root Application
            logger.Printf("Applying root Application...");
            string applyCmd = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl apply -f {tempYamlFile}";
            (string applyOutput, int? applyExit) = pctService.Execute(controlID, applyCmd, 60);
            if (applyExit.HasValue && applyExit.Value != 0)
            {
                logger.Printf("Failed to apply root Application: {0}", applyOutput);
                // Cleanup temp file
                string cleanupCmd = $"rm -f {tempYamlFile}";
                pctService.Execute(controlID, cleanupCmd, 10);
                return false;
            }

            // Cleanup temp file
            string cleanupTempCmd = $"rm -f {tempYamlFile}";
            pctService.Execute(controlID, cleanupTempCmd, 10);

            logger.Printf("Root Application '{0}' created successfully. ArgoCD will now automatically discover and manage applications from {1}", rootAppName, repoUrl);
            return true;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }
}

public static class InstallArgoCDAppsActionFactory
{
    public static IAction NewInstallArgoCDAppsAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallArgoCDAppsAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
