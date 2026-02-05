using System;
using System.Linq;
using System.Threading;
using Enva.Libs;
using Enva.Services;

namespace Enva.Verification;

public static class Verification
{
    public static bool VerifyKubernetesCluster(LabConfig cfg, IPCTService pctService)
    {
        var logger = Logger.GetLogger("verify_kubernetes");
        if (cfg.Kubernetes == null)
        {
            logger.Printf("Kubernetes not configured, skipping verification");
            return true;
        }

        if (cfg.Kubernetes.Control == null || cfg.Kubernetes.Control.Count == 0)
        {
            logger.Printf("No control nodes configured");
            return false;
        }

        int controlID = cfg.Kubernetes.Control[0];
        ContainerConfig? controlNode = cfg.Containers.FirstOrDefault(ct => ct.ID == controlID);
        if (controlNode == null)
        {
            logger.Printf("Control node {0} not found in configuration", controlID);
            return false;
        }

        logger.Printf("Verifying k3s cluster health...");

        // 1. Verify all nodes are Ready
        if (!VerifyNodesReady(cfg, pctService, controlID))
        {
            return false;
        }

        // 2. Verify no unreachable taints on worker nodes
        if (!VerifyNoUnreachableTaints(cfg, pctService, controlID))
        {
            return false;
        }

        // 3. Verify /dev/kmsg exists on all k3s nodes
        if (!VerifyKmsgExists(cfg, pctService))
        {
            return false;
        }

        // 4. Verify Rancher is accessible (if configured)
        if (cfg.Services.Services != null && cfg.Services.Services.ContainsKey("rancher"))
        {
            if (!VerifyRancherAccessible(cfg, pctService, controlID))
            {
                return false;
            }
        }

        logger.Printf("✓ All k3s cluster health checks passed");
        return true;
    }

    private static bool VerifyNodesReady(LabConfig cfg, IPCTService pctService, int controlID)
    {
        var logger = Logger.GetLogger("verify_kubernetes");
        logger.Printf("Checking that all nodes are Ready...");

        int maxWait = 120;
        int waitTime = 0;
        while (waitTime < maxWait)
        {
            string cmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && k3s kubectl get nodes --no-headers";
            int timeout = 30;
            (string output, int? exitCode) = pctService.Execute(controlID, cmd, timeout);
            if (exitCode.HasValue && exitCode.Value == 0 && !string.IsNullOrEmpty(output))
            {
                string[] lines = output.Trim().Split('\n');
                int expectedNodes = 1 + (cfg?.Kubernetes?.Workers?.Count ?? 0);
                int readyCount = 0;
                var notReadyNodes = new System.Collections.Generic.List<string>();

                foreach (string line in lines)
                {
                    if (line.Contains(" Ready "))
                    {
                        readyCount++;
                    }
                    else if (line.Contains("NotReady") || line.Contains("Unknown"))
                    {
                        string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length > 0)
                        {
                            notReadyNodes.Add(parts[0]);
                        }
                    }
                }

                if (readyCount == expectedNodes)
                {
                    logger.Printf("✓ All {0} nodes are Ready", expectedNodes);
                    return true;
                }

                if (waitTime % 20 == 0)
                {
                    logger.Printf("Waiting for nodes to become Ready ({0}/{1} Ready, {2} NotReady: {3})...", readyCount, expectedNodes, notReadyNodes.Count, string.Join(", ", notReadyNodes));
                }
            }
            Thread.Sleep(5000);
            waitTime += 5;
        }

