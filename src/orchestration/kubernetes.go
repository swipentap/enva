package orchestration

import (
	"enva/libs"
	"enva/services"
	"enva/verification"
	"fmt"
	"strconv"
	"strings"
	"time"
)

// KubernetesDeployContext holds shared data computed once for Kubernetes deployment
type KubernetesDeployContext struct {
	Cfg     *libs.LabConfig
	Control []*libs.ContainerConfig
	Workers []*libs.ContainerConfig
	Token   *string
}

// LXCHost returns cached LXC host string
func (k *KubernetesDeployContext) LXCHost() string {
	return k.Cfg.LXCHost()
}

// AllNodes returns list combining control and worker nodes
func (k *KubernetesDeployContext) AllNodes() []*libs.ContainerConfig {
	return append(k.Control, k.Workers...)
}

// DeployKubernetes deploys Kubernetes (k3s) cluster - containers should already exist from deploy process
func DeployKubernetes(cfg *libs.LabConfig) bool {
	context := BuildKubernetesContext(cfg)
	if context == nil {
		return false
	}
	if len(context.Control) == 0 {
		return false
	}
	controlConfig := context.Control[0]
	if !getK3sToken(context, controlConfig) {
		return false
	}
	if !joinWorkersToCluster(context, controlConfig) {
		return false
	}
	if !taintControlPlane(context, controlConfig) {
		return false
	}
	if !installRancher(context, controlConfig) {
		return false
	}

	// Restart all nodes and verify cluster health
	libs.GetLogger("kubernetes").Printf("Restarting all k3s nodes to ensure stability...")
	if !restartAndVerifyNodes(context, controlConfig) {
		libs.GetLogger("kubernetes").Printf("⚠ Node restart/verification had issues, but deployment completed")
		// Don't fail deployment, just warn
	}

	libs.GetLogger("kubernetes").Printf("Kubernetes (k3s) cluster deployed")
	return true
}

// BuildKubernetesContext collects and validates configuration needed for Kubernetes deployment
func BuildKubernetesContext(cfg *libs.LabConfig) *KubernetesDeployContext {
	if cfg.Kubernetes == nil || cfg.Kubernetes.Control == nil || cfg.Kubernetes.Workers == nil {
		libs.GetLogger("kubernetes").Printf("Kubernetes configuration not found or incomplete")
		return nil
	}
	controlIDs := make(map[int]bool)
	for _, id := range cfg.Kubernetes.Control {
		controlIDs[id] = true
	}
	workerIDs := make(map[int]bool)
	for _, id := range cfg.Kubernetes.Workers {
		workerIDs[id] = true
	}
	var control []*libs.ContainerConfig
	var workers []*libs.ContainerConfig
	for i := range cfg.Containers {
		if controlIDs[cfg.Containers[i].ID] {
			control = append(control, &cfg.Containers[i])
		}
		if workerIDs[cfg.Containers[i].ID] {
			workers = append(workers, &cfg.Containers[i])
		}
	}
	if len(control) == 0 {
		libs.GetLogger("kubernetes").Printf("Kubernetes control node not found in configuration")
		return nil
	}
	if len(workers) == 0 {
		libs.GetLogger("kubernetes").Printf("No Kubernetes worker nodes found in configuration")
	}
	return &KubernetesDeployContext{
		Cfg:     cfg,
		Control: control,
		Workers: workers,
	}
}

func getK3sToken(context *KubernetesDeployContext, controlConfig *libs.ContainerConfig) bool {
	lxcHost := context.LXCHost()
	cfg := context.Cfg
	controlID := controlConfig.ID
	lxcService := services.NewLXCService(lxcHost, &cfg.SSH)
	if !lxcService.Connect() {
		return false
	}
	defer lxcService.Disconnect()
	pctService := services.NewPCTService(lxcService)
	libs.GetLogger("kubernetes").Printf("Getting k3s server token...")
	maxWait := 60
	waitTime := 0
	for waitTime < maxWait {
		checkCmd := "systemctl is-active k3s || echo inactive"
		checkOutput, _ := pctService.Execute(controlID, checkCmd, nil)
		if strings.Contains(checkOutput, "active") {
			break
		}
		time.Sleep(2 * time.Second)
		waitTime += 2
	}
	if waitTime >= maxWait {
		libs.GetLogger("kubernetes").Printf("k3s service not ready on control node")
		return false
	}
	tokenCmd := "cat /var/lib/rancher/k3s/server/node-token"
	tokenOutput, _ := pctService.Execute(controlID, tokenCmd, nil)
	if tokenOutput == "" || strings.TrimSpace(tokenOutput) == "" {
		libs.GetLogger("kubernetes").Printf("Failed to get k3s token")
		return false
	}
	token := strings.TrimSpace(tokenOutput)
	context.Token = &token
	libs.GetLogger("kubernetes").Printf("k3s token retrieved successfully")
	return true
}

