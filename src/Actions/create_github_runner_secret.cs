using System;
using Enva.Libs;
using Enva.Orchestration;
using Enva.Services;

namespace Enva.Actions;

public class CreateGithubRunnerSecretAction : BaseAction, IAction
{
    public CreateGithubRunnerSecretAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "create github runner secret";
    }

    public bool Execute()
    {
        if (Cfg == null)
        {
            Logger.GetLogger("create_github_runner_secret").Printf("Lab configuration is missing for CreateGithubRunnerSecretAction.");
            return false;
        }
        if (Cfg.Kubernetes == null)
        {
            Logger.GetLogger("create_github_runner_secret").Printf("Kubernetes configuration is missing. Cannot create GitHub runner secret.");
            return false;
        }

        // Get GitHub token from environment variable (set via --github-token CLI flag)
        string githubToken = Environment.GetEnvironmentVariable("ENVA_GITHUB_TOKEN") ?? "";
        if (string.IsNullOrEmpty(githubToken))
        {
            Logger.GetLogger("create_github_runner_secret").Printf("GitHub token not provided (use --github-token CLI flag), skipping secret creation");
            return true; // Not an error, just skip if token not provided
        }

        Logger.GetLogger("create_github_runner_secret").Printf("Creating GitHub runner secret...");
        
        var context = Kubernetes.BuildKubernetesContext(Cfg);
        if (context == null)
        {
            Logger.GetLogger("create_github_runner_secret").Printf("Failed to build Kubernetes context.");
            return false;
        }
        if (context.Control.Count == 0)
        {
            Logger.GetLogger("create_github_runner_secret").Printf("No Kubernetes control node found.");
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
            var logger = Logger.GetLogger("create_github_runner_secret");

            // Check if kubectl is available
            logger.Printf("Checking kubectl...");
            string kubectlCheckCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && command -v kubectl && echo installed || echo not_installed";
            (string kubectlCheck, _) = pctService.Execute(controlID, kubectlCheckCmd, null);
            if (kubectlCheck.Contains("not_installed"))
            {
                logger.Printf("kubectl not found, cannot create secret");
                return false;
            }

            string namespace_ = "actions-runner-system";
            string secretName = "controller-manager";
            string secretKey = "github_token";

            // Create namespace if it doesn't exist
            logger.Printf("Creating namespace {0} if it doesn't exist...", namespace_);
            string createNsCmd = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl create namespace {namespace_} --dry-run=client -o yaml | kubectl apply -f -";
            int timeout = 30;
            (string nsOutput, int? nsExit) = pctService.Execute(controlID, createNsCmd, timeout);
            if (nsExit.HasValue && nsExit.Value != 0)
            {
                logger.Printf("Failed to create namespace: {0}", nsOutput);
                return false;
            }

            // Check if secret already exists
            logger.Printf("Checking if secret {0} exists...", secretName);
            string checkSecretCmd = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get secret {secretName} -n {namespace_} 2>&1";
            (string secretCheck, _) = pctService.Execute(controlID, checkSecretCmd, timeout);
            
            if (secretCheck.Contains(secretName))
            {
                // Secret exists, delete it first to update
                logger.Printf("Secret {0} already exists, updating...", secretName);
                string deleteSecretCmd = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl delete secret {secretName} -n {namespace_}";
                (string deleteOutput, int? deleteExit) = pctService.Execute(controlID, deleteSecretCmd, timeout);
                if (deleteExit.HasValue && deleteExit.Value != 0 && !deleteOutput.Contains("NotFound"))
                {
                    logger.Printf("Warning: Failed to delete existing secret: {0}", deleteOutput);
                }
            }

            // Create the secret
            logger.Printf("Creating secret {0} with GitHub token...", secretName);
            // Escape the token for shell
            string escapedToken = githubToken.Replace("'", "'\"'\"'");
            string createSecretCmd = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl create secret generic {secretName} --from-literal={secretKey}='{escapedToken}' -n {namespace_}";
            (string createOutput, int? createExit) = pctService.Execute(controlID, createSecretCmd, timeout);
            if (createExit.HasValue && createExit.Value != 0)
            {
                logger.Printf("Failed to create secret: {0}", createOutput);
                return false;
            }

            logger.Printf("GitHub runner secret {0} created successfully in namespace {1}", secretName, namespace_);
            return true;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }
}

public static class CreateGithubRunnerSecretActionFactory
{
    public static IAction NewCreateGithubRunnerSecretAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new CreateGithubRunnerSecretAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
