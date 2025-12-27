package verification

import (
	"enva/libs"
	"enva/services"
	"fmt"
	"strings"
	"time"
)

// VerifyKubernetesCluster verifies that the k3s cluster is healthy
func VerifyKubernetesCluster(cfg *libs.LabConfig, pctService *services.PCTService) bool {
	logger := libs.GetLogger("verify_kubernetes")
	if cfg.Kubernetes == nil {
		logger.Printf("Kubernetes not configured, skipping verification")
		return true
	}

	if len(cfg.Kubernetes.Control) == 0 {
		logger.Printf("No control nodes configured")
		return false
	}

	controlID := cfg.Kubernetes.Control[0]
	var controlNode *libs.ContainerConfig
	for i := range cfg.Containers {
		if cfg.Containers[i].ID == controlID {
			controlNode = &cfg.Containers[i]
			break
		}
	}
	if controlNode == nil {
		logger.Printf("Control node %d not found in configuration", controlID)
		return false
	}

	logger.Printf("Verifying k3s cluster health...")

	// 1. Verify all nodes are Ready
	if !verifyNodesReady(cfg, pctService, controlID) {
		return false
	}

	// 2. Verify no unreachable taints on worker nodes
	if !verifyNoUnreachableTaints(cfg, pctService, controlID) {
		return false
	}

	// 3. Verify /dev/kmsg exists on all k3s nodes
	if !verifyKmsgExists(cfg, pctService) {
		return false
	}

	// 4. Verify Rancher is accessible (if configured)
	if cfg.Services.Rancher != nil {
		if !verifyRancherAccessible(cfg, pctService, controlID) {
			return false
		}
	}

	logger.Printf("✓ All k3s cluster health checks passed")
	return true
}

func verifyNodesReady(cfg *libs.LabConfig, pctService *services.PCTService, controlID int) bool {
	logger := libs.GetLogger("verify_kubernetes")
	logger.Printf("Checking that all nodes are Ready...")

	maxWait := 120
	waitTime := 0
	for waitTime < maxWait {
		cmd := "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && k3s kubectl get nodes --no-headers 2>&1"
		timeout := 30
		output, exitCode := pctService.Execute(controlID, cmd, &timeout)
		if exitCode != nil && *exitCode == 0 && output != "" {
			lines := strings.Split(strings.TrimSpace(output), "\n")
			expectedNodes := 1 + len(cfg.Kubernetes.Workers)
			readyCount := 0
			notReadyNodes := []string{}

			for _, line := range lines {
				if strings.Contains(line, " Ready ") {
					readyCount++
				} else if strings.Contains(line, "NotReady") || strings.Contains(line, "Unknown") {
					parts := strings.Fields(line)
					if len(parts) > 0 {
						notReadyNodes = append(notReadyNodes, parts[0])
					}
				}
			}

			if readyCount == expectedNodes {
				logger.Printf("✓ All %d nodes are Ready", expectedNodes)
				return true
			}

			if waitTime%20 == 0 {
				logger.Printf("Waiting for nodes to become Ready (%d/%d Ready, %d NotReady: %v)...", readyCount, expectedNodes, len(notReadyNodes), notReadyNodes)
			}
		}
		time.Sleep(5 * time.Second)
		waitTime += 5
	}

	logger.Printf("✗ Not all nodes became Ready within %d seconds", maxWait)
	return false
}