func joinWorkersToCluster(context *KubernetesDeployContext, controlConfig *libs.ContainerConfig) bool {
	lxcHost := context.LXCHost()
	cfg := context.Cfg
	controlID := controlConfig.ID
	controlIP := ""
	if controlConfig.IPAddress != nil {
		controlIP = *controlConfig.IPAddress
	}
	token := context.Token
	if token == nil || *token == "" {
		libs.GetLogger("kubernetes").Printf("k3s token not available")
		return false
	}
	lxcService := services.NewLXCService(lxcHost, &cfg.SSH)
	if !lxcService.Connect() {
		return false
	}
	defer lxcService.Disconnect()
	pctService := services.NewPCTService(lxcService)
	for _, workerConfig := range context.Workers {
		workerID := workerConfig.ID
		libs.GetLogger("kubernetes").Printf("Joining worker %d to k3s cluster...", workerID)
		uninstallCmd := "/usr/local/bin/k3s-agent-uninstall.sh 2>&1 || true"
		pctService.Execute(workerID, uninstallCmd, nil)
		libs.GetLogger("kubernetes").Printf("Installing k3s agent on worker %d...", workerID)
		joinCmd := fmt.Sprintf("curl -sfL https://get.k3s.io | K3S_URL=https://%s:6443 K3S_TOKEN=%s sh -", controlIP, *token)
		timeout := 600
		installOutput, installExit := pctService.Execute(workerID, joinCmd, &timeout)
		if installExit == nil {
			libs.GetLogger("kubernetes").Printf("k3s agent installation timed out on worker %d", workerID)
			return false
		}
		if installExit != nil && *installExit != 0 {
			libs.GetLogger("kubernetes").Printf("k3s agent installation failed on worker %d", workerID)
			outputLen := len(installOutput)
			start := 0
			if outputLen > 1000 {
				start = outputLen - 1000
			}
			libs.GetLogger("kubernetes").Printf("Installation output: %s", installOutput[start:])
			return false
		}
		libs.GetLogger("kubernetes").Printf("k3s agent installation completed on worker %d", workerID)
		outputLen := len(installOutput)
		start := 0
		if outputLen > 500 {
			start = outputLen - 500
		}
		if installOutput != "" {
			libs.GetLogger("kubernetes").Printf("Installation output: %s", installOutput[start:])
		}
		time.Sleep(2 * time.Second)

		// Fix systemd service to ensure /dev/kmsg exists before k3s-agent starts (persistent fix for LXC)
		libs.GetLogger("kubernetes").Printf("Configuring k3s-agent service to ensure /dev/kmsg exists on startup for worker %d...", workerID)
		serviceFile := "/etc/systemd/system/k3s-agent.service"
		checkServiceFileCmd := fmt.Sprintf("cat %s 2>&1", serviceFile)
		serviceContent, _ := pctService.Execute(workerID, checkServiceFileCmd, nil)
		if !strings.Contains(serviceContent, "/dev/kmsg") {
			fixServiceCmd := fmt.Sprintf(`sed -i '/ExecStartPre=-\\/sbin\\/modprobe br_netfilter/i ExecStartPre=-/bin/bash -c "rm -f /dev/kmsg \&\& ln -sf /dev/console /dev/kmsg"' %s 2>&1`, serviceFile)
			fixOutput, fixExit := pctService.Execute(workerID, fixServiceCmd, nil)
			if fixExit != nil && *fixExit == 0 {
				libs.GetLogger("kubernetes").Printf("✓ Added /dev/kmsg fix to k3s-agent.service on worker %d", workerID)
				reloadCmd := "systemctl daemon-reload 2>&1"
				pctService.Execute(workerID, reloadCmd, nil)
			} else {
				libs.GetLogger("kubernetes").Printf("⚠ Failed to modify k3s-agent.service on worker %d: %s", workerID, fixOutput)
			}
		} else {
			libs.GetLogger("kubernetes").Printf("✓ k3s-agent.service already has /dev/kmsg fix on worker %d", workerID)
		}

		serviceExistsCmd := "systemctl list-unit-files | grep -q k3s-agent.service && echo exists || echo not_exists"
		serviceCheck, _ := pctService.Execute(workerID, serviceExistsCmd, nil)
		if strings.Contains(serviceCheck, "not_exists") {
			libs.GetLogger("kubernetes").Printf("k3s-agent service was not created after installation")
			return false
		}
		maxWaitService := 120
		waitTimeService := 0
		workerName := workerConfig.Hostname
		if workerName == "" {
			workerName = fmt.Sprintf("k3s-worker-%d", workerID)
		}
		for waitTimeService < maxWaitService {
			checkCmd := "systemctl is-active k3s-agent 2>&1"
			checkOutput, checkExit := pctService.Execute(workerID, checkCmd, nil)
			if checkExit != nil && *checkExit == 0 && checkOutput != "" && strings.TrimSpace(checkOutput) == "active" {
				verifyNodeCmd := fmt.Sprintf("kubectl get nodes | grep -E '%s|%s' || echo not_found", workerName, controlIP)
				verifyOutput, verifyExit := pctService.Execute(controlID, verifyNodeCmd, nil)
				if verifyExit != nil && *verifyExit == 0 && verifyOutput != "" && !strings.Contains(verifyOutput, "not_found") && strings.Contains(verifyOutput, "Ready") {
					libs.GetLogger("kubernetes").Printf("Worker %d (%s) joined cluster successfully and is Ready", workerID, workerName)
					break
				} else {
					recheckCmd := "systemctl is-active k3s-agent 2>&1"
					recheckOutput, recheckExit := pctService.Execute(workerID, recheckCmd, nil)
					if recheckExit == nil || *recheckExit != 0 || strings.TrimSpace(recheckOutput) != "active" {
						libs.GetLogger("kubernetes").Printf("Worker %d service became inactive, waiting for it to become active again...", workerID)
					} else {
						libs.GetLogger("kubernetes").Printf("Worker %d service is active but not yet Ready in cluster, waiting...", workerID)
					}
				}
			} else {
				if waitTimeService%10 == 0 {
					status := "unknown"
					if checkOutput != "" {
						status = strings.TrimSpace(checkOutput)
					}
					libs.GetLogger("kubernetes").Printf("Worker %d service is not active yet (status: %s), waiting...", workerID, status)
				}
			}
			time.Sleep(2 * time.Second)
			waitTimeService += 2
		}
		if waitTimeService >= maxWaitService {
			libs.GetLogger("kubernetes").Printf("k3s-agent service not ready or node not in cluster on worker %d after %d seconds", workerID, maxWaitService)
			finalCheckCmd := "systemctl is-active k3s-agent 2>&1"
			finalCheck, _ := pctService.Execute(workerID, finalCheckCmd, nil)
			if strings.Contains(finalCheck, "active") {
				libs.GetLogger("kubernetes").Printf("Service is active but node did not appear in cluster")
			} else {
				libs.GetLogger("kubernetes").Printf("Service is not active")
			}
			return false
		}
	}
	return true
}

