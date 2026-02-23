using System;
using Enva.Libs;
using Enva.Orchestration;
using Enva.Services;

namespace Enva.Actions;

public class CreateFreeipaCredentialsAction : BaseAction, IAction
{
    public CreateFreeipaCredentialsAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "create freeipa credentials";
    }

    public bool Execute()
    {
        if (Cfg == null)
        {
            Logger.GetLogger("create_freeipa_credentials").Printf("Lab configuration is missing for CreateFreeipaCredentialsAction.");
            return false;
        }
        if (Cfg.Kubernetes == null)
        {
            Logger.GetLogger("create_freeipa_credentials").Printf("Kubernetes configuration is missing. Cannot create FreeIPA credentials.");
            return false;
        }

        string adminPassword = Environment.GetEnvironmentVariable("ENVA_FREEIPA_ADMIN_PASSWORD") ?? "";

        if (string.IsNullOrEmpty(adminPassword))
        {
            Logger.GetLogger("create_freeipa_credentials").Printf("FreeIPA admin password not provided (use --freeipa-admin-password CLI flag), skipping.");
            return true;
        }

        Logger.GetLogger("create_freeipa_credentials").Printf("Creating FreeIPA credentials secret...");

        var context = Kubernetes.BuildKubernetesContext(Cfg);
        if (context == null)
        {
            Logger.GetLogger("create_freeipa_credentials").Printf("Failed to build Kubernetes context.");
            return false;
        }
        if (context.Control.Count == 0)
        {
            Logger.GetLogger("create_freeipa_credentials").Printf("No Kubernetes control node found.");
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
            var logger = Logger.GetLogger("create_freeipa_credentials");

            logger.Printf("Checking kubectl...");
            string kubectlCheckCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && command -v kubectl && echo installed || echo not_installed";
            (string kubectlCheck, _) = pctService.Execute(controlID, kubectlCheckCmd, null);
            if (kubectlCheck.Contains("not_installed"))
            {
                logger.Printf("kubectl not found, cannot create FreeIPA credentials secret.");
                return false;
            }

            string ns = "freeipa";
            string secretName = "freeipa-credentials";
            int timeout = 30;

            logger.Printf("Creating namespace {0} if it doesn't exist...", ns);
            string createNsCmd = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl create namespace {ns} --dry-run=client -o yaml | kubectl apply -f -";
            (string nsOutput, int? nsExit) = pctService.Execute(controlID, createNsCmd, timeout);
            if (nsExit.HasValue && nsExit.Value != 0)
            {
                logger.Printf("Failed to create namespace: {0}", nsOutput);
                return false;
            }

            logger.Printf("Checking if secret {0} exists...", secretName);
            string checkCmd = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get secret {secretName} -n {ns}";
            (string checkOutput, _) = pctService.Execute(controlID, checkCmd, timeout);

            if (checkOutput.Contains(secretName))
            {
                logger.Printf("Secret {0} already exists, updating...", secretName);
                string deleteCmd = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl delete secret {secretName} -n {ns}";
                (string deleteOutput, int? deleteExit) = pctService.Execute(controlID, deleteCmd, timeout);
                if (deleteExit.HasValue && deleteExit.Value != 0 && !deleteOutput.Contains("NotFound"))
                {
                    logger.Printf("Warning: Failed to delete existing secret: {0}", deleteOutput);
                }
            }

            string escapedPass = adminPassword.Replace("'", "'\"'\"'");
            string createCmd = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl create secret generic {secretName} --from-literal=admin-password='{escapedPass}' --from-literal=ds-password='{escapedPass}' -n {ns}";
            (string createOutput, int? createExit) = pctService.Execute(controlID, createCmd, timeout);
            if (createExit.HasValue && createExit.Value != 0)
            {
                logger.Printf("Failed to create secret: {0}", createOutput);
                return false;
            }

            logger.Printf("FreeIPA credentials secret created successfully in namespace {0}.", ns);
            return true;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }
}

public static class CreateFreeipaCredentialsActionFactory
{
    public static IAction NewCreateFreeipaCredentialsAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new CreateFreeipaCredentialsAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