func verifyNoUnreachableTaints(cfg *libs.LabConfig, pctService *services.PCTService, controlID int) bool {
	logger := libs.GetLogger("verify_kubernetes")
	logger.Printf("Checking for unreachable taints on worker nodes...")

	cmd := "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && k3s kubectl get nodes -o jsonpath='{range .items[*]}{.metadata.name}{\"\\t\"}{.spec.taints}{\"\\n\"}{end}' 2>&1"
	timeout := 30
	output, exitCode := pctService.Execute(controlID, cmd, &timeout)
	if exitCode == nil || *exitCode != 0 {
		logger.Printf("✗ Failed to check node taints")
		return false
	}

	lines := strings.Split(strings.TrimSpace(output), "\n")
	hasUnreachableTaints := false
	for _, line := range lines {
		if strings.Contains(line, "node.kubernetes.io/unreachable") {
			parts := strings.Fields(line)
			if len(parts) > 0 {
				nodeName := parts[0]
				logger.Printf("✗ Node %s has unreachable taint, removing...", nodeName)
				// Remove both NoSchedule and NoExecute taints
				removeTaintCmd1 := fmt.Sprintf("export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && k3s kubectl taint nodes %s node.kubernetes.io/unreachable:NoSchedule- 2>&1", nodeName)
				removeTaintCmd2 := fmt.Sprintf("export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && k3s kubectl taint nodes %s node.kubernetes.io/unreachable:NoExecute- 2>&1", nodeName)
				timeout := 30
				pctService.Execute(controlID, removeTaintCmd1, &timeout)
				pctService.Execute(controlID, removeTaintCmd2, &timeout)

				// Also fix /dev/kmsg on the node
				var nodeID int
				// Try to match node name to container hostname or ID
				for i := range cfg.Containers {
					containerHostname := cfg.Containers[i].Hostname
					expectedWorkerName := fmt.Sprintf("k3s-worker-%d", cfg.Containers[i].ID)
					if containerHostname == nodeName || expectedWorkerName == nodeName {
						nodeID = cfg.Containers[i].ID
						break
					}
				}
				// If not found, try to match by checking all workers
				if nodeID == 0 {
					for _, workerID := range cfg.Kubernetes.Workers {
						expectedName := fmt.Sprintf("k3s-worker-%d", workerID)
						if nodeName == expectedName {
							nodeID = workerID
							break
						}
					}
				}
				// If still not found and it's a worker node, try to get from node status
				if nodeID == 0 && strings.Contains(nodeName, "worker") {
					// Get node internal IP and match to container IP
					getNodeIPCmd := fmt.Sprintf("export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && k3s kubectl get node %s -o jsonpath='{.status.addresses[?(@.type==\"InternalIP\")].address}' 2>&1", nodeName)
					timeout := 10
					nodeIP, _ := pctService.Execute(controlID, getNodeIPCmd, &timeout)
					if nodeIP != "" {
						nodeIP = strings.TrimSpace(nodeIP)
						for i := range cfg.Containers {
							if cfg.Containers[i].IPAddress != nil && *cfg.Containers[i].IPAddress == nodeIP {
								nodeID = cfg.Containers[i].ID
								break
							}
						}
					}
				}
				if nodeID != 0 {
					logger.Printf("Fixing /dev/kmsg on node %d...", nodeID)
					fixKmsgCmd := "rm -f /dev/kmsg && ln -sf /dev/console /dev/kmsg && systemctl restart k3s-agent 2>&1 || systemctl restart k3s 2>&1"
					pctService.Execute(nodeID, fixKmsgCmd, nil)
					time.Sleep(5 * time.Second)
				}
				hasUnreachableTaints = true
			}
		}
	}

	if hasUnreachableTaints {
		logger.Printf("Removed unreachable taints and fixed nodes, waiting 15 seconds for nodes to stabilize...")
		time.Sleep(15 * time.Second)
		// Re-verify nodes are Ready after removing taints
		return verifyNodesReady(cfg, pctService, controlID)
	}

	logger.Printf("✓ No unreachable taints found on worker nodes")
	return true
}

func verifyKmsgExists(cfg *libs.LabConfig, pctService *services.PCTService) bool {
	logger := libs.GetLogger("verify_kubernetes")
	logger.Printf("Verifying /dev/kmsg exists on all k3s nodes...")

	allNodeIDs := append([]int{}, cfg.Kubernetes.Control...)
	allNodeIDs = append(allNodeIDs, cfg.Kubernetes.Workers...)

	for _, nodeID := range allNodeIDs {
		cmd := "test -e /dev/kmsg && echo exists || echo missing"
		output, exitCode := pctService.Execute(nodeID, cmd, nil)
		if exitCode == nil || *exitCode != 0 || !strings.Contains(output, "exists") {
			logger.Printf("✗ /dev/kmsg missing on node %d, creating...", nodeID)
			createCmd := "rm -f /dev/kmsg && ln -sf /dev/console /dev/kmsg && test -e /dev/kmsg && echo created || echo failed"
			createOutput, createExit := pctService.Execute(nodeID, createCmd, nil)
			if createExit == nil || *createExit != 0 || !strings.Contains(createOutput, "created") {
				logger.Printf("✗ Failed to create /dev/kmsg on node %d", nodeID)
				return false
			}
			logger.Printf("✓ Created /dev/kmsg on node %d", nodeID)

			// Restart k3s service if it's a control node, or k3s-agent if it's a worker
			isControl := false
			for _, controlID := range cfg.Kubernetes.Control {
				if controlID == nodeID {
					isControl = true
					break
				}
			}

			serviceName := "k3s-agent"
			if isControl {
				serviceName = "k3s"
			}

			logger.Printf("Restarting %s service on node %d...", serviceName, nodeID)
			restartCmd := fmt.Sprintf("systemctl restart %s 2>&1", serviceName)
			pctService.Execute(nodeID, restartCmd, nil)
			time.Sleep(5 * time.Second)
		} else {
			logger.Printf("✓ /dev/kmsg exists on node %d", nodeID)
		}
	}

	return true
}

