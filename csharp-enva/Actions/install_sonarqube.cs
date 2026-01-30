using System;
using System.Threading;
using Enva.Libs;
using Enva.Orchestration;
using Enva.Services;

namespace Enva.Actions;

public class InstallSonarqubeAction : BaseAction, IAction
{
    public InstallSonarqubeAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "install sonarqube";
    }

    public bool Execute()
    {
        if (Cfg == null)
        {
            Logger.GetLogger("install_sonarqube").Printf("Lab configuration is missing for InstallSonarqubeAction.");
            return false;
        }
        if (Cfg.Kubernetes == null)
        {
            Logger.GetLogger("install_sonarqube").Printf("Kubernetes configuration is missing. Cannot install SonarQube.");
            return false;
        }
        if (Cfg.Services.Services == null || !Cfg.Services.Services.ContainsKey("sonarqube"))
        {
            Logger.GetLogger("install_sonarqube").Printf("SonarQube not configured, skipping installation");
            return true;
        }
        Logger.GetLogger("install_sonarqube").Printf("Installing SonarQube on Kubernetes cluster...");
        var context = Kubernetes.BuildKubernetesContext(Cfg);
        if (context == null)
        {
            Logger.GetLogger("install_sonarqube").Printf("Failed to build Kubernetes context.");
            return false;
        }
        if (context.Control.Count == 0)
        {
            Logger.GetLogger("install_sonarqube").Printf("No Kubernetes control node found.");
            return false;
        }
        var controlConfig = context.Control[0];
        string lxcHost = context.LXCHost();
        int controlID = controlConfig.ID;
        // Get port from services or action properties
        int nodePort = 30091;
        if (Cfg.Services.Services.TryGetValue("sonarqube", out var sonarqubeService) && sonarqubeService.Ports != null && sonarqubeService.Ports.Count > 0)
        {
            nodePort = sonarqubeService.Ports[0].Port;
        }

        var lxcService = new LXCService(lxcHost, Cfg.SSH);
        if (!lxcService.Connect())
        {
            return false;
        }

        try
        {
            var pctService = new PCTService(lxcService);

            // Check if kubectl and helm are available
            Logger.GetLogger("install_sonarqube").Printf("Checking kubectl...");
            string kubectlCheckCmd = "command -v kubectl  && echo installed || echo not_installed";
            (string kubectlCheck, _) = pctService.Execute(controlID, kubectlCheckCmd, null);
            if (kubectlCheck.Contains("not_installed"))
            {
                Logger.GetLogger("install_sonarqube").Printf("Installing kubectl...");
                string installKubectlCmd = "curl -LO https://dl.k8s.io/release/$(curl -L -s https://dl.k8s.io/release/stable.txt)/bin/linux/amd64/kubectl && chmod +x kubectl && sudo mv kubectl /usr/local/bin/ && export PATH=/usr/local/bin:$PATH";
                int timeout = 120;
                pctService.Execute(controlID, installKubectlCmd, timeout);
                string verifyCmd = "test -f /usr/local/bin/kubectl && echo installed || echo not_installed";
                (string verifyOutput, _) = pctService.Execute(controlID, verifyCmd, null);
                if (verifyOutput.Contains("not_installed"))
                {
                    Logger.GetLogger("install_sonarqube").Printf("kubectl installation failed");
                    return false;
                }
            }

            Logger.GetLogger("install_sonarqube").Printf("Checking Helm...");
            string helmCheckCmd = "export PATH=/usr/local/bin:$PATH && command -v helm  && echo installed || echo not_installed";
            (string helmCheck, _) = pctService.Execute(controlID, helmCheckCmd, null);
            if (helmCheck.Contains("not_installed"))
            {
                Logger.GetLogger("install_sonarqube").Printf("Installing Helm...");
                string helmInstallCmd = "curl https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 | bash && export PATH=/usr/local/bin:$PATH";
                int timeout = 120;
                pctService.Execute(controlID, helmInstallCmd, timeout);
            }

            Logger.GetLogger("install_sonarqube").Printf("Adding SonarQube Helm repository...");
            string repoAddCmd = "export PATH=/usr/local/bin:$PATH && helm repo add sonarqube https://SonarSource.github.io/helm-chart-sonarqube && helm repo update";
            int timeout2 = 120;
            int maxRepoRetries = 3;
            for (int repoRetry = 0; repoRetry < maxRepoRetries; repoRetry++)
            {
                (string repoOutput, int? repoExit) = pctService.Execute(controlID, repoAddCmd, timeout2);
                if (repoExit.HasValue && repoExit.Value == 0)
                {
                    break;
                }
                if (repoRetry < maxRepoRetries - 1)
                {
                    Logger.GetLogger("install_sonarqube").Printf("Helm repo add failed (attempt {0}/{1}), retrying in 5 seconds...", repoRetry + 1, maxRepoRetries);
                    Thread.Sleep(5000);
                }
                else
                {
                    Logger.GetLogger("install_sonarqube").Printf("Failed to add Helm repo after {0} attempts: {1}", maxRepoRetries, repoOutput);
                    return false;
                }
            }

            Logger.GetLogger("install_sonarqube").Printf("Creating SonarQube namespace...");
            string namespaceCmd = "kubectl create namespace sonarqube --dry-run=client -o yaml | kubectl apply -f -";
            pctService.Execute(controlID, namespaceCmd, null);

            Logger.GetLogger("install_sonarqube").Printf("Installing SonarQube using Helm...");
            string installCmd = $"export PATH=/usr/local/bin:$PATH && helm upgrade --install sonarqube sonarqube/sonarqube --namespace sonarqube --set community.enabled=true --set monitoringPasscode=sonarqube-monitoring-passcode";
            timeout2 = 600;
            (string installOutput, int? installExit) = pctService.Execute(controlID, installCmd, timeout2);
            if (!installExit.HasValue || installExit.Value != 0)
            {
                Logger.GetLogger("install_sonarqube").Printf("SonarQube Helm installation failed");
                int outputLen = installOutput.Length;
                int start = outputLen > 1000 ? outputLen - 1000 : 0;
                Logger.GetLogger("install_sonarqube").Printf("Installation output: {0}", installOutput.Substring(start));
                return false;
            }

            Logger.GetLogger("install_sonarqube").Printf("Waiting for SonarQube service to be created...");
            int maxWaitSvc = 60;
            int waitTimeSvc = 0;
            while (waitTimeSvc < maxWaitSvc)
            {
                string svcCheckCmd = "kubectl get svc -n sonarqube sonarqube-sonarqube";
                timeout2 = 30;
                (string svcCheck, _) = pctService.Execute(controlID, svcCheckCmd, timeout2);
                if (svcCheck.Contains("sonarqube-sonarqube"))
                {
                    Logger.GetLogger("install_sonarqube").Printf("SonarQube service created");
                    break;
                }
                Logger.GetLogger("install_sonarqube").Printf("Waiting for SonarQube service (waited {0}/{1} seconds)...", waitTimeSvc, maxWaitSvc);
                Thread.Sleep(2000);
                waitTimeSvc += 2;
            }

            Logger.GetLogger("install_sonarqube").Printf("Patching SonarQube service to NodePort on port {0}...", nodePort);
            // JSON patch: missing closing braces in original - need }} to close spec and root object
            string patchCmd = $"kubectl patch svc -n sonarqube sonarqube-sonarqube -p '{{\"spec\":{{\"type\":\"NodePort\",\"ports\":[{{\"port\":9000,\"nodePort\":{nodePort},\"targetPort\":9000,\"protocol\":\"TCP\",\"name\":\"http\"}}]}}}}'";
            timeout2 = 30;
            (string patchOutput, int? patchExit) = pctService.Execute(controlID, patchCmd, timeout2);
            if (patchExit.HasValue && patchExit.Value == 0)
            {
                Logger.GetLogger("install_sonarqube").Printf("SonarQube service patched to NodePort {0}", nodePort);
            }
            else
            {
                Logger.GetLogger("install_sonarqube").Printf("Failed to patch SonarQube service: {0}", patchOutput);
                return false;
            }

            Logger.GetLogger("install_sonarqube").Printf("Waiting for SonarQube pod to be ready...");
            int maxWaitPod = 600;
            int waitTimePod = 0;
            while (waitTimePod < maxWaitPod)
            {
                string podStatusCmd = "kubectl get pods -n sonarqube sonarqube-sonarqube-0 -o jsonpath='{.status.phase}'";
                timeout2 = 30;
                (string podStatus, _) = pctService.Execute(controlID, podStatusCmd, timeout2);
                if (podStatus.Contains("Running"))
                {
                    string readyCmd = "kubectl get pods -n sonarqube sonarqube-sonarqube-0 -o jsonpath='{.status.conditions[?(@.type==\"Ready\")].status}'";
                    (string readyStatus, _) = pctService.Execute(controlID, readyCmd, timeout2);
                    if (readyStatus.Contains("True"))
                    {
                        Logger.GetLogger("install_sonarqube").Printf("SonarQube pod is ready");
                        break;
                    }
                }
                if (waitTimePod % 30 == 0)
                {
                    Logger.GetLogger("install_sonarqube").Printf("Waiting for SonarQube pod to be ready (waited {0}/{1} seconds)...", waitTimePod, maxWaitPod);
                }
                Thread.Sleep(10000);
                waitTimePod += 10;
            }
            if (waitTimePod >= maxWaitPod)
            {
                Logger.GetLogger("install_sonarqube").Printf("SonarQube pod not ready after {0} seconds, but installation completed", maxWaitPod);
                string debugCmd = "kubectl get pods -n sonarqube -o wide";
                timeout2 = 30;
                (string debugOutput, _) = pctService.Execute(controlID, debugCmd, timeout2);
                if (!string.IsNullOrEmpty(debugOutput))
                {
                    Logger.GetLogger("install_sonarqube").Printf("Pod status: {0}", debugOutput);
                }
            }

            Logger.GetLogger("install_sonarqube").Printf("SonarQube installed successfully on NodePort {0}", nodePort);
            return true;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }
}

public static class InstallSonarqubeActionFactory
{
    public static IAction NewInstallSonarqubeAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallSonarqubeAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}