using System.Collections.Generic;
using System.Threading;
using Enva.Libs;
using Enva.Orchestration;
using Enva.Services;

namespace Enva.Actions;

public class InstallVaultAction : BaseAction, IAction
{
    private const string VaultHelmVersion = "0.28.0";

    public InstallVaultAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "install vault";
    }

    public bool Execute()
    {
        if (Cfg == null)
        {
            Logger.GetLogger("install_vault").Printf("Lab configuration is missing for InstallVaultAction.");
            return false;
        }
        if (Cfg.Kubernetes == null)
        {
            Logger.GetLogger("install_vault").Printf("Kubernetes configuration is missing. Cannot install Vault.");
            return false;
        }

        var context = Kubernetes.BuildKubernetesContext(Cfg);
        if (context == null)
        {
            Logger.GetLogger("install_vault").Printf("Failed to build Kubernetes context.");
            return false;
        }
        if (context.Control.Count == 0)
        {
            Logger.GetLogger("install_vault").Printf("No Kubernetes control node found.");
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
            var logger = Logger.GetLogger("install_vault");
            string k8sEnv = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml";

            // Check if Vault is already installed
            logger.Printf("Checking if Vault is already installed...");
            string checkCmd = $"{k8sEnv} && kubectl get namespace vault --ignore-not-found -o name";
            (string checkOutput, _) = pctService.Execute(controlID, checkCmd, 30);
            if (checkOutput.Contains("namespace/vault"))
            {
                logger.Printf("Vault namespace already exists, checking if vault-0 pod is ready...");
                string podCheckCmd = $"{k8sEnv} && kubectl get pods -n vault vault-0 --ignore-not-found -o jsonpath='{{.status.phase}}'";
                (string podPhase, _) = pctService.Execute(controlID, podCheckCmd, 30);
                if (podPhase.Trim() == "Running")
                {
                    logger.Printf("Vault is already installed and running, skipping.");
                    return true;
                }
            }

            // Check/install helm
            logger.Printf("Checking if helm is available...");
            string helmCheckCmd = "command -v helm && echo installed || echo not_installed";
            (string helmCheck, _) = pctService.Execute(controlID, helmCheckCmd, 30);
            if (helmCheck.Contains("not_installed"))
            {
                logger.Printf("Installing helm...");
                string installHelmCmd = "curl -fsSL https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 | bash";
                (string helmInstallOutput, int? helmInstallExit) = pctService.Execute(controlID, installHelmCmd, 120);
                if (helmInstallExit.HasValue && helmInstallExit.Value != 0)
                {
                    logger.Printf("Failed to install helm: {0}", helmInstallOutput);
                    return false;
                }
            }

            // Add HashiCorp helm repo
            logger.Printf("Adding HashiCorp helm repo...");
            string addRepoCmd = "export PATH=/usr/local/bin:$PATH && helm repo add hashicorp https://helm.releases.hashicorp.com";
            pctService.Execute(controlID, addRepoCmd, 60);

            string updateRepoCmd = "export PATH=/usr/local/bin:$PATH && helm repo update";
            pctService.Execute(controlID, updateRepoCmd, 60);

            // Install Vault
            logger.Printf("Installing Vault {0} via Helm...", VaultHelmVersion);
            string installCmd = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && helm install vault hashicorp/vault --namespace vault --create-namespace --version {VaultHelmVersion} --set server.dev.enabled=false --set server.standalone.enabled=true --set server.dataStorage.storageClass=local-path";
            (string installOutput, int? installExit) = pctService.Execute(controlID, installCmd, 300);
            if (installExit.HasValue && installExit.Value != 0)
            {
                logger.Printf("Vault helm install failed: {0}", installOutput);
                return false;
            }
            logger.Printf("Vault helm install succeeded.");

            // Wait for vault-0 pod to be in Running state
            logger.Printf("Waiting for vault-0 pod to be running...");
            int maxWait = 180;
            int waited = 0;
            bool podRunning = false;
            while (waited < maxWait)
            {
                string podStatusCmd = $"{k8sEnv} && kubectl get pod -n vault vault-0 -o jsonpath='{{.status.phase}}' --ignore-not-found";
                (string podStatus, _) = pctService.Execute(controlID, podStatusCmd, 30);
                if (podStatus.Trim() == "Running")
                {
                    podRunning = true;
                    logger.Printf("vault-0 is running.");
                    break;
                }
                if (waited % 30 == 0)
                {
                    logger.Printf("Waiting for vault-0 (waited {0}/{1} seconds, status: {2})...", waited, maxWait, podStatus.Trim());
                }
                Thread.Sleep(10000);
                waited += 10;
            }

            if (!podRunning)
            {
                logger.Printf("vault-0 did not reach Running state after {0} seconds.", maxWait);
                return false;
            }

            // Give vault a moment to fully start before init
            Thread.Sleep(5000);

            // Initialize Vault
            logger.Printf("Initializing Vault...");
            string initCmd = $"{k8sEnv} && kubectl exec -n vault vault-0 -- vault operator init -key-shares=5 -key-threshold=3";
            (string initOutput, int? initExit) = pctService.Execute(controlID, initCmd, 60);
            if (initExit.HasValue && initExit.Value != 0)
            {
                logger.Printf("Vault init failed: {0}", initOutput);
                return false;
            }

            // Parse unseal keys and root token
            var unsealKeys = new List<string>();
            string rootToken = "";
            foreach (var line in initOutput.Split('\n'))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("Unseal Key"))
                {
                    var parts = trimmed.Split(':');
                    if (parts.Length >= 2)
                    {
                        unsealKeys.Add(parts[1].Trim());
                    }
                }
                else if (trimmed.StartsWith("Initial Root Token"))
                {
                    var parts = trimmed.Split(':');
                    if (parts.Length >= 2)
                    {
                        rootToken = parts[1].Trim();
                    }
                }
            }

            if (unsealKeys.Count < 3 || string.IsNullOrEmpty(rootToken))
            {
                logger.Printf("Failed to parse Vault init output. Output: {0}", initOutput);
                return false;
            }

            logger.Printf("=== VAULT INIT COMPLETE - SAVE THESE CREDENTIALS ===");
            for (int i = 0; i < unsealKeys.Count; i++)
            {
                logger.Printf("Unseal Key {0}: {1}", i + 1, unsealKeys[i]);
            }
            logger.Printf("Root Token: {0}", rootToken);
            logger.Printf("=====================================================");

            // Unseal Vault with first 3 keys
            logger.Printf("Unsealing Vault...");
            for (int i = 0; i < 3; i++)
            {
                string unsealCmd = $"{k8sEnv} && kubectl exec -n vault vault-0 -- vault operator unseal {unsealKeys[i]}";
                (string unsealOutput, int? unsealExit) = pctService.Execute(controlID, unsealCmd, 30);
                if (unsealExit.HasValue && unsealExit.Value != 0)
                {
                    logger.Printf("Vault unseal {0} failed: {1}", i + 1, unsealOutput);
                    return false;
                }
                logger.Printf("Unseal {0}/3 applied.", i + 1);
            }

            // Wait for Vault to be unsealed
            Thread.Sleep(5000);

            string execPrefix = $"{k8sEnv} && kubectl exec -n vault vault-0 -- env VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN={rootToken}";

            // Enable KV v2 secret engine
            logger.Printf("Enabling KV v2 secret engine at path 'secret'...");
            string kvEnableCmd = $"{execPrefix} vault secrets enable -path=secret kv-v2";
            (string kvOutput, int? kvExit) = pctService.Execute(controlID, kvEnableCmd, 30);
            if (kvExit.HasValue && kvExit.Value != 0 && !kvOutput.Contains("already in use"))
            {
                logger.Printf("Failed to enable KV v2: {0}", kvOutput);
                return false;
            }
            logger.Printf("KV v2 enabled at path 'secret'.");

            // Enable Kubernetes auth
            logger.Printf("Enabling Kubernetes auth method...");
            string k8sAuthEnableCmd = $"{execPrefix} vault auth enable kubernetes";
            (string k8sAuthOutput, int? k8sAuthExit) = pctService.Execute(controlID, k8sAuthEnableCmd, 30);
            if (k8sAuthExit.HasValue && k8sAuthExit.Value != 0 && !k8sAuthOutput.Contains("already enabled"))
            {
                logger.Printf("Failed to enable Kubernetes auth: {0}", k8sAuthOutput);
                return false;
            }

            // Configure Kubernetes auth
            logger.Printf("Configuring Kubernetes auth...");
            string k8sAuthConfigCmd = $"{execPrefix} vault write auth/kubernetes/config kubernetes_host=https://kubernetes.default.svc:443";
            (string k8sAuthConfigOutput, int? k8sAuthConfigExit) = pctService.Execute(controlID, k8sAuthConfigCmd, 30);
            if (k8sAuthConfigExit.HasValue && k8sAuthConfigExit.Value != 0)
            {
                logger.Printf("Failed to configure Kubernetes auth: {0}", k8sAuthConfigOutput);
                return false;
            }
            logger.Printf("Kubernetes auth configured.");

            // Create ESO policy - write HCL to temp file in vault container, then apply
            logger.Printf("Creating external-secrets-policy...");
            string writePolicyLine1Cmd = $"{k8sEnv} && kubectl exec -n vault vault-0 -- sh -c \"echo 'path \\\"secret/data/*\\\" {{ capabilities = [\\\"read\\\"] }}' > /tmp/eso-policy.hcl\"";
            pctService.Execute(controlID, writePolicyLine1Cmd, 30);
            string writePolicyLine2Cmd = $"{k8sEnv} && kubectl exec -n vault vault-0 -- sh -c \"echo 'path \\\"secret/metadata/*\\\" {{ capabilities = [\\\"read\\\"] }}' >> /tmp/eso-policy.hcl\"";
            pctService.Execute(controlID, writePolicyLine2Cmd, 30);
            string createPolicyCmd = $"{execPrefix} vault policy write external-secrets-policy /tmp/eso-policy.hcl";
            (string policyOutput, int? policyExit) = pctService.Execute(controlID, createPolicyCmd, 30);
            if (policyExit.HasValue && policyExit.Value != 0)
            {
                logger.Printf("Failed to create ESO policy: {0}", policyOutput);
                return false;
            }
            logger.Printf("external-secrets-policy created.");

            // Create ESO role
            logger.Printf("Creating external-secrets Kubernetes auth role...");
            string createRoleCmd = $"{execPrefix} vault write auth/kubernetes/role/external-secrets bound_service_account_names=external-secrets bound_service_account_namespaces=external-secrets policies=external-secrets-policy ttl=1h";
            (string roleOutput, int? roleExit) = pctService.Execute(controlID, createRoleCmd, 30);
            if (roleExit.HasValue && roleExit.Value != 0)
            {
                logger.Printf("Failed to create ESO role: {0}", roleOutput);
                return false;
            }
            logger.Printf("external-secrets role created.");

            logger.Printf("Vault installed and configured successfully.");
            logger.Printf("Root Token: {0}", rootToken);
            return true;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }
}

public static class InstallVaultActionFactory
{
    public static IAction NewInstallVaultAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallVaultAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
