using System;
using System.Text.RegularExpressions;
using System.Threading;
using Enva.Libs;
using Enva.Orchestration;
using Enva.Services;

namespace Enva.Actions;

public class InstallMetalLBAction : BaseAction, IAction
{
    public InstallMetalLBAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "install metallb";
    }

    public bool Execute()
    {
        if (Cfg == null)
        {
            Logger.GetLogger("install_metallb").Printf("Lab configuration is missing for InstallMetalLBAction.");
            return false;
        }
        if (Cfg.Kubernetes == null)
        {
            Logger.GetLogger("install_metallb").Printf("Kubernetes configuration is missing. Cannot install MetalLB.");
            return false;
        }
        Logger.GetLogger("install_metallb").Printf("Installing MetalLB on Kubernetes cluster...");
        var context = Kubernetes.BuildKubernetesContext(Cfg);
        if (context == null)
        {
            Logger.GetLogger("install_metallb").Printf("Failed to build Kubernetes context.");
            return false;
        }
        if (context.Control.Count == 0)
        {
            Logger.GetLogger("install_metallb").Printf("No Kubernetes control node found.");
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

            // Check if kubectl is available
            Logger.GetLogger("install_metallb").Printf("Checking kubectl...");
            string kubectlCheckCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && command -v kubectl && echo installed || echo not_installed";
            (string kubectlCheck, _) = pctService.Execute(controlID, kubectlCheckCmd, null);
            if (kubectlCheck.Contains("not_installed"))
            {
                Logger.GetLogger("install_metallb").Printf("Installing kubectl...");
                string installKubectlCmd = "curl -LO https://dl.k8s.io/release/$(curl -L -s https://dl.k8s.io/release/stable.txt)/bin/linux/amd64/kubectl && chmod +x kubectl && sudo mv kubectl /usr/local/bin/ && export PATH=/usr/local/bin:$PATH";
                int timeout = 120;
                pctService.Execute(controlID, installKubectlCmd, timeout);
                string verifyCmd = "test -f /usr/local/bin/kubectl && echo installed || echo not_installed";
                (string verifyOutput, _) = pctService.Execute(controlID, verifyCmd, null);
                if (verifyOutput.Contains("not_installed"))
                {
                    Logger.GetLogger("install_metallb").Printf("kubectl installation failed");
                    return false;
                }
            }

            // Check if MetalLB is already installed
            Logger.GetLogger("install_metallb").Printf("Checking if MetalLB is already installed...");
            string checkCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get namespace metallb-system 2>&1";
            int timeout2 = 30;
            (string checkOutput, int? checkExit) = pctService.Execute(controlID, checkCmd, timeout2);
            if (checkExit.HasValue && checkExit.Value == 0 && checkOutput.Contains("metallb-system"))
            {
                Logger.GetLogger("install_metallb").Printf("MetalLB namespace already exists, checking if installation is complete...");
                string checkPodsCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods -n metallb-system --no-headers 2>&1 | wc -l";
                (string podsCount, _) = pctService.Execute(controlID, checkPodsCmd, timeout2);
                if (int.TryParse(podsCount.Trim(), out int count) && count > 0)
                {
                    Logger.GetLogger("install_metallb").Printf("MetalLB is already installed, skipping installation.");
                    return true;
                }
            }

            Logger.GetLogger("install_metallb").Printf("Installing MetalLB using kubectl apply (server-side)...");
            string installCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl apply --server-side -f https://raw.githubusercontent.com/metallb/metallb/v0.13.7/config/manifests/metallb-native.yaml";
            timeout2 = 300;
            int maxRetries = 3;
            bool installSuccess = false;
            for (int retry = 0; retry < maxRetries; retry++)
            {
                (string installOutput, int? installExit) = pctService.Execute(controlID, installCmd, timeout2);
                if (installExit.HasValue && installExit.Value == 0)
                {
                    Logger.GetLogger("install_metallb").Printf("MetalLB installation started successfully");
                    installSuccess = true;
                    break;
                }
                if (retry < maxRetries - 1)
                {
                    Logger.GetLogger("install_metallb").Printf("MetalLB installation failed (attempt {0}/{1}), retrying in 5 seconds...", retry + 1, maxRetries);
                    Thread.Sleep(5000);
                }
                else
                {
                    int outputLen = installOutput.Length;
                    int start = outputLen > 1000 ? outputLen - 1000 : 0;
                    Logger.GetLogger("install_metallb").Printf("MetalLB installation failed after {0} attempts: {1}", maxRetries, installOutput.Substring(start));
                    return false;
                }
            }

            if (!installSuccess)
            {
                return false;
            }

            Logger.GetLogger("install_metallb").Printf("Waiting for MetalLB pods to be ready...");
            int maxWaitPod = 300;
            int waitTimePod = 0;
            bool allPodsReady = false;
            while (waitTimePod < maxWaitPod)
            {
                // Check pod status - get all pods in metallb-system namespace
                string podStatusCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods -n metallb-system --no-headers 2>&1";
                timeout2 = 30;
                (string podOutput, _) = pctService.Execute(controlID, podStatusCmd, timeout2);
                if (!string.IsNullOrEmpty(podOutput))
                {
                    // Count pods that are ready (READY column shows "1/1" or similar)
                    string[] lines = podOutput.Split('\n');
                    int totalPods = 0;
                    int readyPods = 0;
                    foreach (string line in lines)
                    {
                        string trimmedLine = line.Trim();
                        if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.Contains("NAME") || trimmedLine.Contains("No resources found"))
                            continue;
                        totalPods++;
                        // Check if line contains "1/1" or "2/2" etc followed by "Running" (more flexible pattern)
                        // Pattern: digits/digits followed by whitespace and "Running"
                        if (Regex.IsMatch(trimmedLine, @"\d+/\d+\s+Running"))
                        {
                            readyPods++;
                        }
                    }
                    if (totalPods > 0 && readyPods == totalPods)
                    {
                        Logger.GetLogger("install_metallb").Printf("MetalLB pods are ready ({0}/{1})", readyPods, totalPods);
                        allPodsReady = true;
                        break;
                    }
                }
                if (waitTimePod % 30 == 0)
                {
                    Logger.GetLogger("install_metallb").Printf("Waiting for MetalLB pods to be ready (waited {0}/{1} seconds)...", waitTimePod, maxWaitPod);
                }
                Thread.Sleep(10000);
                waitTimePod += 10;
            }
            if (!allPodsReady && waitTimePod >= maxWaitPod)
            {
                Logger.GetLogger("install_metallb").Printf("MetalLB pods not ready after {0} seconds, checking pod status...", maxWaitPod);
                string debugCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods -n metallb-system -o wide";
                timeout2 = 30;
                (string debugOutput, _) = pctService.Execute(controlID, debugCmd, timeout2);
                if (!string.IsNullOrEmpty(debugOutput))
                {
                    Logger.GetLogger("install_metallb").Printf("Pod status: {0}", debugOutput);
                }
                // Final check - if all pods show Running with ready containers, consider them ready
                string finalCheckCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods -n metallb-system --no-headers 2>&1";
                (string finalOutput, _) = pctService.Execute(controlID, finalCheckCmd, timeout2);
                if (!string.IsNullOrEmpty(finalOutput))
                {
                    Logger.GetLogger("install_metallb").Printf("Final check output: {0}", finalOutput);
                    string[] lines = finalOutput.Split('\n');
                    int totalPods = 0;
                    int readyPods = 0;
                    foreach (string line in lines)
                    {
                        string trimmedLine = line.Trim();
                        if (string.IsNullOrWhiteSpace(trimmedLine) || trimmedLine.Contains("NAME") || trimmedLine.Contains("No resources found"))
                            continue;
                        totalPods++;
                        // More flexible pattern - just check for digits/digits followed by Running
                        if (Regex.IsMatch(trimmedLine, @"\d+/\d+\s+Running"))
                        {
                            readyPods++;
                        }
                        else
                        {
                            Logger.GetLogger("install_metallb").Printf("Line did not match ready pattern: {0}", trimmedLine);
                        }
                    }
                    if (totalPods > 0 && readyPods == totalPods)
                    {
                        Logger.GetLogger("install_metallb").Printf("All {0} MetalLB pods are actually ready, continuing...", readyPods);
                        allPodsReady = true;
                    }
                    else
                    {
                        Logger.GetLogger("install_metallb").Printf("MetalLB installation failed - pods not ready after {0} seconds (ready: {1}/{2})", maxWaitPod, readyPods, totalPods);
                        return false;
                    }
                }
                else
                {
                    Logger.GetLogger("install_metallb").Printf("MetalLB installation failed - pods not ready after {0} seconds (no output from final check)", maxWaitPod);
                    return false;
                }
            }

            Logger.GetLogger("install_metallb").Printf("Configuring MetalLB IPAddressPool and L2Advertisement...");
            
            // Get IP pool range from network configuration
            // Default to 10.11.2.20-10.11.2.30, parse from Cfg.Network if available
            string ipPoolStart = "10.11.2.20";
            string ipPoolEnd = "10.11.2.30";
            if (!string.IsNullOrEmpty(Cfg.Network))
            {
                // Parse network like "10.11.2.0/24" and use .20-.30 for MetalLB pool
                string[] parts = Cfg.Network.Split('.');
                if (parts.Length >= 3)
                {
                    ipPoolStart = $"{parts[0]}.{parts[1]}.{parts[2]}.20";
                    ipPoolEnd = $"{parts[0]}.{parts[1]}.{parts[2]}.30";
                }
            }

            // Create IPAddressPool
            string ipAddressPoolYaml = $@"apiVersion: metallb.io/v1beta1
kind: IPAddressPool
metadata:
  name: default-pool
  namespace: metallb-system
spec:
  addresses:
  - {ipPoolStart}-{ipPoolEnd}
";
            string createPoolCmd = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && cat <<'EOF' | kubectl apply -f -\n{ipAddressPoolYaml}EOF";
            timeout2 = 30;
            (string poolOutput, int? poolExit) = pctService.Execute(controlID, createPoolCmd, timeout2);
            if (poolExit.HasValue && poolExit.Value == 0)
            {
                Logger.GetLogger("install_metallb").Printf("IPAddressPool created successfully ({0} to {1})", ipPoolStart, ipPoolEnd);
            }
            else
            {
                Logger.GetLogger("install_metallb").Printf("Failed to create IPAddressPool: {0}", poolOutput);
                return false;
            }

            // Create L2Advertisement
            string l2AdvertisementYaml = $@"apiVersion: metallb.io/v1beta1
kind: L2Advertisement
metadata:
  name: default
  namespace: metallb-system
spec:
  ipAddressPools:
  - default-pool
";
            string createL2Cmd = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && cat <<'EOF' | kubectl apply -f -\n{l2AdvertisementYaml}EOF";
            timeout2 = 30;
            (string l2Output, int? l2Exit) = pctService.Execute(controlID, createL2Cmd, timeout2);
            if (l2Exit.HasValue && l2Exit.Value == 0)
            {
                Logger.GetLogger("install_metallb").Printf("L2Advertisement created successfully");
            }
            else
            {
                Logger.GetLogger("install_metallb").Printf("Failed to create L2Advertisement: {0}", l2Output);
                return false;
            }

            // Ensure k3s ServiceLB is disabled so MetalLB can own LoadBalancer services
            Logger.GetLogger("install_metallb").Printf("Ensuring k3s ServiceLB is disabled...");
            string readK3sConfigCmd = "cat /etc/rancher/k3s/config.yaml 2>/dev/null || true";
            (string k3sConfigOutput, _) = pctService.Execute(controlID, readK3sConfigCmd, 10);
            if (!k3sConfigOutput.Contains("servicelb", StringComparison.OrdinalIgnoreCase))
            {
                Logger.GetLogger("install_metallb").Printf("Adding disable servicelb to k3s config and restarting k3s...");
                string appendServicelbCmd = "grep -q servicelb /etc/rancher/k3s/config.yaml 2>/dev/null || (printf '\\ndisable:\\n  - servicelb\\n' | sudo tee -a /etc/rancher/k3s/config.yaml)";
                pctService.Execute(controlID, appendServicelbCmd, 10);
                string restartK3sCmd = "sudo systemctl restart k3s";
                pctService.Execute(controlID, restartK3sCmd, 15);
                Logger.GetLogger("install_metallb").Printf("Waiting for k3s API after restart...");
                int waitK3s = 0;
                while (waitK3s < 120)
                {
                    Thread.Sleep(5000);
                    waitK3s += 5;
                    string nodesCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get nodes --no-headers 2>&1 | wc -l";
                    (string nodesOut, int? nodesExit) = pctService.Execute(controlID, nodesCmd, 15);
                    if (nodesExit.HasValue && nodesExit.Value == 0 && int.TryParse(nodesOut.Trim(), out int n) && n >= 1)
                    {
                        Logger.GetLogger("install_metallb").Printf("k3s API ready");
                        break;
                    }
                    if (waitK3s % 30 == 0)
                        Logger.GetLogger("install_metallb").Printf("Waiting for k3s API ({0}/120s)...", waitK3s);
                }
            }
            else
            {
                Logger.GetLogger("install_metallb").Printf("k3s already has ServiceLB disabled");
            }

            // Delete any svclb-* DaemonSets left by k3s ServiceLB so MetalLB can assign IPs
            Logger.GetLogger("install_metallb").Printf("Removing any k3s ServiceLB DaemonSets...");
            string deleteSvclbCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get daemonset -n kube-system -o name 2>/dev/null | grep svclb || true | xargs -r -I {} kubectl delete -n kube-system {} --ignore-not-found --timeout=30s 2>&1 || true";
            pctService.Execute(controlID, deleteSvclbCmd, 60);

            // If Traefik LoadBalancer service exists with multiple IPs in status (node IPs from k3s), hand over to MetalLB by removing finalizer and deleting so it is recreated
            Logger.GetLogger("install_metallb").Printf("Checking Traefik service for MetalLB handover...");
            string traefikStatusCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get svc traefik -n kube-system -o jsonpath='{.status.loadBalancer.ingress}' 2>/dev/null || echo 'none'";
            (string traefikStatus, _) = pctService.Execute(controlID, traefikStatusCmd, 15);
            if (!string.IsNullOrEmpty(traefikStatus) && traefikStatus != "none" && traefikStatus.Contains(","))
            {
                Logger.GetLogger("install_metallb").Printf("Traefik service has node IPs in status; removing finalizer and deleting so MetalLB can assign...");
                string patchFinalizerCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl patch svc traefik -n kube-system -p '{\"metadata\":{\"finalizers\":null}}' --type=merge 2>&1";
                pctService.Execute(controlID, patchFinalizerCmd, 15);
                string deleteTraefikCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl delete svc traefik -n kube-system --ignore-not-found 2>&1";
                pctService.Execute(controlID, deleteTraefikCmd, 15);
                Thread.Sleep(5000);
                Logger.GetLogger("install_metallb").Printf("Traefik service will be recreated by ArgoCD; MetalLB will assign an IP from the pool");
            }

            Logger.GetLogger("install_metallb").Printf("MetalLB installed and configured successfully (IP pool: {0} to {1})", ipPoolStart, ipPoolEnd);
            return true;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }
}

public static class InstallMetalLBActionFactory
{
    public static IAction NewInstallMetalLBAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallMetalLBAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
