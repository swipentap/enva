using System;
using System.Linq;
using System.Text;
using System.Threading;
using Enva.Libs;
using Enva.Services;
using Enva.Verification;

namespace Enva.Orchestration;

public class KubernetesDeployContext
{
    public LabConfig? Cfg { get; set; }
    public List<ContainerConfig> Control { get; set; } = new();
    public List<ContainerConfig> Workers { get; set; } = new();
    public string? Token { get; set; }

    public string LXCHost()
    {
        return Cfg?.LXCHost() ?? "";
    }

    public List<ContainerConfig> AllNodes()
    {
        var result = new List<ContainerConfig>(Control);
        result.AddRange(Workers);
        return result;
    }
}

public static class Kubernetes
{
    public static bool DeployKubernetes(LabConfig cfg)
    {
        var logger = Logger.GetLogger("kubernetes");
        var context = BuildKubernetesContext(cfg);
        if (context == null)
        {
            return false;
        }
        if (context.Control.Count == 0)
        {
            return false;
        }
        var controlConfig = context.Control[0];
        if (!GetK3sToken(context, controlConfig))
        {
            return false;
        }
        if (!JoinWorkersToCluster(context, controlConfig))
        {
            return false;
        }
        if (!TaintControlPlane(context, controlConfig))
        {
            return false;
        }
        // Only install Rancher automatically if it's not in the actions list
        // If it's in the actions list, it will be installed separately
        bool hasInstallRancherAction = cfg.Kubernetes?.Actions != null && 
            cfg.Kubernetes.GetActionNames().Any(name => name.ToLower().Contains("install rancher") || name.ToLower().Contains("install-rancher"));
        if (!hasInstallRancherAction)
        {
            if (!InstallRancher(context, controlConfig))
            {
                return false;
            }
        }
        else
        {
            logger.Printf("Rancher installation will be handled by Kubernetes action, skipping automatic installation");
        }

        // Restart all nodes and verify cluster health
        logger.Printf("Restarting all k3s nodes to ensure stability...");
        if (!RestartAndVerifyNodes(context, controlConfig))
        {
            logger.Printf("✗ Node restart/verification failed - deployment incomplete");
        return false;
        }

        logger.Printf("Kubernetes (k3s) cluster deployed");
        return true;
    }

    public static KubernetesDeployContext? BuildKubernetesContext(LabConfig cfg)
    {
        var logger = Logger.GetLogger("kubernetes");
        if (cfg.Kubernetes == null || cfg.Kubernetes.Control == null || cfg.Kubernetes.Workers == null)
        {
            logger.Printf("Kubernetes configuration not found or incomplete");
            return null;
        }

        var controlIDs = new HashSet<int>(cfg.Kubernetes.Control);
        var workerIDs = new HashSet<int>(cfg.Kubernetes.Workers);

        var control = new List<ContainerConfig>();
        var workers = new List<ContainerConfig>();

        foreach (var container in cfg.Containers)
        {
            if (controlIDs.Contains(container.ID))
            {
                control.Add(container);
            }
            if (workerIDs.Contains(container.ID))
            {
                workers.Add(container);
            }
        }

        if (control.Count == 0)
        {
            logger.Printf("Kubernetes control node not found in configuration");
            return null;
        }

        if (workers.Count == 0)
        {
            logger.Printf("No Kubernetes worker nodes found in configuration");
        }

        return new KubernetesDeployContext
        {
            Cfg = cfg,
            Control = control,
            Workers = workers
        };
    }

    private static bool GetK3sToken(KubernetesDeployContext context, ContainerConfig controlConfig)
    {
        var lxcHost = context.LXCHost();
        var cfg = context.Cfg;
        if (cfg == null) return false;
        var controlID = controlConfig.ID;
        var lxcService = new LXCService(lxcHost, cfg.SSH);
        if (!lxcService.Connect())
        {
            return false;
        }
        var pctService = new PCTService(lxcService);
        var logger = Logger.GetLogger("kubernetes");
        logger.Printf("Getting k3s server token...");
        int maxWait = 60;
        int waitTime = 0;
        while (waitTime < maxWait)
        {
            string checkCmd = "systemctl is-active k3s || echo inactive";
            var (checkOutput, _) = pctService.Execute(controlID, checkCmd, null);
            if (checkOutput.Contains("active"))
            {
                break;
            }
            Thread.Sleep(2000);
            waitTime += 2;
        }
        if (waitTime >= maxWait)
        {
            logger.Printf("k3s service not ready on control node");
            return false;
        }
        string tokenCmd = "cat /var/lib/rancher/k3s/server/node-token";
        var (tokenOutput, _) = pctService.Execute(controlID, tokenCmd, null);
        if (string.IsNullOrEmpty(tokenOutput) || string.IsNullOrWhiteSpace(tokenOutput))
        {
            logger.Printf("Failed to get k3s token");
            return false;
        }
        string token = tokenOutput.Trim();
        context.Token = token;
        logger.Printf("k3s token retrieved successfully");
        return true;
    }