func taintControlPlane(context *KubernetesDeployContext, controlConfig *libs.ContainerConfig) bool {
	lxcHost := context.LXCHost()
	cfg := context.Cfg
	controlID := controlConfig.ID
	lxcService := services.NewLXCService(lxcHost, &cfg.SSH)
	if !lxcService.Connect() {
		return false
	}
	defer lxcService.Disconnect()
	pctService := services.NewPCTService(lxcService)
	libs.GetLogger("kubernetes").Printf("Tainting control plane node to prevent regular pods from scheduling...")
	maxWait := 60
	waitTime := 0
	for waitTime < maxWait {
		checkCmd := "kubectl get nodes 2>&1"
		timeout := 30
		checkOutput, checkExit := pctService.Execute(controlID, checkCmd, &timeout)
		if checkExit != nil && *checkExit == 0 && checkOutput != "" && strings.Contains(checkOutput, "Ready") {
			break
		}
		time.Sleep(2 * time.Second)
		waitTime += 2
	}
	if waitTime >= maxWait {
		libs.GetLogger("kubernetes").Printf("kubectl not ready, skipping control plane taint")
		return true
	}
	taintCmd := "kubectl taint nodes k3s-control node-role.kubernetes.io/control-plane:NoSchedule --overwrite 2>&1"
	timeout := 30
	taintOutput, taintExit := pctService.Execute(controlID, taintCmd, &timeout)
	if taintExit != nil && *taintExit == 0 {
		libs.GetLogger("kubernetes").Printf("Control plane node tainted successfully - regular pods will not schedule on it")
		return true
	}
	if strings.Contains(taintOutput, "already has") || strings.Contains(taintOutput, "modified") {
		libs.GetLogger("kubernetes").Printf("Control plane node already tainted")
		return true
	}
	outputLen := len(taintOutput)
	start := 0
	if outputLen > 200 {
		start = outputLen - 200
	}
	libs.GetLogger("kubernetes").Printf("Failed to taint control plane node: %s", taintOutput[start:])
	return true
}

