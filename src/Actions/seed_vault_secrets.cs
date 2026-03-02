using System.Collections.Generic;
using System.Linq;
using System.Text;
using Enva.Libs;
using Enva.Orchestration;
using Enva.Services;

namespace Enva.Actions;

public class SeedVaultSecretsAction : BaseAction, IAction
{
    public SeedVaultSecretsAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "seed vault secrets";
    }

    public bool Execute()
    {
        if (Cfg == null)
        {
            Logger.GetLogger("seed_vault_secrets").Printf("Lab configuration is missing for SeedVaultSecretsAction.");
            return false;
        }
        if (Cfg.Kubernetes == null)
        {
            Logger.GetLogger("seed_vault_secrets").Printf("Kubernetes configuration is missing. Cannot seed vault secrets.");
            return false;
        }
        if (Cfg.VaultSecrets == null || Cfg.VaultSecrets.Count == 0)
        {
            Logger.GetLogger("seed_vault_secrets").Printf("No vault_secrets defined in enva.yaml, nothing to seed.");
            return true;
        }

        var context = Kubernetes.BuildKubernetesContext(Cfg);
        if (context == null)
        {
            Logger.GetLogger("seed_vault_secrets").Printf("Failed to build Kubernetes context.");
            return false;
        }
        if (context.Control.Count == 0)
        {
            Logger.GetLogger("seed_vault_secrets").Printf("No Kubernetes control node found.");
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
            var logger = Logger.GetLogger("seed_vault_secrets");
            string k8sEnv = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml";
            string env = Cfg.Environment ?? "prod";

            // Retrieve Vault root token from K8s secret
            logger.Printf("Retrieving Vault root token from K8s secret vault-root-token in vault namespace...");
            string getTokenCmd = $"{k8sEnv} && kubectl get secret vault-root-token -n vault -o jsonpath='{{.data.token}}' | base64 -d";
            (string rootToken, int? getTokenExit) = pctService.Execute(controlID, getTokenCmd, 30);
            rootToken = rootToken.Trim();
            if (string.IsNullOrEmpty(rootToken) || (getTokenExit.HasValue && getTokenExit.Value != 0))
            {
                logger.Printf("Failed to retrieve Vault root token. Is Vault installed and initialized?");
                return false;
            }
            logger.Printf("Vault root token retrieved.");

            string execPrefix = $"{k8sEnv} && kubectl exec -n vault vault-0 -- env VAULT_ADDR=http://127.0.0.1:8200 VAULT_TOKEN={rootToken}";

            // Seed each secret path
            foreach (var (path, kvPairs) in Cfg.VaultSecrets)
            {
                if (kvPairs == null || kvPairs.Count == 0)
                {
                    logger.Printf("Skipping empty secret path: {0}", path);
                    continue;
                }

                string vaultPath = $"secret/{env}/{path}";
                logger.Printf("Seeding {0}...", vaultPath);

                // Build key=value args
                var kvArgs = new StringBuilder();
                foreach (var (key, value) in kvPairs)
                {
                    // Escape single quotes in value: replace ' with '\''
                    string escapedValue = value.Replace("'", "'\\''");
                    kvArgs.Append($" {key}='{escapedValue}'");
                }

                string kvPutCmd = $"{execPrefix} vault kv put {vaultPath}{kvArgs}";
                (string kvPutOutput, int? kvPutExit) = pctService.Execute(controlID, kvPutCmd, 30);
                if (kvPutExit.HasValue && kvPutExit.Value != 0)
                {
                    logger.Printf("Failed to seed {0}: {1}", vaultPath, kvPutOutput);
                    return false;
                }
                logger.Printf("Seeded {0} with keys: {1}", vaultPath, string.Join(", ", kvPairs.Keys));
            }

            logger.Printf("All vault secrets seeded successfully for environment '{0}'.", env);
            return true;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }
}

public static class SeedVaultSecretsActionFactory
{
    public static IAction NewSeedVaultSecretsAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new SeedVaultSecretsAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
