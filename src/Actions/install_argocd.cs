using System;
using System.Threading;
using Enva.Libs;
using Enva.Orchestration;
using Enva.Services;

namespace Enva.Actions;

public class InstallArgoCDAction : BaseAction, IAction
{
    public InstallArgoCDAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "install argocd";
    }

    public bool Execute()
    {
        if (Cfg == null)
        {
            Logger.GetLogger("install_argocd").Printf("Lab configuration is missing for InstallArgoCDAction.");
            return false;
        }
        if (Cfg.Kubernetes == null)
        {
            Logger.GetLogger("install_argocd").Printf("Kubernetes configuration is missing. Cannot install ArgoCD.");
            return false;
        }
        if (Cfg.Services.Services == null || !Cfg.Services.Services.ContainsKey("argocd"))
        {
            Logger.GetLogger("install_argocd").Printf("ArgoCD not configured, skipping installation");
            return true;
        }
        Logger.GetLogger("install_argocd").Printf("Installing ArgoCD on Kubernetes cluster...");
        var context = Kubernetes.BuildKubernetesContext(Cfg);
        if (context == null)
        {
            Logger.GetLogger("install_argocd").Printf("Failed to build Kubernetes context.");
            return false;
        }
        if (context.Control.Count == 0)
        {
            Logger.GetLogger("install_argocd").Printf("No Kubernetes control node found.");
            return false;
        }
        var controlConfig = context.Control[0];
        string lxcHost = context.LXCHost();
        int controlID = controlConfig.ID;
        // Get port from services or action properties
        int nodePort = 30080;
        if (Cfg.Services.Services.TryGetValue("argocd", out var argocdService) && argocdService.Ports != null && argocdService.Ports.Count > 0)
        {
            nodePort = argocdService.Ports[0].Port;
        }

        var lxcService = new LXCService(lxcHost, Cfg.SSH);
        if (!lxcService.Connect())
        {
            return false;
        }

        try
        {
            var pctService = new PCTService(lxcService);

            // Check if kubectl is available
            Logger.GetLogger("install_argocd").Printf("Checking kubectl...");
            string kubectlCheckCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && command -v kubectl && echo installed || echo not_installed";
            (string kubectlCheck, _) = pctService.Execute(controlID, kubectlCheckCmd, null);
            if (kubectlCheck.Contains("not_installed"))
            {
                Logger.GetLogger("install_argocd").Printf("Installing kubectl...");
                string installKubectlCmd = "curl -LO https://dl.k8s.io/release/$(curl -L -s https://dl.k8s.io/release/stable.txt)/bin/linux/amd64/kubectl && chmod +x kubectl && sudo mv kubectl /usr/local/bin/ && export PATH=/usr/local/bin:$PATH";
                int timeout = 120;
                pctService.Execute(controlID, installKubectlCmd, timeout);
                string verifyCmd = "test -f /usr/local/bin/kubectl && echo installed || echo not_installed";
                (string verifyOutput, _) = pctService.Execute(controlID, verifyCmd, null);
                if (verifyOutput.Contains("not_installed"))
                {
                    Logger.GetLogger("install_argocd").Printf("kubectl installation failed");
                    return false;
                }
            }

            Logger.GetLogger("install_argocd").Printf("Creating ArgoCD namespace...");
            string namespaceCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl create namespace argocd --dry-run=client -o yaml | kubectl apply -f -";
            int timeout2 = 30;
            (string namespaceOutput, int? namespaceExit) = pctService.Execute(controlID, namespaceCmd, timeout2);
            if (namespaceExit.HasValue && namespaceExit.Value != 0)
            {
                Logger.GetLogger("install_argocd").Printf("Failed to create namespace: {0}", namespaceOutput);
                return false;
            }

            Logger.GetLogger("install_argocd").Printf("Installing ArgoCD using kubectl apply (server-side)...");
            string installCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl apply --server-side -n argocd -f https://raw.githubusercontent.com/argoproj/argo-cd/stable/manifests/install.yaml";
            timeout2 = 300;
            int maxRetries = 3;
            bool installSuccess = false;
            for (int retry = 0; retry < maxRetries; retry++)
            {
                (string installOutput, int? installExit) = pctService.Execute(controlID, installCmd, timeout2);
                if (installExit.HasValue && installExit.Value == 0)
                {
                    Logger.GetLogger("install_argocd").Printf("ArgoCD installation started successfully");
                    installSuccess = true;
                    break;
                }
                if (retry < maxRetries - 1)
                {
                    Logger.GetLogger("install_argocd").Printf("ArgoCD installation failed (attempt {0}/{1}), retrying in 5 seconds...", retry + 1, maxRetries);
                    Thread.Sleep(5000);
                }
                else
                {
                    int outputLen = installOutput.Length;
                    int start = outputLen > 1000 ? outputLen - 1000 : 0;
                    Logger.GetLogger("install_argocd").Printf("ArgoCD installation failed after {0} attempts: {1}", maxRetries, installOutput.Substring(start));
                    return false;
                }
            }

            if (!installSuccess)
            {
                return false;
            }

            // CRITICAL: The argocd-redis secret is created by an init container in the redis pod.
            // Other pods (server, application-controller, repo-server) will fail if they start
            // before the redis init container completes. We must wait for the redis pod's
            // init container to finish creating the secret before proceeding.
            Logger.GetLogger("install_argocd").Printf("Waiting for ArgoCD redis init container to create secret...");
            int maxWaitRedis = 300;
            int waitTimeRedis = 0;
            bool redisSecretCreated = false;
            while (waitTimeRedis < maxWaitRedis)
            {
                // Check if redis pod exists and its init container has completed
                string redisPodCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods -n argocd -l app.kubernetes.io/name=argocd-redis -o jsonpath='{.items[0].status.initContainerStatuses[0].ready}' 2>&1";
                (string redisInitReady, _) = pctService.Execute(controlID, redisPodCmd, 30);
                
                // Also check if the secret exists (init container creates it)
                string secretCheckCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get secret argocd-redis -n argocd -o jsonpath='{.data.auth}' 2>&1";
                (string secretKey, _) = pctService.Execute(controlID, secretCheckCmd, 30);
                
                if (!string.IsNullOrEmpty(secretKey) && secretKey.Length > 0 && !secretKey.Contains("Error") && !secretKey.Contains("NotFound"))
                {
                    Logger.GetLogger("install_argocd").Printf("ArgoCD redis secret created successfully by init container");
                    redisSecretCreated = true;
                    break;
                }
                
                // Check init container status for debugging
                if (waitTimeRedis % 30 == 0)
                {
                    string redisStatusCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods -n argocd -l app.kubernetes.io/name=argocd-redis -o jsonpath='{.items[0].status.initContainerStatuses[0].state}' 2>&1";
                    (string initStatus, _) = pctService.Execute(controlID, redisStatusCmd, 30);
                    Logger.GetLogger("install_argocd").Printf("Waiting for redis init container (waited {0}/{1} seconds, status: {2})...", waitTimeRedis, maxWaitRedis, initStatus);
                }
                
                Thread.Sleep(5000);
                waitTimeRedis += 5;
            }

            if (!redisSecretCreated)
            {
                Logger.GetLogger("install_argocd").Printf("ArgoCD redis secret not created after {0} seconds, checking pod status...", maxWaitRedis);
                string debugCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods -n argocd -l app.kubernetes.io/name=argocd-redis -o yaml | grep -A 10 'initContainerStatuses'";
                (string debugOutput, _) = pctService.Execute(controlID, debugCmd, 30);
                Logger.GetLogger("install_argocd").Printf("Redis pod init container status: {0}", debugOutput);
                return false;
            }

            Logger.GetLogger("install_argocd").Printf("Waiting for ArgoCD server service to be created...");
            int maxWaitSvc = 120;
            int waitTimeSvc = 0;
            while (waitTimeSvc < maxWaitSvc)
            {
                string svcCheckCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get svc -n argocd argocd-server";
                timeout2 = 30;
                (string svcCheck, _) = pctService.Execute(controlID, svcCheckCmd, timeout2);
                if (svcCheck.Contains("argocd-server"))
                {
                    Logger.GetLogger("install_argocd").Printf("ArgoCD server service created");
                    break;
                }
                Logger.GetLogger("install_argocd").Printf("Waiting for ArgoCD service (waited {0}/{1} seconds)...", waitTimeSvc, maxWaitSvc);
                Thread.Sleep(2000);
                waitTimeSvc += 2;
            }

            Logger.GetLogger("install_argocd").Printf("Patching ArgoCD server service to NodePort on port {0}...", nodePort);
            string patchCmd = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl patch svc -n argocd argocd-server -p '{{\"spec\":{{\"type\":\"NodePort\",\"ports\":[{{\"port\":80,\"nodePort\":{nodePort},\"targetPort\":8080,\"protocol\":\"TCP\",\"name\":\"http\"}},{{\"port\":443,\"nodePort\":{nodePort + 1},\"targetPort\":8080,\"protocol\":\"TCP\",\"name\":\"https\"}}]}}}}'";
            timeout2 = 30;
            (string patchOutput, int? patchExit) = pctService.Execute(controlID, patchCmd, timeout2);
            if (patchExit.HasValue && patchExit.Value == 0)
            {
                Logger.GetLogger("install_argocd").Printf("ArgoCD server service patched to NodePort {0} (HTTP) and {1} (HTTPS)", nodePort, nodePort + 1);
            }
            else
            {
                Logger.GetLogger("install_argocd").Printf("Failed to patch ArgoCD service: {0}", patchOutput);
                return false;
            }

            Logger.GetLogger("install_argocd").Printf("Waiting for ArgoCD pods to be ready...");
            int maxWaitPod = 600;
            int waitTimePod = 0;
            bool allPodsReady = false;
            while (waitTimePod < maxWaitPod)
            {
                string podStatusCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods -n argocd -l app.kubernetes.io/name=argocd-server -o jsonpath='{.items[*].status.phase}'";
                timeout2 = 30;
                (string podStatus, _) = pctService.Execute(controlID, podStatusCmd, timeout2);
                if (podStatus.Contains("Running"))
                {
                    string readyCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods -n argocd -l app.kubernetes.io/name=argocd-server -o jsonpath='{.items[*].status.conditions[?(@.type==\"Ready\")].status}'";
                    (string readyStatus, _) = pctService.Execute(controlID, readyCmd, timeout2);
                    if (readyStatus.Contains("True"))
                    {
                        Logger.GetLogger("install_argocd").Printf("ArgoCD server pod is ready");
                        allPodsReady = true;
                        break;
                    }
                }
                if (waitTimePod % 30 == 0)
                {
                    Logger.GetLogger("install_argocd").Printf("Waiting for ArgoCD pods to be ready (waited {0}/{1} seconds)...", waitTimePod, maxWaitPod);
                }
                Thread.Sleep(10000);
                waitTimePod += 10;
            }
            if (!allPodsReady && waitTimePod >= maxWaitPod)
            {
                Logger.GetLogger("install_argocd").Printf("ArgoCD pods not ready after {0} seconds, checking pod status and logs...", maxWaitPod);
                string debugCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods -n argocd -o wide";
                timeout2 = 30;
                (string debugOutput, _) = pctService.Execute(controlID, debugCmd, timeout2);
                if (!string.IsNullOrEmpty(debugOutput))
                {
                    Logger.GetLogger("install_argocd").Printf("Pod status: {0}", debugOutput);
                }
                
                // Get logs from argocd-server pod if it exists
                string getPodCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods -n argocd -l app.kubernetes.io/name=argocd-server -o jsonpath='{.items[0].metadata.name}' 2>&1";
                (string podName, _) = pctService.Execute(controlID, getPodCmd, timeout2);
                if (!string.IsNullOrEmpty(podName) && !podName.Contains("error"))
                {
                    podName = podName.Trim();
                    string logsCmd = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl logs -n argocd {podName} --tail=100 2>&1";
                    (string logs, _) = pctService.Execute(controlID, logsCmd, timeout2);
                    Logger.GetLogger("install_argocd").Printf("ArgoCD server pod logs (last 100 lines): {0}", logs);
                }
                
                // Check for events
                string eventsCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get events -n argocd --sort-by='.lastTimestamp' | tail -20";
                (string events, _) = pctService.Execute(controlID, eventsCmd, timeout2);
                if (!string.IsNullOrEmpty(events))
                {
                    Logger.GetLogger("install_argocd").Printf("Recent events in argocd namespace: {0}", events);
                }
                
                Logger.GetLogger("install_argocd").Printf("ArgoCD installation failed - pods not ready after {0} seconds", maxWaitPod);
                return false;
            }

            // Set bootstrap password to "admin1234" (similar to Rancher, but ArgoCD requires 8+ characters)
            // Use ArgoCD CLI to change the password after pod is ready
            Logger.GetLogger("install_argocd").Printf("Setting ArgoCD bootstrap password to 'admin'...");
            
            // Wait a bit for ArgoCD to fully initialize after pod is ready
            Thread.Sleep(10000);
            
            // Get the initial admin password from the secret
            string getInitialPasswordCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get secret -n argocd argocd-initial-admin-secret -o jsonpath='{.data.password}' 2>&1 | base64 -d";
            timeout2 = 30;
            (string initialPassword, int? getPasswordExit) = pctService.Execute(controlID, getInitialPasswordCmd, timeout2);
            
            if (getPasswordExit.HasValue && getPasswordExit.Value == 0 && !string.IsNullOrEmpty(initialPassword))
            {
                initialPassword = initialPassword.Trim();
                    Logger.GetLogger("install_argocd").Printf("Retrieved initial admin password, changing to 'admin1234'...");
                
                // Change password using ArgoCD CLI via kubectl exec
                // Get the argocd-server pod name
                string getPodCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods -n argocd -l app.kubernetes.io/name=argocd-server -o jsonpath='{.items[0].metadata.name}'";
                timeout2 = 30;
                (string podName, int? getPodExit) = pctService.Execute(controlID, getPodCmd, timeout2);
                
                if (getPodExit.HasValue && getPodExit.Value == 0 && !string.IsNullOrEmpty(podName))
                {
                    podName = podName.Trim();
                    Logger.GetLogger("install_argocd").Printf("Changing password using ArgoCD CLI in pod {0}...", podName);
                    
                    // Wait a bit more for ArgoCD server to be ready to accept CLI commands
                    Thread.Sleep(5000);
                    
                    // Use argocd CLI inside the pod to change password
                    // Escape the password properly for shell (need to escape for bash inside kubectl exec)
                    string escapedInitialPassword = initialPassword.Replace("\\", "\\\\").Replace("\"", "\\\"");
                    string newPassword = "admin1234"; // ArgoCD requires 8-32 characters
                    // Login first, then change password in the same exec session
                    string changePasswordCmd = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl exec -n argocd {podName} -- bash -c \"argocd login localhost:8080 --username admin --password \\\"{escapedInitialPassword}\\\" --insecure && argocd account update-password --account admin --current-password \\\"{escapedInitialPassword}\\\" --new-password \\\"{newPassword}\\\" --server localhost:8080 --insecure\" 2>&1";
                    timeout2 = 60;
                    (string changePasswordOutput, int? changePasswordExit) = pctService.Execute(controlID, changePasswordCmd, timeout2);
                    
                    if (changePasswordExit.HasValue && changePasswordExit.Value == 0)
                    {
                        Logger.GetLogger("install_argocd").Printf("ArgoCD bootstrap password changed to '{0}' successfully", newPassword);
                    }
                    else
                    {
                        Logger.GetLogger("install_argocd").Printf("Failed to change password via CLI (exit code: {0}): {1}", changePasswordExit, changePasswordOutput);
                    }
                }
                else
                {
                    Logger.GetLogger("install_argocd").Printf("Could not find ArgoCD server pod, skipping password change");
                }
            }
            else
            {
                Logger.GetLogger("install_argocd").Printf("Could not retrieve initial password (exit code: {0}), skipping password change", getPasswordExit);
            }

            Logger.GetLogger("install_argocd").Printf("ArgoCD installed successfully on NodePort {0}", nodePort);
            Logger.GetLogger("install_argocd").Printf("ArgoCD Bootstrap Password: admin1234");

            // Configure ArgoCD to not redirect when behind a reverse proxy that terminates TLS
            Logger.GetLogger("install_argocd").Printf("Configuring ArgoCD to disable HTTP redirect when behind TLS-terminating proxy...");
            string patchConfigMapCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl patch configmap argocd-cmd-params-cm -n argocd --type merge -p '{\"data\":{\"server.insecure\":\"true\"}}' 2>&1 || echo 'ConfigMap patch failed or already configured'";
            timeout2 = 30;
            (string configMapOutput, int? configMapExit) = pctService.Execute(controlID, patchConfigMapCmd, timeout2);
            if (configMapExit.HasValue && configMapExit.Value == 0)
            {
                Logger.GetLogger("install_argocd").Printf("ArgoCD ConfigMap patched successfully");
                // Restart ArgoCD server to apply the change
                string restartServerCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl rollout restart deployment argocd-server -n argocd";
                timeout2 = 30;
                (string restartOutput, int? restartExit) = pctService.Execute(controlID, restartServerCmd, timeout2);
                if (restartExit.HasValue && restartExit.Value == 0)
                {
                    Logger.GetLogger("install_argocd").Printf("ArgoCD server deployment restarted to apply configuration");
                    // Wait for server to be ready again
                    Thread.Sleep(10000);
                    int maxWaitRestart = 120;
                    int waitTimeRestart = 0;
                    while (waitTimeRestart < maxWaitRestart)
                    {
                        string podReadyCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods -n argocd -l app.kubernetes.io/name=argocd-server -o jsonpath='{.items[0].status.conditions[?(@.type==\"Ready\")].status}' 2>&1";
                        (string podReady, _) = pctService.Execute(controlID, podReadyCmd, 30);
                        if (podReady.Trim() == "True")
                        {
                            Logger.GetLogger("install_argocd").Printf("ArgoCD server is ready after restart");
                            break;
                        }
                        Thread.Sleep(5000);
                        waitTimeRestart += 5;
                    }
                }
            }
            else
            {
                Logger.GetLogger("install_argocd").Printf("Failed to patch ArgoCD ConfigMap (may already be configured): {0}", configMapOutput);
            }

            // Create Ingress for ArgoCD
            if (!string.IsNullOrEmpty(Cfg.Domain))
            {
                Logger.GetLogger("install_argocd").Printf("Creating Ingress for ArgoCD...");
                string argocdHost = $"argocd.{Cfg.Domain}";
                string ingressYaml = $@"apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: argocd-server
  namespace: argocd
  annotations:
    traefik.ingress.kubernetes.io/router.entrypoints: web,websecure
spec:
  rules:
  - host: {argocdHost}
    http:
      paths:
      - path: /
        pathType: Prefix
        backend:
          service:
            name: argocd-server
            port:
              number: 80
  tls:
  - hosts:
    - {argocdHost}
";
                string createIngressCmd = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && cat <<'EOF' | kubectl apply -f -\n{ingressYaml}EOF";
                timeout2 = 30;
                (string ingressOutput, int? ingressExit) = pctService.Execute(controlID, createIngressCmd, timeout2);
                if (ingressExit.HasValue && ingressExit.Value == 0)
                {
                    Logger.GetLogger("install_argocd").Printf("ArgoCD Ingress created successfully for {0}", argocdHost);
                }
                else
                {
                    Logger.GetLogger("install_argocd").Printf("Failed to create ArgoCD Ingress: {0}", ingressOutput);
                }
            }

            return true;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }
}

public static class InstallArgoCDActionFactory
{
    public static IAction NewInstallArgoCDAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallArgoCDAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