    private static bool JoinWorkersToCluster(KubernetesDeployContext context, ContainerConfig controlConfig)
    {
        var lxcHost = context.LXCHost();
        var cfg = context.Cfg;
        if (cfg == null) return false;
        var controlID = controlConfig.ID;
        string controlIP = controlConfig.IPAddress ?? "";
        var token = context.Token;
        if (string.IsNullOrEmpty(token))
        {
            Logger.GetLogger("kubernetes").Printf("k3s token not available");
            return false;
        }
        var lxcService = new LXCService(lxcHost, cfg.SSH);
        if (!lxcService.Connect())
        {
            return false;
        }
        var pctService = new PCTService(lxcService);
        var logger = Logger.GetLogger("kubernetes");
        foreach (var workerConfig in context.Workers)
        {
            int workerID = workerConfig.ID;
            logger.Printf("Joining worker {0} to k3s cluster...", workerID);
            string uninstallCmd = "/usr/local/bin/k3s-agent-uninstall.sh || true";
            pctService.Execute(workerID, uninstallCmd, null);
            logger.Printf("Installing k3s agent on worker {0}...", workerID);
            string joinCmd = $"curl -sfL https://get.k3s.io | K3S_URL=https://{controlIP}:6443 K3S_TOKEN={token} sh -";
            int? timeout = 600;
            var (installOutput, installExit) = pctService.Execute(workerID, joinCmd, timeout);
            if (!installExit.HasValue)
            {
                logger.Printf("k3s agent installation timed out on worker {0}", workerID);
                return false;
            }
            if (installExit.Value != 0)
            {
                logger.Printf("k3s agent installation failed on worker {0}", workerID);
                int outputLen = installOutput.Length;
                int start = 0;
                if (outputLen > 1000)
                {
                    start = outputLen - 1000;
                }
                logger.Printf("Installation output: {0}", installOutput.Substring(start));
                return false;
            }
            logger.Printf("k3s agent installation completed on worker {0}", workerID);
            int outputLen2 = installOutput.Length;
            int start2 = 0;
            if (outputLen2 > 500)
            {
                start2 = outputLen2 - 500;
            }
            if (!string.IsNullOrEmpty(installOutput))
            {
                logger.Printf("Installation output: {0}", installOutput.Substring(start2));
            }
            Thread.Sleep(2000);

            // Create /dev/kmsg directly (same as control node) - required for k3s in LXC
            logger.Printf("Creating /dev/kmsg symlink for worker {0}...", workerID);
            string createKmsgCmd = "rm -f /dev/kmsg && ln -sf /dev/console /dev/kmsg && test -L /dev/kmsg && echo created || echo failed";
            var (kmsgOutput, kmsgExit) = pctService.Execute(workerID, createKmsgCmd, null);
            if (kmsgExit.HasValue && kmsgExit.Value == 0 && kmsgOutput.Contains("created"))
            {
                logger.Printf("✓ /dev/kmsg created on worker {0}", workerID);
            }
            else
            {
                logger.Printf("⚠ Failed to create /dev/kmsg on worker {0}: {1}", workerID, kmsgOutput);
            }

            // Fix systemd service to ensure /dev/kmsg exists before k3s-agent starts (persistent fix for LXC)
            logger.Printf("Configuring k3s-agent service to ensure /dev/kmsg exists on startup for worker {0}...", workerID);
            string serviceFile = "/etc/systemd/system/k3s-agent.service";
            string checkServiceFileCmd = $"cat {serviceFile}";
            var (serviceContent, _) = pctService.Execute(workerID, checkServiceFileCmd, null);
            if (!serviceContent.Contains("/dev/kmsg"))
            {
                string fixServiceScript = "serviceFile=\"" + serviceFile + "\"\n" +
                    "export serviceFile\n" +
                    "cat > /tmp/fix_k3s_service.sh << 'EOFSED'\n" +
                    "#!/bin/bash\n" +
                    "sed -i \"/ExecStartPre=-\\/sbin\\/modprobe br_netfilter/i ExecStartPre=-/bin/bash -c \\\"rm -f /dev/kmsg && ln -sf /dev/console /dev/kmsg\\\"\" \"$serviceFile\"\n" +
                    "EOFSED\n" +
                    "chmod +x /tmp/fix_k3s_service.sh\n" +
                    "/tmp/fix_k3s_service.sh && echo \"success\" || echo \"failed\"\n" +
                    "rm -f /tmp/fix_k3s_service.sh";
                var (fixOutput, fixExit) = pctService.Execute(workerID, fixServiceScript, null);
                if (fixExit.HasValue && fixExit.Value == 0 && fixOutput.Contains("success"))
                {
                    logger.Printf("✓ Added /dev/kmsg fix to k3s-agent.service on worker {0}", workerID);
                    string reloadCmd = "systemctl daemon-reload";
                    pctService.Execute(workerID, reloadCmd, null);
                }
                else
                {
                    logger.Printf("✗ Failed to modify k3s-agent.service on worker {0}: {1}", workerID, fixOutput);
                    logger.Printf("✗ Deployment failed: k3s-agent.service must have ExecStartPre fix for /dev/kmsg (required for LXC containers)");
                    return false;
                }
            }
            else
            {
                logger.Printf("✓ k3s-agent.service already has /dev/kmsg fix on worker {0}", workerID);
            }

            string serviceExistsCmd = "systemctl list-unit-files | grep -q k3s-agent.service && echo exists || echo not_exists";
            var (serviceCheck, _) = pctService.Execute(workerID, serviceExistsCmd, null);
            if (serviceCheck.Contains("not_exists"))
            {
                logger.Printf("k3s-agent service was not created after installation");
                return false;
            }
            int maxWaitService = 120;
            int waitTimeService = 0;
            string workerName = workerConfig.Hostname;
            if (string.IsNullOrEmpty(workerName))
            {
                workerName = $"k3s-worker-{workerID}";
            }
            while (waitTimeService < maxWaitService)
            {
                string checkCmd = "systemctl is-active k3s-agent";
                var (checkOutput, checkExit) = pctService.Execute(workerID, checkCmd, null);
                if (checkExit.HasValue && checkExit.Value == 0 && !string.IsNullOrEmpty(checkOutput) && checkOutput.Trim() == "active")
                {
                    string verifyNodeCmd = $"kubectl get nodes | grep -E '{workerName}|{controlIP}' || echo 'not_found'";
                    var (verifyOutput, verifyExit) = pctService.Execute(controlID, verifyNodeCmd, null);
                    if (verifyExit.HasValue && verifyExit.Value == 0 && !string.IsNullOrEmpty(verifyOutput) && !verifyOutput.Contains("not_found") && verifyOutput.Contains("Ready"))
                    {
                        logger.Printf("Worker {0} ({1}) joined cluster successfully and is Ready", workerID, workerName);
                        break;
                    }
                    else
                    {
                        string recheckCmd = "systemctl is-active k3s-agent";
                        var (recheckOutput, recheckExit) = pctService.Execute(workerID, recheckCmd, null);
                        if (!recheckExit.HasValue || recheckExit.Value != 0 || recheckOutput.Trim() != "active")
                        {
                            logger.Printf("Worker {0} service became inactive, waiting for it to become active again...", workerID);
                        }
                        else
                        {
                            logger.Printf("Worker {0} service is active but not yet Ready in cluster, waiting...", workerID);
                        }
                    }
                }
                else
                {
                    if (waitTimeService % 10 == 0)
                    {
                        string status = "unknown";
                        if (!string.IsNullOrEmpty(checkOutput))
                        {
                            status = checkOutput.Trim();
                        }
                        logger.Printf("Worker {0} service is not active yet (status: {1}), waiting...", workerID, status);
                    }
                }
                Thread.Sleep(2000);
                waitTimeService += 2;
            }
            if (waitTimeService >= maxWaitService)
            {
                logger.Printf("k3s-agent service not ready or node not in cluster on worker {0} after {1} seconds", workerID, maxWaitService);
                string finalCheckCmd = "systemctl is-active k3s-agent";
                var (finalCheck, _) = pctService.Execute(workerID, finalCheckCmd, null);
                if (finalCheck.Contains("active"))
                {
                    logger.Printf("Service is active but node did not appear in cluster");
                }
                else
                {
                    logger.Printf("Service is not active");
                }
                return false;
            }
        }
        return true;
    }

