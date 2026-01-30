using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Enva.Libs;
using Enva.Orchestration;
using Enva.Services;

namespace Enva.Actions;

public class InstallGithubRunnerAction : BaseAction, IAction
{
    public InstallGithubRunnerAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "install github runner";
    }

    public bool Execute()
    {
        if (Cfg == null)
        {
            Logger.GetLogger("install_github_runner").Printf("Lab configuration is missing for InstallGithubRunnerAction.");
            return false;
        }
        if (Cfg.Kubernetes == null)
        {
            Logger.GetLogger("install_github_runner").Printf("Kubernetes configuration is missing. Cannot install GitHub Runner.");
            return false;
        }
        if (Cfg.Services.Services == null || !Cfg.Services.Services.ContainsKey("github_runner"))
        {
            Logger.GetLogger("install_github_runner").Printf("GitHub Runner not configured, skipping installation");
            return true;
        }
        Logger.GetLogger("install_github_runner").Printf("Installing GitHub Actions Runner Controller (ARC) on Kubernetes cluster...");
        KubernetesDeployContext? context = Kubernetes.BuildKubernetesContext(Cfg);
        if (context == null)
        {
            Logger.GetLogger("install_github_runner").Printf("Failed to build Kubernetes context.");
            return false;
        }
        if (context.Control.Count == 0)
        {
            Logger.GetLogger("install_github_runner").Printf("No Kubernetes control node found.");
            return false;
        }
        ContainerConfig controlConfig = context.Control[0];
        string lxcHost = context.LXCHost();
        int controlID = controlConfig.ID;
        // Get configuration values - GitHubRunner config is in environment-specific config
        // For now, use defaults and environment variable for token
        // TODO: Read from environment-specific config when available
        string githubToken = Environment.GetEnvironmentVariable("ENVA_GITHUB_TOKEN") ?? "";
        if (string.IsNullOrEmpty(githubToken))
        {
            Logger.GetLogger("install_github_runner").Printf("GitHub token not configured (set ENVA_GITHUB_TOKEN environment variable), skipping installation");
            return true;
        }

        // Default values - should be read from environment config
        string organization = "swipentap";
        int replicas = 3;
        string runnerLabel = "k3s";
        string runnerNamePrefix = "k3s-runner";
        string namespace_ = "actions-runner-system";
        string runnerGroup = "";

        // Create LXCService from lxcHost and SSH config
        var lxcService = new LXCService(lxcHost, Cfg.SSH);
        if (!lxcService.Connect())
        {
            return false;
        }
        try
        {
            PCTService pctService = new PCTService(lxcService);

        int timeout; // Declare timeout at method level for reuse

        // Check if kubectl is available
        string kubectlCheckCmd = "command -v kubectl  && echo installed || echo not_installed";
        (string kubectlCheck, _) = pctService.Execute(controlID, kubectlCheckCmd, null);
        if (kubectlCheck.Contains("not_installed"))
        {
            Logger.GetLogger("install_github_runner").Printf("Installing kubectl...");
            string installKubectlCmd = "curl -LO https://dl.k8s.io/release/$(curl -L -s https://dl.k8s.io/release/stable.txt)/bin/linux/amd64/kubectl && chmod +x kubectl && sudo mv kubectl /usr/local/bin/ && export PATH=/usr/local/bin:$PATH";
            timeout = 120;
            pctService.Execute(controlID, installKubectlCmd, timeout);
            string verifyCmd = "test -f /usr/local/bin/kubectl && echo installed || echo not_installed";
            (string verifyOutput, _) = pctService.Execute(controlID, verifyCmd, null);
            if (verifyOutput.Contains("not_installed"))
            {
                Logger.GetLogger("install_github_runner").Printf("kubectl installation failed");
                return false;
            }
        }

        // Check if Helm is installed
        string helmCheckCmd = "export PATH=/usr/local/bin:$PATH && command -v helm  && echo installed || echo not_installed";
        (string helmCheck, _) = pctService.Execute(controlID, helmCheckCmd, null);
        if (helmCheck.Contains("not_installed"))
        {
            Logger.GetLogger("install_github_runner").Printf("Installing Helm...");
            // Use a more robust installation method: download script first, then execute with proper error handling
            string helmInstallCmd = "set -e && set -o pipefail && export PATH=/usr/local/bin:$PATH && curl -fsSL https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 -o /tmp/get-helm-3.sh && chmod +x /tmp/get-helm-3.sh && /tmp/get-helm-3.sh && rm -f /tmp/get-helm-3.sh";
            timeout = 180;
            (string installOutput, int? installExit) = pctService.Execute(controlID, helmInstallCmd, timeout);
            if (installExit.HasValue && installExit.Value != 0)
            {
                Logger.GetLogger("install_github_runner").Printf("Helm installation command failed with exit code {0}: {1}", installExit.Value, installOutput);
                // Try alternative installation method using direct download
                Logger.GetLogger("install_github_runner").Printf("Trying alternative Helm installation method...");
                string altInstallCmd = "set -e && export PATH=/usr/local/bin:$PATH && HELM_VERSION=$(curl -s https://api.github.com/repos/helm/helm/releases/latest | grep '\"tag_name\":' | sed -E 's/.*\"([^\"]+)\".*/\\1/') && curl -fsSL https://get.helm.sh/helm-${HELM_VERSION}-linux-amd64.tar.gz -o /tmp/helm.tar.gz && tar -xzf /tmp/helm.tar.gz -C /tmp && mv /tmp/linux-amd64/helm /usr/local/bin/helm && chmod +x /usr/local/bin/helm && rm -rf /tmp/helm.tar.gz /tmp/linux-amd64";
                int altTimeout = 180;
                (string altOutput, int? altExit) = pctService.Execute(controlID, altInstallCmd, altTimeout);
                if (altExit.HasValue && altExit.Value != 0)
                {
                    Logger.GetLogger("install_github_runner").Printf("Alternative Helm installation also failed with exit code {0}: {1}", altExit.Value, altOutput);
                    return false;
                }
                Logger.GetLogger("install_github_runner").Printf("Helm installed successfully using alternative method");
            }
            // Verify helm installation
            string verifyHelmCmd = "export PATH=/usr/local/bin:$PATH && command -v helm  && echo installed || echo not_installed";
            (string verifyHelmOutput, _) = pctService.Execute(controlID, verifyHelmCmd, null);
            if (verifyHelmOutput.Contains("not_installed"))
            {
                // Also check if helm binary exists directly
                string helmBinaryCheck = "test -f /usr/local/bin/helm && echo installed || echo not_installed";
                (string helmBinaryOutput, _) = pctService.Execute(controlID, helmBinaryCheck, null);
                if (helmBinaryOutput.Contains("not_installed"))
                {
                    Logger.GetLogger("install_github_runner").Printf("Helm installation failed - binary not found after installation");
                    return false;
                }
                Logger.GetLogger("install_github_runner").Printf("Helm binary found at /usr/local/bin/helm but not in PATH, continuing...");
            }
            else
            {
                Logger.GetLogger("install_github_runner").Printf("Helm installed and verified successfully");
            }
        }

        // Create namespace
        Logger.GetLogger("install_github_runner").Printf("Creating namespace {0}...", namespace_);
        string createNsCmd = $"kubectl create namespace {namespace_} --dry-run=client -o yaml | kubectl apply -f -";
        timeout = 30;
        (string nsOutput, int? nsExit) = pctService.Execute(controlID, createNsCmd, timeout);
        if (nsExit.HasValue && nsExit.Value != 0 && !nsOutput.Contains("already exists"))
        {
            Logger.GetLogger("install_github_runner").Printf("Failed to create namespace: {0}", nsOutput);
            return false;
        }

        // Authenticator secret will be created after ARC is ready

        // Add ARC Helm repository
        Logger.GetLogger("install_github_runner").Printf("Adding Actions Runner Controller Helm repository...");
        string repoAddCmd = "export PATH=/usr/local/bin:$PATH && helm repo add actions-runner-controller https://actions-runner-controller.github.io/actions-runner-controller && helm repo update";
        int maxRepoRetries = 3;
        for (int repoRetry = 0; repoRetry < maxRepoRetries; repoRetry++)
        {
            timeout = 120;
            (string repoOutput, int? repoExit) = pctService.Execute(controlID, repoAddCmd, timeout);
            if (repoExit.HasValue && repoExit.Value == 0)
            {
                break;
            }
            if (repoRetry < maxRepoRetries - 1)
            {
                Logger.GetLogger("install_github_runner").Printf("Helm repo add failed (attempt {0}/{1}), retrying in 5 seconds...", repoRetry + 1, maxRepoRetries);
                Thread.Sleep(5000);
            }
            else
            {
                Logger.GetLogger("install_github_runner").Printf("Failed to add Helm repo after {0} attempts: {1}", maxRepoRetries, repoOutput);
                return false;
            }
        }
        Logger.GetLogger("install_github_runner").Printf("Helm repository added successfully");

        // Create authenticator secret first (ARC needs it)
        // Helm chart expects secret named "controller-manager" with key "github_token"
        Logger.GetLogger("install_github_runner").Printf("Creating GitHub authenticator secret...");
        string authSecretName = "controller-manager";
        string authSecretExistsCmd = $"kubectl get secret {authSecretName} -n {namespace_}  && echo exists || echo not_exists";
        (string authSecretExists, _) = pctService.Execute(controlID, authSecretExistsCmd, null);
        if (authSecretExists.Contains("exists"))
        {
            string deleteAuthSecretCmd = $"kubectl delete secret {authSecretName} -n {namespace_}";
            pctService.Execute(controlID, deleteAuthSecretCmd, null);
        }
        string createAuthSecretCmd = $"kubectl create secret generic {authSecretName} --from-literal=github_token={githubToken} -n {namespace_}";
        timeout = 30;
        (string authSecretOutput, int? authSecretExit) = pctService.Execute(controlID, createAuthSecretCmd, timeout);
        if (authSecretExit.HasValue && authSecretExit.Value != 0)
        {
            Logger.GetLogger("install_github_runner").Printf("Failed to create authenticator secret: {0}", authSecretOutput);
            return false;
        }
        Logger.GetLogger("install_github_runner").Printf("GitHub authenticator secret created successfully");

        // Install ARC
        Logger.GetLogger("install_github_runner").Printf("Installing Actions Runner Controller...");
        string arcReleaseName = "actions-runner-controller";
        // Install without --wait to avoid timeout issues, we'll check readiness manually
        // Set authSecret.create=false and authSecret.name to use the secret we created
        string installArcCmd = $"export PATH=/usr/local/bin:$PATH && helm upgrade --install {arcReleaseName} actions-runner-controller/actions-runner-controller --namespace {namespace_} --set syncPeriod=1m --set authSecret.create=false --set authSecret.name={authSecretName} --timeout 5m";
        timeout = 300;
        (string installArcOutput, int? installArcExit) = pctService.Execute(controlID, installArcCmd, timeout);
        if (installArcExit.HasValue && installArcExit.Value != 0)
        {
            Logger.GetLogger("install_github_runner").Printf("Failed to install Actions Runner Controller: {0}", installArcOutput);
            int outputLen = installArcOutput.Length;
            int start = outputLen > 1000 ? outputLen - 1000 : 0;
            if (!string.IsNullOrEmpty(installArcOutput))
            {
                Logger.GetLogger("install_github_runner").Printf("Installation output: {0}", installArcOutput.Substring(start));
            }
            return false;
        }
        Logger.GetLogger("install_github_runner").Printf("Actions Runner Controller helm release installed, waiting for resources to be ready...");

        // Check if webhook certificate secret exists, create it if missing (required for pods to start)
        Logger.GetLogger("install_github_runner").Printf("Checking for webhook certificate secret...");
        string secretCheckCmd = $"kubectl get secret actions-runner-controller-serving-cert -n {namespace_}  && echo exists || echo missing";
        (string secretStatus, _) = pctService.Execute(controlID, secretCheckCmd, null);
        if (secretStatus.Contains("missing"))
        {
            Logger.GetLogger("install_github_runner").Printf("Webhook certificate secret missing, creating self-signed certificate...");
            // Check if cert-manager is available
            string certManagerCheckCmd = "kubectl get crd certificates.cert-manager.io  && echo exists || echo missing";
            (string certManagerStatus, _) = pctService.Execute(controlID, certManagerCheckCmd, null);
            if (certManagerStatus.Contains("exists"))
            {
                Logger.GetLogger("install_github_runner").Printf("cert-manager detected, waiting for it to create the secret...");
                int maxWaitSecret = 120;
                int waitTimeSecret = 0;
                while (waitTimeSecret < maxWaitSecret)
                {
                    (secretStatus, _) = pctService.Execute(controlID, secretCheckCmd, null);
                    if (secretStatus.Contains("exists"))
                    {
                        Logger.GetLogger("install_github_runner").Printf("Webhook certificate secret created by cert-manager");
                        break;
                    }
                    if (waitTimeSecret % 30 == 0 && waitTimeSecret > 0)
                    {
                        Logger.GetLogger("install_github_runner").Printf("Still waiting for cert-manager to create secret (waited {0}/{1} seconds)...", waitTimeSecret, maxWaitSecret);
                    }
                    Thread.Sleep(5000);
                    waitTimeSecret += 5;
                }
                // Re-check after waiting
                (secretStatus, _) = pctService.Execute(controlID, secretCheckCmd, null);
            }
            // If still missing, create self-signed cert manually
            if (secretStatus.Contains("missing"))
            {
                Logger.GetLogger("install_github_runner").Printf("Creating self-signed webhook certificate...");
                // Check if openssl is available
                string opensslCheckCmd = "command -v openssl  && echo installed || echo not_installed";
                (string opensslCheck, _) = pctService.Execute(controlID, opensslCheckCmd, null);
                if (opensslCheck.Contains("not_installed"))
                {
                    Logger.GetLogger("install_github_runner").Printf("Installing openssl...");
                    string installOpensslCmd = "apt-get update && apt-get install -y openssl";
                    timeout = 120;
                    (string opensslOutput, int? opensslExit) = pctService.Execute(controlID, installOpensslCmd, timeout);
                    if (opensslExit.HasValue && opensslExit.Value != 0)
                    {
                        Logger.GetLogger("install_github_runner").Printf("Failed to install openssl: {0}", opensslOutput);
                        return false;
                    }
                }
                string createCertCmd = $"set -e && set -o pipefail && export PATH=/usr/local/bin:$PATH && openssl genrsa -out /tmp/cert.key 2048 && openssl req -new -key /tmp/cert.key -out /tmp/cert.csr -subj \"/CN=actions-runner-controller-webhook-service.{namespace_}.svc\" && openssl x509 -req -in /tmp/cert.csr -signkey /tmp/cert.key -out /tmp/cert.crt -days 365 && kubectl create secret tls actions-runner-controller-serving-cert --cert=/tmp/cert.crt --key=/tmp/cert.key -n {namespace_} && rm -f /tmp/cert.key /tmp/cert.csr /tmp/cert.crt";
                timeout = 60;
                (string certOutput, int? certExit) = pctService.Execute(controlID, createCertCmd, timeout);
                if (certExit.HasValue && certExit.Value != 0)
                {
                    Logger.GetLogger("install_github_runner").Printf("Failed to create webhook certificate secret: {0}", certOutput);
                    // Try alternative method using kubectl create secret generic
                    Logger.GetLogger("install_github_runner").Printf("Trying alternative certificate creation method...");
                    string altCreateCertCmd = $"set -e && set -o pipefail && export PATH=/usr/local/bin:$PATH && openssl genrsa -out /tmp/cert.key 2048 && openssl req -new -x509 -key /tmp/cert.key -out /tmp/cert.crt -days 365 -subj \"/CN=actions-runner-controller-webhook-service.{namespace_}.svc\" && kubectl create secret generic actions-runner-controller-serving-cert --from-file=tls.crt=/tmp/cert.crt --from-file=tls.key=/tmp/cert.key -n {namespace_} && rm -f /tmp/cert.key /tmp/cert.crt";
                    (string altCertOutput, int? altCertExit) = pctService.Execute(controlID, altCreateCertCmd, timeout);
                    if (altCertExit.HasValue && altCertExit.Value != 0)
                    {
                        Logger.GetLogger("install_github_runner").Printf("Alternative certificate creation also failed: {0}", altCertOutput);
                        return false;
                    }
                    Logger.GetLogger("install_github_runner").Printf("Webhook certificate secret created successfully using alternative method");
                }
                else
                {
                    Logger.GetLogger("install_github_runner").Printf("Webhook certificate secret created successfully");
                }
                // Update webhook configuration caBundle with the certificate we just created
                Logger.GetLogger("install_github_runner").Printf("Updating webhook configuration caBundle...");
                string updateCaBundleCmd = $"set -e && export PATH=/usr/local/bin:$PATH && CA_BUNDLE=$(kubectl get secret actions-runner-controller-serving-cert -n {namespace_} -o jsonpath='{{.data.tls\\.crt}}') && (kubectl patch validatingwebhookconfiguration actions-runner-controller-validating-webhook-configuration --type='json' -p=\"[{{\\\"op\\\": \\\"replace\\\", \\\"path\\\": \\\"/webhooks/0/clientConfig/caBundle\\\", \\\"value\\\":\\\"$CA_BUNDLE\\\"}}]\" || true) && (kubectl patch mutatingwebhookconfiguration actions-runner-controller-mutating-webhook-configuration --type='json' -p=\"[{{\\\"op\\\": \\\"replace\\\", \\\"path\\\": \\\"/webhooks/0/clientConfig/caBundle\\\", \\\"value\\\":\\\"$CA_BUNDLE\\\"}}]\" || true)";
                timeout = 30;
                (string caBundleOutput, _) = pctService.Execute(controlID, updateCaBundleCmd, timeout);
                if (!string.IsNullOrEmpty(caBundleOutput) && !caBundleOutput.Contains("NotFound"))
                {
                    Logger.GetLogger("install_github_runner").Printf("Webhook caBundle updated: {0}", caBundleOutput);
                }
            }
        }
        else
        {
            Logger.GetLogger("install_github_runner").Printf("Webhook certificate secret already exists");
        }

        // Wait for ARC to be ready
        Logger.GetLogger("install_github_runner").Printf("Waiting for Actions Runner Controller to be ready...");
        int maxWaitARC = 600; // Increased to 10 minutes to allow for image pulls
        int waitTimeARC = 0;
        bool arcReady = false;
        while (waitTimeARC < maxWaitARC)
        {
            // Check if pods exist and their status
            string podsCheckCmd = $"kubectl get pods -n {namespace_} -l app.kubernetes.io/name=actions-runner-controller -o jsonpath='{{range .items[*]}}{{.metadata.name}}{{\" \"}}{{.status.phase}}{{\" \"}}{{.status.containerStatuses[*].ready}}{{\"\\n\"}}{{end}}'";
            timeout = 30;
            (string podsOutput, int? podsExit) = pctService.Execute(controlID, podsCheckCmd, timeout);

            // Check for ready pods
            string readyPodsCmd = $"kubectl get pods -n {namespace_} -l app.kubernetes.io/name=actions-runner-controller --field-selector=status.phase=Running -o jsonpath='{{.items[*].status.conditions[?(@.type==\"Ready\")].status}}'";
            (string readyOutput, _) = pctService.Execute(controlID, readyPodsCmd, timeout);
            int readyCount = (readyOutput.Length - readyOutput.Replace("True", "").Length) / "True".Length;

            // Need at least 1 ready pod (manager container), ideally 2 (manager + rbac-proxy)
            if (readyCount >= 1)
            {
                Logger.GetLogger("install_github_runner").Printf("Actions Runner Controller is ready with {0} container(s) ready", readyCount);
                arcReady = true;
                break;
            }

            // Check for stuck pods and diagnose issues
            if (podsExit.HasValue && podsExit.Value == 0 && !string.IsNullOrEmpty(podsOutput))
            {
                if (podsOutput.Contains("ContainerCreating") || podsOutput.Contains("Pending"))
                {
                    // Check for specific issues
                    if (waitTimeARC % 60 == 0 && waitTimeARC > 0)
                    {
                        // Every minute, check pod events for issues
                        string describeCmd = $"kubectl get events -n {namespace_} --field-selector involvedObject.kind=Pod --sort-by='.lastTimestamp' | tail -5";
                        (string eventsOutput, _) = pctService.Execute(controlID, describeCmd, timeout);
                        if (!string.IsNullOrEmpty(eventsOutput))
                        {
                            Logger.GetLogger("install_github_runner").Printf("Recent pod events: {0}", eventsOutput);
                        }
                        // Check for missing secrets
                        (secretStatus, _) = pctService.Execute(controlID, secretCheckCmd, null);
                        if (secretStatus.Contains("missing"))
                        {
                            Logger.GetLogger("install_github_runner").Printf("Warning: serving-cert secret missing, waiting for webhook setup...");
                        }
                    }
                }
            }

            if (waitTimeARC % 30 == 0)
            {
                Logger.GetLogger("install_github_runner").Printf("Waiting for Actions Runner Controller to be ready (waited {0}/{1} seconds, found {2} ready container(s))...", waitTimeARC, maxWaitARC, readyCount);
                if (!string.IsNullOrEmpty(podsOutput))
                {
                    Logger.GetLogger("install_github_runner").Printf("Pod status: {0}", podsOutput.Trim());
                }
            }
            Thread.Sleep(5000);
            waitTimeARC += 5;
        }
        if (!arcReady)
        {
            Logger.GetLogger("install_github_runner").Printf("Actions Runner Controller not ready after {0} seconds", maxWaitARC);
            string debugCmd = $"kubectl get pods -n {namespace_} -l app.kubernetes.io/name=actions-runner-controller -o wide";
            timeout = 30;
            (string debugOutput, _) = pctService.Execute(controlID, debugCmd, timeout);
            if (!string.IsNullOrEmpty(debugOutput))
            {
                Logger.GetLogger("install_github_runner").Printf("ARC pods status: {0}", debugOutput);
            }
            // Get detailed pod description for the first pod
            string describeCmd = $"kubectl describe pod -n {namespace_} -l app.kubernetes.io/name=actions-runner-controller | grep -A 20 'Events:'";
            (string describeOutput, _) = pctService.Execute(controlID, describeCmd, timeout);
            if (!string.IsNullOrEmpty(describeOutput))
            {
                Logger.GetLogger("install_github_runner").Printf("Pod events: {0}", describeOutput);
            }
            return false;
        }
        Logger.GetLogger("install_github_runner").Printf("Actions Runner Controller is ready and operational");

        // Create RunnerDeployment
        Logger.GetLogger("install_github_runner").Printf("Creating RunnerDeployment...");
        string runnerDeploymentName = $"{runnerNamePrefix}-deployment";
        
        // Build the manifest with optional group field
        string groupField = "";
        if (!string.IsNullOrEmpty(runnerGroup))
        {
            groupField = $"      group: {runnerGroup}\n";
        }
        
        string runnerDeploymentManifest = $@"apiVersion: actions.summerwind.dev/v1alpha1
kind: RunnerDeployment
metadata:
  name: {runnerDeploymentName}
  namespace: {namespace_}
spec:
  replicas: {replicas}
  template:
    spec:
      organization: {organization}
{groupField}      labels:
        - {runnerLabel}
      dockerdWithinRunnerContainer: true
      dockerEnabled: true
      image: summerwind/actions-runner-dind:latest
      resources:
        requests:
          cpu: ""250m""
          memory: ""256Mi""
        limits:
          cpu: ""1""
          memory: ""768Mi""
";
        // Write manifest to temporary file to avoid shell escaping issues with heredoc
        string applyDeploymentCmd = $@"cat > /tmp/runner-deployment.yaml << 'DEPLOYMENT_EOF'
{runnerDeploymentManifest}
DEPLOYMENT_EOF
kubectl apply -f /tmp/runner-deployment.yaml && rm -f /tmp/runner-deployment.yaml";
        timeout = 60;
        (string deploymentOutput, int? deploymentExit) = pctService.Execute(controlID, applyDeploymentCmd, timeout);
        if (deploymentExit.HasValue && deploymentExit.Value != 0)
        {
            Logger.GetLogger("install_github_runner").Printf("Failed to create RunnerDeployment: {0}", deploymentOutput);
            return false;
        }
        Logger.GetLogger("install_github_runner").Printf("RunnerDeployment created successfully");

        // Create HorizontalRunnerAutoscaler (optional, for auto-scaling)
        // We'll skip this for now as it's optional

        // Wait for runners to be ready
        Logger.GetLogger("install_github_runner").Printf("Waiting for GitHub runners to be ready...");
        int maxWaitRunners = 300;
        int waitTimeRunners = 0;
        bool runnersReady = false;
        while (waitTimeRunners < maxWaitRunners)
        {
            // Check if runner pods are running - ARC creates pods with specific labels
            string podsCheckCmd = $"kubectl get pods -n {namespace_} -l runner-deployment-name={runnerDeploymentName} --field-selector=status.phase=Running -o jsonpath='{{.items[*].status.conditions[?(@.type==\"Ready\")].status}}'";
            timeout = 30;
            (string podsOutput, int? podsExit) = pctService.Execute(controlID, podsCheckCmd, timeout);
            int readyCount = 0;
            if (podsExit.HasValue && podsExit.Value == 0 && !string.IsNullOrEmpty(podsOutput))
            {
                readyCount = (podsOutput.Length - podsOutput.Replace("True", "").Length) / "True".Length;
                if (readyCount >= replicas)
                {
                    Logger.GetLogger("install_github_runner").Printf("All {0} GitHub runners are ready", replicas);
                    runnersReady = true;
                    break;
                }
            }
            if (waitTimeRunners % 30 == 0)
            {
                Logger.GetLogger("install_github_runner").Printf("Waiting for GitHub runners to be ready (waited {0}/{1} seconds, found {2} ready)...", waitTimeRunners, maxWaitRunners, readyCount);
            }
            Thread.Sleep(10000);
            waitTimeRunners += 10;
        }
        if (!runnersReady)
        {
            Logger.GetLogger("install_github_runner").Printf("GitHub runners not ready after {0} seconds", maxWaitRunners);
            // Check final status for debugging
            string finalCheckCmd = $"kubectl get pods -n {namespace_} -l runner-deployment-name={runnerDeploymentName} -o wide";
            timeout = 30;
            (string finalOutput, _) = pctService.Execute(controlID, finalCheckCmd, timeout);
            if (!string.IsNullOrEmpty(finalOutput))
            {
                Logger.GetLogger("install_github_runner").Printf("Final runner pod status: {0}", finalOutput);
            }
            Logger.GetLogger("install_github_runner").Printf("Failed: GitHub runners did not become ready within timeout");
            return false;
        }

        // Verify and fix runner pod count to match desired replicas
        Logger.GetLogger("install_github_runner").Printf("Verifying runner pod count matches desired replicas...");
        timeout = 30;
        
        // Check if we have excess pods
        string totalPodsCmd = $"kubectl get pods -n {namespace_} -l runner-deployment-name={runnerDeploymentName} --no-headers | wc -l";
        (string totalPodsOutput, _) = pctService.Execute(controlID, totalPodsCmd, null);
        string totalPodsStr = totalPodsOutput.Trim();
        int totalPods = 0;
        if (int.TryParse(totalPodsStr, out int parsedTotal))
        {
            totalPods = parsedTotal;
        }
        
        if (totalPods > replicas)
        {
            Logger.GetLogger("install_github_runner").Printf("Found {0} runner pods, but desired is {1}. Fixing pod count...", totalPods, replicas);
            
            // Ensure RunnerDeployment spec has correct replicas
            string patchCmd = $"kubectl patch runnerdeployment {runnerDeploymentName} -n {namespace_} --type=merge -p '{{\"spec\":{{\"replicas\":{replicas}}}}}'";
            (string patchOutput, int? patchExit) = pctService.Execute(controlID, patchCmd, timeout);
            if (patchExit.HasValue && patchExit.Value != 0)
            {
                Logger.GetLogger("install_github_runner").Printf("Warning: Failed to patch RunnerDeployment: {0}", patchOutput);
            }
            else
            {
                Logger.GetLogger("install_github_runner").Printf("RunnerDeployment patched to {0} replicas", replicas);
            }
            
            // Wait a bit for controller to reconcile
            Thread.Sleep(10000);
            
            // Get list of pods and delete excess ones (keep the newest ones)
            string getPodsCmd = $"kubectl get pods -n {namespace_} -l runner-deployment-name={runnerDeploymentName} --no-headers --sort-by=.metadata.creationTimestamp";
            (string podsListOutput, _) = pctService.Execute(controlID, getPodsCmd, null);
            string[] podLines = podsListOutput.Trim().Split('\n');
            
            if (podLines.Length > replicas)
            {
                // Delete the oldest pods (first in the sorted list)
                string[] podsToDelete = podLines.Take(podLines.Length - replicas).ToArray();
                foreach (string podLine in podsToDelete)
                {
                    string[] fields = podLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (fields.Length > 0)
                    {
                        string podName = fields[0];
                        string deleteCmd = $"kubectl delete pod {podName} -n {namespace_}";
                        (string deleteOutput, _) = pctService.Execute(controlID, deleteCmd, timeout);
                        if (!string.IsNullOrEmpty(deleteOutput) && !deleteOutput.Contains("NotFound"))
                        {
                            Logger.GetLogger("install_github_runner").Printf("Deleted excess pod: {0}", podName);
                        }
                    }
                }
                
                // Wait for controller to stabilize
                Logger.GetLogger("install_github_runner").Printf("Waiting for pod count to stabilize...");
                Thread.Sleep(15000);
                
                // Verify final count
                string finalCountCmd = $"kubectl get pods -n {namespace_} -l runner-deployment-name={runnerDeploymentName} --no-headers | wc -l";
                (string finalCountOutput, _) = pctService.Execute(controlID, finalCountCmd, null);
                string finalCountStr = finalCountOutput.Trim();
                if (int.TryParse(finalCountStr, out int finalCount))
                {
                    if (finalCount == replicas)
                    {
                        Logger.GetLogger("install_github_runner").Printf("Pod count fixed: {0} pods running (matches desired {1})", finalCount, replicas);
                    }
                    else
                    {
                        Logger.GetLogger("install_github_runner").Printf("Warning: Pod count is {0}, expected {1}. Controller may still be reconciling.", finalCount, replicas);
                    }
                }
            }
        }
        else if (totalPods == replicas)
        {
            Logger.GetLogger("install_github_runner").Printf("Pod count is correct: {0} pods running", totalPods);
        }

        Logger.GetLogger("install_github_runner").Printf("GitHub Actions Runner Controller installation completed");
        Logger.GetLogger("install_github_runner").Printf("RunnerDeployment: {0}/{1} with {2} replicas, label: {3}", namespace_, runnerDeploymentName, replicas, runnerLabel);
        return true;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }
}

public static class InstallGithubRunnerActionFactory
{
    public static IAction NewInstallGithubRunnerAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallGithubRunnerAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}