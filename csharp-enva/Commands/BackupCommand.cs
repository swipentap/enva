using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Enva.Libs;
using Enva.Services;

namespace Enva.Commands;

public class BackupError : Exception
{
    public BackupError(string message) : base(message) { }
}

public class BackupCommand
{
    private LabConfig? cfg;
    private ILXCService? lxcService;
    private PCTService? pctService;

    public BackupCommand(LabConfig? cfg, ILXCService? lxcService, PCTService? pctService)
    {
        this.cfg = cfg;
        this.lxcService = lxcService;
        this.pctService = pctService;
    }

    private long GetDirectorySize(int containerID, string path, int? timeout)
    {
        string cmd = $"du -sb {path} | cut -f1";
        int timeoutVal = 300; // Default 5 minutes for large directories
        if (timeout.HasValue && timeout.Value > 0)
        {
            // Use custom timeout if provided, but cap at reasonable value for size check
            if (timeout.Value > 600)
            {
                timeoutVal = 600; // Max 10 minutes for size check
            }
            else
            {
                timeoutVal = timeout.Value;
            }
        }
        if (pctService == null)
        {
            throw new BackupError("PCT service not initialized");
        }
        (string output, int? exitCode) = pctService.Execute(containerID, cmd, timeoutVal);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            throw new BackupError($"failed to get directory size: {output}");
        }
        if (!long.TryParse(output.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long size))
        {
            throw new BackupError("failed to parse size");
        }
        return size;
    }

    private static string FormatSize(long bytes)
    {
        const long unit = 1024;
        if (bytes < unit)
        {
            return $"{bytes} B";
        }
        long div = unit;
        int exp = 0;
        for (long n = bytes / unit; n >= unit; n /= unit)
        {
            div *= unit;
            exp++;
        }
        return string.Format(CultureInfo.InvariantCulture, "{0:F2} {1}B", (double)bytes / div, "KMGTPE"[exp]);
    }

    private string CheckSpaceUsage(int containerID, string path, int topN, int? timeout)
    {
        string cmd = $"du -h --max-depth=1 {path} | sort -hr | head -n {topN}";
        int timeoutVal = 300; // Default 5 minutes for large directories
        if (timeout.HasValue && timeout.Value > 0)
        {
            // Use custom timeout if provided, but cap at reasonable value for space check
            if (timeout.Value > 600)
            {
                timeoutVal = 600; // Max 10 minutes for space check
            }
            else
            {
                timeoutVal = timeout.Value;
            }
        }
        if (pctService == null)
        {
            throw new BackupError("PCT service not initialized");
        }
        (string output, int? exitCode) = pctService.Execute(containerID, cmd, timeoutVal);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            throw new BackupError($"failed to check space usage: {output}");
        }
        return output;
    }

    public void Run(int? customTimeout, List<string> excludePaths, bool showSizes, bool checkSpace)
    {
        var logger = Logger.GetLogger("backup");
        try
        {
            logger.Printf("=== Backing Up Cluster ===");
            if (customTimeout.HasValue && customTimeout.Value > 0)
            {
                logger.Printf("Using custom timeout: {0} seconds", customTimeout.Value);
            }
            if (cfg == null || cfg.Backup == null)
            {
                throw new BackupError("Backup configuration not found in enva.yaml");
            }
            if (lxcService == null || pctService == null)
            {
                throw new BackupError("LXC or PCT service not initialized");
            }
            if (!lxcService.Connect())
            {
                throw new BackupError($"Failed to connect to LXC host {cfg.LXCHost()}");
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
                    throw new BackupError($"Backup container with ID {cfg.Backup.ContainerID} not found");
                }
                // Restart all k3s nodes before backup to ensure clean state
                logger.Printf("Restarting all k3s nodes to ensure clean state before backup...");
                RestartK3sNodes();

                // Wait for all pods to be ready
                logger.Printf("Waiting for all pods to be ready after restart...");
                WaitForAllPodsReady();
                logger.Printf("All nodes restarted and pods are ready, proceeding with backup...");

                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
                string backupName = $"{cfg.Backup.NamePrefix}-{timestamp}";
                logger.Printf("Creating backup: {0}", backupName);
                logger.Printf("Backup will be stored on container {0} at {1}", backupContainer.ID, cfg.Backup.BackupDir);
                string mkdirCmd = $"mkdir -p {cfg.Backup.BackupDir}";
                int timeout = 30;
                (string mkdirOutput, int? mkdirExit) = pctService.Execute(backupContainer.ID, mkdirCmd, timeout);
                if (!mkdirExit.HasValue || mkdirExit.Value != 0)
                {
                    throw new BackupError("Failed to create backup directory");
                }
                List<(int sourceID, string tempFile, string destName)> backupFiles = new List<(int, string, string)>();
                foreach (var item in cfg.Backup.Items)
                {
                    logger.Printf("Backing up item: {0} from container {1}", item.Name, item.SourceContainerID);

                    // Show size before backup if requested
                    if (showSizes && !string.IsNullOrEmpty(item.ArchiveBase) && !string.IsNullOrEmpty(item.ArchivePath))
                    {
                        string fullPath = $"{item.ArchiveBase}/{item.ArchivePath}";
                        try
                        {
                            long size = GetDirectorySize(item.SourceContainerID, fullPath, customTimeout);
                            logger.Printf("  Directory size: {0}", FormatSize(size));
                        }
                        catch (Exception ex)
                        {
                            logger.Printf("  Could not determine size: {0}", ex.Message);
                        }
                    }
                    else if (showSizes && !string.IsNullOrEmpty(item.SourcePath))
                    {
                        try
                        {
                            long size = GetDirectorySize(item.SourceContainerID, item.SourcePath, customTimeout);
                            logger.Printf("  File/directory size: {0}", FormatSize(size));
                        }
                        catch (Exception ex)
                        {
                            logger.Printf("  Could not determine size: {0}", ex.Message);
                        }
                    }

                    // Check space usage if requested
                    if (checkSpace && !string.IsNullOrEmpty(item.ArchiveBase) && !string.IsNullOrEmpty(item.ArchivePath))
                    {
                        string fullPath = $"{item.ArchiveBase}/{item.ArchivePath}";
                        logger.Printf("  Checking space usage in {0}...", fullPath);
                        try
                        {
                            string usage = CheckSpaceUsage(item.SourceContainerID, fullPath, 10, customTimeout);
                            logger.Printf("  Top space consumers:\n{0}", usage);
                        }
                        catch (Exception ex)
                        {
                            logger.Printf("  Could not check space usage: {0}", ex.Message);
                        }
                    }
                    else if (checkSpace && !string.IsNullOrEmpty(item.SourcePath))
                    {
                        logger.Printf("  Checking space usage in {0}...", item.SourcePath);
                        try
                        {
                            string usage = CheckSpaceUsage(item.SourceContainerID, item.SourcePath, 10, customTimeout);
                            logger.Printf("  Top space consumers:\n{0}", usage);
                        }
                        catch (Exception ex)
                        {
                            logger.Printf("  Could not check space usage: {0}", ex.Message);
                        }
                    }
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
                        throw new BackupError($"Source container {item.SourceContainerID} not found for backup item {item.Name}");
                    }
                    string tempFile = $"/tmp/{backupName}-{item.Name}";
                    if (!string.IsNullOrEmpty(item.ArchiveBase) && !string.IsNullOrEmpty(item.ArchivePath))
                    {
                        logger.Printf("  Archiving: {0}", item.SourcePath);
                        string archiveFile = $"{tempFile}.tar.gz";
                        if (item.Name == "k3s-control-data")
                        {
                            logger.Printf("  Checkpointing SQLite WAL to ensure all data is in main database file...");
                            string dbPath = $"{item.SourcePath}/server/db/state.db";
                            string checkpointCmd = $"python3 -c \"import sqlite3; conn = sqlite3.connect('{dbPath}'); result = conn.execute('PRAGMA wal_checkpoint(TRUNCATE)').fetchone(); conn.close(); print(f'Checkpoint: {{result}}')\"";
                            int checkpointTimeout = 30;
                            (string checkpointOutput, int? checkpointExit) = pctService.Execute(item.SourceContainerID, checkpointCmd, checkpointTimeout);
                            bool checkpointTimedOut = !checkpointExit.HasValue;
                            if (checkpointTimedOut)
                            {
                                logger.Printf("  SQLite WAL checkpoint timed out (directory may be corrupted/inaccessible): {0}", checkpointOutput);
                            }
                            else if (checkpointExit.HasValue && checkpointExit.Value == 0)
                            {
                                string output = checkpointOutput;
                                if (string.IsNullOrEmpty(output))
                                {
                                    output = "OK";
                                }
                                logger.Printf("  SQLite WAL checkpoint completed: {0}", output.Trim());
                            }
                            else
                            {
                                logger.Printf("  SQLite WAL checkpoint failed (may not be in WAL mode): {0}", checkpointOutput);
                            }
                        }
                        // Build exclude options for tar
                        string excludeOpts = "";
                        if (excludePaths != null && excludePaths.Count > 0)
                        {
                            logger.Printf("  Excluding paths: {0}", string.Join(", ", excludePaths));
                            foreach (string excludePath in excludePaths)
                            {
                                // Exclude path is relative to the archive path being backed up
                                excludeOpts += $" --exclude='{excludePath}'";
                            }
                        }
                        string archiveCmd = $"tar --warning=no-file-changed -czvf {archiveFile}{excludeOpts} -C {item.ArchiveBase} {item.ArchivePath}";
                        if (customTimeout.HasValue && customTimeout.Value > 0)
                        {
                            timeout = customTimeout.Value;
                        }
                        else
                        {
                            timeout = 300;
                        }
                        (string archiveOutput, int? archiveExit) = pctService.Execute(item.SourceContainerID, archiveCmd, timeout);
                        bool archiveTimedOut = !archiveExit.HasValue;
                        if (archiveTimedOut)
                        {
                            logger.Printf("  Archive command timed out after {0} seconds, checking if file was created...", timeout);
                        }
                        // Use longer timeout for verification, especially after a timeout
                        int verifyTimeout = 30;
                        if (archiveTimedOut)
                        {
                            verifyTimeout = 60;
                        }
                        string verifyCmd = $"test -f {archiveFile} && echo exists || echo missing";
                        timeout = verifyTimeout;
                        (string verifyOutput, int? verifyExit) = pctService.Execute(item.SourceContainerID, verifyCmd, timeout);
                        // If verification also times out, try one more time with a longer wait
                        if (!verifyExit.HasValue)
                        {
                            logger.Printf("  Verification timed out, waiting 10 seconds and retrying...");
                            Thread.Sleep(10000);
                            timeout = 60;
                            (verifyOutput, verifyExit) = pctService.Execute(item.SourceContainerID, verifyCmd, timeout);
                        }
                        if (!verifyExit.HasValue || verifyExit.Value != 0 || string.IsNullOrEmpty(verifyOutput) || verifyOutput.Contains("missing"))
                        {
                            logger.Printf("  Failed to create archive for {0}: {1}", item.Name, archiveOutput);
                            if (archiveTimedOut)
                            {
                                logger.Printf("  Archive command timed out and file verification failed. This usually indicates filesystem corruption or inaccessible directory.");
                                // For k3s-control-data, try backing up subdirectories individually
                                if (item.Name == "k3s-control-data")
                                {
                                    logger.Printf("  Attempting to backup k3s-control-data subdirectories individually...");
                                    // Try backing up server and storage separately
                                    string serverPath = $"{item.ArchivePath}/server";
                                    string storagePath = $"{item.ArchivePath}/storage";
                                    string serverFile = $"{tempFile}-server.tar.gz";
                                    string storageFile = $"{tempFile}-storage.tar.gz";

                                    bool backedUpAny = false;

                                    // Try server directory
                                    string serverCmd = $"tar --warning=no-file-changed -czvf {serverFile} -C {item.ArchiveBase} {serverPath}";
                                    (string serverOutput, int? serverExit) = pctService.Execute(item.SourceContainerID, serverCmd, timeout);
                                    if (serverExit.HasValue && serverExit.Value == 0)
                                    {
                                        // Verify server file exists
                                        string serverVerifyCmd = $"test -f {serverFile} && echo exists || echo missing";
                                        (string serverVerifyOutput, int? serverVerifyExit) = pctService.Execute(item.SourceContainerID, serverVerifyCmd, verifyTimeout);
                                        if (serverVerifyExit.HasValue && serverVerifyExit.Value == 0 && serverVerifyOutput.Contains("exists"))
                                        {
                                            logger.Printf("  Successfully backed up server subdirectory");
                                            backupFiles.Add((item.SourceContainerID, serverFile, $"{backupName}-{item.Name}-server.tar.gz"));
                                            backedUpAny = true;
                                        }
                                    }
                                    else
                                    {
                                        logger.Printf("  Failed to backup server subdirectory: {0}", serverOutput);
                                    }

                                    // Try storage directory
                                    string storageCmd = $"tar --warning=no-file-changed -czvf {storageFile} -C {item.ArchiveBase} {storagePath}";
                                    (string storageOutput, int? storageExit) = pctService.Execute(item.SourceContainerID, storageCmd, timeout);
                                    if (storageExit.HasValue && storageExit.Value == 0)
                                    {
                                        // Verify storage file exists
                                        string storageVerifyCmd = $"test -f {storageFile} && echo exists || echo missing";
                                        (string storageVerifyOutput, int? storageVerifyExit) = pctService.Execute(item.SourceContainerID, storageVerifyCmd, verifyTimeout);
                                        if (storageVerifyExit.HasValue && storageVerifyExit.Value == 0 && storageVerifyOutput.Contains("exists"))
                                        {
                                            logger.Printf("  Successfully backed up storage subdirectory");
                                            backupFiles.Add((item.SourceContainerID, storageFile, $"{backupName}-{item.Name}-storage.tar.gz"));
                                            backedUpAny = true;
                                        }
                                    }
                                    else
                                    {
                                        logger.Printf("  Failed to backup storage subdirectory: {0}", storageOutput);
                                    }

                                    // If we didn't backup any subdirectory, fail
                                    if (!backedUpAny)
                                    {
                                        logger.Printf("  CRITICAL: Failed to backup k3s-control-data - backup is incomplete and may not be usable");
                                        throw new BackupError($"Failed to create archive for {item.Name} - backup incomplete");
                                    }
                                    logger.Printf("  Backed up k3s-control-data subdirectories (partial backup)");
                                    continue;
                                }
                                logger.Printf("  CRITICAL: Failed to backup {0} - backup is incomplete", item.Name);
                                throw new BackupError($"Failed to create archive for {item.Name}");
                            }
                            logger.Printf("  Archive verification failed for {0}", item.Name);
                            throw new BackupError($"Failed to create archive for {item.Name}");
                        }
                        if (archiveTimedOut)
                        {
                            logger.Printf("  Archive file exists despite timeout - backup may have completed on server");
                        }
                        if (archiveExit.HasValue && archiveExit.Value != 0)
                        {
                            logger.Printf("  Archive created with warnings (file changed during backup - expected for live databases)");
                        }
                        backupFiles.Add((item.SourceContainerID, archiveFile, $"{item.Name}.tar.gz"));
                    }
                    else
                    {
                        logger.Printf("  Copying file: {0}", item.SourcePath);
                        string copyCmd = $"cp {item.SourcePath} {tempFile}";
                        timeout = 60;
                        (string copyOutput, int? copyExit) = pctService.Execute(item.SourceContainerID, copyCmd, timeout);
                        if (!copyExit.HasValue || copyExit.Value != 0)
                        {
                            throw new BackupError($"Failed to copy file for {item.Name}");
                        }
                        backupFiles.Add((item.SourceContainerID, tempFile, item.Name));
                    }
                }
                logger.Printf("Copying backup files to backup container...");
                List<string> backupFileNames = new List<string>();
                foreach (var (sourceID, tempFile, destName) in backupFiles)
                {
                    string lxcTemp = $"/tmp/{backupName}-{destName}";
                    string pullCmd = $"pct pull {sourceID} {tempFile} {lxcTemp}";
                    timeout = 300;
                    (string pullOutput, int? pullExit) = lxcService.Execute(pullCmd, timeout);
                    if (!pullExit.HasValue || pullExit.Value != 0)
                    {
                        throw new BackupError($"Failed to pull file from container {sourceID}");
                    }
                    string backupFilePath = $"{cfg.Backup.BackupDir}/{backupName}-{destName}";
                    string pushCmd = $"pct push {backupContainer.ID} {lxcTemp} {backupFilePath}";
                    timeout = 300;
                    (string pushOutput, int? pushExit) = lxcService.Execute(pushCmd, timeout);
                    if (!pushExit.HasValue || pushExit.Value != 0)
                    {
                        throw new BackupError("Failed to push file to backup container");
                    }
                    timeout = 30;
                    lxcService.Execute($"rm -f {lxcTemp}", timeout);
                    timeout = 30;
                    pctService.Execute(sourceID, $"rm -f {tempFile} {tempFile}.tar.gz || true", timeout);
                    backupFileNames.Add($"{backupName}-{destName}");
                }
                logger.Printf("Creating final backup tarball...");
                string finalTarball = $"{cfg.Backup.BackupDir}/{backupName}.tar.gz";
                string tarFilesList = string.Join(" ", backupFileNames);
                string tarCmd = $"cd {cfg.Backup.BackupDir} && tar -czvf {backupName}.tar.gz {tarFilesList}";
                if (customTimeout.HasValue && customTimeout.Value > 0)
                {
                    timeout = customTimeout.Value;
                }
                else
                {
                    timeout = 300;
                }
                (string tarOutput, int? tarExit) = pctService.Execute(backupContainer.ID, tarCmd, timeout);
                if (!tarExit.HasValue || tarExit.Value != 0)
                {
                    throw new BackupError("Failed to create final tarball");
                }
                string cleanupCmd = $"cd {cfg.Backup.BackupDir} && rm -f {tarFilesList} || true";
                timeout = 30;
                pctService.Execute(backupContainer.ID, cleanupCmd, timeout);
                logger.Printf("=== Backup completed successfully! ===");
                logger.Printf("Backup name: {0}", backupName);
                logger.Printf("Backup location: {0} on container {1}", finalTarball, backupContainer.ID);
            }
            finally
            {
                lxcService.Disconnect();
            }
        }
        catch (Exception ex)
        {
            var logger2 = Logger.GetLogger("backup");
            logger2.Printf("Error during backup: {0}", ex.Message);
            if (ex is BackupError)
            {
                throw;
            }
            throw new BackupError($"Error during backup: {ex.Message}");
        }
    }

    private void RestartK3sNodes()
    {
        var logger = Logger.GetLogger("backup");
        if (cfg == null || cfg.Backup == null || pctService == null)
        {
            throw new BackupError("Config or PCT service not initialized");
        }

        // Find k3s control and worker nodes from backup items
        int? controlNodeID = null;
        List<int> workerNodeIDs = new List<int>();
        HashSet<int> seenContainers = new HashSet<int>();

        foreach (var item in cfg.Backup.Items)
        {
            if (item.Name.StartsWith("k3s-"))
            {
                int containerID = item.SourceContainerID;
                if (seenContainers.Contains(containerID))
                {
                    continue;
                }
                seenContainers.Add(containerID);

                if (item.Name.Contains("control"))
                {
                    controlNodeID = containerID;
                }
                else if (item.Name.Contains("worker"))
                {
                    workerNodeIDs.Add(containerID);
                }
            }
        }

        // Restart control node first
        if (controlNodeID.HasValue)
        {
            logger.Printf("Restarting control node container {0}...", controlNodeID.Value);
            // Stop container
            pctService.Stop(controlNodeID.Value, false);
            // Wait a bit for clean shutdown
            Thread.Sleep(5000);
            // Start container
            pctService.Start(controlNodeID.Value);
            logger.Printf("Control node container {0} restarted, waiting for it to be ready...", controlNodeID.Value);
            // Wait for container to be ready
            int maxWait = 120;
            int waitTime = 0;
            while (waitTime < maxWait)
            {
                string testCmd = "echo test";
                (string testOutput, int? testExit) = pctService.Execute(controlNodeID.Value, testCmd, 5);
                if (testExit.HasValue && testExit.Value == 0 && testOutput.Contains("test"))
                {
                    logger.Printf("Control node container {0} is ready", controlNodeID.Value);
                    break;
                }
                Thread.Sleep(5000);
                waitTime += 5;
            }
            if (waitTime >= maxWait)
            {
                throw new BackupError($"control node container {controlNodeID.Value} did not become ready after {maxWait} seconds");
            }
        }

        // Restart worker nodes
        foreach (int workerID in workerNodeIDs)
        {
            logger.Printf("Restarting worker node container {0}...", workerID);
            // Stop container
            pctService.Stop(workerID, false);
            // Wait a bit for clean shutdown
            Thread.Sleep(5000);
            // Start container
            pctService.Start(workerID);
            logger.Printf("Worker node container {0} restarted, waiting for it to be ready...", workerID);
            // Wait for container to be ready
            int maxWait = 120;
            int waitTime = 0;
            while (waitTime < maxWait)
            {
                string testCmd = "echo test";
                (string testOutput, int? testExit) = pctService.Execute(workerID, testCmd, 5);
                if (testExit.HasValue && testExit.Value == 0 && testOutput.Contains("test"))
                {
                    logger.Printf("Worker node container {0} is ready", workerID);
                    break;
                }
                Thread.Sleep(5000);
                waitTime += 5;
            }
            if (waitTime >= maxWait)
            {
                throw new BackupError($"worker node container {workerID} did not become ready after {maxWait} seconds");
            }
        }

        logger.Printf("All k3s node containers restarted successfully");
    }

    private void WaitForAllPodsReady()
    {
        var logger = Logger.GetLogger("backup");
        if (cfg == null || cfg.Backup == null || pctService == null)
        {
            throw new BackupError("Config or PCT service not initialized");
        }

        // Find k3s control node
        int? controlNodeID = null;
        foreach (var item in cfg.Backup.Items)
        {
            if (item.Name.StartsWith("k3s-") && item.Name.Contains("control"))
            {
                controlNodeID = item.SourceContainerID;
                break;
            }
        }

        if (!controlNodeID.HasValue)
        {
            logger.Printf("No k3s control node found in backup items, skipping pod readiness check");
            return;
        }

        logger.Printf("Waiting for k3s control plane to be ready...");
        int maxWait = 300; // 5 minutes maximum timeout
        int waitTime = 0;
        bool controlPlaneReady = false;
        while (waitTime < maxWait)
        {
            // Check if k3s service is active
            string checkCmd = "systemctl is-active k3s || echo inactive";
            int timeout = 30;
            (string checkOutput, int? checkExit) = pctService.Execute(controlNodeID.Value, checkCmd, timeout);
            if (checkExit.HasValue && checkExit.Value == 0 && checkOutput.Contains("active"))
            {
                // Check if kubectl is available and nodes are ready
                string nodeCmd = "export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get nodes --no-headers 2>&1 | grep -q Ready || echo not-ready";
                (string nodeOutput, int? nodeExit) = pctService.Execute(controlNodeID.Value, nodeCmd, timeout);
                if (nodeExit.HasValue && nodeExit.Value == 0 && !nodeOutput.Contains("not-ready"))
                {
                    logger.Printf("k3s control plane is ready");
                    controlPlaneReady = true;
                    break;
                }
            }
            logger.Printf("Waiting for k3s control plane to be ready (waited {0}/{1} seconds)...", waitTime, maxWait);
            Thread.Sleep(10000);
            waitTime += 10;
        }
        if (!controlPlaneReady)
        {
            throw new BackupError($"k3s control plane did not become ready after {maxWait} seconds");
        }

        logger.Printf("Waiting until all pods are ready...");
        int maxWaitPods = 600; // 10 minutes maximum timeout
        int waitTimePods = 0;
        bool allPodsReady = false;
        while (waitTimePods < maxWaitPods)
        {
            // Get all pods and check if they're all ready
            string podsCmd = "export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods --all-namespaces --field-selector=status.phase!=Succeeded --no-headers -o jsonpath='{range .items[*]}{.metadata.namespace}{\"/\"}{.metadata.name}{\" \"}{.status.phase}{\" \"}{.status.containerStatuses[*].ready}{\"\\n\"}{end}' 2>&1";
            int timeout = 60;
            (string podsOutput, int? podsExit) = pctService.Execute(controlNodeID.Value, podsCmd, timeout);

            if (podsExit.HasValue && podsExit.Value == 0)
            {
                // Parse output to check if all pods are ready
                bool allReady = true;
                string[] lines = podsOutput.Trim().Split('\n');
                int nonSucceededPods = 0;
                foreach (string line in lines)
                {
                    if (string.IsNullOrEmpty(line))
                    {
                        continue;
                    }
                    string[] parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 3)
                    {
                        string phase = parts[1];
                        string ready = parts[2];
                        // Skip if phase is Succeeded (completed jobs)
                        if (phase == "Succeeded")
                        {
                            continue;
                        }
                        nonSucceededPods++;
                        // Check if pod is running and ready
                        if (phase != "Running" || !ready.Contains("true"))
                        {
                            allReady = false;
                            break;
                        }
                    }
                }

                if (allReady && nonSucceededPods > 0)
                {
                    logger.Printf("All pods are ready");
                    allPodsReady = true;
                    break;
                }
                else if (nonSucceededPods == 0)
                {
                    // No pods yet, keep waiting
                    logger.Printf("No pods found yet, waiting...");
                }
            }

            logger.Printf("Waiting for all pods to be ready (waited {0}/{1} seconds)...", waitTimePods, maxWaitPods);
            Thread.Sleep(15000);
            waitTimePods += 15;
        }

        if (!allPodsReady)
        {
            throw new BackupError($"not all pods are ready after {maxWaitPods} seconds");
        }
    }
}