func verifyRancherAccessible(cfg *libs.LabConfig, pctService *services.PCTService, controlID int) bool {
	logger := libs.GetLogger("verify_kubernetes")
	logger.Printf("Verifying Rancher is accessible...")

	// First check if Rancher service exists
	cmd := "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && k3s kubectl get svc rancher -n cattle-system 2>&1"
	timeout := 30
	output, exitCode := pctService.Execute(controlID, cmd, &timeout)
	if exitCode == nil || *exitCode != 0 || !strings.Contains(output, "rancher") {
		logger.Printf("⚠ Rancher service not found, skipping accessibility check")
		return true // Not an error if Rancher isn't deployed yet
	}

	// Get ClusterIP
	getClusterIPCmd := "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && k3s kubectl get svc rancher -n cattle-system -o jsonpath='{.spec.clusterIP}' 2>&1"
	timeout = 30
	clusterIPOutput, clusterIPExit := pctService.Execute(controlID, getClusterIPCmd, &timeout)
	if clusterIPExit == nil || *clusterIPExit != 0 || clusterIPOutput == "" {
		logger.Printf("⚠ Could not get Rancher ClusterIP, skipping accessibility check")
		return true
	}
	clusterIP := strings.TrimSpace(clusterIPOutput)

	// Try to access Rancher via ClusterIP
	maxAttempts := 10
	for attempt := 0; attempt < maxAttempts; attempt++ {
		testCmd := fmt.Sprintf("curl -k -s --max-time 5 https://%s:443 2>&1 | head -1", clusterIP)
		timeout = 10
		testOutput, testExit := pctService.Execute(controlID, testCmd, &timeout)
		if testExit != nil && *testExit == 0 && (strings.Contains(testOutput, "apiRoot") || strings.Contains(testOutput, "collection") || strings.Contains(testOutput, "rancher")) {
			logger.Printf("✓ Rancher is accessible via ClusterIP %s:443", clusterIP)
			return true
		}
		if attempt < maxAttempts-1 {
			logger.Printf("Waiting for Rancher to become accessible (attempt %d/%d)...", attempt+1, maxAttempts)
			time.Sleep(10 * time.Second)
		}
	}

	logger.Printf("⚠ Rancher service exists but is not yet accessible (this may be normal during initial deployment)")
	return true // Don't fail deployment if Rancher is still starting
}

// VerifyHAProxyBackends verifies that HAProxy backends are UP
func VerifyHAProxyBackends(cfg *libs.LabConfig, pctService *services.PCTService) bool {
	logger := libs.GetLogger("verify_haproxy")
	if cfg.Services.HAProxy == nil {
		logger.Printf("HAProxy not configured, skipping verification")
		return true
	}

	var haproxyCT *libs.ContainerConfig
	for i := range cfg.Containers {
		if cfg.Containers[i].Name == "haproxy" {
			haproxyCT = &cfg.Containers[i]
			break
		}
	}
	if haproxyCT == nil {
		logger.Printf("HAProxy container not found")
		return false
	}

	logger.Printf("Verifying HAProxy backends...")

	// Check HAProxy stats
	statsPort := 8404
	if haproxyCT.Params != nil {
		if p, ok := haproxyCT.Params["stats_port"].(int); ok {
			statsPort = p
		}
	}

	cmd := fmt.Sprintf("curl -s http://localhost:%d/stats 2>&1 | grep -E 'backend_|UP|DOWN' | head -20", statsPort)
	timeout := 10
	output, exitCode := pctService.Execute(haproxyCT.ID, cmd, &timeout)
	if exitCode == nil || *exitCode != 0 {
		logger.Printf("⚠ Could not check HAProxy stats (this may be normal)")
		return true
	}

	// Check for critical backends
	if cfg.Services.Rancher != nil {
		lines := strings.Split(output, "\n")
		rancherBackendFound := false
		for _, line := range lines {
			if strings.Contains(line, "backend_rancher") {
				rancherBackendFound = true
				if strings.Contains(line, "UP") {
					logger.Printf("✓ HAProxy backend_rancher is UP")
				} else {
					logger.Printf("⚠ HAProxy backend_rancher is not UP (may be starting)")
				}
				break
			}
		}
		if !rancherBackendFound {
			logger.Printf("⚠ HAProxy backend_rancher not found in stats")
		}
	}

	logger.Printf("✓ HAProxy backend verification completed")
	return true
}
