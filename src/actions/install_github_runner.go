package actions

import (
	"enva/libs"
	"enva/orchestration"
	"enva/services"
	"fmt"
	"os"
	"strconv"
	"strings"
	"time"
)

// InstallGithubRunnerAction installs GitHub Actions Runner Controller (ARC) on Kubernetes cluster
type InstallGithubRunnerAction struct {
	*BaseAction
}

func NewInstallGithubRunnerAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &InstallGithubRunnerAction{
		BaseAction: &BaseAction{
			SSHService:   sshService,
			APTService:   aptService,
			PCTService:   pctService,
			ContainerID:  containerID,
			Cfg:          cfg,
			ContainerCfg: containerCfg,
		},
	}
}

func (a *InstallGithubRunnerAction) Description() string {
	return "install github runner"
}

func (a *InstallGithubRunnerAction) Execute() bool {
	if a.Cfg == nil {
		libs.GetLogger("install_github_runner").Printf("Lab configuration is missing for InstallGithubRunnerAction.")
		return false
	}
	if a.Cfg.Kubernetes == nil {
		libs.GetLogger("install_github_runner").Printf("Kubernetes configuration is missing. Cannot install GitHub Runner.")
		return false
	}
	if a.Cfg.Services.GitHubRunner == nil {
		libs.GetLogger("install_github_runner").Printf("GitHub Runner not configured, skipping installation")
		return true
	}
	libs.GetLogger("install_github_runner").Printf("Installing GitHub Actions Runner Controller (ARC) on Kubernetes cluster...")
	context := orchestration.BuildKubernetesContext(a.Cfg)
	if context == nil {
		libs.GetLogger("install_github_runner").Printf("Failed to build Kubernetes context.")
		return false
	}
	if len(context.Control) == 0 {
		libs.GetLogger("install_github_runner").Printf("No Kubernetes control node found.")
		return false
	}
	controlConfig := context.Control[0]
	lxcHost := context.LXCHost()
	controlID := controlConfig.ID
	runnerCfg := a.Cfg.Services.GitHubRunner

	// Get configuration values with defaults
	githubToken := ""
	if runnerCfg.Token != nil {
		githubToken = *runnerCfg.Token
	}
	// Fall back to environment variable if not in config
	if githubToken == "" {
		githubToken = os.Getenv("ENVA_GITHUB_TOKEN")
	}
	if githubToken == "" {
		libs.GetLogger("install_github_runner").Printf("GitHub token not configured (check config or ENVA_GITHUB_TOKEN environment variable)")
		return false
	}

	organization := ""
	if runnerCfg.Organization != nil {
		organization = *runnerCfg.Organization
	}
	if organization == "" {
		libs.GetLogger("install_github_runner").Printf("GitHub organization not configured")
		return false
	}

	replicas := 1
	if runnerCfg.Replicas != nil {
		replicas = *runnerCfg.Replicas
	}

	runnerLabel := "self-hosted"
	if runnerCfg.Label != nil {
		runnerLabel = *runnerCfg.Label
	}

	runnerNamePrefix := "k3s-runner"
	if runnerCfg.NamePrefix != nil {
		runnerNamePrefix = *runnerCfg.NamePrefix
	}

	namespace := "actions-runner-system"
	if runnerCfg.Namespace != nil {
		namespace = *runnerCfg.Namespace
	}

	runnerGroup := ""
	if runnerCfg.Group != nil {
		runnerGroup = *runnerCfg.Group
	}

	lxcService := services.NewLXCService(lxcHost, &a.Cfg.SSH)
	if !lxcService.Connect() {
		return false
	}
	defer lxcService.Disconnect()
	pctService := services.NewPCTService(lxcService)

	// Check if kubectl is available
	kubectlCheckCmd := "command -v kubectl >/dev/null 2>&1 && echo installed || echo not_installed"
	kubectlCheck, _ := pctService.Execute(controlID, kubectlCheckCmd, nil)
	if strings.Contains(kubectlCheck, "not_installed") {
		libs.GetLogger("install_github_runner").Printf("Installing kubectl...")
		installKubectlCmd := "curl -LO https://dl.k8s.io/release/$(curl -L -s https://dl.k8s.io/release/stable.txt)/bin/linux/amd64/kubectl && chmod +x kubectl && sudo mv kubectl /usr/local/bin/ && export PATH=/usr/local/bin:$PATH"
		timeout := 120
		pctService.Execute(controlID, installKubectlCmd, &timeout)
		verifyCmd := "test -f /usr/local/bin/kubectl && echo installed || echo not_installed"
		verifyOutput, _ := pctService.Execute(controlID, verifyCmd, nil)
		if strings.Contains(verifyOutput, "not_installed") {
			libs.GetLogger("install_github_runner").Printf("kubectl installation failed")
			return false
		}
	}

	// Check if Helm is installed
	helmCheckCmd := "export PATH=/usr/local/bin:$PATH && command -v helm >/dev/null 2>&1 && echo installed || echo not_installed"
	helmCheck, _ := pctService.Execute(controlID, helmCheckCmd, nil)
	if strings.Contains(helmCheck, "not_installed") {
		libs.GetLogger("install_github_runner").Printf("Installing Helm...")
		// Use a more robust installation method: download script first, then execute with proper error handling
		helmInstallCmd := `set -e && set -o pipefail && export PATH=/usr/local/bin:$PATH && curl -fsSL https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 -o /tmp/get-helm-3.sh && chmod +x /tmp/get-helm-3.sh && /tmp/get-helm-3.sh && rm -f /tmp/get-helm-3.sh`
		timeout := 180
		installOutput, installExit := pctService.Execute(controlID, helmInstallCmd, &timeout)
		if installExit != nil && *installExit != 0 {
			libs.GetLogger("install_github_runner").Printf("Helm installation command failed with exit code %d: %s", *installExit, installOutput)
			// Try alternative installation method using direct download
			libs.GetLogger("install_github_runner").Printf("Trying alternative Helm installation method...")
			altInstallCmd := `set -e && export PATH=/usr/local/bin:$PATH && HELM_VERSION=$(curl -s https://api.github.com/repos/helm/helm/releases/latest | grep '"tag_name":' | sed -E 's/.*"([^"]+)".*/\1/') && curl -fsSL https://get.helm.sh/helm-${HELM_VERSION}-linux-amd64.tar.gz -o /tmp/helm.tar.gz && tar -xzf /tmp/helm.tar.gz -C /tmp && mv /tmp/linux-amd64/helm /usr/local/bin/helm && chmod +x /usr/local/bin/helm && rm -rf /tmp/helm.tar.gz /tmp/linux-amd64`
			altTimeout := 180
			altOutput, altExit := pctService.Execute(controlID, altInstallCmd, &altTimeout)
			if altExit != nil && *altExit != 0 {
				libs.GetLogger("install_github_runner").Printf("Alternative Helm installation also failed with exit code %d: %s", *altExit, altOutput)
				return false
			}
			libs.GetLogger("install_github_runner").Printf("Helm installed successfully using alternative method")
		}
		// Verify helm installation
		verifyHelmCmd := "export PATH=/usr/local/bin:$PATH && command -v helm >/dev/null 2>&1 && echo installed || echo not_installed"
		verifyHelmOutput, _ := pctService.Execute(controlID, verifyHelmCmd, nil)
		if strings.Contains(verifyHelmOutput, "not_installed") {
			// Also check if helm binary exists directly
			helmBinaryCheck := "test -f /usr/local/bin/helm && echo installed || echo not_installed"
			helmBinaryOutput, _ := pctService.Execute(controlID, helmBinaryCheck, nil)
			if strings.Contains(helmBinaryOutput, "not_installed") {
				libs.GetLogger("install_github_runner").Printf("Helm installation failed - binary not found after installation")
				return false
			}
			libs.GetLogger("install_github_runner").Printf("Helm binary found at /usr/local/bin/helm but not in PATH, continuing...")
		} else {
			libs.GetLogger("install_github_runner").Printf("Helm installed and verified successfully")
		}
	}

	// Create namespace
	libs.GetLogger("install_github_runner").Printf("Creating namespace %s...", namespace)
	createNsCmd := fmt.Sprintf("kubectl create namespace %s --dry-run=client -o yaml | kubectl apply -f -", namespace)
	timeout := 30
	nsOutput, nsExit := pctService.Execute(controlID, createNsCmd, &timeout)
	if nsExit != nil && *nsExit != 0 && !strings.Contains(nsOutput, "already exists") {
		libs.GetLogger("install_github_runner").Printf("Failed to create namespace: %s", nsOutput)
		return false
	}

	// Authenticator secret will be created after ARC is ready

	// Add ARC Helm repository
	libs.GetLogger("install_github_runner").Printf("Adding Actions Runner Controller Helm repository...")
	repoAddCmd := "export PATH=/usr/local/bin:$PATH && helm repo add actions-runner-controller https://actions-runner-controller.github.io/actions-runner-controller && helm repo update"
	maxRepoRetries := 3
	for repoRetry := 0; repoRetry < maxRepoRetries; repoRetry++ {
		timeout = 120
		repoOutput, repoExit := pctService.Execute(controlID, repoAddCmd, &timeout)
		if repoExit != nil && *repoExit == 0 {
			break
		}
		if repoRetry < maxRepoRetries-1 {
			libs.GetLogger("install_github_runner").Printf("Helm repo add failed (attempt %d/%d), retrying in 5 seconds...", repoRetry+1, maxRepoRetries)
			time.Sleep(5 * time.Second)
		} else {
			libs.GetLogger("install_github_runner").Printf("Failed to add Helm repo after %d attempts: %s", maxRepoRetries, repoOutput)
			return false
		}
	}
	libs.GetLogger("install_github_runner").Printf("Helm repository added successfully")

	// Create authenticator secret first (ARC needs it)
	// Helm chart expects secret named "controller-manager" with key "github_token"
	libs.GetLogger("install_github_runner").Printf("Creating GitHub authenticator secret...")
	authSecretName := "controller-manager"
	authSecretExistsCmd := fmt.Sprintf("kubectl get secret %s -n %s >/dev/null 2>&1 && echo exists || echo not_exists", authSecretName, namespace)
	authSecretExists, _ := pctService.Execute(controlID, authSecretExistsCmd, nil)
	if strings.Contains(authSecretExists, "exists") {
		deleteAuthSecretCmd := fmt.Sprintf("kubectl delete secret %s -n %s", authSecretName, namespace)
		pctService.Execute(controlID, deleteAuthSecretCmd, nil)
	}
	createAuthSecretCmd := fmt.Sprintf("kubectl create secret generic %s --from-literal=github_token=%s -n %s", authSecretName, githubToken, namespace)
	timeout = 30
	authSecretOutput, authSecretExit := pctService.Execute(controlID, createAuthSecretCmd, &timeout)
	if authSecretExit != nil && *authSecretExit != 0 {
		libs.GetLogger("install_github_runner").Printf("Failed to create authenticator secret: %s", authSecretOutput)
		return false
	}
	libs.GetLogger("install_github_runner").Printf("GitHub authenticator secret created successfully")

	// Install ARC
	libs.GetLogger("install_github_runner").Printf("Installing Actions Runner Controller...")
	arcReleaseName := "actions-runner-controller"
	// Install without --wait to avoid timeout issues, we'll check readiness manually
	// Set authSecret.create=false and authSecret.name to use the secret we created
	installArcCmd := fmt.Sprintf(`export PATH=/usr/local/bin:$PATH && helm upgrade --install %s actions-runner-controller/actions-runner-controller \
		--namespace %s \
		--set syncPeriod=1m \
		--set authSecret.create=false \
		--set authSecret.name=%s \
		--timeout 5m`, arcReleaseName, namespace, authSecretName)
	timeout = 300
	installOutput, installExit := pctService.Execute(controlID, installArcCmd, &timeout)
	if installExit != nil && *installExit != 0 {
		libs.GetLogger("install_github_runner").Printf("Failed to install Actions Runner Controller: %s", installOutput)
		outputLen := len(installOutput)
		start := 0
		if outputLen > 1000 {
			start = outputLen - 1000
		}
		if installOutput != "" {
			libs.GetLogger("install_github_runner").Printf("Installation output: %s", installOutput[start:])
		}
		return false
	}
	libs.GetLogger("install_github_runner").Printf("Actions Runner Controller helm release installed, waiting for resources to be ready...")

	// Check if webhook certificate secret exists, create it if missing (required for pods to start)
	libs.GetLogger("install_github_runner").Printf("Checking for webhook certificate secret...")
	secretCheckCmd := fmt.Sprintf("kubectl get secret actions-runner-controller-serving-cert -n %s >/dev/null 2>&1 && echo exists || echo missing", namespace)
	secretStatus, _ := pctService.Execute(controlID, secretCheckCmd, nil)
	if strings.Contains(secretStatus, "missing") {
		libs.GetLogger("install_github_runner").Printf("Webhook certificate secret missing, creating self-signed certificate...")
		// Check if cert-manager is available
		certManagerCheckCmd := "kubectl get crd certificates.cert-manager.io >/dev/null 2>&1 && echo exists || echo missing"
		certManagerStatus, _ := pctService.Execute(controlID, certManagerCheckCmd, nil)
		if strings.Contains(certManagerStatus, "exists") {
			libs.GetLogger("install_github_runner").Printf("cert-manager detected, waiting for it to create the secret...")
			maxWaitSecret := 120
			waitTimeSecret := 0
			for waitTimeSecret < maxWaitSecret {
				secretStatus, _ := pctService.Execute(controlID, secretCheckCmd, nil)
				if strings.Contains(secretStatus, "exists") {
					libs.GetLogger("install_github_runner").Printf("Webhook certificate secret created by cert-manager")
					break
				}
				if waitTimeSecret%30 == 0 && waitTimeSecret > 0 {
					libs.GetLogger("install_github_runner").Printf("Still waiting for cert-manager to create secret (waited %d/%d seconds)...", waitTimeSecret, maxWaitSecret)
				}
				time.Sleep(5 * time.Second)
				waitTimeSecret += 5
			}
			// Re-check after waiting
			secretStatus, _ = pctService.Execute(controlID, secretCheckCmd, nil)
		}
		// If still missing, create self-signed cert manually
		if strings.Contains(secretStatus, "missing") {
			libs.GetLogger("install_github_runner").Printf("Creating self-signed webhook certificate...")
			// Check if openssl is available
			opensslCheckCmd := "command -v openssl >/dev/null 2>&1 && echo installed || echo not_installed"
			opensslCheck, _ := pctService.Execute(controlID, opensslCheckCmd, nil)
			if strings.Contains(opensslCheck, "not_installed") {
				libs.GetLogger("install_github_runner").Printf("Installing openssl...")
				installOpensslCmd := "apt-get update && apt-get install -y openssl"
				timeout = 120
				opensslOutput, opensslExit := pctService.Execute(controlID, installOpensslCmd, &timeout)
				if opensslExit != nil && *opensslExit != 0 {
					libs.GetLogger("install_github_runner").Printf("Failed to install openssl: %s", opensslOutput)
					return false
				}
			}
			createCertCmd := fmt.Sprintf(`set -e && set -o pipefail && export PATH=/usr/local/bin:$PATH && \
				openssl genrsa -out /tmp/cert.key 2048 && \
				openssl req -new -key /tmp/cert.key -out /tmp/cert.csr -subj "/CN=actions-runner-controller-webhook-service.%s.svc" && \
				openssl x509 -req -in /tmp/cert.csr -signkey /tmp/cert.key -out /tmp/cert.crt -days 365 && \
				kubectl create secret tls actions-runner-controller-serving-cert --cert=/tmp/cert.crt --key=/tmp/cert.key -n %s && \
				rm -f /tmp/cert.key /tmp/cert.csr /tmp/cert.crt`, namespace, namespace)
			timeout = 60
			certOutput, certExit := pctService.Execute(controlID, createCertCmd, &timeout)
			if certExit != nil && *certExit != 0 {
				libs.GetLogger("install_github_runner").Printf("Failed to create webhook certificate secret: %s", certOutput)
				// Try alternative method using kubectl create secret generic
				libs.GetLogger("install_github_runner").Printf("Trying alternative certificate creation method...")
				altCreateCertCmd := fmt.Sprintf(`set -e && set -o pipefail && export PATH=/usr/local/bin:$PATH && \
					openssl genrsa -out /tmp/cert.key 2048 && \
					openssl req -new -x509 -key /tmp/cert.key -out /tmp/cert.crt -days 365 -subj "/CN=actions-runner-controller-webhook-service.%s.svc" && \
					kubectl create secret generic actions-runner-controller-serving-cert --from-file=tls.crt=/tmp/cert.crt --from-file=tls.key=/tmp/cert.key -n %s && \
					rm -f /tmp/cert.key /tmp/cert.crt`, namespace, namespace)
				altCertOutput, altCertExit := pctService.Execute(controlID, altCreateCertCmd, &timeout)
				if altCertExit != nil && *altCertExit != 0 {
					libs.GetLogger("install_github_runner").Printf("Alternative certificate creation also failed: %s", altCertOutput)
					return false
				}
				libs.GetLogger("install_github_runner").Printf("Webhook certificate secret created successfully using alternative method")
			} else {
				libs.GetLogger("install_github_runner").Printf("Webhook certificate secret created successfully")
			}
			// Update webhook configuration caBundle with the certificate we just created
			libs.GetLogger("install_github_runner").Printf("Updating webhook configuration caBundle...")
			updateCaBundleCmd := fmt.Sprintf(`set -e && export PATH=/usr/local/bin:$PATH && \
				CA_BUNDLE=$(kubectl get secret actions-runner-controller-serving-cert -n %s -o jsonpath='{.data.tls\.crt}') && \
				(kubectl patch validatingwebhookconfiguration actions-runner-controller-validating-webhook-configuration --type='json' -p="[{\"op\": \"replace\", \"path\": \"/webhooks/0/clientConfig/caBundle\", \"value\":\"$CA_BUNDLE\"}]" 2>&1 || true) && \
				(kubectl patch mutatingwebhookconfiguration actions-runner-controller-mutating-webhook-configuration --type='json' -p="[{\"op\": \"replace\", \"path\": \"/webhooks/0/clientConfig/caBundle\", \"value\":\"$CA_BUNDLE\"}]" 2>&1 || true)`, namespace)
			timeout = 30
			caBundleOutput, _ := pctService.Execute(controlID, updateCaBundleCmd, &timeout)
			if caBundleOutput != "" && !strings.Contains(caBundleOutput, "NotFound") {
				libs.GetLogger("install_github_runner").Printf("Webhook caBundle updated: %s", caBundleOutput)
			}
		}
	} else {
		libs.GetLogger("install_github_runner").Printf("Webhook certificate secret already exists")
	}

	// Wait for ARC to be ready
	libs.GetLogger("install_github_runner").Printf("Waiting for Actions Runner Controller to be ready...")
	maxWaitARC := 600 // Increased to 10 minutes to allow for image pulls
	waitTimeARC := 0
	arcReady := false
	for waitTimeARC < maxWaitARC {
		// Check if pods exist and their status
		podsCheckCmd := fmt.Sprintf("kubectl get pods -n %s -l app.kubernetes.io/name=actions-runner-controller -o jsonpath='{range .items[*]}{.metadata.name}{\" \"}{.status.phase}{\" \"}{.status.containerStatuses[*].ready}{\"\\n\"}{end}' 2>&1", namespace)
		timeout = 30
		podsOutput, podsExit := pctService.Execute(controlID, podsCheckCmd, &timeout)

		// Check for ready pods
		readyPodsCmd := fmt.Sprintf("kubectl get pods -n %s -l app.kubernetes.io/name=actions-runner-controller --field-selector=status.phase=Running -o jsonpath='{.items[*].status.conditions[?(@.type==\"Ready\")].status}' 2>&1", namespace)
		readyOutput, _ := pctService.Execute(controlID, readyPodsCmd, &timeout)
		readyCount := strings.Count(readyOutput, "True")

		// Need at least 1 ready pod (manager container), ideally 2 (manager + rbac-proxy)
		if readyCount >= 1 {
			libs.GetLogger("install_github_runner").Printf("Actions Runner Controller is ready with %d container(s) ready", readyCount)
			arcReady = true
			break
		}

		// Check for stuck pods and diagnose issues
		if podsExit != nil && *podsExit == 0 && podsOutput != "" {
			if strings.Contains(podsOutput, "ContainerCreating") || strings.Contains(podsOutput, "Pending") {
				// Check for specific issues
				if waitTimeARC%60 == 0 && waitTimeARC > 0 {
					// Every minute, check pod events for issues
					describeCmd := fmt.Sprintf("kubectl get events -n %s --field-selector involvedObject.kind=Pod --sort-by='.lastTimestamp' | tail -5 2>&1", namespace)
					eventsOutput, _ := pctService.Execute(controlID, describeCmd, &timeout)
					if eventsOutput != "" {
						libs.GetLogger("install_github_runner").Printf("Recent pod events: %s", eventsOutput)
					}
					// Check for missing secrets
					secretCheckCmd := fmt.Sprintf("kubectl get secret actions-runner-controller-serving-cert -n %s >/dev/null 2>&1 && echo exists || echo missing", namespace)
					secretStatus, _ := pctService.Execute(controlID, secretCheckCmd, nil)
					if strings.Contains(secretStatus, "missing") {
						libs.GetLogger("install_github_runner").Printf("Warning: serving-cert secret missing, waiting for webhook setup...")
					}
				}
			}
		}

		if waitTimeARC%30 == 0 {
			libs.GetLogger("install_github_runner").Printf("Waiting for Actions Runner Controller to be ready (waited %d/%d seconds, found %d ready container(s))...", waitTimeARC, maxWaitARC, readyCount)
			if podsOutput != "" {
				libs.GetLogger("install_github_runner").Printf("Pod status: %s", strings.TrimSpace(podsOutput))
			}
		}
		time.Sleep(5 * time.Second)
		waitTimeARC += 5
	}
	if !arcReady {
		libs.GetLogger("install_github_runner").Printf("Actions Runner Controller not ready after %d seconds", maxWaitARC)
		debugCmd := fmt.Sprintf("kubectl get pods -n %s -l app.kubernetes.io/name=actions-runner-controller -o wide 2>&1", namespace)
		timeout = 30
		debugOutput, _ := pctService.Execute(controlID, debugCmd, &timeout)
		if debugOutput != "" {
			libs.GetLogger("install_github_runner").Printf("ARC pods status: %s", debugOutput)
		}
		// Get detailed pod description for the first pod
		describeCmd := fmt.Sprintf("kubectl describe pod -n %s -l app.kubernetes.io/name=actions-runner-controller 2>&1 | grep -A 20 'Events:'", namespace)
		describeOutput, _ := pctService.Execute(controlID, describeCmd, &timeout)
		if describeOutput != "" {
			libs.GetLogger("install_github_runner").Printf("Pod events: %s", describeOutput)
		}
		return false
	}
	libs.GetLogger("install_github_runner").Printf("Actions Runner Controller is ready and operational")

	// Create RunnerDeployment
	libs.GetLogger("install_github_runner").Printf("Creating RunnerDeployment...")
	runnerDeploymentName := fmt.Sprintf("%s-deployment", runnerNamePrefix)
	
	// Build the manifest with optional group field
	groupField := ""
	if runnerGroup != "" {
		groupField = fmt.Sprintf("      group: %s\n", runnerGroup)
	}
	
	runnerDeploymentManifest := fmt.Sprintf(`apiVersion: actions.summerwind.dev/v1alpha1
kind: RunnerDeployment
metadata:
  name: %s
  namespace: %s
spec:
  replicas: %d
  template:
    spec:
      organization: %s
%s      labels:
        - %s
      dockerdWithinRunnerContainer: true
      dockerEnabled: true
      image: summerwind/actions-runner-dind:latest
      resources:
        requests:
          cpu: "250m"
          memory: "256Mi"
        limits:
          cpu: "1"
          memory: "768Mi"
`, runnerDeploymentName, namespace, replicas, organization, groupField, runnerLabel)
	// Write manifest to temporary file to avoid shell escaping issues with heredoc
	applyDeploymentCmd := fmt.Sprintf(`cat > /tmp/runner-deployment.yaml << 'DEPLOYMENT_EOF'
%s
DEPLOYMENT_EOF
kubectl apply -f /tmp/runner-deployment.yaml && rm -f /tmp/runner-deployment.yaml`, runnerDeploymentManifest)
	timeout = 60
	deploymentOutput, deploymentExit := pctService.Execute(controlID, applyDeploymentCmd, &timeout)
	if deploymentExit != nil && *deploymentExit != 0 {
		libs.GetLogger("install_github_runner").Printf("Failed to create RunnerDeployment: %s", deploymentOutput)
		return false
	}
	libs.GetLogger("install_github_runner").Printf("RunnerDeployment created successfully")

	// Create HorizontalRunnerAutoscaler (optional, for auto-scaling)
	// We'll skip this for now as it's optional

	// Wait for runners to be ready
	libs.GetLogger("install_github_runner").Printf("Waiting for GitHub runners to be ready...")
	maxWaitRunners := 300
	waitTimeRunners := 0
	runnersReady := false
	for waitTimeRunners < maxWaitRunners {
		// Check if runner pods are running - ARC creates pods with specific labels
		podsCheckCmd := fmt.Sprintf("kubectl get pods -n %s -l runner-deployment-name=%s --field-selector=status.phase=Running -o jsonpath='{.items[*].status.conditions[?(@.type==\"Ready\")].status}' 2>&1", namespace, runnerDeploymentName)
		timeout = 30
		podsOutput, podsExit := pctService.Execute(controlID, podsCheckCmd, &timeout)
		readyCount := 0
		if podsExit != nil && *podsExit == 0 && podsOutput != "" {
			readyCount = strings.Count(podsOutput, "True")
			if readyCount >= replicas {
				libs.GetLogger("install_github_runner").Printf("All %d GitHub runners are ready", replicas)
				runnersReady = true
				break
			}
		}
		if waitTimeRunners%30 == 0 {
			libs.GetLogger("install_github_runner").Printf("Waiting for GitHub runners to be ready (waited %d/%d seconds, found %d ready)...", waitTimeRunners, maxWaitRunners, readyCount)
		}
		time.Sleep(10 * time.Second)
		waitTimeRunners += 10
	}
	if !runnersReady {
		libs.GetLogger("install_github_runner").Printf("GitHub runners not ready after %d seconds", maxWaitRunners)
		// Check final status for debugging
		finalCheckCmd := fmt.Sprintf("kubectl get pods -n %s -l runner-deployment-name=%s -o wide 2>&1", namespace, runnerDeploymentName)
		timeout = 30
		finalOutput, _ := pctService.Execute(controlID, finalCheckCmd, &timeout)
		if finalOutput != "" {
			libs.GetLogger("install_github_runner").Printf("Final runner pod status: %s", finalOutput)
		}
		libs.GetLogger("install_github_runner").Printf("Failed: GitHub runners did not become ready within timeout")
		return false
	}

	// Verify and fix runner pod count to match desired replicas
	libs.GetLogger("install_github_runner").Printf("Verifying runner pod count matches desired replicas...")
	timeout = 30
	
	// Check if we have excess pods
	totalPodsCmd := fmt.Sprintf("kubectl get pods -n %s -l runner-deployment-name=%s --no-headers 2>&1 | wc -l", namespace, runnerDeploymentName)
	totalPodsOutput, _ := pctService.Execute(controlID, totalPodsCmd, nil)
	totalPodsStr := strings.TrimSpace(totalPodsOutput)
	totalPods := 0
	if count, err := strconv.Atoi(totalPodsStr); err == nil {
		totalPods = count
	}
	
	if totalPods > replicas {
		libs.GetLogger("install_github_runner").Printf("Found %d runner pods, but desired is %d. Fixing pod count...", totalPods, replicas)
		
		// Ensure RunnerDeployment spec has correct replicas
		patchCmd := fmt.Sprintf("kubectl patch runnerdeployment %s -n %s --type=merge -p '{\"spec\":{\"replicas\":%d}}' 2>&1", runnerDeploymentName, namespace, replicas)
		patchOutput, patchExit := pctService.Execute(controlID, patchCmd, &timeout)
		if patchExit != nil && *patchExit != 0 {
			libs.GetLogger("install_github_runner").Printf("Warning: Failed to patch RunnerDeployment: %s", patchOutput)
		} else {
			libs.GetLogger("install_github_runner").Printf("RunnerDeployment patched to %d replicas", replicas)
		}
		
		// Wait a bit for controller to reconcile
		time.Sleep(10 * time.Second)
		
		// Get list of pods and delete excess ones (keep the newest ones)
		getPodsCmd := fmt.Sprintf("kubectl get pods -n %s -l runner-deployment-name=%s --no-headers --sort-by=.metadata.creationTimestamp 2>&1", namespace, runnerDeploymentName)
		podsListOutput, _ := pctService.Execute(controlID, getPodsCmd, nil)
		podLines := strings.Split(strings.TrimSpace(podsListOutput), "\n")
		
		if len(podLines) > replicas {
			// Delete the oldest pods (first in the sorted list)
			podsToDelete := podLines[:len(podLines)-replicas]
			for _, podLine := range podsToDelete {
				if podName := strings.Fields(podLine)[0]; podName != "" {
					deleteCmd := fmt.Sprintf("kubectl delete pod %s -n %s 2>&1", podName, namespace)
					deleteOutput, _ := pctService.Execute(controlID, deleteCmd, &timeout)
					if deleteOutput != "" && !strings.Contains(deleteOutput, "NotFound") {
						libs.GetLogger("install_github_runner").Printf("Deleted excess pod: %s", podName)
					}
				}
			}
			
			// Wait for controller to stabilize
			libs.GetLogger("install_github_runner").Printf("Waiting for pod count to stabilize...")
			time.Sleep(15 * time.Second)
			
			// Verify final count
			finalCountCmd := fmt.Sprintf("kubectl get pods -n %s -l runner-deployment-name=%s --no-headers 2>&1 | wc -l", namespace, runnerDeploymentName)
			finalCountOutput, _ := pctService.Execute(controlID, finalCountCmd, nil)
			finalCountStr := strings.TrimSpace(finalCountOutput)
			if finalCount, err := strconv.Atoi(finalCountStr); err == nil {
				if finalCount == replicas {
					libs.GetLogger("install_github_runner").Printf("Pod count fixed: %d pods running (matches desired %d)", finalCount, replicas)
				} else {
					libs.GetLogger("install_github_runner").Printf("Warning: Pod count is %d, expected %d. Controller may still be reconciling.", finalCount, replicas)
				}
			}
		}
	} else if totalPods == replicas {
		libs.GetLogger("install_github_runner").Printf("Pod count is correct: %d pods running", totalPods)
	}

	libs.GetLogger("install_github_runner").Printf("GitHub Actions Runner Controller installation completed")
	libs.GetLogger("install_github_runner").Printf("RunnerDeployment: %s/%s with %d replicas, label: %s", namespace, runnerDeploymentName, replicas, runnerLabel)
	return true
}