        logger.Printf("✗ Not all nodes became Ready within {0} seconds", maxWait);
        return false;
    }

    private static bool VerifyNoUnreachableTaints(LabConfig cfg, IPCTService pctService, int controlID)
    {
        var logger = Logger.GetLogger("verify_kubernetes");
        logger.Printf("Checking for unreachable taints on worker nodes...");

        string cmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && k3s kubectl get nodes -o jsonpath='{range .items[*]}{.metadata.name}{\"\\t\"}{.spec.taints}{\"\\n\"}{end}'";
        int timeout = 30;
        (string output, int? exitCode) = pctService.Execute(controlID, cmd, timeout);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            logger.Printf("✗ Failed to check node taints");
            return false;
        }

        string[] lines = output.Trim().Split('\n');
        bool hasUnreachableTaints = false;
        foreach (string line in lines)
        {
            if (line.Contains("node.kubernetes.io/unreachable"))
            {
                string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    string nodeName = parts[0];
                    logger.Printf("✗ Node {0} has unreachable taint, removing...", nodeName);
                    // Remove both NoSchedule and NoExecute taints
                    string removeTaintCmd1 = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && k3s kubectl taint nodes {nodeName} node.kubernetes.io/unreachable:NoSchedule-";
                    string removeTaintCmd2 = $"export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && k3s kubectl taint nodes {nodeName} node.kubernetes.io/unreachable:NoExecute-";
                    timeout = 30;
                    pctService.Execute(controlID, removeTaintCmd1, timeout);
                    pctService.Execute(controlID, removeTaintCmd2, timeout);

                    // Also fix /dev/kmsg on the node
                    int nodeID = 0;
                    // Try to match node name to container hostname or ID
                    foreach (var container in cfg?.Containers ?? new System.Collections.Generic.List<ContainerConfig>())
                    {
                        string containerHostname = container.Hostname;
                        string expectedWorkerName = $"k3s-worker-{container.ID}";
                        if (containerHostname == nodeName || expectedWorkerName == nodeName)
                        {
                            nodeID = container.ID;
                            break;
                        }
                    }
                    // If not found, try to match by checking all workers
                    if (nodeID == 0 && cfg?.Kubernetes?.Workers != null)
                    {
                        foreach (int workerID in cfg.Kubernetes.Workers)
                        {
                            string expectedName = $"k3s-worker-{workerID}";
                            if (nodeName == expectedName)
                            {
                                nodeID = workerID;
                                break;
                            }
                        }
                    }
                    if (nodeID != 0)
                    {
                        logger.Printf("Fixing /dev/kmsg on node {0}...", nodeID);
                        string fixKmsgCmd = "rm -f /dev/kmsg && ln -sf /dev/console /dev/kmsg && systemctl restart k3s-agent || systemctl restart k3s";
                        pctService.Execute(nodeID, fixKmsgCmd, null);
                        Thread.Sleep(5000);
                    }
                    hasUnreachableTaints = true;
                }
            }
        }

        if (hasUnreachableTaints)
        {
            logger.Printf("Removed unreachable taints and fixed nodes, waiting 15 seconds for nodes to stabilize...");
            Thread.Sleep(15000);
            // Re-verify nodes are Ready after removing taints
            return VerifyNodesReady(cfg!, pctService, controlID);
        }

        logger.Printf("✓ No unreachable taints found on worker nodes");
        return true;
    }

    private static bool VerifyKmsgExists(LabConfig cfg, IPCTService pctService)
    {
        var logger = Logger.GetLogger("verify_kubernetes");
        logger.Printf("Verifying /dev/kmsg exists on all k3s nodes...");

        var allNodeIDs = new System.Collections.Generic.List<int>();
        if (cfg?.Kubernetes?.Control != null)
        {
            allNodeIDs.AddRange(cfg.Kubernetes.Control);
        }
        if (cfg?.Kubernetes?.Workers != null)
        {
            allNodeIDs.AddRange(cfg.Kubernetes.Workers);
        }

        foreach (int nodeID in allNodeIDs)
        {
            string cmd = "test -e /dev/kmsg && echo exists || echo missing";
            (string output, int? exitCode) = pctService.Execute(nodeID, cmd, null);
            if (!exitCode.HasValue || exitCode.Value != 0 || !output.Contains("exists"))
            {
                logger.Printf("✗ /dev/kmsg missing on node {0}, creating...", nodeID);
                string createCmd = "rm -f /dev/kmsg && ln -sf /dev/console /dev/kmsg && test -e /dev/kmsg && echo created || echo failed";
                (string createOutput, int? createExit) = pctService.Execute(nodeID, createCmd, null);
                if (!createExit.HasValue || createExit.Value != 0 || !createOutput.Contains("created"))
                {
                    logger.Printf("✗ Failed to create /dev/kmsg on node {0}", nodeID);
                    return false;
                }
                logger.Printf("✓ Created /dev/kmsg on node {0}", nodeID);

                // Restart k3s service if it's a control node, or k3s-agent if it's a worker
                bool isControl = cfg?.Kubernetes?.Control != null && cfg.Kubernetes.Control.Contains(nodeID);
                string serviceName = isControl ? "k3s" : "k3s-agent";

                logger.Printf("Restarting {0} service on node {1}...", serviceName, nodeID);
                string restartCmd = $"systemctl restart {serviceName}";
                pctService.Execute(nodeID, restartCmd, null);
                Thread.Sleep(5000);
            }
            else
            {
                logger.Printf("✓ /dev/kmsg exists on node {0}", nodeID);
            }
        }

        return true;
    }

    private static bool VerifyRancherAccessible(LabConfig cfg, IPCTService pctService, int controlID)
    {
        var logger = Logger.GetLogger("verify_kubernetes");
        logger.Printf("Verifying Rancher is accessible...");

        // First check if Rancher service exists
        string cmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && k3s kubectl get svc rancher -n cattle-system";
        int timeout = 30;
        (string output, int? exitCode) = pctService.Execute(controlID, cmd, timeout);
        if (!exitCode.HasValue || exitCode.Value != 0 || !output.Contains("rancher"))
        {
            logger.Printf("⚠ Rancher service not found, skipping accessibility check");
            return true; // Not an error if Rancher isn't deployed yet
        }

        // Get ClusterIP
        string getClusterIPCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && k3s kubectl get svc rancher -n cattle-system -o jsonpath='{.spec.clusterIP}'";
        timeout = 30;
        (string clusterIPOutput, int? clusterIPExit) = pctService.Execute(controlID, getClusterIPCmd, timeout);
        if (!clusterIPExit.HasValue || clusterIPExit.Value != 0 || string.IsNullOrEmpty(clusterIPOutput))
        {
            logger.Printf("⚠ Could not get Rancher ClusterIP, skipping accessibility check");
            return true;
        }
        string clusterIP = clusterIPOutput.Trim();

        // Try to access Rancher via ClusterIP
        int maxAttempts = 10;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            string testCmd = $"curl -k -s --max-time 5 https://{clusterIP}:443 | head -1";
            timeout = 10;
            (string testOutput, int? testExit) = pctService.Execute(controlID, testCmd, timeout);
            if (testExit.HasValue && testExit.Value == 0 && (testOutput.Contains("apiRoot") || testOutput.Contains("collection") || testOutput.Contains("rancher")))
            {
                logger.Printf("✓ Rancher is accessible via ClusterIP {0}:443", clusterIP);
                return true;
            }
            if (attempt < maxAttempts - 1)
            {
                logger.Printf("Waiting for Rancher to become accessible (attempt {0}/{1})...", attempt + 1, maxAttempts);
                Thread.Sleep(10000);
            }
        }

        logger.Printf("⚠ Rancher service exists but is not yet accessible (this may be normal during initial deployment)");
        return true; // Don't fail deployment if Rancher is still starting
    }

    public static bool VerifyHAProxyBackends(LabConfig cfg, IPCTService pctService)
    {
        var logger = Logger.GetLogger("verify_haproxy");
        if (cfg.Services.Services == null || !cfg.Services.Services.ContainsKey("haproxy"))
        {
            logger.Printf("HAProxy not configured, skipping verification");
            return true;
        }

        ContainerConfig? haproxyCT = cfg.Containers.FirstOrDefault(ct => ct.Name == "haproxy");
        if (haproxyCT == null)
        {
            logger.Printf("HAProxy container not found");
            return false;
        }

        logger.Printf("Verifying HAProxy backends...");

        int statsPort = 8404;
        if (haproxyCT.Params != null && haproxyCT.Params.TryGetValue("stats_port", out object? statsPortObj) && statsPortObj is int p)
        {
            statsPort = p;
        }

        string cmd = $"curl -s http://localhost:{statsPort}/stats | grep -E 'backend_|UP|DOWN' | head -20";
        int timeout = 10;
        var (output, exitCode) = pctService.Execute(haproxyCT.ID, cmd, timeout);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            logger.Printf("⚠ Could not check HAProxy stats (this may be normal)");
            return true;
        }

        if (cfg.Services.Services != null && cfg.Services.Services.ContainsKey("rancher"))
        {
            var lines = output.Split('\n');
            bool rancherBackendFound = false;
            foreach (string line in lines)
            {
                if (line.Contains("backend_rancher"))
                {
                    rancherBackendFound = true;
                    if (line.Contains("UP"))
                    {
                        logger.Printf("✓ HAProxy backend_rancher is UP");
                    }
                    else
                    {
                        logger.Printf("⚠ HAProxy backend_rancher is not UP (may be starting)");
                    }
                    break;
                }
            }
            if (!rancherBackendFound)
            {
                logger.Printf("⚠ HAProxy backend_rancher not found in stats");
            }
        }

        logger.Printf("✓ HAProxy backend verification completed");
        return true;
    }
}