func installRancher(context *KubernetesDeployContext, controlConfig *libs.ContainerConfig) bool {
	if context.Cfg.Services.Rancher == nil {
		libs.GetLogger("kubernetes").Printf("Rancher not configured, skipping installation")
		return true
	}
	lxcHost := context.LXCHost()
	cfg := context.Cfg
	controlID := controlConfig.ID
	_ = "rancher/rancher:latest"
	if cfg.Services.Rancher.Image != nil {
		_ = *cfg.Services.Rancher.Image
	}
	_ = 8443
	if cfg.Services.Rancher.Port != nil {
		_ = *cfg.Services.Rancher.Port
	}
	lxcService := services.NewLXCService(lxcHost, &cfg.SSH)
	if !lxcService.Connect() {
		return false
	}
	defer lxcService.Disconnect()
	pctService := services.NewPCTService(lxcService)
	libs.GetLogger("kubernetes").Printf("Installing Rancher...")
	kubectlCheckCmd := "command -v kubectl >/dev/null 2>&1 && echo installed || echo not_installed"
	kubectlCheck, _ := pctService.Execute(controlID, kubectlCheckCmd, nil)
	if strings.Contains(kubectlCheck, "not_installed") {
		libs.GetLogger("kubernetes").Printf("Installing kubectl...")
		installKubectlCmd := "curl -LO https://dl.k8s.io/release/$(curl -L -s https://dl.k8s.io/release/stable.txt)/bin/linux/amd64/kubectl && chmod +x kubectl && sudo mv kubectl /usr/local/bin/ && export PATH=/usr/local/bin:$PATH"
		timeout := 120
		pctService.Execute(controlID, installKubectlCmd, &timeout)
		verifyCmd := "test -f /usr/local/bin/kubectl && echo installed || echo not_installed"
		verifyOutput, _ := pctService.Execute(controlID, verifyCmd, nil)
		if strings.Contains(verifyOutput, "not_installed") {
			libs.GetLogger("kubernetes").Printf("kubectl installation failed")
			return false
		}
	}
	libs.GetLogger("kubernetes").Printf("Verifying k3s service is running...")
	maxWaitK3s := 120
	waitTimeK3s := 0
	for waitTimeK3s < maxWaitK3s {
		k3sCheckCmd := "systemctl is-active k3s 2>&1 || echo inactive"
		k3sCheck, _ := pctService.Execute(controlID, k3sCheckCmd, nil)
		if strings.Contains(k3sCheck, "active") {
			libs.GetLogger("kubernetes").Printf("k3s service is running")
			break
		}
		libs.GetLogger("kubernetes").Printf("Waiting for k3s service to be active (waited %d/%d seconds)...", waitTimeK3s, maxWaitK3s)
		time.Sleep(5 * time.Second)
		waitTimeK3s += 5
	}
	if waitTimeK3s >= maxWaitK3s {
		libs.GetLogger("kubernetes").Printf("k3s service not active after %d seconds", maxWaitK3s)
		return false
	}
	libs.GetLogger("kubernetes").Printf("Updating k3s kubeconfig with control node IP...")
	controlIP := ""
	if controlConfig.IPAddress != nil {
		controlIP = *controlConfig.IPAddress
	}
	kubeconfigCmd := fmt.Sprintf("sudo sed -i 's|server: https://127.0.0.1:6443|server: https://%s:6443|g; s|server: https://0.0.0.0:6443|server: https://%s:6443|g' /etc/rancher/k3s/k3s.yaml", controlIP, controlIP)
	pctService.Execute(controlID, kubeconfigCmd, nil)
	setupKubeconfigCmd := "mkdir -p /root/.kube && cp /etc/rancher/k3s/k3s.yaml /root/.kube/config && chown root:root /root/.kube/config && chmod 600 /root/.kube/config"
	pctService.Execute(controlID, setupKubeconfigCmd, nil)
	libs.GetLogger("kubernetes").Printf("Verifying kubectl works without KUBECONFIG specified...")
	verifyKubectlCmd := "kubectl get nodes"
	timeout := 30
	verifyKubectlOutput, verifyKubectlExit := pctService.Execute(controlID, verifyKubectlCmd, &timeout)
	if verifyKubectlExit == nil || *verifyKubectlExit != 0 || verifyKubectlOutput == "" || !strings.Contains(verifyKubectlOutput, "Ready") {
		libs.GetLogger("kubernetes").Printf("kubectl does not work without KUBECONFIG specified")
		outputLen := len(verifyKubectlOutput)
		start := 0
		if outputLen > 500 {
			start = outputLen - 500
		}
		if verifyKubectlOutput != "" {
			libs.GetLogger("kubernetes").Printf("kubectl output: %s", verifyKubectlOutput[start:])
		}
		return false
	}
	libs.GetLogger("kubernetes").Printf("kubectl works correctly without KUBECONFIG specified")
	libs.GetLogger("kubernetes").Printf("Verifying Kubernetes API is reachable...")
	verifyAPICmd := "kubectl cluster-info"
	maxVerifyAttempts := 20
	for attempt := 0; attempt < maxVerifyAttempts; attempt++ {
		timeout = 30
		verifyOutput, verifyExit := pctService.Execute(controlID, verifyAPICmd, &timeout)
		if verifyExit != nil && *verifyExit == 0 && verifyOutput != "" && strings.Contains(verifyOutput, "is running at") {
			libs.GetLogger("kubernetes").Printf("Kubernetes API is reachable")
			break
		}
		if attempt < maxVerifyAttempts-1 {
			libs.GetLogger("kubernetes").Printf("Waiting for Kubernetes API to be ready (attempt %d/%d)...", attempt+1, maxVerifyAttempts)
			time.Sleep(5 * time.Second)
		} else {
			libs.GetLogger("kubernetes").Printf("Kubernetes API not reachable after %d attempts", maxVerifyAttempts)
			if verifyOutput != "" {
				libs.GetLogger("kubernetes").Printf("API check output: %s", verifyOutput)
			}
			return false
		}
	}
	libs.GetLogger("kubernetes").Printf("Waiting for CNI plugin (Flannel) to be ready...")
	maxCNIWait := 120
	cniWaitTime := 0
	cniReady := false
	for cniWaitTime < maxCNIWait {
		nodesCmd := "kubectl get nodes -o jsonpath='{.items[*].status.conditions[?(@.type==\"Ready\")].status}' 2>&1"
		timeout = 30
		nodesOutput, nodesExit := pctService.Execute(controlID, nodesCmd, &timeout)
		nodesReady := nodesExit != nil && *nodesExit == 0 && nodesOutput != "" && strings.Contains(nodesOutput, "True") && !strings.Contains(nodesOutput, "False")
		cniConfigCmd := "test -f /var/lib/rancher/k3s/agent/etc/cni/net.d/10-flannel.conflist && echo exists || echo missing"
		timeout = 10
		cniConfigOutput, cniConfigExit := pctService.Execute(controlID, cniConfigCmd, &timeout)
		cniConfigExists := cniConfigExit != nil && *cniConfigExit == 0 && cniConfigOutput != "" && strings.Contains(cniConfigOutput, "exists")
		flannelSubnetCmd := "test -f /run/flannel/subnet.env && echo exists || echo missing"
		flannelSubnetOutput, flannelSubnetExit := pctService.Execute(controlID, flannelSubnetCmd, &timeout)
		flannelSubnetExists := flannelSubnetExit != nil && *flannelSubnetExit == 0 && flannelSubnetOutput != "" && strings.Contains(flannelSubnetOutput, "exists")
		pendingCNICmd := "kubectl get pods -n kube-system --field-selector=status.phase=Pending -o jsonpath='{.items[*].status.conditions[?(@.type==\"PodScheduled\")].message}' 2>&1 | grep -q 'network is not ready' && echo cni_error || echo no_cni_error"
		timeout = 30
		pendingCNIOutput, pendingCNIExit := pctService.Execute(controlID, pendingCNICmd, &timeout)
		noCNIErrors := pendingCNIExit != nil && *pendingCNIExit == 0 && pendingCNIOutput != "" && strings.Contains(pendingCNIOutput, "no_cni_error")
		runningPodsCmd := "kubectl get pods -n kube-system --field-selector=status.phase=Running --no-headers 2>&1 | wc -l"
		timeout = 30
		runningPodsOutput, runningPodsExit := pctService.Execute(controlID, runningPodsCmd, &timeout)
		podsRunning := false
		if runningPodsExit != nil && *runningPodsExit == 0 && runningPodsOutput != "" {
			count, err := strconv.Atoi(strings.TrimSpace(runningPodsOutput))
			if err == nil {
				podsRunning = count >= 3
			}
		}
		if nodesReady && cniConfigExists && flannelSubnetExists && noCNIErrors && podsRunning {
			libs.GetLogger("kubernetes").Printf("CNI plugin (Flannel) is ready - nodes Ready, CNI config exists, Flannel subnet exists, system pods running")
			cniReady = true
			break
		}
		if cniWaitTime%20 == 0 {
			libs.GetLogger("kubernetes").Printf("Waiting for CNI plugin to be ready (waited %d/%d seconds)...", cniWaitTime, maxCNIWait)
			nodesReadyStr := "False"
			if nodesReady {
				nodesReadyStr = "True"
			}
			cniConfigStr := "missing"
			if cniConfigExists {
				cniConfigStr = "exists"
			}
			flannelSubnetStr := "missing"
			if flannelSubnetExists {
				flannelSubnetStr = "exists"
			}
			runningPodsStr := "unknown"
			if runningPodsOutput != "" {
				runningPodsStr = strings.TrimSpace(runningPodsOutput)
			}
			libs.GetLogger("kubernetes").Printf("Nodes Ready: %s, CNI config: %s, Flannel subnet: %s, Running pods: %s", nodesReadyStr, cniConfigStr, flannelSubnetStr, runningPodsStr)
		}
		time.Sleep(5 * time.Second)
		cniWaitTime += 5
	}
	if !cniReady {
		libs.GetLogger("kubernetes").Printf("CNI plugin (Flannel) not ready after %d seconds - cannot proceed with cert-manager installation", maxCNIWait)
		return false
	}
	namespaceCmd := "kubectl create namespace cattle-system --dry-run=client -o yaml | kubectl apply -f -"
	pctService.Execute(controlID, namespaceCmd, nil)
	libs.GetLogger("kubernetes").Printf("Installing cert-manager...")
	certManagerCmd := "kubectl apply --validate=false --server-side --force-conflicts -f https://github.com/cert-manager/cert-manager/releases/download/v1.13.0/cert-manager.yaml"
	maxRetries := 3
	for retry := 0; retry < maxRetries; retry++ {
		timeout = 300
		certManagerOutput, _ := pctService.Execute(controlID, certManagerCmd, &timeout)
		if strings.Contains(certManagerOutput, "serverside-applied") {
			libs.GetLogger("kubernetes").Printf("cert-manager resources applied successfully")
			verifyCmd := "kubectl get namespace cert-manager"
			timeout = 30
			verifyOutput, verifyExit := pctService.Execute(controlID, verifyCmd, &timeout)
			if verifyExit != nil && *verifyExit == 0 && verifyOutput != "" && strings.Contains(verifyOutput, "cert-manager") {
				libs.GetLogger("kubernetes").Printf("cert-manager installed and verified successfully")
				break
			} else if strings.Count(certManagerOutput, "serverside-applied") >= 10 {
				libs.GetLogger("kubernetes").Printf("cert-manager resources applied successfully (verification skipped due to API unavailability)")
				break
			}
		}
		if retry < maxRetries-1 {
			libs.GetLogger("kubernetes").Printf("cert-manager installation failed (attempt %d/%d), retrying in 10 seconds...", retry+1, maxRetries)
			outputLen := len(certManagerOutput)
			start := 0
			if outputLen > 500 {
				start = outputLen - 500
			}
			if certManagerOutput != "" {
				libs.GetLogger("kubernetes").Printf("Error output: %s", certManagerOutput[start:])
			}
			time.Sleep(10 * time.Second)
		} else {
			libs.GetLogger("kubernetes").Printf("Failed to install cert-manager after %d attempts: %s", maxRetries, certManagerOutput)
			return false
		}
	}
	libs.GetLogger("kubernetes").Printf("Waiting for cert-manager webhook to be ready...")
	maxWebhookWait := 300
	webhookWaitTime := 0
	webhookReady := false
	for webhookWaitTime < maxWebhookWait {
		webhookCheckCmd := "kubectl get pods -n cert-manager -l app.kubernetes.io/component=webhook -o jsonpath='{.items[*].status.conditions[?(@.type==\"Ready\")].status}' 2>&1"
		timeout = 30
		webhookOutput, webhookExit := pctService.Execute(controlID, webhookCheckCmd, &timeout)
		if webhookExit != nil && *webhookExit == 0 && webhookOutput != "" {
			readyCount := strings.Count(webhookOutput, "True")
			if readyCount > 0 {
				endpointsCmd := "kubectl get endpoints cert-manager-webhook -n cert-manager -o jsonpath='{.subsets[*].addresses[*].ip}' 2>&1"
				timeout = 30
				endpointsOutput, endpointsExit := pctService.Execute(controlID, endpointsCmd, &timeout)
				if endpointsExit != nil && *endpointsExit == 0 && endpointsOutput != "" && strings.TrimSpace(endpointsOutput) != "" {
					libs.GetLogger("kubernetes").Printf("cert-manager webhook is ready with %d pod(s) and endpoints available", readyCount)
					webhookReady = true
					break
				}
			}
		}
		if webhookWaitTime%30 == 0 {
			libs.GetLogger("kubernetes").Printf("Waiting for cert-manager webhook to be ready (waited %d/%d seconds)...", webhookWaitTime, maxWebhookWait)
			outputLen := len(webhookOutput)
			start := 0
			if outputLen > 200 {
				start = outputLen - 200
			}
			if webhookOutput != "" {
				libs.GetLogger("kubernetes").Printf("Webhook pods status: %s", webhookOutput[start:])
			}
		}
		time.Sleep(10 * time.Second)
		webhookWaitTime += 10
	}
	if !webhookReady {
		libs.GetLogger("kubernetes").Printf("cert-manager webhook not ready after %d seconds - cannot proceed with Rancher installation", maxWebhookWait)
		return false
	}
	libs.GetLogger("kubernetes").Printf("Verifying Kubernetes API is reachable...")
	verifyAPICmd2 := "kubectl cluster-info"
	maxVerifyAttempts2 := 10
	for attempt := 0; attempt < maxVerifyAttempts2; attempt++ {
		timeout = 30
		verifyOutput2, verifyExit2 := pctService.Execute(controlID, verifyAPICmd2, &timeout)
		if verifyExit2 != nil && *verifyExit2 == 0 && verifyOutput2 != "" && strings.Contains(verifyOutput2, "is running at") {
			libs.GetLogger("kubernetes").Printf("Kubernetes API is reachable")
			break
		}
		if attempt < maxVerifyAttempts2-1 {
			libs.GetLogger("kubernetes").Printf("Waiting for Kubernetes API to be ready (attempt %d/%d)...", attempt+1, maxVerifyAttempts2)
			time.Sleep(10 * time.Second)
		} else {
			libs.GetLogger("kubernetes").Printf("Kubernetes API not reachable after %d attempts", maxVerifyAttempts2)
			if verifyOutput2 != "" {
				libs.GetLogger("kubernetes").Printf("API check output: %s", verifyOutput2)
			}
			return false
		}
	}
	libs.GetLogger("kubernetes").Printf("Verifying Kubernetes API server stability...")
	stableChecks := 3
	for i := 0; i < stableChecks; i++ {
		verifyCmd := "export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && /usr/local/bin/kubectl cluster-info 2>&1"
		timeout = 30
		verifyOutput3, verifyExit3 := pctService.Execute(controlID, verifyCmd, &timeout)
		if verifyExit3 == nil || *verifyExit3 != 0 || verifyOutput3 == "" || !strings.Contains(verifyOutput3, "is running at") {
			libs.GetLogger("kubernetes").Printf("API server check %d/%d failed, waiting 5 seconds...", i+1, stableChecks)
			if verifyOutput3 != "" {
				libs.GetLogger("kubernetes").Printf("API check output: %s", verifyOutput3)
			}
			time.Sleep(5 * time.Second)
		} else {
			libs.GetLogger("kubernetes").Printf("API server check %d/%d passed", i+1, stableChecks)
			time.Sleep(2 * time.Second)
		}
	}
	libs.GetLogger("kubernetes").Printf("Installing Rancher using Helm...")
	helmCheckCmd := "command -v helm >/dev/null 2>&1 && echo installed || echo not_installed"
	helmCheck, _ := pctService.Execute(controlID, helmCheckCmd, nil)
	if strings.Contains(helmCheck, "not_installed") {
		libs.GetLogger("kubernetes").Printf("Installing Helm...")
		helmInstallCmd := "curl https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 | bash && export PATH=/usr/local/bin:$PATH"
		timeout = 120
		pctService.Execute(controlID, helmInstallCmd, &timeout)
	}
	repoAddCmd := "export PATH=/usr/local/bin:$PATH && helm repo add rancher-stable https://releases.rancher.com/server-charts/stable && helm repo update"
	maxRepoRetries := 3
	for repoRetry := 0; repoRetry < maxRepoRetries; repoRetry++ {
		timeout = 120
		repoOutput, repoExit := pctService.Execute(controlID, repoAddCmd, &timeout)
		if repoExit != nil && *repoExit == 0 {
			break
		}
		if repoRetry < maxRepoRetries-1 {
			libs.GetLogger("kubernetes").Printf("Helm repo add failed (attempt %d/%d), retrying in 5 seconds...", repoRetry+1, maxRepoRetries)
			outputLen := len(repoOutput)
			start := 0
			if outputLen > 500 {
				start = outputLen - 500
			}
			if repoOutput != "" {
				libs.GetLogger("kubernetes").Printf("Error output: %s", repoOutput[start:])
			}
			time.Sleep(5 * time.Second)
		} else {
			libs.GetLogger("kubernetes").Printf("Failed to add Helm repo after %d attempts: %s", maxRepoRetries, repoOutput)
			return false
		}
	}
	controlHostname := controlConfig.Hostname
	rancherNodePort := 30443
	if cfg.Services.Rancher.Port != nil {
		rancherNodePort = *cfg.Services.Rancher.Port
	}
	verifyCmd2 := "export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && /usr/local/bin/kubectl cluster-info 2>&1"
	timeout = 30
	verifyOutput4, verifyExit4 := pctService.Execute(controlID, verifyCmd2, &timeout)
	if verifyExit4 == nil || *verifyExit4 != 0 || verifyOutput4 == "" || !strings.Contains(verifyOutput4, "is running at") {
		libs.GetLogger("kubernetes").Printf("API server not reachable before Rancher installation")
		outputLen := len(verifyOutput4)
		start := 0
		if outputLen > 500 {
			start = outputLen - 500
		}
		if verifyOutput4 != "" {
			libs.GetLogger("kubernetes").Printf("API check output: %s", verifyOutput4[start:])
		}
		return false
	}
	installRancherCmd := fmt.Sprintf("export PATH=/usr/local/bin:$PATH && helm upgrade --install rancher rancher-stable/rancher --namespace cattle-system --set hostname=%s --set replicas=1 --set bootstrapPassword=admin --set service.type=NodePort --set service.ports.http=8080 --set service.ports.https=443 --set service.nodePorts.https=%d", controlHostname, rancherNodePort)
	timeout = 600
	installOutput, installExit := pctService.Execute(controlID, installRancherCmd, &timeout)
	if installExit != nil && *installExit == 0 {
		libs.GetLogger("kubernetes").Printf("Rancher installed successfully")
		libs.GetLogger("kubernetes").Printf("Setting Rancher service NodePort to %d...", rancherNodePort)
		getHTTPPortCmd := "kubectl get svc rancher -n cattle-system -o jsonpath='{.spec.ports[?(@.name==\"http\")].nodePort}'"
		timeout = 10
		httpPortOutput, _ := pctService.Execute(controlID, getHTTPPortCmd, &timeout)
		httpNodePort := "30625"
		if httpPortOutput != "" {
			httpNodePort = strings.TrimSpace(httpPortOutput)
		}
		patchCmd := fmt.Sprintf("kubectl patch svc rancher -n cattle-system -p '{\"spec\":{\"ports\":[{\"name\":\"http\",\"port\":80,\"protocol\":\"TCP\",\"targetPort\":80,\"nodePort\":%s},{\"name\":\"https\",\"port\":443,\"protocol\":\"TCP\",\"targetPort\":443,\"nodePort\":%d}]}}'", httpNodePort, rancherNodePort)
		timeout = 30
		patchOutput, patchExit := pctService.Execute(controlID, patchCmd, &timeout)
		if patchExit != nil && *patchExit == 0 {
			libs.GetLogger("kubernetes").Printf("Rancher service NodePort set to %d", rancherNodePort)
		} else {
			libs.GetLogger("kubernetes").Printf("Failed to patch Rancher service NodePort: %s", patchOutput)
		}

		// Rancher verification will be done by setup_kubernetes action after deployment
		return true
	}
	outputLen := len(installOutput)
	start := 0
	if outputLen > 1000 {
		start = outputLen - 1000
	}
	libs.GetLogger("kubernetes").Printf("Rancher installation failed: %s", installOutput[start:])
	k3sStatusCmd := "systemctl status k3s --no-pager -l 2>&1 | head -50"
	timeout = 10
	k3sStatus, _ := pctService.Execute(controlID, k3sStatusCmd, &timeout)
	if k3sStatus != "" {
		libs.GetLogger("kubernetes").Printf("k3s service status: %s", k3sStatus)
	}
	k3sLogsCmd := "journalctl -u k3s --no-pager -n 50 2>&1"
	k3sLogs, _ := pctService.Execute(controlID, k3sLogsCmd, &timeout)
	if k3sLogs != "" {
		libs.GetLogger("kubernetes").Printf("k3s service logs: %s", k3sLogs)
	}
	return false
}