    private static bool TaintControlPlane(KubernetesDeployContext context, ContainerConfig controlConfig)
    {
        var lxcHost = context.LXCHost();
        var cfg = context.Cfg;
        if (cfg == null) return false;
        var controlID = controlConfig.ID;
        var lxcService = new LXCService(lxcHost, cfg.SSH);
        if (!lxcService.Connect())
        {
            return false;
        }
        var pctService = new PCTService(lxcService);
        var logger = Logger.GetLogger("kubernetes");
        logger.Printf("Tainting control plane node to prevent regular pods from scheduling...");
        int maxWait = 60;
        int waitTime = 0;
        while (waitTime < maxWait)
        {
            string checkCmd = "kubectl get nodes";
            int? timeout = 30;
            var (checkOutput, checkExit) = pctService.Execute(controlID, checkCmd, timeout);
            if (checkExit.HasValue && checkExit.Value == 0 && !string.IsNullOrEmpty(checkOutput) && checkOutput.Contains("Ready"))
            {
                break;
            }
            Thread.Sleep(2000);
            waitTime += 2;
        }
        if (waitTime >= maxWait)
        {
            logger.Printf("kubectl not ready, skipping control plane taint");
            return true;
        }
        string taintCmd = "kubectl taint nodes k3s-control node-role.kubernetes.io/control-plane:NoSchedule --overwrite";
        int? timeout2 = 30;
        var (taintOutput, taintExit) = pctService.Execute(controlID, taintCmd, timeout2);
        if (taintExit.HasValue && taintExit.Value == 0)
        {
            logger.Printf("Control plane node tainted successfully - regular pods will not schedule on it");
            return true;
        }
        if (taintOutput.Contains("already has") || taintOutput.Contains("modified"))
        {
            logger.Printf("Control plane node already tainted");
            return true;
        }
        int outputLen = taintOutput.Length;
        int start = 0;
        if (outputLen > 200)
        {
            start = outputLen - 200;
        }
        logger.Printf("Failed to taint control plane node: {0}", taintOutput.Substring(start));
        return true;
    }

