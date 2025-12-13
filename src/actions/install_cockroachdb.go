package actions

import (
	"enva/libs"
	"enva/orchestration"
	"enva/services"
	"fmt"
	"strings"
	"time"
)

// InstallCockroachdbAction installs CockroachDB on Kubernetes cluster
type InstallCockroachdbAction struct {
	*BaseAction
}

func NewInstallCockroachdbAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &InstallCockroachdbAction{
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

func (a *InstallCockroachdbAction) Description() string {
	return "install cockroachdb"
}

func (a *InstallCockroachdbAction) Execute() bool {
	if a.Cfg == nil {
		libs.GetLogger("install_cockroachdb").Printf("Lab configuration is missing for InstallCockroachdbAction.")
		return false
	}
	if a.Cfg.Kubernetes == nil {
		libs.GetLogger("install_cockroachdb").Printf("Kubernetes configuration is missing. Cannot install CockroachDB.")
		return false
	}
	if a.Cfg.Services.CockroachDB == nil {
		libs.GetLogger("install_cockroachdb").Printf("CockroachDB not configured, skipping installation")
		return true
	}
	libs.GetLogger("install_cockroachdb").Printf("Installing CockroachDB on Kubernetes cluster...")
	context := orchestration.BuildKubernetesContext(a.Cfg)
	if context == nil {
		libs.GetLogger("install_cockroachdb").Printf("Failed to build Kubernetes context.")
		return false
	}
	if len(context.Control) == 0 {
		libs.GetLogger("install_cockroachdb").Printf("No Kubernetes control node found.")
		return false
	}
	controlConfig := context.Control[0]
	lxcHost := context.LXCHost()
	controlID := controlConfig.ID
	cockroachdbCfg := a.Cfg.Services.CockroachDB
	cockroachdbVersion := "v25.2.4"
	if cockroachdbCfg.Version != nil {
		cockroachdbVersion = *cockroachdbCfg.Version
	}
	cockroachdbNodes := 3
	if cockroachdbCfg.Nodes != nil {
		cockroachdbNodes = *cockroachdbCfg.Nodes
	}
	cockroachdbStorage := "10Gi"
	if cockroachdbCfg.Storage != nil {
		cockroachdbStorage = *cockroachdbCfg.Storage
	}
	cockroachdbPassword := "root123"
	if cockroachdbCfg.Password != nil {
		cockroachdbPassword = *cockroachdbCfg.Password
	}
	sqlNodeport := 32657
	if cockroachdbCfg.SQLPort != nil {
		sqlNodeport = *cockroachdbCfg.SQLPort
	}
	httpNodeport := 30080
	if cockroachdbCfg.HTTPPort != nil {
		httpNodeport = *cockroachdbCfg.HTTPPort
	}
	grpcNodeport := 32658
	if cockroachdbCfg.GRPCPort != nil {
		grpcNodeport = *cockroachdbCfg.GRPCPort
	}
	lxcService := services.NewLXCService(lxcHost, &a.Cfg.SSH)
	if !lxcService.Connect() {
		return false
	}
	defer lxcService.Disconnect()
	pctService := services.NewPCTService(lxcService)
	libs.GetLogger("install_cockroachdb").Printf("Installing CockroachDB Operator...")
	installCrdsCmd := "kubectl apply -f https://raw.githubusercontent.com/cockroachdb/cockroach-operator/master/install/crds.yaml"
	timeout := 120
	crdsOutput, crdsExit := pctService.Execute(controlID, installCrdsCmd, &timeout)
	if crdsExit != nil && *crdsExit != 0 {
		libs.GetLogger("install_cockroachdb").Printf("Failed to install CockroachDB Operator CRDs")
		outputLen := len(crdsOutput)
		start := 0
		if outputLen > 500 {
			start = outputLen - 500
		}
		libs.GetLogger("install_cockroachdb").Printf("CRDs installation output: %s", crdsOutput[start:])
		return false
	}
	libs.GetLogger("install_cockroachdb").Printf("CockroachDB Operator CRDs installed")
	installOperatorCmd := "kubectl apply -f https://raw.githubusercontent.com/cockroachdb/cockroach-operator/master/install/operator.yaml"
	operatorOutput, operatorExit := pctService.Execute(controlID, installOperatorCmd, &timeout)
	if operatorExit != nil && *operatorExit != 0 {
		libs.GetLogger("install_cockroachdb").Printf("Failed to install CockroachDB Operator")
		outputLen := len(operatorOutput)
		start := 0
		if outputLen > 500 {
			start = outputLen - 500
		}
		libs.GetLogger("install_cockroachdb").Printf("Operator installation output: %s", operatorOutput[start:])
		return false
	}
	libs.GetLogger("install_cockroachdb").Printf("CockroachDB Operator installed")
	libs.GetLogger("install_cockroachdb").Printf("Waiting for CockroachDB Operator to be ready...")
	maxWaitOperator := 120
	waitTimeOperator := 0
	for waitTimeOperator < maxWaitOperator {
		operatorCheckCmd := "kubectl get pods -n cockroach-operator-system -o jsonpath='{.items[0].status.phase}' 2>&1"
		timeout = 30
		operatorCheck, _ := pctService.Execute(controlID, operatorCheckCmd, &timeout)
		if strings.Contains(operatorCheck, "Running") {
			readyCheckCmd := "kubectl get pods -n cockroach-operator-system -o jsonpath='{.items[0].status.conditions[?(@.type==\"Ready\")].status}' 2>&1"
			readyCheck, _ := pctService.Execute(controlID, readyCheckCmd, &timeout)
			if strings.Contains(readyCheck, "True") {
				libs.GetLogger("install_cockroachdb").Printf("CockroachDB Operator is ready")
				break
			}
		}
		libs.GetLogger("install_cockroachdb").Printf("Waiting for CockroachDB Operator to be ready (waited %d/%d seconds)...", waitTimeOperator, maxWaitOperator)
		time.Sleep(5 * time.Second)
		waitTimeOperator += 5
	}
	if waitTimeOperator >= maxWaitOperator {
		libs.GetLogger("install_cockroachdb").Printf("CockroachDB Operator not ready after %d seconds", maxWaitOperator)
		debugCmd := "kubectl get pods -n cockroach-operator-system -o wide 2>&1"
		timeout = 30
		debugOutput, _ := pctService.Execute(controlID, debugCmd, &timeout)
		if debugOutput != "" {
			libs.GetLogger("install_cockroachdb").Printf("Operator pod status: %s", debugOutput)
		}
		return false
	}
	libs.GetLogger("install_cockroachdb").Printf("Waiting for CockroachDB Operator webhook to be ready...")
	maxWaitWebhook := 60
	waitTimeWebhook := 0
	for waitTimeWebhook < maxWaitWebhook {
		webhookCheckCmd := "kubectl get endpoints cockroach-operator-webhook-service -n cockroach-operator-system -o jsonpath='{.subsets[0].addresses[0].ip}' 2>&1"
		timeout = 30
		webhookCheck, _ := pctService.Execute(controlID, webhookCheckCmd, &timeout)
		if webhookCheck != "" && strings.TrimSpace(webhookCheck) != "" {
			libs.GetLogger("install_cockroachdb").Printf("CockroachDB Operator webhook is ready")
			break
		}
		libs.GetLogger("install_cockroachdb").Printf("Waiting for CockroachDB Operator webhook to be ready (waited %d/%d seconds)...", waitTimeWebhook, maxWaitWebhook)
		time.Sleep(5 * time.Second)
		waitTimeWebhook += 5
	}
	libs.GetLogger("install_cockroachdb").Printf("Creating CockroachDB cluster...")
	cockroachdbManifest := fmt.Sprintf(`apiVersion: crdb.cockroachlabs.com/v1alpha1
kind: CrdbCluster
metadata:
  name: cockroachdb
spec:
  dataStore:
    pvc:
      spec:
        accessModes:
          - ReadWriteOnce
        resources:
          requests:
            storage: "%s"
        volumeMode: Filesystem
  resources:
    requests:
      cpu: 500m
      memory: 2Gi
    limits:
      cpu: 2
      memory: 8Gi
  tlsEnabled: true
  image:
    name: cockroachdb/cockroach:%s
  nodes: %d
`, cockroachdbStorage, cockroachdbVersion, cockroachdbNodes)
	libs.GetLogger("install_cockroachdb").Printf("Creating CockroachDB cluster (with retry)...")
	maxRetries := 3
	retryDelay := 10
	for attempt := 0; attempt < maxRetries; attempt++ {
		applyCmd := fmt.Sprintf("kubectl apply -f - << 'EOF'\n%sEOF", cockroachdbManifest)
		timeout = 60
		applyOutput, applyExit := pctService.Execute(controlID, applyCmd, &timeout)
		if applyExit != nil && *applyExit == 0 {
			libs.GetLogger("install_cockroachdb").Printf("CockroachDB cluster created successfully")
			break
		}
		if attempt < maxRetries-1 {
			libs.GetLogger("install_cockroachdb").Printf("Failed to create CockroachDB cluster (attempt %d/%d), retrying in %d seconds...", attempt+1, maxRetries, retryDelay)
			if strings.Contains(strings.ToLower(applyOutput), "webhook") {
				libs.GetLogger("install_cockroachdb").Printf("Webhook error detected, waiting for webhook to be ready...")
			}
			time.Sleep(time.Duration(retryDelay) * time.Second)
		} else {
			libs.GetLogger("install_cockroachdb").Printf("Failed to create CockroachDB cluster after %d attempts", maxRetries)
			outputLen := len(applyOutput)
			start := 0
			if outputLen > 500 {
				start = outputLen - 500
			}
			libs.GetLogger("install_cockroachdb").Printf("Cluster creation output: %s", applyOutput[start:])
			return false
		}
	}
	libs.GetLogger("install_cockroachdb").Printf("Waiting for CockroachDB cluster to be ready...")
	maxWaitCluster := 300
	waitTimeCluster := 0
	for waitTimeCluster < maxWaitCluster {
		clusterStatusCmd := "kubectl get crdbcluster cockroachdb -o jsonpath='{.status.clusterStatus}' 2>&1"
		timeout = 30
		clusterStatus, _ := pctService.Execute(controlID, clusterStatusCmd, &timeout)
		if strings.Contains(clusterStatus, "Finished") {
			libs.GetLogger("install_cockroachdb").Printf("CockroachDB cluster is ready")
			break
		}
		podsReadyCmd := "kubectl get pods -l app.kubernetes.io/instance=cockroachdb -o jsonpath='{.items[*].status.phase}' 2>&1"
		podsStatus, _ := pctService.Execute(controlID, podsReadyCmd, &timeout)
		if podsStatus != "" {
			runningCount := strings.Count(podsStatus, "Running")
			if runningCount == cockroachdbNodes {
				libs.GetLogger("install_cockroachdb").Printf("All CockroachDB pods are running")
				break
			}
		}
		libs.GetLogger("install_cockroachdb").Printf("Waiting for CockroachDB cluster to be ready (waited %d/%d seconds)...", waitTimeCluster, maxWaitCluster)
		time.Sleep(10 * time.Second)
		waitTimeCluster += 10
	}
	libs.GetLogger("install_cockroachdb").Printf("Setting root user password for Admin UI access...")
	setPasswordCmd := fmt.Sprintf("kubectl exec cockroachdb-0 -- ./cockroach sql --certs-dir=/cockroach/cockroach-certs --host=cockroachdb-public -e \"ALTER USER root WITH PASSWORD '%s';\" 2>&1", cockroachdbPassword)
	timeout = 60
	passwordOutput, passwordExit := pctService.Execute(controlID, setPasswordCmd, &timeout)
	if passwordExit != nil && *passwordExit != 0 {
		outputLen := len(passwordOutput)
		start := 0
		if outputLen > 200 {
			start = outputLen - 200
		}
		libs.GetLogger("install_cockroachdb").Printf("Failed to set root password: %s", passwordOutput[start:])
	} else {
		libs.GetLogger("install_cockroachdb").Printf("Root user password set successfully")
	}
	libs.GetLogger("install_cockroachdb").Printf("Creating NodePort service for external access...")
	nodeportServiceManifest := fmt.Sprintf(`apiVersion: v1
kind: Service
metadata:
  name: cockroachdb-external
  labels:
    app.kubernetes.io/instance: cockroachdb
spec:
  type: NodePort
  selector:
    app.kubernetes.io/component: database
    app.kubernetes.io/instance: cockroachdb
    app.kubernetes.io/name: cockroachdb
  ports:
  - name: sql
    port: 26257
    targetPort: 26257
    nodePort: %d
    protocol: TCP
  - name: http
    port: 8080
    targetPort: 8080
    nodePort: %d
    protocol: TCP
  - name: grpc
    port: 26258
    targetPort: 26258
    nodePort: %d
    protocol: TCP
`, sqlNodeport, httpNodeport, grpcNodeport)
	applySvcCmd := fmt.Sprintf("kubectl apply -f - << 'EOF'\n%sEOF", nodeportServiceManifest)
	timeout = 60
	applySvcOutput, applySvcExit := pctService.Execute(controlID, applySvcCmd, &timeout)
	if applySvcExit != nil && *applySvcExit != 0 {
		outputLen := len(applySvcOutput)
		start := 0
		if outputLen > 200 {
			start = outputLen - 200
		}
		libs.GetLogger("install_cockroachdb").Printf("Failed to create NodePort service: %s", applySvcOutput[start:])
	} else {
		libs.GetLogger("install_cockroachdb").Printf("CockroachDB NodePort service created (SQL: %d, HTTP: %d, gRPC: %d)", sqlNodeport, httpNodeport, grpcNodeport)
	}
	libs.GetLogger("install_cockroachdb").Printf("CockroachDB installed successfully")
	return true
}
