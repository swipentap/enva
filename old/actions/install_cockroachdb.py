"""
Install CockroachDB action
"""
import logging
import time
from orchestration.kubernetes import KubernetesDeployContext, _build_kubernetes_context
from services.lxc import LXCService
from services.pct import PCTService
from .base import Action

logger = logging.getLogger(__name__)

class InstallCockroachdbAction(Action):
    """Action to install CockroachDB on Kubernetes cluster"""
    description = "install cockroachdb"

    def execute(self) -> bool:
        """Execute CockroachDB installation."""
        if not self.cfg:
            logger.error("Lab configuration is missing for InstallCockroachdbAction.")
            return False

        if not self.cfg.kubernetes:
            logger.error("Kubernetes configuration is missing. Cannot install CockroachDB.")
            return False

        if not self.cfg.services.cockroachdb:
            logger.info("CockroachDB not configured, skipping installation")
            return True

        logger.info("Installing CockroachDB on Kubernetes cluster...")
        
        # Build Kubernetes context
        context = _build_kubernetes_context(self.cfg)
        if not context:
            logger.error("Failed to build Kubernetes context.")
            return False

        # Get control node
        if not context.control:
            logger.error("No Kubernetes control node found.")
            return False
        
        control_config = context.control[0]
        proxmox_host = context.proxmox_host
        cfg = self.cfg
        control_id = control_config.id
        cockroachdb_cfg = cfg.services.cockroachdb
        cockroachdb_version = getattr(cockroachdb_cfg, 'version', None) or "v25.2.4"
        cockroachdb_nodes = int(getattr(cockroachdb_cfg, 'nodes', None) or 3)
        cockroachdb_storage = getattr(cockroachdb_cfg, 'storage', None) or "10Gi"
        cockroachdb_password = getattr(cockroachdb_cfg, 'password', None) or "root123"
        sql_nodeport = int(getattr(cockroachdb_cfg, 'sql_port', None) or 32657)
        http_nodeport = int(getattr(cockroachdb_cfg, 'http_port', None) or 30080)
        grpc_nodeport = int(getattr(cockroachdb_cfg, 'grpc_port', None) or 32658)
        
        lxc_service = LXCService(proxmox_host, cfg.ssh)
        if not lxc_service.connect():
            return False
        try:
            pct_service = PCTService(lxc_service)
            logger.info("Installing CockroachDB Operator...")
            
            # Install CockroachDB Operator CRDs
            install_crds_cmd = "kubectl apply -f https://raw.githubusercontent.com/cockroachdb/cockroach-operator/master/install/crds.yaml"
            crds_output, crds_exit = pct_service.execute(str(control_id), install_crds_cmd, timeout=120)
            if crds_exit != 0:
                logger.error("Failed to install CockroachDB Operator CRDs")
                if crds_output:
                    logger.error("CRDs installation output: %s", crds_output[-500:])
                return False
            logger.info("CockroachDB Operator CRDs installed")
            
            # Install CockroachDB Operator
            install_operator_cmd = "kubectl apply -f https://raw.githubusercontent.com/cockroachdb/cockroach-operator/master/install/operator.yaml"
            operator_output, operator_exit = pct_service.execute(str(control_id), install_operator_cmd, timeout=120)
            if operator_exit != 0:
                logger.error("Failed to install CockroachDB Operator")
                if operator_output:
                    logger.error("Operator installation output: %s", operator_output[-500:])
                return False
            logger.info("CockroachDB Operator installed")
            
            # Wait for operator to be ready
            logger.info("Waiting for CockroachDB Operator to be ready...")
            max_wait_operator = 120
            wait_time_operator = 0
            while wait_time_operator < max_wait_operator:
                # Check if any pod in the namespace is Running
                operator_check_cmd = "kubectl get pods -n cockroach-operator-system -o jsonpath='{.items[0].status.phase}' 2>&1"
                operator_check, _ = pct_service.execute(str(control_id), operator_check_cmd, timeout=30)
                if operator_check and "Running" in operator_check:
                    # Also verify it's actually ready (not just Running)
                    ready_check_cmd = "kubectl get pods -n cockroach-operator-system -o jsonpath='{.items[0].status.conditions[?(@.type==\"Ready\")].status}' 2>&1"
                    ready_check, _ = pct_service.execute(str(control_id), ready_check_cmd, timeout=30)
                    if ready_check and "True" in ready_check:
                        logger.info("CockroachDB Operator is ready")
                        break
                logger.info("Waiting for CockroachDB Operator to be ready (waited %d/%d seconds)...", wait_time_operator, max_wait_operator)
                time.sleep(5)
                wait_time_operator += 5
            else:
                logger.error("CockroachDB Operator not ready after %d seconds", max_wait_operator)
                # Get pod status for debugging
                debug_cmd = "kubectl get pods -n cockroach-operator-system -o wide 2>&1"
                debug_output, _ = pct_service.execute(str(control_id), debug_cmd, timeout=30)
                if debug_output:
                    logger.error("Operator pod status: %s", debug_output)
                return False
            
            # Wait for webhook to be ready (webhook server needs time to start and register)
            logger.info("Waiting for CockroachDB Operator webhook to be ready...")
            max_wait_webhook = 60
            wait_time_webhook = 0
            while wait_time_webhook < max_wait_webhook:
                # Check if webhook service has endpoints
                webhook_check_cmd = "kubectl get endpoints cockroach-operator-webhook-service -n cockroach-operator-system -o jsonpath='{.subsets[0].addresses[0].ip}' 2>&1"
                webhook_check, _ = pct_service.execute(str(control_id), webhook_check_cmd, timeout=30)
                if webhook_check and webhook_check.strip():
                    logger.info("CockroachDB Operator webhook is ready")
                    break
                logger.info("Waiting for CockroachDB Operator webhook to be ready (waited %d/%d seconds)...", wait_time_webhook, max_wait_webhook)
                time.sleep(5)
                wait_time_webhook += 5
            else:
                logger.warning("Webhook may not be fully ready, but continuing...")
            
            # Create CockroachDB cluster manifest
            logger.info("Creating CockroachDB cluster...")
            cockroachdb_manifest = f"""apiVersion: crdb.cockroachlabs.com/v1alpha1
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
            storage: "{cockroachdb_storage}"
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
    name: cockroachdb/cockroach:{cockroachdb_version}
  nodes: {cockroachdb_nodes}
"""
            # Apply the manifest with retry (webhook may need time to be fully ready)
            logger.info("Creating CockroachDB cluster (with retry)...")
            max_retries = 3
            retry_delay = 10
            for attempt in range(max_retries):
                apply_cmd = f"kubectl apply -f - << 'EOF'\n{cockroachdb_manifest}EOF"
                apply_output, apply_exit = pct_service.execute(str(control_id), apply_cmd, timeout=60)
                if apply_exit == 0:
                    logger.info("CockroachDB cluster created successfully")
                    break
                if attempt < max_retries - 1:
                    logger.warning("Failed to create CockroachDB cluster (attempt %d/%d), retrying in %d seconds...", attempt + 1, max_retries, retry_delay)
                    if apply_output and "webhook" in apply_output.lower():
                        logger.info("Webhook error detected, waiting for webhook to be ready...")
                        time.sleep(retry_delay)
                    else:
                        time.sleep(retry_delay)
                else:
                    logger.error("Failed to create CockroachDB cluster after %d attempts", max_retries)
                    if apply_output:
                        logger.error("Cluster creation output: %s", apply_output[-500:])
                    return False
            
            # Wait for cluster to be ready
            logger.info("Waiting for CockroachDB cluster to be ready...")
            max_wait_cluster = 300
            wait_time_cluster = 0
            while wait_time_cluster < max_wait_cluster:
                cluster_status_cmd = "kubectl get crdbcluster cockroachdb -o jsonpath='{.status.clusterStatus}' 2>&1"
                cluster_status, _ = pct_service.execute(str(control_id), cluster_status_cmd, timeout=30)
                if cluster_status and "Finished" in cluster_status:
                    logger.info("CockroachDB cluster is ready")
                    break
                pods_ready_cmd = f"kubectl get pods -l app.kubernetes.io/instance=cockroachdb -o jsonpath='{{.items[*].status.phase}}' 2>&1"
                pods_status, _ = pct_service.execute(str(control_id), pods_ready_cmd, timeout=30)
                if pods_status:
                    running_count = pods_status.count("Running")
                    if running_count == cockroachdb_nodes:
                        logger.info("All CockroachDB pods are running")
                        break
                logger.info("Waiting for CockroachDB cluster to be ready (waited %d/%d seconds)...", wait_time_cluster, max_wait_cluster)
                time.sleep(10)
                wait_time_cluster += 10
            else:
                logger.warning("CockroachDB cluster may not be fully ready, but continuing...")
            
            # Set root user password for Admin UI access
            logger.info("Setting root user password for Admin UI access...")
            set_password_cmd = f"kubectl exec cockroachdb-0 -- ./cockroach sql --certs-dir=/cockroach/cockroach-certs --host=cockroachdb-public -e \"ALTER USER root WITH PASSWORD '{cockroachdb_password}';\" 2>&1"
            password_output, password_exit = pct_service.execute(str(control_id), set_password_cmd, timeout=60)
            if password_exit != 0:
                logger.warning("Failed to set root password: %s", password_output[-200:] if password_output else "No output")
            else:
                logger.info("Root user password set successfully")
            
            # Create NodePort service for external access
            logger.info("Creating NodePort service for external access...")
            nodeport_service_manifest = f"""apiVersion: v1
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
    nodePort: {sql_nodeport}
    protocol: TCP
  - name: http
    port: 8080
    targetPort: 8080
    nodePort: {http_nodeport}
    protocol: TCP
  - name: grpc
    port: 26258
    targetPort: 26258
    nodePort: {grpc_nodeport}
    protocol: TCP
"""
            apply_svc_cmd = f"kubectl apply -f - << 'EOF'\n{nodeport_service_manifest}EOF"
            apply_svc_output, apply_svc_exit = pct_service.execute(str(control_id), apply_svc_cmd, timeout=60)
            if apply_svc_exit != 0:
                logger.warning("Failed to create NodePort service: %s", apply_svc_output[-200:] if apply_svc_output else "No output")
            else:
                logger.info("CockroachDB NodePort service created (SQL: %d, HTTP: %d, gRPC: %d)", sql_nodeport, http_nodeport, grpc_nodeport)
            
            logger.info("CockroachDB installed successfully")
            return True
        except Exception as e:
            logger.error("Error installing CockroachDB: %s", str(e))
            return False
        finally:
            lxc_service.disconnect()