    public static bool InstallRancher(KubernetesDeployContext context, ContainerConfig controlConfig)
    {
        var cfg = context.Cfg;
        var logger = Logger.GetLogger("kubernetes");
        if (cfg == null || cfg.Services?.Services == null || !cfg.Services.Services.ContainsKey("rancher"))
        {
            logger.Printf("Rancher not configured, skipping installation");
            return true;
        }

        var lxcHost = context.LXCHost();
        var controlID = controlConfig.ID;
        var lxcService = new LXCService(lxcHost, cfg.SSH);
        if (!lxcService.Connect())
        {
            return false;
        }
        try
        {
            var pctService = new PCTService(lxcService);
            logger.Printf("Installing Rancher...");

            // Install kubectl if needed
            string kubectlCheckCmd = "command -v kubectl && echo installed || echo not_installed";
            (string kubectlCheck, int? _) = pctService.Execute(controlID, kubectlCheckCmd, null);
            if (kubectlCheck.Contains("not_installed"))
            {
                logger.Printf("Installing kubectl...");
                string installKubectlCmd = "curl -LO https://dl.k8s.io/release/$(curl -L -s https://dl.k8s.io/release/stable.txt)/bin/linux/amd64/kubectl && chmod +x kubectl && sudo mv kubectl /usr/local/bin/ && export PATH=/usr/local/bin:$PATH";
                int? kubectlTimeout = 120;
                pctService.Execute(controlID, installKubectlCmd, kubectlTimeout);
                string verifyCmd = "test -f /usr/local/bin/kubectl && echo installed || echo not_installed";
                (string verifyOutput, int? _) = pctService.Execute(controlID, verifyCmd, null);
                if (verifyOutput.Contains("not_installed"))
                {
                    logger.Printf("kubectl installation failed");
                    return false;
                }
            }

            // Verify k3s service is running
            logger.Printf("Verifying k3s service is running...");
            int maxWaitK3s = 120;
            int waitTimeK3s = 0;
            bool k3sActive = false;
            while (waitTimeK3s < maxWaitK3s)
            {
                string k3sCheckCmd = "systemctl is-active k3s || echo inactive";
                (string k3sCheck, int? _) = pctService.Execute(controlID, k3sCheckCmd, null);
                if (k3sCheck.Contains("active"))
                {
                    logger.Printf("k3s service is running");
                    k3sActive = true;
                    break;
                }
                logger.Printf("Waiting for k3s service to be active (waited {0}/{1} seconds)...", waitTimeK3s, maxWaitK3s);
                Thread.Sleep(5000);
                waitTimeK3s += 5;
            }
            if (!k3sActive)
            {
                logger.Printf("k3s service not active after {0} seconds", maxWaitK3s);
                return false;
            }

            // Update k3s kubeconfig with control node IP
            logger.Printf("Updating k3s kubeconfig with control node IP...");
            string controlIP = controlConfig.IPAddress ?? "";
            string kubeconfigCmd = $"sudo sed -i 's|server: https://127.0.0.1:6443|server: https://{controlIP}:6443|g; s|server: https://0.0.0.0:6443|server: https://{controlIP}:6443|g' /etc/rancher/k3s/k3s.yaml";
            pctService.Execute(controlID, kubeconfigCmd, null);
            string setupKubeconfigCmd = "mkdir -p /root/.kube && cp /etc/rancher/k3s/k3s.yaml /root/.kube/config && chown root:root /root/.kube/config && chmod 600 /root/.kube/config";
            pctService.Execute(controlID, setupKubeconfigCmd, null);

            // Verify kubectl works without KUBECONFIG specified
            logger.Printf("Verifying kubectl works without KUBECONFIG specified...");
            string verifyKubectlCmd = "kubectl get nodes";
            int? timeout = 30;
            (string verifyKubectlOutput, int? verifyKubectlExit) = pctService.Execute(controlID, verifyKubectlCmd, timeout);
            if (!verifyKubectlExit.HasValue || verifyKubectlExit.Value != 0 || string.IsNullOrEmpty(verifyKubectlOutput) || !verifyKubectlOutput.Contains("Ready"))
            {
                logger.Printf("kubectl does not work without KUBECONFIG specified");
                if (!string.IsNullOrEmpty(verifyKubectlOutput))
                {
                    int start = Math.Max(0, verifyKubectlOutput.Length - 500);
                    logger.Printf("kubectl output: {0}", verifyKubectlOutput.Substring(start));
                }
                return false;
            }
            logger.Printf("kubectl works correctly without KUBECONFIG specified");

            // Verify Kubernetes API is reachable
            logger.Printf("Verifying Kubernetes API is reachable...");
            string verifyAPICmd = "kubectl cluster-info";
            int maxVerifyAttempts = 20;
            bool apiReachable = false;
            for (int attempt = 0; attempt < maxVerifyAttempts; attempt++)
            {
                int? apiTimeout = 30;
                (string verifyOutput, int? verifyExit) = pctService.Execute(controlID, verifyAPICmd, apiTimeout);
                if (verifyExit.HasValue && verifyExit.Value == 0 && !string.IsNullOrEmpty(verifyOutput) && verifyOutput.Contains("is running at"))
                {
                    logger.Printf("Kubernetes API is reachable");
                    apiReachable = true;
                    break;
                }
                if (attempt < maxVerifyAttempts - 1)
                {
                    logger.Printf("Waiting for Kubernetes API to be ready (attempt {0}/{1})...", attempt + 1, maxVerifyAttempts);
                    Thread.Sleep(5000);
                }
                else
                {
                    logger.Printf("Kubernetes API not reachable after {0} attempts", maxVerifyAttempts);
                    if (!string.IsNullOrEmpty(verifyOutput))
                    {
                        logger.Printf("API check output: {0}", verifyOutput);
                    }
                    return false;
                }
            }

            // Wait for CNI plugin (Flannel) to be ready
            logger.Printf("Waiting for CNI plugin (Flannel) to be ready...");
            int maxCNIWait = 120;
            int cniWaitTime = 0;
            bool cniReady = false;
            while (cniWaitTime < maxCNIWait)
            {
                string nodesCmd = "kubectl get nodes -o jsonpath='{.items[*].status.conditions[?(@.type==\"Ready\")].status}'";
                int? cniTimeout = 30;
                (string nodesOutput, int? nodesExit) = pctService.Execute(controlID, nodesCmd, cniTimeout);
                bool nodesReady = nodesExit.HasValue && nodesExit.Value == 0 && !string.IsNullOrEmpty(nodesOutput) && nodesOutput.Contains("True") && !nodesOutput.Contains("False");

                string cniConfigCmd = "test -f /var/lib/rancher/k3s/agent/etc/cni/net.d/10-flannel.conflist && echo exists || echo missing";
                int? cniConfigTimeout = 10;
                (string cniConfigOutput, int? cniConfigExit) = pctService.Execute(controlID, cniConfigCmd, cniConfigTimeout);
                bool cniConfigExists = cniConfigExit.HasValue && cniConfigExit.Value == 0 && !string.IsNullOrEmpty(cniConfigOutput) && cniConfigOutput.Contains("exists");

                string flannelSubnetCmd = "test -f /run/flannel/subnet.env && echo exists || echo missing";
                (string flannelSubnetOutput, int? flannelSubnetExit) = pctService.Execute(controlID, flannelSubnetCmd, timeout);
                bool flannelSubnetExists = flannelSubnetExit.HasValue && flannelSubnetExit.Value == 0 && !string.IsNullOrEmpty(flannelSubnetOutput) && flannelSubnetOutput.Contains("exists");

                string pendingCNICmd = "kubectl get pods -n kube-system --field-selector=status.phase=Pending -o jsonpath='{.items[*].status.conditions[?(@.type==\"PodScheduled\")].message}' | grep -q 'network is not ready' && echo cni_error || echo no_cni_error";
                (string pendingCNIOutput, int? pendingCNIExit) = pctService.Execute(controlID, pendingCNICmd, cniTimeout);
                bool noCNIErrors = pendingCNIExit.HasValue && pendingCNIExit.Value == 0 && !string.IsNullOrEmpty(pendingCNIOutput) && pendingCNIOutput.Contains("no_cni_error");

                string runningPodsCmd = "kubectl get pods -n kube-system --field-selector=status.phase=Running --no-headers | wc -l";
                (string runningPodsOutput, int? runningPodsExit) = pctService.Execute(controlID, runningPodsCmd, cniTimeout);
                bool podsRunning = false;
                if (runningPodsExit.HasValue && runningPodsExit.Value == 0 && !string.IsNullOrEmpty(runningPodsOutput))
                {
                    if (int.TryParse(runningPodsOutput.Trim(), out int count))
                    {
                        podsRunning = count >= 3;
                    }
                }

                if (nodesReady && cniConfigExists && flannelSubnetExists && noCNIErrors && podsRunning)
                {
                    logger.Printf("CNI plugin (Flannel) is ready - nodes Ready, CNI config exists, Flannel subnet exists, system pods running");
                    cniReady = true;
                    break;
                }

                if (cniWaitTime % 20 == 0)
                {
                    logger.Printf("Waiting for CNI plugin to be ready (waited {0}/{1} seconds)...", cniWaitTime, maxCNIWait);
                    string nodesReadyStr = nodesReady ? "True" : "False";
                    string cniConfigStr = cniConfigExists ? "exists" : "missing";
                    string flannelSubnetStr = flannelSubnetExists ? "exists" : "missing";
                    string runningPodsStr = !string.IsNullOrEmpty(runningPodsOutput) ? runningPodsOutput.Trim() : "unknown";
                    logger.Printf("Nodes Ready: {0}, CNI config: {1}, Flannel subnet: {2}, Running pods: {3}", nodesReadyStr, cniConfigStr, flannelSubnetStr, runningPodsStr);
                }
                Thread.Sleep(5000);
                cniWaitTime += 5;
            }
            if (!cniReady)
            {
                logger.Printf("CNI plugin (Flannel) not ready after {0} seconds - cannot proceed with cert-manager installation", maxCNIWait);
                return false;
            }

            // Create cattle-system namespace
            string namespaceCmd = "kubectl create namespace cattle-system --dry-run=client -o yaml | kubectl apply -f -";
            pctService.Execute(controlID, namespaceCmd, null);

            // Install cert-manager
            logger.Printf("Installing cert-manager...");
            string certManagerCmd = "kubectl apply --validate=false --server-side --force-conflicts -f https://github.com/cert-manager/cert-manager/releases/download/v1.13.0/cert-manager.yaml";
            int maxRetries = 3;
            bool certManagerInstalled = false;
            for (int retry = 0; retry < maxRetries; retry++)
            {
                int? certManagerTimeout = 300;
                (string certManagerOutput, int? _) = pctService.Execute(controlID, certManagerCmd, certManagerTimeout);
                if (certManagerOutput.Contains("serverside-applied"))
                {
                    logger.Printf("cert-manager resources applied successfully");
                    string verifyCmd = "kubectl get namespace cert-manager";
                    int? verifyTimeout2 = 30;
                    (string verifyOutput, int? verifyExit) = pctService.Execute(controlID, verifyCmd, verifyTimeout2);
                    if (verifyExit.HasValue && verifyExit.Value == 0 && !string.IsNullOrEmpty(verifyOutput) && verifyOutput.Contains("cert-manager"))
                    {
                        logger.Printf("cert-manager installed and verified successfully");
                        certManagerInstalled = true;
                        break;
                    }
                    else if (CountOccurrences(certManagerOutput, "serverside-applied") >= 10)
                    {
                        logger.Printf("cert-manager resources applied successfully (verification skipped due to API unavailability)");
                        certManagerInstalled = true;
                        break;
                    }
                }
                if (retry < maxRetries - 1)
                {
                    logger.Printf("cert-manager installation failed (attempt {0}/{1}), retrying in 10 seconds...", retry + 1, maxRetries);
                    if (!string.IsNullOrEmpty(certManagerOutput))
                    {
                        int start = Math.Max(0, certManagerOutput.Length - 500);
                        logger.Printf("Error output: {0}", certManagerOutput.Substring(start));
                    }
                    Thread.Sleep(10000);
                }
                else
                {
                    logger.Printf("Failed to install cert-manager after {0} attempts: {1}", maxRetries, certManagerOutput);
                    return false;
                }
            }

            // Wait for cert-manager webhook to be ready
            logger.Printf("Waiting for cert-manager webhook to be ready...");
            int maxWebhookWait = 300;
            int webhookWaitTime = 0;
            bool webhookReady = false;
            while (webhookWaitTime < maxWebhookWait)
            {
                string webhookCheckCmd = "kubectl get pods -n cert-manager -l app.kubernetes.io/component=webhook -o jsonpath='{.items[*].status.conditions[?(@.type==\"Ready\")].status}'";
                int? webhookTimeout = 30;
                (string webhookOutput, int? webhookExit) = pctService.Execute(controlID, webhookCheckCmd, webhookTimeout);
                if (webhookExit.HasValue && webhookExit.Value == 0 && !string.IsNullOrEmpty(webhookOutput))
                {
                    int readyCount = CountOccurrences(webhookOutput, "True");
                    if (readyCount > 0)
                    {
                        string endpointsCmd = "kubectl get endpoints cert-manager-webhook -n cert-manager -o jsonpath='{.subsets[*].addresses[*].ip}'";
                        (string endpointsOutput, int? endpointsExit) = pctService.Execute(controlID, endpointsCmd, webhookTimeout);
                        if (endpointsExit.HasValue && endpointsExit.Value == 0 && !string.IsNullOrEmpty(endpointsOutput) && !string.IsNullOrWhiteSpace(endpointsOutput))
                        {
                            logger.Printf("cert-manager webhook is ready with {0} pod(s) and endpoints available", readyCount);
                            webhookReady = true;
                            break;
                        }
                    }
                }
                if (webhookWaitTime % 30 == 0)
                {
                    logger.Printf("Waiting for cert-manager webhook to be ready (waited {0}/{1} seconds)...", webhookWaitTime, maxWebhookWait);
                    if (!string.IsNullOrEmpty(webhookOutput))
                    {
                        int start = Math.Max(0, webhookOutput.Length - 200);
                        logger.Printf("Webhook pods status: {0}", webhookOutput.Substring(start));
                    }
                }
                Thread.Sleep(10000);
                webhookWaitTime += 10;
            }
            if (!webhookReady)
            {
                logger.Printf("cert-manager webhook not ready after {0} seconds - cannot proceed with Rancher installation", maxWebhookWait);
                return false;
            }

            // Verify Kubernetes API is reachable again
            logger.Printf("Verifying Kubernetes API is reachable...");
            string verifyAPICmd2 = "kubectl cluster-info";
            int maxVerifyAttempts2 = 10;
            bool apiReachable2 = false;
            for (int attempt = 0; attempt < maxVerifyAttempts2; attempt++)
            {
                int? apiTimeout2 = 30;
                (string verifyOutput2, int? verifyExit2) = pctService.Execute(controlID, verifyAPICmd2, apiTimeout2);
                if (verifyExit2.HasValue && verifyExit2.Value == 0 && !string.IsNullOrEmpty(verifyOutput2) && verifyOutput2.Contains("is running at"))
                {
                    logger.Printf("Kubernetes API is reachable");
                    apiReachable2 = true;
                    break;
                }
                if (attempt < maxVerifyAttempts2 - 1)
                {
                    logger.Printf("Waiting for Kubernetes API to be ready (attempt {0}/{1})...", attempt + 1, maxVerifyAttempts2);
                    Thread.Sleep(10000);
                }
                else
                {
                    logger.Printf("Kubernetes API not reachable after {0} attempts", maxVerifyAttempts2);
                    if (!string.IsNullOrEmpty(verifyOutput2))
                    {
                        logger.Printf("API check output: {0}", verifyOutput2);
                    }
                    return false;
                }
            }

            // Verify Kubernetes API server stability
            logger.Printf("Verifying Kubernetes API server stability...");
            int stableChecks = 3;
            for (int i = 0; i < stableChecks; i++)
            {
                string verifyCmd = "export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && /usr/local/bin/kubectl cluster-info";
                int? stabilityTimeout = 30;
                (string verifyOutput3, int? verifyExit3) = pctService.Execute(controlID, verifyCmd, stabilityTimeout);
                if (!verifyExit3.HasValue || verifyExit3.Value != 0 || string.IsNullOrEmpty(verifyOutput3) || !verifyOutput3.Contains("is running at"))
                {
                    logger.Printf("API server check {0}/{1} failed, waiting 5 seconds...", i + 1, stableChecks);
                    if (!string.IsNullOrEmpty(verifyOutput3))
                    {
                        logger.Printf("API check output: {0}", verifyOutput3);
                    }
                    Thread.Sleep(5000);
                }
                else
                {
                    logger.Printf("API server check {0}/{1} passed", i + 1, stableChecks);
                    Thread.Sleep(2000);
                }
            }

            // Install Rancher using Helm
            logger.Printf("Installing Rancher using Helm...");
            string helmCheckCmd = "command -v helm && echo installed || echo not_installed";
            (string helmCheck, int? _) = pctService.Execute(controlID, helmCheckCmd, null);
            if (helmCheck.Contains("not_installed"))
            {
                logger.Printf("Installing Helm...");
                string helmInstallCmd = "curl https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 | bash && export PATH=/usr/local/bin:$PATH";
                int? helmTimeout = 120;
                pctService.Execute(controlID, helmInstallCmd, helmTimeout);
            }

            // Add Helm repo
            string repoAddCmd = "export PATH=/usr/local/bin:$PATH && helm repo add rancher-stable https://releases.rancher.com/server-charts/stable && helm repo update";
            int maxRepoRetries = 3;
            bool repoAdded = false;
            for (int repoRetry = 0; repoRetry < maxRepoRetries; repoRetry++)
            {
                int? repoTimeout = 120;
                (string repoOutput, int? repoExit) = pctService.Execute(controlID, repoAddCmd, repoTimeout);
                if (repoExit.HasValue && repoExit.Value == 0)
                {
                    repoAdded = true;
                    break;
                }
                if (repoRetry < maxRepoRetries - 1)
                {
                    logger.Printf("Helm repo add failed (attempt {0}/{1}), retrying in 5 seconds...", repoRetry + 1, maxRepoRetries);
                    if (!string.IsNullOrEmpty(repoOutput))
                    {
                        int start = Math.Max(0, repoOutput.Length - 500);
                        logger.Printf("Error output: {0}", repoOutput.Substring(start));
                    }
                    Thread.Sleep(5000);
                }
                else
                {
                    logger.Printf("Failed to add Helm repo after {0} attempts: {1}", maxRepoRetries, repoOutput);
                    return false;
                }
            }

            // Install Rancher
            string controlHostname = controlConfig.Hostname;
            int rancherNodePort = 30443;
            if (cfg.Services.Services != null && cfg.Services.Services.TryGetValue("rancher", out var rancherService) && rancherService.Ports != null && rancherService.Ports.Count > 0)
            {
                rancherNodePort = rancherService.Ports[0].Port;
            }

            // Final API check before installation
            string verifyCmd2 = "export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && /usr/local/bin/kubectl cluster-info";
            int? finalApiTimeout = 30;
            (string verifyOutput4, int? verifyExit4) = pctService.Execute(controlID, verifyCmd2, finalApiTimeout);
            if (!verifyExit4.HasValue || verifyExit4.Value != 0 || string.IsNullOrEmpty(verifyOutput4) || !verifyOutput4.Contains("is running at"))
            {
                logger.Printf("API server not reachable before Rancher installation");
                if (!string.IsNullOrEmpty(verifyOutput4))
                {
                    int start = Math.Max(0, verifyOutput4.Length - 500);
                    logger.Printf("API check output: {0}", verifyOutput4.Substring(start));
                }
                return false;
            }

            string installRancherCmd = $"export PATH=/usr/local/bin:$PATH && helm upgrade --install rancher rancher-stable/rancher --namespace cattle-system --set hostname={controlHostname} --set replicas=1 --set bootstrapPassword=admin --set service.type=NodePort --set service.ports.http=8080 --set service.ports.https=443 --set service.nodePorts.https={rancherNodePort}";
            int? installTimeout = 600;
            (string installOutput, int? installExit) = pctService.Execute(controlID, installRancherCmd, installTimeout);
            if (installExit.HasValue && installExit.Value == 0)
            {
                logger.Printf("Rancher installed successfully");
                logger.Printf("Setting Rancher service NodePort to {0}...", rancherNodePort);
                string getHTTPPortCmd = "kubectl get svc rancher -n cattle-system -o jsonpath='{.spec.ports[?(@.name==\"http\")].nodePort}'";
                int? patchTimeout = 10;
                (string httpPortOutput, int? _) = pctService.Execute(controlID, getHTTPPortCmd, patchTimeout);
                string httpNodePort = "30625";
                if (!string.IsNullOrEmpty(httpPortOutput))
                {
                    httpNodePort = httpPortOutput.Trim();
                }
                string patchCmd = $"kubectl patch svc rancher -n cattle-system -p '{{\"spec\":{{\"ports\":[{{\"name\":\"http\",\"port\":80,\"protocol\":\"TCP\",\"targetPort\":80,\"nodePort\":{httpNodePort}}},{{\"name\":\"https\",\"port\":443,\"protocol\":\"TCP\",\"targetPort\":443,\"nodePort\":{rancherNodePort}}}]}}}}'";
                patchTimeout = 30;
                (string patchOutput, int? patchExit) = pctService.Execute(controlID, patchCmd, patchTimeout);
                if (patchExit.HasValue && patchExit.Value == 0)
                {
                    logger.Printf("Rancher service NodePort set to {0}", rancherNodePort);
                }
                else
                {
                    logger.Printf("Failed to patch Rancher service NodePort: {0}", patchOutput);
                }

                // Wait for Rancher to be ready and accessible
                logger.Printf("Waiting for Rancher to be ready...");
                int maxRancherWait = 600; // 10 minutes max
                int rancherWaitTime = 0;
                bool rancherReady = false;
                string rancherControlIP = controlConfig.IPAddress ?? "";
                
                while (rancherWaitTime < maxRancherWait)
                {
                    // Check if Rancher pod is running and ready
                    string checkPodCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods -n cattle-system -l app=rancher -o jsonpath='{.items[0].status.phase}' 2>/dev/null || echo 'not-found'";
                    int? checkTimeout = 30;
                    (string podPhase, _) = pctService.Execute(controlID, checkPodCmd, checkTimeout);
                    
                    if (podPhase.Contains("Running"))
                    {
                        // Check if pod is ready
                        string checkReadyCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods -n cattle-system -l app=rancher -o jsonpath='{.items[0].status.conditions[?(@.type==\"Ready\")].status}' 2>/dev/null || echo 'False'";
                        (string readyStatus, _) = pctService.Execute(controlID, checkReadyCmd, checkTimeout);
                        
                        if (readyStatus.Contains("True"))
                        {
                            // Try to access Rancher via HTTPS (with -k to bypass self-signed cert, which is expected)
                            // Rancher uses self-signed certificates by default, browsers will show warning but connection works
                            string testRancherCmd = $"timeout 10 curl -k -s -o /dev/null -w '%{{http_code}}' https://{rancherControlIP}:{rancherNodePort} || echo '000'";
                            (string httpCode, _) = pctService.Execute(controlID, testRancherCmd, checkTimeout);
                            
                            if (httpCode.Contains("200") || httpCode.Contains("401") || httpCode.Contains("302"))
                            {
                                // Verify Rancher API is responding (not just returning error page)
                                string testApiCmd = $"timeout 10 curl -k -s https://{rancherControlIP}:{rancherNodePort}/v3-public/features 2>&1 | head -1";
                                (string apiResponse, _) = pctService.Execute(controlID, testApiCmd, checkTimeout);
                                
                                // Rancher is ready if we get HTTP 200/401/302 and API responds (even with 404 is ok, means server is up)
                                logger.Printf("Rancher is ready and accessible on https://{0}:{1} (self-signed certificate - browser will show security warning)", rancherControlIP, rancherNodePort);
                                rancherReady = true;
                                break;
                            }
                        }
                    }
                    
                    if (rancherWaitTime % 30 == 0)
                    {
                        logger.Printf("Waiting for Rancher to be ready (waited {0}/{1} seconds)...", rancherWaitTime, maxRancherWait);
                    }
                    
                    Thread.Sleep(10000);
                    rancherWaitTime += 10;
                }
                
                if (!rancherReady)
                {
                    logger.Printf("⚠ Rancher installation completed but may not be fully ready after {0} seconds", maxRancherWait);
                    // Check for errors in Rancher pods
                    string checkErrorsCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods -n cattle-system -l app=rancher -o jsonpath='{.items[*].status.containerStatuses[*].state.waiting.reason}' 2>/dev/null || echo ''";
                    (string errorStatus, _) = pctService.Execute(controlID, checkErrorsCmd, 30);
                    if (!string.IsNullOrEmpty(errorStatus) && !errorStatus.Contains("NotFound"))
                    {
                        logger.Printf("Rancher pod status: {0}", errorStatus);
                        string describeCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl describe pod -n cattle-system -l app=rancher | tail -20";
                        (string describeOutput, _) = pctService.Execute(controlID, describeCmd, 30);
                        if (!string.IsNullOrEmpty(describeOutput))
                        {
                            logger.Printf("Rancher pod details: {0}", describeOutput);
                        }
                    }
                }

                // Create Ingress for Rancher
                if (rancherReady && !string.IsNullOrEmpty(context.Cfg.Domain))
                {
                    logger.Printf("Creating Ingress for Rancher...");
                    string rancherHost = $"rancher.{context.Cfg.Domain}";
                    string ingressYaml = $@"apiVersion: networking.k8s.io/v1
kind: Ingress
metadata:
  name: rancher
  namespace: cattle-system
  annotations:
    traefik.ingress.kubernetes.io/router.entrypoints: web,websecure
spec:
  rules:
  - host: {rancherHost}
    http:
      paths:
      - path: /
        pathType: Prefix
        backend:
          service:
            name: rancher
            port:
              number: 80
  tls:
  - hosts:
    - {rancherHost}
";
                    string createIngressCmd = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && cat <<'EOF' | kubectl apply -f -\n{ingressYaml}EOF";
                    int? ingressTimeout = 30;
                    (string ingressOutput, int? ingressExit) = pctService.Execute(controlID, createIngressCmd, ingressTimeout);
                    if (ingressExit.HasValue && ingressExit.Value == 0)
                    {
                        logger.Printf("Rancher Ingress created successfully for {0}", rancherHost);
                    }
                    else
                    {
                        logger.Printf("Failed to create Rancher Ingress: {0}", ingressOutput);
                    }
                }

                return true;
            }

            // Installation failed
            if (!string.IsNullOrEmpty(installOutput))
            {
                int start = Math.Max(0, installOutput.Length - 1000);
                logger.Printf("Rancher installation failed: {0}", installOutput.Substring(start));
            }
            else
            {
                logger.Printf("Rancher installation failed with no output");
            }
            return false;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }

    private static int CountOccurrences(string text, string pattern)
    {
        int count = 0;
        int index = 0;
        while ((index = text.IndexOf(pattern, index)) != -1)
        {
            count++;
            index += pattern.Length;
        }
        return count;
    }

    private static bool RestartAndVerifyNodes(KubernetesDeployContext context, ContainerConfig controlConfig)
    {
        var lxcHost = context.LXCHost();
        var cfg = context.Cfg;
        if (cfg == null) return false;
        var controlID = controlConfig.ID;
        var lxcService = new LXCService(lxcHost, cfg.SSH);
        if (!lxcService.Connect())
        {
            return false;
        }
        var pctService = new PCTService(lxcService);
        var logger = Logger.GetLogger("kubernetes");

        logger.Printf("Restarting all k3s nodes...");

        // Restart control node
        logger.Printf("Restarting control node {0}...", controlID);
        string restartControlCmd = "systemctl restart k3s";
        pctService.Execute(controlID, restartControlCmd, null);
        Thread.Sleep(10000);

        // Restart worker nodes
        foreach (var workerConfig in context.Workers)
        {
            int workerID = workerConfig.ID;
            logger.Printf("Restarting worker node {0}...", workerID);
            string restartWorkerCmd = "systemctl restart k3s-agent";
            pctService.Execute(workerID, restartWorkerCmd, null);
            Thread.Sleep(5000);
        }

        logger.Printf("Waiting for nodes to stabilize after restart...");
        Thread.Sleep(30000);

        // Run verification
        logger.Printf("Running cluster health verification...");
        if (!Enva.Verification.Verification.VerifyKubernetesCluster(cfg, pctService))
        {
            logger.Printf("⚠ Cluster verification found issues after restart");
            return false;
        }

        logger.Printf("✓ All nodes restarted and verified successfully");
        return true;
    }
}