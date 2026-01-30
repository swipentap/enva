package actions

import (
	"enva/libs"
	"enva/orchestration"
	"enva/services"
	"fmt"
	"strings"
	"time"
)

// InstallCertAAction installs CertA (Certificate Authority) on Kubernetes cluster
type InstallCertAAction struct {
	*BaseAction
}

func NewInstallCertAAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &InstallCertAAction{
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

func (a *InstallCertAAction) Description() string {
	return "install certa"
}

func (a *InstallCertAAction) Execute() bool {
	if a.Cfg == nil {
		libs.GetLogger("install_certa").Printf("Lab configuration is missing for InstallCertAAction.")
		return false
	}
	if a.Cfg.Kubernetes == nil {
		libs.GetLogger("install_certa").Printf("Kubernetes configuration is missing. Cannot install CertA.")
		return false
	}
	if a.Cfg.Services.CertA == nil {
		libs.GetLogger("install_certa").Printf("CertA not configured, skipping installation")
		return true
	}
	libs.GetLogger("install_certa").Printf("Installing CertA (Certificate Authority) on Kubernetes cluster...")
	context := orchestration.BuildKubernetesContext(a.Cfg)
	if context == nil {
		libs.GetLogger("install_certa").Printf("Failed to build Kubernetes context.")
		return false
	}
	if len(context.Control) == 0 {
		libs.GetLogger("install_certa").Printf("No Kubernetes control node found.")
		return false
	}
	controlConfig := context.Control[0]
	lxcHost := context.LXCHost()
	controlID := controlConfig.ID
	certaCfg := a.Cfg.Services.CertA

	// Get configuration values with defaults
	image := "judyandiealvarez/certa:latest"
	if certaCfg.Image != nil {
		image = *certaCfg.Image
	}

	port := 30080
	if certaCfg.Port != nil {
		port = *certaCfg.Port
		// Ensure port is in valid NodePort range (30000-32767)
		if port < 30000 || port > 32767 {
			libs.GetLogger("install_certa").Printf("Port %d is not in valid NodePort range (30000-32767), using default 30080", port)
			port = 30080
		}
	}

	namespace := "certa"
	if certaCfg.Namespace != nil {
		namespace = *certaCfg.Namespace
	}

	replicas := 1
	if certaCfg.Replicas != nil {
		replicas = *certaCfg.Replicas
	}

	// Database configuration
	databaseHost := "postgres"
	if certaCfg.DatabaseHost != nil {
		databaseHost = *certaCfg.DatabaseHost
	} else if a.Cfg.Services.PostgreSQL != nil && a.Cfg.PostgresHost != nil {
		// Use configured PostgreSQL host if available
		databaseHost = *a.Cfg.PostgresHost
	}

	databasePort := 5432
	if certaCfg.DatabasePort != nil {
		databasePort = *certaCfg.DatabasePort
	} else if a.Cfg.Services.PostgreSQL != nil && a.Cfg.Services.PostgreSQL.Port != nil {
		databasePort = *a.Cfg.Services.PostgreSQL.Port
	}

	databaseName := "certa"
	if certaCfg.DatabaseName != nil {
		databaseName = *certaCfg.DatabaseName
	}

	databaseUser := "certa"
	if certaCfg.DatabaseUser != nil {
		databaseUser = *certaCfg.DatabaseUser
	} else if a.Cfg.Services.PostgreSQL != nil && a.Cfg.Services.PostgreSQL.Username != nil {
		databaseUser = *a.Cfg.Services.PostgreSQL.Username
	}

	databasePassword := "certa123"
	if certaCfg.DatabasePassword != nil {
		databasePassword = *certaCfg.DatabasePassword
	} else if a.Cfg.Services.PostgreSQL != nil && a.Cfg.Services.PostgreSQL.Password != nil {
		databasePassword = *a.Cfg.Services.PostgreSQL.Password
	}

	lxcService := services.NewLXCService(lxcHost, &a.Cfg.SSH)
	if !lxcService.Connect() {
		return false
	}
	defer lxcService.Disconnect()
	pctService := services.NewPCTService(lxcService)

	// Check if kubectl is available
	kubectlCheckCmd := "command -v kubectl  && echo installed || echo not_installed"
	kubectlCheck, _ := pctService.Execute(controlID, kubectlCheckCmd, nil)
	if strings.Contains(kubectlCheck, "not_installed") {
		libs.GetLogger("install_certa").Printf("Installing kubectl...")
		installKubectlCmd := "curl -LO https://dl.k8s.io/release/$(curl -L -s https://dl.k8s.io/release/stable.txt)/bin/linux/amd64/kubectl && chmod +x kubectl && sudo mv kubectl /usr/local/bin/ && export PATH=/usr/local/bin:$PATH"
		timeout := 120
		pctService.Execute(controlID, installKubectlCmd, &timeout)
		verifyCmd := "test -f /usr/local/bin/kubectl && echo installed || echo not_installed"
		verifyOutput, _ := pctService.Execute(controlID, verifyCmd, nil)
		if strings.Contains(verifyOutput, "not_installed") {
			libs.GetLogger("install_certa").Printf("kubectl installation failed")
			return false
		}
	}

	// Create namespace
	libs.GetLogger("install_certa").Printf("Creating namespace %s...", namespace)
	createNsCmd := fmt.Sprintf("kubectl create namespace %s --dry-run=client -o yaml | kubectl apply -f -", namespace)
	timeout := 30
	nsOutput, nsExit := pctService.Execute(controlID, createNsCmd, &timeout)
	if nsExit != nil && *nsExit != 0 && !strings.Contains(nsOutput, "already exists") {
		libs.GetLogger("install_certa").Printf("Failed to create namespace: %s", nsOutput)
		return false
	}

	// Create database secret
	libs.GetLogger("install_certa").Printf("Creating database secret...")
	secretName := "certa-db-secret"
	secretExistsCmd := fmt.Sprintf("kubectl get secret %s -n %s  && echo exists || echo not_exists", secretName, namespace)
	secretExists, _ := pctService.Execute(controlID, secretExistsCmd, nil)
	if strings.Contains(secretExists, "exists") {
		deleteSecretCmd := fmt.Sprintf("kubectl delete secret %s -n %s", secretName, namespace)
		pctService.Execute(controlID, deleteSecretCmd, nil)
	}
	createSecretCmd := fmt.Sprintf("kubectl create secret generic %s --from-literal=username=%s --from-literal=password=%s --from-literal=database=%s -n %s", secretName, databaseUser, databasePassword, databaseName, namespace)
	timeout = 30
	secretOutput, secretExit := pctService.Execute(controlID, createSecretCmd, &timeout)
	if secretExit != nil && *secretExit != 0 {
		libs.GetLogger("install_certa").Printf("Failed to create database secret: %s", secretOutput)
		return false
	}
	libs.GetLogger("install_certa").Printf("Database secret created successfully")

	// Build connection string
	connectionString := fmt.Sprintf("Host=%s;Database=%s;Username=%s;Password=%s;Port=%d", databaseHost, databaseName, databaseUser, databasePassword, databasePort)

	// Create ConfigMap for CertA configuration
	libs.GetLogger("install_certa").Printf("Creating ConfigMap for CertA configuration...")
	configMapName := "certa-config"
	configMapExistsCmd := fmt.Sprintf("kubectl get configmap %s -n %s  && echo exists || echo not_exists", configMapName, namespace)
	configMapExists, _ := pctService.Execute(controlID, configMapExistsCmd, nil)
	if strings.Contains(configMapExists, "exists") {
		deleteConfigMapCmd := fmt.Sprintf("kubectl delete configmap %s -n %s", configMapName, namespace)
		pctService.Execute(controlID, deleteConfigMapCmd, nil)
	}
	createConfigMapCmd := fmt.Sprintf(`kubectl create configmap %s \
		--from-literal=ConnectionStrings__DefaultConnection="%s" \
		--from-literal=ASPNETCORE_ENVIRONMENT=Production \
		--from-literal=ASPNETCORE_URLS=http://+:8080 \
		-n %s`, configMapName, connectionString, namespace)
	timeout = 30
	configMapOutput, configMapExit := pctService.Execute(controlID, createConfigMapCmd, &timeout)
	if configMapExit != nil && *configMapExit != 0 {
		libs.GetLogger("install_certa").Printf("Failed to create ConfigMap: %s", configMapOutput)
		return false
	}
	libs.GetLogger("install_certa").Printf("ConfigMap created successfully")

	// Create Deployment
	libs.GetLogger("install_certa").Printf("Creating CertA deployment...")
	deploymentManifest := fmt.Sprintf(`apiVersion: apps/v1
kind: Deployment
metadata:
  name: certa
  namespace: %s
spec:
  replicas: %d
  selector:
    matchLabels:
      app: certa
  template:
    metadata:
      labels:
        app: certa
    spec:
      containers:
      - name: certa
        image: %s
        ports:
        - containerPort: 8080
          name: http
        env:
        - name: ConnectionStrings__DefaultConnection
          valueFrom:
            configMapKeyRef:
              name: %s
              key: ConnectionStrings__DefaultConnection
        - name: ASPNETCORE_ENVIRONMENT
          valueFrom:
            configMapKeyRef:
              name: %s
              key: ASPNETCORE_ENVIRONMENT
        - name: ASPNETCORE_URLS
          valueFrom:
            configMapKeyRef:
              name: %s
              key: ASPNETCORE_URLS
        resources:
          requests:
            cpu: "250m"
            memory: "512Mi"
          limits:
            cpu: "1"
            memory: "1Gi"
        livenessProbe:
          httpGet:
            path: /
            port: 8080
          initialDelaySeconds: 30
          periodSeconds: 10
        readinessProbe:
          httpGet:
            path: /
            port: 8080
          initialDelaySeconds: 10
          periodSeconds: 5
`, namespace, replicas, image, configMapName, configMapName, configMapName)

	applyDeploymentCmd := fmt.Sprintf(`cat > /tmp/certa-deployment.yaml << 'DEPLOYMENT_EOF'
%s
DEPLOYMENT_EOF
kubectl apply -f /tmp/certa-deployment.yaml && rm -f /tmp/certa-deployment.yaml`, deploymentManifest)
	timeout = 60
	deploymentOutput, deploymentExit := pctService.Execute(controlID, applyDeploymentCmd, &timeout)
	if deploymentExit != nil && *deploymentExit != 0 {
		libs.GetLogger("install_certa").Printf("Failed to create deployment: %s", deploymentOutput)
		return false
	}
	libs.GetLogger("install_certa").Printf("Deployment created successfully")

	// Create Service
	libs.GetLogger("install_certa").Printf("Creating CertA service...")
	serviceManifest := fmt.Sprintf(`apiVersion: v1
kind: Service
metadata:
  name: certa
  namespace: %s
spec:
  type: NodePort
  selector:
    app: certa
  ports:
  - name: http
    port: 8080
    targetPort: 8080
    nodePort: %d
    protocol: TCP
`, namespace, port)

	applyServiceCmd := fmt.Sprintf(`cat > /tmp/certa-service.yaml << 'SERVICE_EOF'
%s
SERVICE_EOF
kubectl apply -f /tmp/certa-service.yaml && rm -f /tmp/certa-service.yaml`, serviceManifest)
	timeout = 60
	serviceOutput, serviceExit := pctService.Execute(controlID, applyServiceCmd, &timeout)
	if serviceExit != nil && *serviceExit != 0 {
		libs.GetLogger("install_certa").Printf("Failed to create service: %s", serviceOutput)
		return false
	}
	libs.GetLogger("install_certa").Printf("Service created successfully")

	// Wait for CertA to be ready
	libs.GetLogger("install_certa").Printf("Waiting for CertA to be ready...")
	maxWait := 300
	waitTime := 0
	certaReady := false
	for waitTime < maxWait {
		// Check if pods are running and ready
		podsCheckCmd := fmt.Sprintf("kubectl get pods -n %s -l app=certa --field-selector=status.phase=Running -o jsonpath='{.items[*].status.conditions[?(@.type==\"Ready\")].status}'", namespace)
		timeout = 30
		podsOutput, podsExit := pctService.Execute(controlID, podsCheckCmd, &timeout)
		readyCount := 0
		if podsExit != nil && *podsExit == 0 && podsOutput != "" {
			readyCount = strings.Count(podsOutput, "True")
			if readyCount >= replicas {
				libs.GetLogger("install_certa").Printf("All %d CertA pods are ready", replicas)
				certaReady = true
				break
			}
		}
		if waitTime%30 == 0 {
			libs.GetLogger("install_certa").Printf("Waiting for CertA to be ready (waited %d/%d seconds, found %d ready)...", waitTime, maxWait, readyCount)
		}
		time.Sleep(10 * time.Second)
		waitTime += 10
	}
	if !certaReady {
		libs.GetLogger("install_certa").Printf("CertA not ready after %d seconds", maxWait)
		// Check final status for debugging
		finalCheckCmd := fmt.Sprintf("kubectl get pods -n %s -l app=certa -o wide", namespace)
		timeout = 30
		finalOutput, _ := pctService.Execute(controlID, finalCheckCmd, &timeout)
		if finalOutput != "" {
			libs.GetLogger("install_certa").Printf("Final pod status: %s", finalOutput)
		}
		return false
	}

	libs.GetLogger("install_certa").Printf("CertA installed successfully")
	libs.GetLogger("install_certa").Printf("CertA is accessible at NodePort %d on any cluster node", port)
	libs.GetLogger("install_certa").Printf("Default admin credentials: admin@certa.local / Admin123!")
	return true
}







