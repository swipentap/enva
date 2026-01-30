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

            // Install git if not available
            logger.Printf("Checking for git...");
            string gitCheckCmd = "command -v git && echo installed || echo not_installed";
            (string gitCheck, _) = pctService.Execute(controlID, gitCheckCmd, null);
            if (gitCheck.Contains("not_installed"))
            {
                logger.Printf("Installing git...");
                string installGitCmd = "apt-get update && apt-get install -y git";
                pctService.Execute(controlID, installGitCmd, 120);
            }

            // Clone or update the repository
            string tempDir = "/tmp/argocd-apps-repo";
            logger.Printf("Cloning repository to {0}...", tempDir);
            
            // Remove existing directory if it exists
            string cleanupCmd = $"rm -rf {tempDir}";
            pctService.Execute(controlID, cleanupCmd, 30);
            
            // Clone the repository
            string cloneCmd = $"git clone --depth 1 --branch {targetRevision} {repoUrl} {tempDir}";
            int timeout = 300;
            (string cloneOutput, int? cloneExit) = pctService.Execute(controlID, cloneCmd, timeout);
            if (cloneExit.HasValue && cloneExit.Value != 0)
            {
                logger.Printf("Failed to clone repository: {0}", cloneOutput);
                return false;
            }

            // Apply all YAML files from the applications directory
            string appsPath = $"{tempDir}/{path}";
            logger.Printf("Applying ArgoCD applications from {0}...", appsPath);
            
            // Check if directory exists
            string checkDirCmd = $"test -d {appsPath} && echo exists || echo not_exists";
            (string dirCheck, _) = pctService.Execute(controlID, checkDirCmd, 30);
            if (dirCheck.Contains("not_exists"))
            {
                logger.Printf("Applications directory {0} does not exist in repository", appsPath);
                return false;
            }

            // Find all YAML files in the directory
            string findFilesCmd = $"find {appsPath} -name '*.yaml' -o -name '*.yml'";
            (string filesList, _) = pctService.Execute(controlID, findFilesCmd, 30);
            if (string.IsNullOrEmpty(filesList) || filesList.Trim().Length == 0)
            {
                logger.Printf("No YAML files found in {0}", appsPath);
                return false;
            }

            string[] files = filesList.Trim().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            logger.Printf("Found {0} YAML file(s) to apply", files.Length);

            // Apply each file
            bool allSuccess = true;
            foreach (string file in files)
            {
                string filePath = file.Trim();
                logger.Printf("Applying {0}...", filePath);
                string applyCmd = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl apply -f {filePath}";
                (string applyOutput, int? applyExit) = pctService.Execute(controlID, applyCmd, 60);
                if (applyExit.HasValue && applyExit.Value == 0)
                {
                    logger.Printf("✓ Successfully applied {0}", filePath);
                }
                else
                {
                    logger.Printf("✗ Failed to apply {0}: {1}", filePath, applyOutput);
                    allSuccess = false;
                }
            }

            // Cleanup
            logger.Printf("Cleaning up temporary repository...");
            pctService.Execute(controlID, cleanupCmd, 30);

            if (allSuccess)
            {
                logger.Printf("ArgoCD applications installed successfully");
            }
            else
            {
                logger.Printf("Some ArgoCD applications failed to install");
            }

            return allSuccess;
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
