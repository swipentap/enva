using System;
using System.Linq;
using System.Threading;
using Enva.Libs;
using Enva.Orchestration;
using Enva.Services;

namespace Enva.Actions;

public class InstallCockroachdbAction : BaseAction, IAction
{
    public InstallCockroachdbAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "install cockroachdb";
    }

    public bool Execute()
    {
        if (Cfg == null)
        {
            Logger.GetLogger("install_cockroachdb").Printf("Lab configuration is missing for InstallCockroachdbAction.");
            return false;
        }
        if (Cfg.Kubernetes == null)
        {
            Logger.GetLogger("install_cockroachdb").Printf("Kubernetes configuration is missing. Cannot install CockroachDB.");
            return false;
        }
        if (Cfg.Services.Services == null || !Cfg.Services.Services.ContainsKey("cockroachdb"))
        {
            Logger.GetLogger("install_cockroachdb").Printf("CockroachDB not configured, skipping installation");
            return true;
        }
        Logger.GetLogger("install_cockroachdb").Printf("Installing CockroachDB on Kubernetes cluster...");
        var context = Kubernetes.BuildKubernetesContext(Cfg);
        if (context == null)
        {
            Logger.GetLogger("install_cockroachdb").Printf("Failed to build Kubernetes context.");
            return false;
        }
        if (context.Control.Count == 0)
        {
            Logger.GetLogger("install_cockroachdb").Printf("No Kubernetes control node found.");
            return false;
        }
        var controlConfig = context.Control[0];
        string lxcHost = context.LXCHost();
        int controlID = controlConfig.ID;
        // Get configuration from action properties
        string cockroachdbVersion = GetProperty<string>("version", "v25.2.4");
        int cockroachdbNodes = GetProperty<int>("nodes", 3);
        string cockroachdbStorage = GetProperty<string>("storage", "10Gi");
        string cockroachdbPassword = GetProperty<string>("password", "root123");
        int sqlNodeport = GetProperty<int>("sql_port", 32657);
        int httpNodeport = GetProperty<int>("http_port", 30080);
        int grpcNodeport = GetProperty<int>("grpc_port", 32658);

        var lxcService = new LXCService(lxcHost, Cfg.SSH);
        if (!lxcService.Connect())
        {
            return false;
        }

        try
        {
            var pctService = new PCTService(lxcService);
            Logger.GetLogger("install_cockroachdb").Printf("Installing CockroachDB Operator...");
            string installCrdsCmd = "kubectl apply -f https://raw.githubusercontent.com/cockroachdb/cockroach-operator/master/install/crds.yaml";
            int timeout = 120;
            (string crdsOutput, int? crdsExit) = pctService.Execute(controlID, installCrdsCmd, timeout);
            if (crdsExit.HasValue && crdsExit.Value != 0)
            {
                Logger.GetLogger("install_cockroachdb").Printf("Failed to install CockroachDB Operator CRDs");
                int outputLen = crdsOutput.Length;
                int start = outputLen > 500 ? outputLen - 500 : 0;
                Logger.GetLogger("install_cockroachdb").Printf("CRDs installation output: {0}", crdsOutput.Substring(start));
                return false;
            }
            Logger.GetLogger("install_cockroachdb").Printf("CockroachDB Operator CRDs installed");
            string installOperatorCmd = "kubectl apply -f https://raw.githubusercontent.com/cockroachdb/cockroach-operator/master/install/operator.yaml";
            (string operatorOutput, int? operatorExit) = pctService.Execute(controlID, installOperatorCmd, timeout);
            if (operatorExit.HasValue && operatorExit.Value != 0)
            {
                Logger.GetLogger("install_cockroachdb").Printf("Failed to install CockroachDB Operator");
                int outputLen = operatorOutput.Length;
                int start = outputLen > 500 ? outputLen - 500 : 0;
                Logger.GetLogger("install_cockroachdb").Printf("Operator installation output: {0}", operatorOutput.Substring(start));
                return false;
            }
            Logger.GetLogger("install_cockroachdb").Printf("CockroachDB Operator installed");
            Logger.GetLogger("install_cockroachdb").Printf("Waiting for CockroachDB Operator to be ready...");
            int maxWaitOperator = 120;
            int waitTimeOperator = 0;
            while (waitTimeOperator < maxWaitOperator)
            {
                string operatorCheckCmd = "kubectl get pods -n cockroach-operator-system -o jsonpath='{.items[0].status.phase}'";
                timeout = 30;
                (string operatorCheck, _) = pctService.Execute(controlID, operatorCheckCmd, timeout);
                if (operatorCheck.Contains("Running"))
                {
                    string readyCheckCmd = "kubectl get pods -n cockroach-operator-system -o jsonpath='{.items[0].status.conditions[?(@.type==\"Ready\")].status}'";
                    (string readyCheck, _) = pctService.Execute(controlID, readyCheckCmd, timeout);
                    if (readyCheck.Contains("True"))
                    {
                        Logger.GetLogger("install_cockroachdb").Printf("CockroachDB Operator is ready");
                        break;
                    }
                }
                Logger.GetLogger("install_cockroachdb").Printf("Waiting for CockroachDB Operator to be ready (waited {0}/{1} seconds)...", waitTimeOperator, maxWaitOperator);
                Thread.Sleep(5000);
                waitTimeOperator += 5;
            }
            if (waitTimeOperator >= maxWaitOperator)
            {
                Logger.GetLogger("install_cockroachdb").Printf("CockroachDB Operator not ready after {0} seconds", maxWaitOperator);
                string debugCmd = "kubectl get pods -n cockroach-operator-system -o wide";
                timeout = 30;
                (string debugOutput, _) = pctService.Execute(controlID, debugCmd, timeout);
                if (!string.IsNullOrEmpty(debugOutput))
                {
                    Logger.GetLogger("install_cockroachdb").Printf("Operator pod status: {0}", debugOutput);
                }
                return false;
            }
            Logger.GetLogger("install_cockroachdb").Printf("Waiting for CockroachDB Operator webhook to be ready...");
            int maxWaitWebhook = 60;
            int waitTimeWebhook = 0;
            while (waitTimeWebhook < maxWaitWebhook)
            {
                string webhookCheckCmd = "kubectl get endpoints cockroach-operator-webhook-service -n cockroach-operator-system -o jsonpath='{.subsets[0].addresses[0].ip}'";
                timeout = 30;
                (string webhookCheck, _) = pctService.Execute(controlID, webhookCheckCmd, timeout);
                if (!string.IsNullOrEmpty(webhookCheck) && !string.IsNullOrWhiteSpace(webhookCheck))
                {
                    Logger.GetLogger("install_cockroachdb").Printf("CockroachDB Operator webhook is ready");
                    break;
                }
                Logger.GetLogger("install_cockroachdb").Printf("Waiting for CockroachDB Operator webhook to be ready (waited {0}/{1} seconds)...", waitTimeWebhook, maxWaitWebhook);
                Thread.Sleep(5000);
                waitTimeWebhook += 5;
            }
            Logger.GetLogger("install_cockroachdb").Printf("Creating CockroachDB cluster...");
            string cockroachdbManifest = $@"apiVersion: crdb.cockroachlabs.com/v1alpha1
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
            storage: ""{cockroachdbStorage}""
        volumeMode: Filesystem
  resources:
    requests:
      cpu: 500m
      memory: 512Mi
    limits:
      cpu: 2
      memory: 2Gi
  tlsEnabled: true
  image:
    name: cockroachdb/cockroach:{cockroachdbVersion}
  nodes: {cockroachdbNodes}
";
            Logger.GetLogger("install_cockroachdb").Printf("Creating CockroachDB cluster (with retry)...");
            int maxRetries = 3;
            int retryDelay = 10;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                string applyCmd = $"kubectl apply -f - << 'EOF'\n{cockroachdbManifest}EOF";
                timeout = 60;
                (string applyOutput, int? applyExit) = pctService.Execute(controlID, applyCmd, timeout);
                if (applyExit.HasValue && applyExit.Value == 0)
                {
                    Logger.GetLogger("install_cockroachdb").Printf("CockroachDB cluster created successfully");
                    break;
                }
                if (attempt < maxRetries - 1)
                {
                    Logger.GetLogger("install_cockroachdb").Printf("Failed to create CockroachDB cluster (attempt {0}/{1}), retrying in {2} seconds...", attempt + 1, maxRetries, retryDelay);
                    if (applyOutput.ToLower().Contains("webhook"))
                    {
                        Logger.GetLogger("install_cockroachdb").Printf("Webhook error detected, waiting for webhook to be ready...");
                    }
                    Thread.Sleep(retryDelay * 1000);
                }
                else
                {
                    Logger.GetLogger("install_cockroachdb").Printf("Failed to create CockroachDB cluster after {0} attempts", maxRetries);
                    int outputLen = applyOutput.Length;
                    int start = outputLen > 500 ? outputLen - 500 : 0;
                    Logger.GetLogger("install_cockroachdb").Printf("Cluster creation output: {0}", applyOutput.Substring(start));
                    return false;
                }
            }
            Logger.GetLogger("install_cockroachdb").Printf("Waiting for CockroachDB cluster to be ready...");
            int maxWaitCluster = 300;
            int waitTimeCluster = 0;
            while (waitTimeCluster < maxWaitCluster)
            {
                string clusterStatusCmd = "kubectl get crdbcluster cockroachdb -o jsonpath='{.status.clusterStatus}'";
                timeout = 30;
                (string clusterStatus, _) = pctService.Execute(controlID, clusterStatusCmd, timeout);
                if (clusterStatus.Contains("Finished"))
                {
                    Logger.GetLogger("install_cockroachdb").Printf("CockroachDB cluster is ready");
                    break;
                }
                string podsReadyCmd = "kubectl get pods -l app.kubernetes.io/instance=cockroachdb -o jsonpath='{.items[*].status.phase}'";
                (string podsStatus, _) = pctService.Execute(controlID, podsReadyCmd, timeout);
                if (!string.IsNullOrEmpty(podsStatus))
                {
                    // Count occurrences of "Running" in the status string
                    int runningCount = (podsStatus.Length - podsStatus.Replace("Running", "").Length) / "Running".Length;
                    if (runningCount == cockroachdbNodes)
                    {
                        Logger.GetLogger("install_cockroachdb").Printf("All CockroachDB pods are running");
                        break;
                    }
                }
                Logger.GetLogger("install_cockroachdb").Printf("Waiting for CockroachDB cluster to be ready (waited {0}/{1} seconds)...", waitTimeCluster, maxWaitCluster);
                Thread.Sleep(10000);
                waitTimeCluster += 10;
            }
            Logger.GetLogger("install_cockroachdb").Printf("Setting root user password for Admin UI access...");
            string setPasswordCmd = $"kubectl exec cockroachdb-0 -- ./cockroach sql --certs-dir=/cockroach/cockroach-certs --host=cockroachdb-public -e \"ALTER USER root WITH PASSWORD '{cockroachdbPassword}';\"";
            timeout = 60;
            (string passwordOutput, int? passwordExit) = pctService.Execute(controlID, setPasswordCmd, timeout);
            if (passwordExit.HasValue && passwordExit.Value != 0)
            {
                int outputLen = passwordOutput.Length;
                int start = outputLen > 200 ? outputLen - 200 : 0;
                Logger.GetLogger("install_cockroachdb").Printf("Failed to set root password: {0}", passwordOutput.Substring(start));
            }
            else
            {
                Logger.GetLogger("install_cockroachdb").Printf("Root user password set successfully");
            }
            Logger.GetLogger("install_cockroachdb").Printf("Creating NodePort service for external access...");
            string nodeportServiceManifest = $@"apiVersion: v1
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
    nodePort: {sqlNodeport}
    protocol: TCP
  - name: http
    port: 8080
    targetPort: 8080
    nodePort: {httpNodeport}
    protocol: TCP
  - name: grpc
    port: 26258
    targetPort: 26258
    nodePort: {grpcNodeport}
    protocol: TCP
";
            string applySvcCmd = $"kubectl apply -f - << 'EOF'\n{nodeportServiceManifest}EOF";
            timeout = 60;
            (string applySvcOutput, int? applySvcExit) = pctService.Execute(controlID, applySvcCmd, timeout);
            if (applySvcExit.HasValue && applySvcExit.Value != 0)
            {
                int outputLen = applySvcOutput.Length;
                int start = outputLen > 200 ? outputLen - 200 : 0;
                Logger.GetLogger("install_cockroachdb").Printf("Failed to create NodePort service: {0}", applySvcOutput.Substring(start));
            }
            else
            {
                Logger.GetLogger("install_cockroachdb").Printf("CockroachDB NodePort service created (SQL: {0}, HTTP: {1}, gRPC: {2})", sqlNodeport, httpNodeport, grpcNodeport);
            }
            Logger.GetLogger("install_cockroachdb").Printf("CockroachDB installed successfully");
            return true;
        }
        finally
        {
            lxcService.Disconnect();
        }
    }
}

public static class InstallCockroachdbActionFactory
{
    public static IAction NewInstallCockroachdbAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallCockroachdbAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}