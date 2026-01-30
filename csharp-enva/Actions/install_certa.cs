using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Enva.Libs;
using Enva.Orchestration;
using Enva.Services;

namespace Enva.Actions;

public class InstallCertAAction : BaseAction, IAction
{
    public InstallCertAAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "install certa";
    }

    public bool Execute()
    {
        if (Cfg == null)
        {
            Logger.GetLogger("install_certa").Printf("Lab configuration is missing for InstallCertAAction.");
            return false;
        }
        if (Cfg.Kubernetes == null)
        {
            Logger.GetLogger("install_certa").Printf("Kubernetes configuration is missing. Cannot install CertA.");
            return false;
        }
        if (Cfg.Services.Services == null || !Cfg.Services.Services.ContainsKey("certa"))
        {
            Logger.GetLogger("install_certa").Printf("CertA not configured, skipping installation");
            return true;
        }
        Logger.GetLogger("install_certa").Printf("Installing CertA (Certificate Authority) on Kubernetes cluster...");
        var context = Kubernetes.BuildKubernetesContext(Cfg);
        if (context == null)
        {
            Logger.GetLogger("install_certa").Printf("Failed to build Kubernetes context.");
            return false;
        }
        if (context.Control.Count == 0)
        {
            Logger.GetLogger("install_certa").Printf("No Kubernetes control node found.");
            return false;
        }
        var controlConfig = context.Control[0];
        string lxcHost = context.LXCHost();
        int controlID = controlConfig.ID;

        // Get configuration values from action properties
        string image = GetProperty<string>("image", "judyandiealvarez/certa:latest");
        int port = GetProperty<int>("port", 30081);
        // Ensure port is in valid NodePort range (30000-32767)
        if (port < 30000 || port > 32767)
        {
            Logger.GetLogger("install_certa").Printf("Port {0} is not in valid NodePort range (30000-32767), using default 30081", port);
            port = 30081;
        }
        string namespace_ = GetProperty<string>("namespace", "certa");
        int replicas = GetProperty<int>("replicas", 1);

        // Database configuration from action properties
        string databaseHost = GetProperty<string>("database_host", Cfg.PostgresHost ?? "postgres");
        int databasePort = GetProperty<int>("database_port", 5432);
        string databaseName = GetProperty<string>("database_name", "certa");
        string databaseUser = GetProperty<string>("database_user", "certa");
        string databasePassword = GetProperty<string>("database_password", "certa123");

        // Create LXCService from lxcHost and SSH config
        var lxcService = new LXCService(lxcHost, Cfg.SSH);
        if (!lxcService.Connect())
        {
            return false;
        }
        try
        {
            PCTService pctService = new PCTService(lxcService);

            // Check if kubectl is available
            string kubectlCheckCmd = "command -v kubectl  && echo installed || echo not_installed";
            (string kubectlCheck, _) = pctService.Execute(controlID, kubectlCheckCmd, null);
            if (kubectlCheck.Contains("not_installed"))
            {
                Logger.GetLogger("install_certa").Printf("Installing kubectl...");
                string installKubectlCmd = "curl -LO https://dl.k8s.io/release/$(curl -L -s https://dl.k8s.io/release/stable.txt)/bin/linux/amd64/kubectl && chmod +x kubectl && sudo mv kubectl /usr/local/bin/ && export PATH=/usr/local/bin:$PATH";
                int timeout = 120;
                pctService.Execute(controlID, installKubectlCmd, timeout);
                string verifyCmd = "test -f /usr/local/bin/kubectl && echo installed || echo not_installed";
                (string verifyOutput, _) = pctService.Execute(controlID, verifyCmd, null);
                if (verifyOutput.Contains("not_installed"))
                {
                    Logger.GetLogger("install_certa").Printf("kubectl installation failed");
                    return false;
                }
            }

            // Create namespace
            Logger.GetLogger("install_certa").Printf("Creating namespace {0}...", namespace_);
            string createNsCmd = $"kubectl create namespace {namespace_} --dry-run=client -o yaml | kubectl apply -f -";
            int timeout2 = 30;
            (string nsOutput, int? nsExit) = pctService.Execute(controlID, createNsCmd, timeout2);
            if (nsExit.HasValue && nsExit.Value != 0 && !nsOutput.Contains("already exists"))
            {
                Logger.GetLogger("install_certa").Printf("Failed to create namespace: {0}", nsOutput);
                return false;
            }

            // Create database secret
            Logger.GetLogger("install_certa").Printf("Creating database secret...");
            string secretName = "certa-db-secret";
            string secretExistsCmd = $"kubectl get secret {secretName} -n {namespace_}  && echo exists || echo not_exists";
            (string secretExists, _) = pctService.Execute(controlID, secretExistsCmd, null);
            if (secretExists.Contains("exists"))
            {
                string deleteSecretCmd = $"kubectl delete secret {secretName} -n {namespace_}";
                pctService.Execute(controlID, deleteSecretCmd, null);
            }
            string createSecretCmd = $"kubectl create secret generic {secretName} --from-literal=username={databaseUser} --from-literal=password={databasePassword} --from-literal=database={databaseName} -n {namespace_}";
            timeout2 = 30;
            (string secretOutput, int? secretExit) = pctService.Execute(controlID, createSecretCmd, timeout2);
            if (secretExit.HasValue && secretExit.Value != 0)
            {
                Logger.GetLogger("install_certa").Printf("Failed to create database secret: {0}", secretOutput);
                return false;
            }
            Logger.GetLogger("install_certa").Printf("Database secret created successfully");

            // Build connection string
            string connectionString = $"Host={databaseHost};Database={databaseName};Username={databaseUser};Password={databasePassword};Port={databasePort}";

            // Create ConfigMap for CertA configuration
            Logger.GetLogger("install_certa").Printf("Creating ConfigMap for CertA configuration...");
            string configMapName = "certa-config";
            string configMapExistsCmd = $"kubectl get configmap {configMapName} -n {namespace_}  && echo exists || echo not_exists";
            (string configMapExists, _) = pctService.Execute(controlID, configMapExistsCmd, null);
            if (configMapExists.Contains("exists"))
            {
                string deleteConfigMapCmd = $"kubectl delete configmap {configMapName} -n {namespace_}";
                pctService.Execute(controlID, deleteConfigMapCmd, null);
            }
            string createConfigMapCmd = $"kubectl create configmap {configMapName} --from-literal=ConnectionStrings__DefaultConnection=\"{connectionString}\" --from-literal=ASPNETCORE_ENVIRONMENT=Production --from-literal=ASPNETCORE_URLS=http://+:8080 -n {namespace_}";
            timeout2 = 30;
            (string configMapOutput, int? configMapExit) = pctService.Execute(controlID, createConfigMapCmd, timeout2);
            if (configMapExit.HasValue && configMapExit.Value != 0)
            {
                Logger.GetLogger("install_certa").Printf("Failed to create ConfigMap: {0}", configMapOutput);
                return false;
            }
            Logger.GetLogger("install_certa").Printf("ConfigMap created successfully");

            // Create Deployment
            Logger.GetLogger("install_certa").Printf("Creating CertA deployment...");
            string deploymentManifest = $@"apiVersion: apps/v1
kind: Deployment
metadata:
  name: certa
  namespace: {namespace_}
spec:
  replicas: {replicas}
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
        image: {image}
        ports:
        - containerPort: 8080
          name: http
        env:
        - name: ConnectionStrings__DefaultConnection
          valueFrom:
            configMapKeyRef:
              name: {configMapName}
              key: ConnectionStrings__DefaultConnection
        - name: ASPNETCORE_ENVIRONMENT
          valueFrom:
            configMapKeyRef:
              name: {configMapName}
              key: ASPNETCORE_ENVIRONMENT
        - name: ASPNETCORE_URLS
          valueFrom:
            configMapKeyRef:
              name: {configMapName}
              key: ASPNETCORE_URLS
        resources:
          requests:
            cpu: ""250m""
            memory: ""512Mi""
          limits:
            cpu: ""1""
            memory: ""1Gi""
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
";

            string applyDeploymentCmd = $@"cat > /tmp/certa-deployment.yaml << 'DEPLOYMENT_EOF'
{deploymentManifest}
DEPLOYMENT_EOF
kubectl apply -f /tmp/certa-deployment.yaml && rm -f /tmp/certa-deployment.yaml";
            timeout2 = 60;
            (string deploymentOutput, int? deploymentExit) = pctService.Execute(controlID, applyDeploymentCmd, timeout2);
            if (deploymentExit.HasValue && deploymentExit.Value != 0)
            {
                Logger.GetLogger("install_certa").Printf("Failed to create deployment: {0}", deploymentOutput);
                return false;
            }
            Logger.GetLogger("install_certa").Printf("Deployment created successfully");

            // Create Service
            Logger.GetLogger("install_certa").Printf("Creating CertA service...");
            string serviceManifest = $@"apiVersion: v1
kind: Service
metadata:
  name: certa
  namespace: {namespace_}
spec:
  type: NodePort
  selector:
    app: certa
  ports:
  - name: http
    port: 8080
    targetPort: 8080
    nodePort: {port}
    protocol: TCP
";

            string applyServiceCmd = $@"cat > /tmp/certa-service.yaml << 'SERVICE_EOF'
{serviceManifest}
SERVICE_EOF
kubectl apply -f /tmp/certa-service.yaml && rm -f /tmp/certa-service.yaml";
            timeout2 = 60;
            (string serviceOutput, int? serviceExit) = pctService.Execute(controlID, applyServiceCmd, timeout2);
            if (serviceExit.HasValue && serviceExit.Value != 0)
            {
                Logger.GetLogger("install_certa").Printf("Failed to create service: {0}", serviceOutput);
                return false;
            }
            Logger.GetLogger("install_certa").Printf("Service created successfully");

            // Wait for CertA to be ready
            Logger.GetLogger("install_certa").Printf("Waiting for CertA to be ready...");
            int maxWait = 300;
            int waitTime = 0;
            bool certaReady = false;
            while (waitTime < maxWait)
            {
                // Check if pods are running and ready
                string podsCheckCmd = $"kubectl get pods -n {namespace_} -l app=certa --field-selector=status.phase=Running -o jsonpath='{{.items[*].status.conditions[?(@.type==\"Ready\")].status}}'";
                timeout2 = 30;
                (string podsOutput, int? podsExit) = pctService.Execute(controlID, podsCheckCmd, timeout2);
                int readyCount = 0;
                if (podsExit.HasValue && podsExit.Value == 0 && !string.IsNullOrEmpty(podsOutput))
                {
                    readyCount = (podsOutput.Length - podsOutput.Replace("True", "").Length) / "True".Length;
                    if (readyCount >= replicas)
                    {
                        Logger.GetLogger("install_certa").Printf("All {0} CertA pods are ready", replicas);
                        certaReady = true;
                        break;
                    }
                }
                if (waitTime % 30 == 0)
                {
                    Logger.GetLogger("install_certa").Printf("Waiting for CertA to be ready (waited {0}/{1} seconds, found {2} ready)...", waitTime, maxWait, readyCount);
                }
                Thread.Sleep(10000);
                waitTime += 10;
            }
            if (!certaReady)
            {
                Logger.GetLogger("install_certa").Printf("CertA not ready after {0} seconds", maxWait);
                // Check final status for debugging
                string finalCheckCmd = $"kubectl get pods -n {namespace_} -l app=certa -o wide";
                timeout2 = 30;
                (string finalOutput, _) = pctService.Execute(controlID, finalCheckCmd, timeout2);
                if (!string.IsNullOrEmpty(finalOutput))
                {
                    Logger.GetLogger("install_certa").Printf("Final pod status: {0}", finalOutput);
                }
                return false;
            }

            Logger.GetLogger("install_certa").Printf("CertA installed successfully");
            Logger.GetLogger("install_certa").Printf("CertA is accessible at NodePort {0} on any cluster node", port);
            Logger.GetLogger("install_certa").Printf("Default admin credentials: admin@certa.local / Admin123!");
            return true;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }
}

public static class InstallCertAActionFactory
{
    public static IAction NewInstallCertAAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallCertAAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}