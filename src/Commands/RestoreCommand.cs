using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Enva.Libs;
using Enva.Services;

namespace Enva.Commands;

public class RestoreError : Exception
{
    public RestoreError(string message) : base(message) { }
}

public class RestoreCommand
{
    private LabConfig? cfg;
    private ILXCService? lxcService;
    private PCTService? pctService;

    public RestoreCommand(LabConfig? cfg, ILXCService? lxcService, PCTService? pctService)
    {
        this.cfg = cfg;
        this.lxcService = lxcService;
        this.pctService = pctService;
    }

    public void Run(string backupName)
    {
        var logger = Logger.GetLogger("restore");
        try
        {
            logger.Printf("=== Restoring Cluster from Backup ===");
            if (string.IsNullOrEmpty(backupName))
            {
                throw new RestoreError("Backup name is required. Use --backup-name <name>");
            }
            if (cfg == null || cfg.Backup == null)
            {
                throw new RestoreError("Backup configuration not found in enva.yaml");
            }
            if (lxcService == null || pctService == null)
            {
                throw new RestoreError("LXC or PCT service not initialized");
            }
            if (!lxcService.Connect())
            {
                throw new RestoreError($"Failed to connect to LXC host {cfg.LXCHost()}");
            }

            try
            {
                ContainerConfig? backupContainer = null;
                foreach (var ct in cfg.Containers)
                {
                    if (ct.ID == cfg.Backup.ContainerID)
                    {
                        backupContainer = ct;
                        break;
                    }
                }
                if (backupContainer == null)
                {
                    throw new RestoreError($"Backup container with ID {cfg.Backup.ContainerID} not found");
                }
                string backupTarball = $"{cfg.Backup.BackupDir}/{backupName}.tar.gz";
                logger.Printf("Restoring from backup: {0}", backupName);
                logger.Printf("Backup location: {0}", backupTarball);
                logger.Printf("Verifying backup exists...");
                string checkCmd = $"test -f {backupTarball} && echo exists || echo missing";
                int timeout = 30;
                (string checkOutput, int? checkExit) = pctService.Execute(backupContainer.ID, checkCmd, timeout);
                if (!checkExit.HasValue || checkExit.Value != 0 || string.IsNullOrEmpty(checkOutput) || checkOutput.Contains("missing"))
                {
                    throw new RestoreError($"Backup not found: {backupName}");
                }
                logger.Printf("Extracting backup tarball...");
                string extractDir = $"{cfg.Backup.BackupDir}/{backupName}";
                string extractCmd = $"mkdir -p {extractDir} && cd {cfg.Backup.BackupDir} && tar -xzf {backupName}.tar.gz -C {extractDir}";
                timeout = 300;
                (string extractOutput, int? extractExit) = pctService.Execute(backupContainer.ID, extractCmd, timeout);
                if (!extractExit.HasValue || extractExit.Value != 0)
                {
                    throw new RestoreError("Failed to extract backup");
                }
                int? controlNodeID = null;
                List<int> workerNodeIDs = new List<int>();
                List<BackupItemConfig> k3sItems = new List<BackupItemConfig>();
                foreach (var item in cfg.Backup.Items)
                {
                    if (item.Name.StartsWith("k3s-"))
                    {
                        k3sItems.Add(item);
                    }
                }
                if (k3sItems.Count > 0)
                {
                    if (cfg.Kubernetes != null && cfg.Kubernetes.Control != null && cfg.Kubernetes.Control.Count > 0)
                    {
                        controlNodeID = cfg.Kubernetes.Control[0];
                    }
                    if (cfg.Kubernetes != null && cfg.Kubernetes.Workers != null && cfg.Kubernetes.Workers.Count > 0)
                    {
                        workerNodeIDs = new List<int>(cfg.Kubernetes.Workers);
                    }
                    logger.Printf("Stopping all k3s services before restore...");
                    if (controlNodeID.HasValue)
                    {
                        logger.Printf("Stopping k3s service on control node {0}...", controlNodeID.Value);
                        string stopCmd = "systemctl stop k3s";
                        timeout = 60;
                        (string stopOutput, int? stopExit) = pctService.Execute(controlNodeID.Value, stopCmd, timeout);
                        if (!stopExit.HasValue || stopExit.Value != 0)
                        {
                            logger.Printf("Failed to stop k3s on control node (may not be running): {0}", stopOutput);
                        }
                        else
                        {
                            logger.Printf("k3s service stopped on control node");
                        }
                    }
                    foreach (int workerID in workerNodeIDs)
                    {
                        logger.Printf("Stopping k3s-agent service on worker node {0}...", workerID);
                        string stopAgentCmd = "systemctl stop k3s-agent || true";
                        timeout = 60;
                        (string stopAgentOutput, int? stopAgentExit) = pctService.Execute(workerID, stopAgentCmd, timeout);
                        if (!stopAgentExit.HasValue || stopAgentExit.Value != 0)
                        {
                            logger.Printf("Failed to stop k3s-agent on worker {0} (may not be running): {1}", workerID, stopAgentOutput);
                        }
                        else
                        {
                            logger.Printf("k3s-agent service stopped on worker {0}", workerID);
                        }
                    }
                    logger.Printf("All k3s services stopped");
                    BackupItemConfig? controlDataItem = null;
                    foreach (var item in cfg.Backup.Items)
                    {
                        if (item.Name == "k3s-control-data")
                        {
                            controlDataItem = item;
                            break;
                        }
                    }
                    if (controlNodeID.HasValue && controlDataItem != null)
                    {
                        logger.Printf("Clearing existing k3s state on control node to ensure clean restore...");
                        string removeCmd = $"rm -rf {controlDataItem.SourcePath} && mkdir -p {controlDataItem.SourcePath} && chmod 700 {controlDataItem.SourcePath} || true";
                        timeout = 30;
                        (string removeOutput, int? removeExit) = pctService.Execute(controlNodeID.Value, removeCmd, timeout);
                        if (!removeExit.HasValue || removeExit.Value != 0)
                        {
                            logger.Printf("Failed to clear k3s state (may not exist): {0}", removeOutput);
                        }
                        else
                        {
                            logger.Printf("k3s state cleared on control node");
                        }
                    }
                }
                List<BackupItemConfig> itemsToRestore = new List<BackupItemConfig>(cfg.Backup.Items);
                itemsToRestore.Sort((i, j) =>
                {
                    int priorityI = GetRestorePriority(i.Name);
                    int priorityJ = GetRestorePriority(j.Name);
                    return priorityI.CompareTo(priorityJ);
                });
                foreach (var item in itemsToRestore)
                {
                    logger.Printf("Restoring item: {0} to container {1}", item.Name, item.SourceContainerID);
                    ContainerConfig? sourceContainer = null;
                    foreach (var ct in cfg.Containers)
                    {
                        if (ct.ID == item.SourceContainerID)
                        {
                            sourceContainer = ct;
                            break;
                        }
                    }
                    if (sourceContainer == null)
                    {
                        throw new RestoreError($"Source container {item.SourceContainerID} not found");
                    }
                    bool isArchive = !string.IsNullOrEmpty(item.ArchiveBase) && !string.IsNullOrEmpty(item.ArchivePath);
                    string backupFileName = $"{backupName}-{item.Name}";
                    if (isArchive)
                    {
                        backupFileName += ".tar.gz";
                    }
                    string backupFilePath = $"{extractDir}/{backupFileName}";
                    string checkFileCmd = $"test -f {backupFilePath} && echo exists || echo missing";
                    timeout = 30;
                    (string checkFileOutput, int? checkFileExit) = pctService.Execute(backupContainer.ID, checkFileCmd, timeout);
                    if (!checkFileExit.HasValue || checkFileExit.Value != 0 || string.IsNullOrEmpty(checkFileOutput) || checkFileOutput.Contains("missing"))
                    {
                        logger.Printf("Backup file not found for item {0}, skipping...", item.Name);
                        continue;
                    }
                    string lxcTemp = $"/tmp/{backupFileName}";
                    string pullCmd = $"pct pull {backupContainer.ID} {backupFilePath} {lxcTemp}";
                    timeout = 300;
                    (string pullOutput, int? pullExit) = lxcService.Execute(pullCmd, timeout);
                    if (!pullExit.HasValue || pullExit.Value != 0)
                    {
                        throw new RestoreError("Failed to pull backup file");
                    }
                    string sourceTemp = $"/tmp/{backupFileName}";
                    string pushCmd = $"pct push {item.SourceContainerID} {lxcTemp} {sourceTemp}";
                    timeout = 300;
                    (string pushOutput, int? pushExit) = lxcService.Execute(pushCmd, timeout);
                    if (!pushExit.HasValue || pushExit.Value != 0)
                    {
                        throw new RestoreError("Failed to push backup file to source container");
                    }
                    timeout = 30;
                    lxcService.Execute($"rm -f {lxcTemp}", timeout);
                    if (isArchive)
                    {
                        logger.Printf("  Extracting archive to: {0}", item.SourcePath);
                        if (item.Name == "k3s-etcd")
                        {
                            string dbDir = item.SourcePath;
                            string removeDBCmd = $"rm -rf {dbDir}/* || true";
                            timeout = 30;
                            pctService.Execute(item.SourceContainerID, removeDBCmd, timeout);
                        }
                        else
                        {
                            string backupExistingCmd = $"mv {item.SourcePath} {item.SourcePath}.backup.$(date +%s) || true";
                            timeout = 30;
                            pctService.Execute(item.SourceContainerID, backupExistingCmd, timeout);
                        }
                        string extractCmd2 = $"mkdir -p {item.ArchiveBase} && tar -xzf {sourceTemp} -C {item.ArchiveBase}";
                        timeout = 300;
                        if (item.Name == "glusterfs-data")
                        {
                            timeout = 600; // 10 minutes for large GlusterFS data
                        }
                        (string extractOutput2, int? extractExit2) = pctService.Execute(item.SourceContainerID, extractCmd2, timeout);
                        if (!extractExit2.HasValue || extractExit2.Value != 0)
                        {
                            throw new RestoreError($"Failed to extract archive for {item.Name}");
                        }
                    }
                    else
                    {
                        logger.Printf("  Copying file to: {0}", item.SourcePath);
                        string backupExistingCmd = $"mv {item.SourcePath} {item.SourcePath}.backup.$(date +%s) || true";
                        timeout = 30;
                        pctService.Execute(item.SourceContainerID, backupExistingCmd, timeout);
                        string copyCmd = $"cp {sourceTemp} {item.SourcePath}";
                        timeout = 60;
                        (string copyOutput, int? copyExit) = pctService.Execute(item.SourceContainerID, copyCmd, timeout);
                        if (!copyExit.HasValue || copyExit.Value != 0)
                        {
                            throw new RestoreError($"Failed to copy file for {item.Name}");
                        }
                        if (item.Name == "k3s-token")
                        {
                            string tokenCopyCmd = "cp /var/lib/rancher/k3s/server/token /var/lib/rancher/k3s/server/token";
                            timeout = 30;
                            (string tokenCopyOutput, int? tokenCopyExit) = pctService.Execute(item.SourceContainerID, tokenCopyCmd, timeout);
                            if (!tokenCopyExit.HasValue || tokenCopyExit.Value != 0)
                            {
                                logger.Printf("  Failed to copy token to server/token location: {0}", tokenCopyOutput);
                            }
                            else
                            {
                                logger.Printf("  Token also copied to /var/lib/rancher/k3s/server/token");
                            }
                        }
                        if (item.Name.Contains("service-env"))
                        {
                            string dirPath = "/etc/systemd/system";
                            string mkdirCmd = $"mkdir -p {dirPath} || true";
                            timeout = 30;
                            pctService.Execute(item.SourceContainerID, mkdirCmd, timeout);
                            string reloadCmd = "systemctl daemon-reload";
                            timeout = 30;
                            (string reloadOutput, int? reloadExit) = pctService.Execute(item.SourceContainerID, reloadCmd, timeout);
                            if (!reloadExit.HasValue || reloadExit.Value != 0)
                            {
                                logger.Printf("  Failed to reload systemd daemon: {0}", reloadOutput);
                            }
                            else
                            {
                                logger.Printf("  Systemd daemon reloaded to pick up restored service.env");
                            }
                        }
                        if (item.Name == "haproxy-config")
                        {
                            logger.Printf("  Reloading HAProxy to apply restored configuration...");
                            string reloadCmd = "systemctl reload haproxy || systemctl restart haproxy";
                            timeout = 30;
                            (string reloadOutput, int? reloadExit) = pctService.Execute(item.SourceContainerID, reloadCmd, timeout);
                            if (!reloadExit.HasValue || reloadExit.Value != 0)
                            {
                                logger.Printf("  Failed to reload HAProxy: {0}", reloadOutput);
                            }
                            else
                            {
                                logger.Printf("  HAProxy reloaded successfully");
                            }
                        }
                    }
                    timeout = 30;
                    pctService.Execute(item.SourceContainerID, $"rm -f {sourceTemp} || true", timeout);
                }
                string cleanupCmd = $"rm -rf {extractDir} || true";
                timeout = 30;
                pctService.Execute(backupContainer.ID, cleanupCmd, timeout);
                if (k3sItems.Count > 0)
                {
                    if (controlNodeID.HasValue)
                    {
                        logger.Printf("Removing credential files that may conflict with restored datastore...");
                        string credCleanupCmd = "rm -f /var/lib/rancher/k3s/server/cred/passwd /var/lib/rancher/k3s/server/cred/ipsec.psk || true";
                        timeout = 30;
                        pctService.Execute(controlNodeID.Value, credCleanupCmd, timeout);
                        logger.Printf("Starting k3s service on control node with restored data...");
                        string startCmd = "systemctl start k3s";
                        timeout = 60;
                        (string startOutput, int? startExit) = pctService.Execute(controlNodeID.Value, startCmd, timeout);
                        if (!startExit.HasValue || startExit.Value != 0)
                        {
                            throw new RestoreError("Failed to start k3s after restore");
                        }
                        logger.Printf("k3s service started on control node, waiting for it to be ready...");
                        int maxWait = 120;
                        int waitTime = 0;
                        while (waitTime < maxWait)
                        {
                            string checkCmd2 = "systemctl is-active k3s && kubectl get nodes | grep -q Ready || echo not-ready";
                            timeout = 30;
                            (string checkOutput2, int? checkExit2) = pctService.Execute(controlNodeID.Value, checkCmd2, timeout);
                            if (checkExit2.HasValue && checkExit2.Value == 0 && !checkOutput2.Contains("not-ready"))
                            {
                                logger.Printf("k3s control plane is ready");
                                break;
                            }
                            logger.Printf("Waiting for k3s control plane to be ready (waited {0}/{1} seconds)...", waitTime, maxWait);
                            Thread.Sleep(5000);
                            waitTime += 5;
                        }
                        if (waitTime >= maxWait)
                        {
                            logger.Printf("k3s control plane may not be fully ready after restore, but continuing...");
                        }
                    }
                    if (workerNodeIDs.Count > 0)
                    {
                        logger.Printf("Starting k3s-agent services on worker nodes...");
                        foreach (int workerID in workerNodeIDs)
                        {
                            logger.Printf("Starting k3s-agent service on worker node {0}...", workerID);
                            string startAgentCmd = "systemctl start k3s-agent";
                            timeout = 60;
                            (string startAgentOutput, int? startAgentExit) = pctService.Execute(workerID, startAgentCmd, timeout);
                            if (!startAgentExit.HasValue || startAgentExit.Value != 0)
                            {
                                logger.Printf("Failed to start k3s-agent on worker {0}: {1}", workerID, startAgentOutput);
                            }
                            else
                            {
                                logger.Printf("k3s-agent service started on worker {0}", workerID);
                            }
                        }
                        logger.Printf("Waiting for worker nodes to connect to control plane...");
                        Thread.Sleep(10000);
                        if (controlNodeID.HasValue)
                        {
                            string verifyCmd = "kubectl get nodes";
                            timeout = 30;
                            (string verifyOutput, _) = pctService.Execute(controlNodeID.Value, verifyCmd, timeout);
                            if (!string.IsNullOrEmpty(verifyOutput))
                            {
                                logger.Printf("Current node status:\n{0}", verifyOutput);
                            }
                        }
                    }
                    if (controlNodeID.HasValue)
                    {
                        logger.Printf("Waiting for Rancher to be ready after restore...");
                        int maxWait2 = 180;
                        int waitTime2 = 0;
                        bool rancherReady = false;
                        while (waitTime2 < maxWait2)
                        {
                            string checkRancherCmd = "kubectl get pods -n cattle-system -l app=rancher --field-selector=status.phase=Running --no-headers 2>/dev/null | grep -q rancher && echo 'ready' || echo 'not-ready'";
                            timeout = 30;
                            (string rancherStatus, _) = pctService.Execute(controlNodeID.Value, checkRancherCmd, timeout);
                            if (!string.IsNullOrEmpty(rancherStatus) && rancherStatus.Contains("ready"))
                            {
                                rancherReady = true;
                                break;
                            }
                            logger.Printf("Waiting for Rancher to be ready (waited {0}/{1} seconds)...", waitTime2, maxWait2);
                            Thread.Sleep(10000);
                            waitTime2 += 10;
                        }
                        if (!rancherReady)
                        {
                            logger.Printf("Rancher may not be fully ready after restore, but continuing...");
                        }
                        logger.Printf("Checking Rancher first-login setting consistency...");
                        Thread.Sleep(10000);
                        string checkFirstLoginCmd = "kubectl get settings.management.cattle.io first-login -o jsonpath='{.value}' || echo 'not-found'";
                        timeout = 30;
                        (string firstLoginValue, _) = pctService.Execute(controlNodeID.Value, checkFirstLoginCmd, timeout);
                        string checkUserCmd = "kubectl get users.management.cattle.io -o jsonpath='{range .items[*]}{.username}{\"\\n\"}{end}' | grep -q '^admin$' && echo 'exists' || echo 'not-found'";
                        (string userExists, _) = pctService.Execute(controlNodeID.Value, checkUserCmd, timeout);
                        string checkBootstrapSecretCmd = "kubectl get secret -n cattle-system bootstrap-secret -o jsonpath='{.data.bootstrapPassword}' 2>/dev/null | base64 -d 2>/dev/null || echo 'not-found'";
                        (string bootstrapPassword, _) = pctService.Execute(controlNodeID.Value, checkBootstrapSecretCmd, timeout);
                        if (!string.IsNullOrEmpty(bootstrapPassword) && !bootstrapPassword.Contains("not-found"))
                        {
                            logger.Printf("Rancher bootstrap password restored: {0}", bootstrapPassword.Trim());
                        }
                        else
                        {
                            logger.Printf("Rancher bootstrap secret not found after restore - Rancher may have recreated it with default password 'admin'");
                        }
                        if (!string.IsNullOrEmpty(firstLoginValue) && string.IsNullOrWhiteSpace(firstLoginValue) && !string.IsNullOrEmpty(userExists) && userExists.Contains("exists"))
                        {
                            logger.Printf("Detected inconsistent backup state: admin user exists but first-login is empty");
                            logger.Printf("Fixing first-login setting to 'false'...");
                            string patchCmd = "kubectl patch settings.management.cattle.io first-login --type json -p '[{\"op\": \"replace\", \"path\": \"/value\", \"value\": \"false\"}]'";
                            timeout = 30;
                            (string patchOutput, int? patchExit) = pctService.Execute(controlNodeID.Value, patchCmd, timeout);
                            if (patchExit.HasValue && patchExit.Value == 0)
                            {
                                logger.Printf("first-login setting fixed to 'false'");
                                string restartCmd = "kubectl rollout restart deployment rancher -n cattle-system";
                                timeout = 30;
                                pctService.Execute(controlNodeID.Value, restartCmd, timeout);
                            }
                            else
                            {
                                logger.Printf("Failed to fix first-login setting: {0}", patchOutput);
                            }
                        }
                    }
                }
                logger.Printf("=== Restore completed successfully! ===");
                logger.Printf("Backup restored: {0}", backupName);
            }
            finally
            {
                lxcService.Disconnect();
            }
        }
        catch (Exception ex)
        {
            var logger2 = Logger.GetLogger("restore");
            logger2.Printf("Error during restore: {0}", ex.Message);
            if (ex is RestoreError)
            {
                throw;
            }
            throw new RestoreError($"Error during restore: {ex.Message}");
        }
    }

    private static int GetRestorePriority(string name)
    {
        if (name == "k3s-control-data")
        {
            return 0;
        }
        if (name == "k3s-control-config")
        {
            return 1;
        }
        if (name.Contains("k3s-worker") && name.Contains("data"))
        {
            return 2;
        }
        if (name.Contains("k3s-worker") && name.Contains("config"))
        {
            return 3;
        }
        return 4;
    }
}