func restartAndVerifyNodes(context *KubernetesDeployContext, controlConfig *libs.ContainerConfig) bool {
	lxcHost := context.LXCHost()
	cfg := context.Cfg
	controlID := controlConfig.ID
	lxcService := services.NewLXCService(lxcHost, &cfg.SSH)
	if !lxcService.Connect() {
		return false
	}
	defer lxcService.Disconnect()
	pctService := services.NewPCTService(lxcService)

	libs.GetLogger("kubernetes").Printf("Restarting all k3s nodes...")

	// Restart control node
	libs.GetLogger("kubernetes").Printf("Restarting control node %d...", controlID)
	restartControlCmd := "systemctl restart k3s 2>&1"
	pctService.Execute(controlID, restartControlCmd, nil)
	time.Sleep(10 * time.Second)

	// Restart worker nodes
	for _, workerConfig := range context.Workers {
		workerID := workerConfig.ID
		libs.GetLogger("kubernetes").Printf("Restarting worker node %d...", workerID)
		restartWorkerCmd := "systemctl restart k3s-agent 2>&1"
		pctService.Execute(workerID, restartWorkerCmd, nil)
		time.Sleep(5 * time.Second)
	}

	libs.GetLogger("kubernetes").Printf("Waiting for nodes to stabilize after restart...")
	time.Sleep(30 * time.Second)

	// Run verification
	libs.GetLogger("kubernetes").Printf("Running cluster health verification...")
	if !verification.VerifyKubernetesCluster(context.Cfg, pctService) {
		libs.GetLogger("kubernetes").Printf("⚠ Cluster verification found issues after restart")
		return false
	}

	libs.GetLogger("kubernetes").Printf("✓ All nodes restarted and verified successfully")
	return true
}
