package commands

import (
	"enva/libs"
	"enva/services"
	"fmt"
	"os"
	"strconv"
	"strings"
	"time"
)

// BackupError is raised when backup fails
type BackupError struct {
	Message string
}

func (e *BackupError) Error() string {
	return e.Message
}

// Backup handles backup operations
type Backup struct {
	cfg        *libs.LabConfig
	lxcService *services.LXCService
	pctService *services.PCTService
}

// getDirectorySize gets the size of a directory in bytes
func (b *Backup) getDirectorySize(containerID int, path string, timeout *int) (int64, error) {
	cmd := fmt.Sprintf("du -sb %s | cut -f1", path)
	timeoutVal := 300 // Default 5 minutes for large directories
	if timeout != nil && *timeout > 0 {
		// Use custom timeout if provided, but cap at reasonable value for size check
		if *timeout > 600 {
			timeoutVal = 600 // Max 10 minutes for size check
		} else {
			timeoutVal = *timeout
		}
	}
	output, exitCode := b.pctService.Execute(containerID, cmd, &timeoutVal)
	if exitCode == nil || *exitCode != 0 {
		return 0, fmt.Errorf("failed to get directory size: %s", output)
	}
	size, err := strconv.ParseInt(strings.TrimSpace(output), 10, 64)
	if err != nil {
		return 0, fmt.Errorf("failed to parse size: %w", err)
	}
	return size, nil
}

// formatSize formats bytes into human-readable format
func formatSize(bytes int64) string {
	const unit = 1024
	if bytes < unit {
		return fmt.Sprintf("%d B", bytes)
	}
	div, exp := int64(unit), 0
	for n := bytes / unit; n >= unit; n /= unit {
		div *= unit
		exp++
	}
	return fmt.Sprintf("%.2f %cB", float64(bytes)/float64(div), "KMGTPE"[exp])
}

// checkSpaceUsage checks what's taking up space in a directory
func (b *Backup) checkSpaceUsage(containerID int, path string, topN int, timeout *int) (string, error) {
	cmd := fmt.Sprintf("du -h --max-depth=1 %s | sort -hr | head -n %d", path, topN)
	timeoutVal := 300 // Default 5 minutes for large directories
	if timeout != nil && *timeout > 0 {
		// Use custom timeout if provided, but cap at reasonable value for space check
		if *timeout > 600 {
			timeoutVal = 600 // Max 10 minutes for space check
		} else {
			timeoutVal = *timeout
		}
	}
	output, exitCode := b.pctService.Execute(containerID, cmd, &timeoutVal)
	if exitCode == nil || *exitCode != 0 {
		return "", fmt.Errorf("failed to check space usage: %s", output)
	}
	return output, nil
}

// NewBackup creates a new Backup command
func NewBackup(cfg *libs.LabConfig, lxcService *services.LXCService, pctService *services.PCTService) *Backup {
	return &Backup{
		cfg:        cfg,
		lxcService: lxcService,
		pctService: pctService,
	}
}

// Run executes the backup
func (b *Backup) Run(customTimeout *int, excludePaths []string, showSizes bool, checkSpace bool) error {
	logger := libs.GetLogger("backup")
	defer func() {
		if r := recover(); r != nil {
			if err, ok := r.(error); ok {
				logger.Error("Error during backup: %s", err.Error())
				logger.LogTraceback(err)
			} else {
				logger.Error("Error during backup: %v", r)
			}
			os.Exit(1)
		}
	}()
	logger.InfoBanner("Backing Up Cluster")
	if customTimeout != nil && *customTimeout > 0 {
		logger.Info("Using custom timeout: %d seconds", *customTimeout)
	}
	if b.cfg.Backup == nil {
		logger.Error("Backup configuration not found in enva.yaml")
		return &BackupError{Message: "Backup configuration not found"}
	}
	if !b.lxcService.Connect() {
		logger.Error("Failed to connect to LXC host %s", b.cfg.LXCHost())
		return &BackupError{Message: "Failed to connect to LXC host"}
	}
	defer b.lxcService.Disconnect()
	var backupContainer *libs.ContainerConfig
	for i := range b.cfg.Containers {
		if b.cfg.Containers[i].ID == b.cfg.Backup.ContainerID {
			backupContainer = &b.cfg.Containers[i]
			break
		}
	}
	if backupContainer == nil {
		logger.Error("Backup container with ID %d not found", b.cfg.Backup.ContainerID)
		return &BackupError{Message: fmt.Sprintf("Backup container with ID %d not found", b.cfg.Backup.ContainerID)}
	}
	// Restart all k3s nodes before backup to ensure clean state
	logger.Info("Restarting all k3s nodes to ensure clean state before backup...")
	if err := b.restartK3sNodes(); err != nil {
		logger.Error("Failed to restart k3s nodes: %s", err.Error())
		return &BackupError{Message: fmt.Sprintf("Failed to restart k3s nodes: %s", err.Error())}
	}

	// Wait for all pods to be ready
	logger.Info("Waiting for all pods to be ready after restart...")
	if err := b.waitForAllPodsReady(); err != nil {
		logger.Error("Failed to wait for pods to be ready: %s", err.Error())
		return &BackupError{Message: fmt.Sprintf("Failed to wait for pods to be ready: %s", err.Error())}
	}
	logger.Info("All nodes restarted and pods are ready, proceeding with backup...")

	timestamp := time.Now().Format("20060102_150405")
	backupName := fmt.Sprintf("%s-%s", b.cfg.Backup.NamePrefix, timestamp)
	logger.Info("Creating backup: %s", backupName)
	logger.Info("Backup will be stored on container %d at %s", backupContainer.ID, b.cfg.Backup.BackupDir)
	mkdirCmd := fmt.Sprintf("mkdir -p %s", b.cfg.Backup.BackupDir)
	timeout := 30
	mkdirOutput, mkdirExit := b.pctService.Execute(backupContainer.ID, mkdirCmd, &timeout)
	if mkdirExit != nil && *mkdirExit != 0 {
		logger.Error("Failed to create backup directory: %s", mkdirOutput)
		return &BackupError{Message: "Failed to create backup directory"}
	}
	type backupFile struct {
		sourceID int
		tempFile string
		destName string
	}
	var backupFiles []backupFile
	for _, item := range b.cfg.Backup.Items {
		logger.Info("Backing up item: %s from container %d", item.Name, item.SourceContainerID)

		// Show size before backup if requested
		if showSizes && item.ArchiveBase != nil && item.ArchivePath != nil {
			fullPath := fmt.Sprintf("%s/%s", *item.ArchiveBase, *item.ArchivePath)
			size, err := b.getDirectorySize(item.SourceContainerID, fullPath, customTimeout)
			if err == nil {
				logger.Info("  Directory size: %s", formatSize(size))
			} else {
				logger.Info("  Could not determine size: %v", err)
			}
		} else if showSizes && item.SourcePath != "" {
			size, err := b.getDirectorySize(item.SourceContainerID, item.SourcePath, customTimeout)
			if err == nil {
				logger.Info("  File/directory size: %s", formatSize(size))
			} else {
				logger.Info("  Could not determine size: %v", err)
			}
		}

		// Check space usage if requested
		if checkSpace && item.ArchiveBase != nil && item.ArchivePath != nil {
			fullPath := fmt.Sprintf("%s/%s", *item.ArchiveBase, *item.ArchivePath)
			logger.Info("  Checking space usage in %s...", fullPath)
			usage, err := b.checkSpaceUsage(item.SourceContainerID, fullPath, 10, customTimeout)
			if err == nil {
				logger.Info("  Top space consumers:\n%s", usage)
			} else {
				logger.Info("  Could not check space usage: %v", err)
			}
		} else if checkSpace && item.SourcePath != "" {
			logger.Info("  Checking space usage in %s...", item.SourcePath)
			usage, err := b.checkSpaceUsage(item.SourceContainerID, item.SourcePath, 10, customTimeout)
			if err == nil {
				logger.Info("  Top space consumers:\n%s", usage)
			} else {
				logger.Info("  Could not check space usage: %v", err)
			}
		}
		var sourceContainer *libs.ContainerConfig
		for i := range b.cfg.Containers {
			if b.cfg.Containers[i].ID == item.SourceContainerID {
				sourceContainer = &b.cfg.Containers[i]
				break
			}
		}
		if sourceContainer == nil {
			logger.Error("Source container %d not found for backup item %s", item.SourceContainerID, item.Name)
			return &BackupError{Message: fmt.Sprintf("Source container %d not found", item.SourceContainerID)}
		}
		tempFile := fmt.Sprintf("/tmp/%s-%s", backupName, item.Name)
		if item.ArchiveBase != nil && item.ArchivePath != nil {
			logger.Info("  Archiving: %s", item.SourcePath)
			archiveFile := fmt.Sprintf("%s.tar.gz", tempFile)
			if item.Name == "k3s-control-data" {
				logger.Info("  Checkpointing SQLite WAL to ensure all data is in main database file...")
				dbPath := fmt.Sprintf("%s/server/db/state.db", item.SourcePath)
				checkpointCmd := fmt.Sprintf("python3 -c \"import sqlite3; conn = sqlite3.connect('%s'); result = conn.execute('PRAGMA wal_checkpoint(TRUNCATE)').fetchone(); conn.close(); print(f'Checkpoint: {result}')\"", dbPath)
				checkpointTimeout := 30
				checkpointOutput, checkpointExit := b.pctService.Execute(item.SourceContainerID, checkpointCmd, &checkpointTimeout)
				checkpointTimedOut := checkpointExit == nil
				if checkpointTimedOut {
					logger.Info("  SQLite WAL checkpoint timed out (directory may be corrupted/inaccessible): %s", checkpointOutput)
				} else if *checkpointExit == 0 {
					output := checkpointOutput
					if output == "" {
						output = "OK"
					}
					logger.Info("  SQLite WAL checkpoint completed: %s", strings.TrimSpace(output))
				} else {
					logger.Info("  SQLite WAL checkpoint failed (may not be in WAL mode): %s", checkpointOutput)
				}
			}
			// Build exclude options for tar
			excludeOpts := ""
			if len(excludePaths) > 0 {
				logger.Info("  Excluding paths: %v", excludePaths)
				for _, excludePath := range excludePaths {
					// Exclude path is relative to the archive path being backed up
					excludeOpts += fmt.Sprintf(" --exclude='%s'", excludePath)
				}
			}
			archiveCmd := fmt.Sprintf("tar --warning=no-file-changed -czvf %s%s -C %s %s", archiveFile, excludeOpts, *item.ArchiveBase, *item.ArchivePath)
			if customTimeout != nil && *customTimeout > 0 {
				timeout = *customTimeout
			} else {
				timeout = 300
			}
			archiveOutput, archiveExit := b.pctService.Execute(item.SourceContainerID, archiveCmd, &timeout)
			archiveTimedOut := archiveExit == nil
			if archiveTimedOut {
				logger.Info("  Archive command timed out after %d seconds, checking if file was created...", timeout)
			}
			// Use longer timeout for verification, especially after a timeout
			verifyTimeout := 30
			if archiveTimedOut {
				verifyTimeout = 60
			}
			verifyCmd := fmt.Sprintf("test -f %s && echo exists || echo missing", archiveFile)
			timeout = verifyTimeout
			verifyOutput, verifyExit := b.pctService.Execute(item.SourceContainerID, verifyCmd, &timeout)
			// If verification also times out, try one more time with a longer wait
			if verifyExit == nil {
				logger.Info("  Verification timed out, waiting 10 seconds and retrying...")
				time.Sleep(10 * time.Second)
				timeout = 60
				verifyOutput, verifyExit = b.pctService.Execute(item.SourceContainerID, verifyCmd, &timeout)
			}
			if verifyExit == nil || *verifyExit != 0 || verifyOutput == "" || strings.Contains(verifyOutput, "missing") {
				logger.Error("  Failed to create archive for %s: %s", item.Name, archiveOutput)
				if archiveTimedOut {
					logger.Error("  Archive command timed out and file verification failed. This usually indicates filesystem corruption or inaccessible directory.")
					// For k3s-control-data, try backing up subdirectories individually
					if item.Name == "k3s-control-data" {
						logger.Info("  Attempting to backup k3s-control-data subdirectories individually...")
						// Try backing up server and storage separately
						serverPath := fmt.Sprintf("%s/server", *item.ArchivePath)
						storagePath := fmt.Sprintf("%s/storage", *item.ArchivePath)
						serverFile := fmt.Sprintf("%s-server.tar.gz", tempFile)
						storageFile := fmt.Sprintf("%s-storage.tar.gz", tempFile)

						backedUpAny := false

						// Try server directory
						serverCmd := fmt.Sprintf("tar --warning=no-file-changed -czvf %s -C %s %s", serverFile, *item.ArchiveBase, serverPath)
						serverOutput, serverExit := b.pctService.Execute(item.SourceContainerID, serverCmd, &timeout)
						if serverExit != nil && *serverExit == 0 {
							// Verify server file exists
							serverVerifyCmd := fmt.Sprintf("test -f %s && echo exists || echo missing", serverFile)
							serverVerifyOutput, serverVerifyExit := b.pctService.Execute(item.SourceContainerID, serverVerifyCmd, &verifyTimeout)
							if serverVerifyExit != nil && *serverVerifyExit == 0 && strings.Contains(serverVerifyOutput, "exists") {
								logger.Info("  Successfully backed up server subdirectory")
								backupFiles = append(backupFiles, backupFile{
									sourceID: item.SourceContainerID,
									tempFile: serverFile,
									destName: fmt.Sprintf("%s-%s-server.tar.gz", backupName, item.Name),
								})
								backedUpAny = true
							}
						} else {
							logger.Error("  Failed to backup server subdirectory: %s", serverOutput)
						}

						// Try storage directory
						storageCmd := fmt.Sprintf("tar --warning=no-file-changed -czvf %s -C %s %s", storageFile, *item.ArchiveBase, storagePath)
						storageOutput, storageExit := b.pctService.Execute(item.SourceContainerID, storageCmd, &timeout)
						if storageExit != nil && *storageExit == 0 {
							// Verify storage file exists
							storageVerifyCmd := fmt.Sprintf("test -f %s && echo exists || echo missing", storageFile)
							storageVerifyOutput, storageVerifyExit := b.pctService.Execute(item.SourceContainerID, storageVerifyCmd, &verifyTimeout)
							if storageVerifyExit != nil && *storageVerifyExit == 0 && strings.Contains(storageVerifyOutput, "exists") {
								logger.Info("  Successfully backed up storage subdirectory")
								backupFiles = append(backupFiles, backupFile{
									sourceID: item.SourceContainerID,
									tempFile: storageFile,
									destName: fmt.Sprintf("%s-%s-storage.tar.gz", backupName, item.Name),
								})
								backedUpAny = true
							}
						} else {
							logger.Error("  Failed to backup storage subdirectory: %s", storageOutput)
						}

						// If we didn't backup any subdirectory, fail
						if !backedUpAny {
							logger.Error("  CRITICAL: Failed to backup k3s-control-data - backup is incomplete and may not be usable")
							return &BackupError{Message: fmt.Sprintf("Failed to create archive for %s - backup incomplete", item.Name)}
						}
						logger.Info("  Backed up k3s-control-data subdirectories (partial backup)")
						continue
					}
					logger.Error("  CRITICAL: Failed to backup %s - backup is incomplete", item.Name)
					return &BackupError{Message: fmt.Sprintf("Failed to create archive for %s", item.Name)}
				}
				logger.Error("  Archive verification failed for %s", item.Name)
				return &BackupError{Message: fmt.Sprintf("Failed to create archive for %s", item.Name)}
			}
			if archiveTimedOut {
				logger.Info("  Archive file exists despite timeout - backup may have completed on server")
			}
			if archiveExit != nil && *archiveExit != 0 {
				logger.Info("  Archive created with warnings (file changed during backup - expected for live databases)")
			}
			backupFiles = append(backupFiles, backupFile{
				sourceID: item.SourceContainerID,
				tempFile: archiveFile,
				destName: fmt.Sprintf("%s.tar.gz", item.Name),
			})
		} else {
			logger.Info("  Copying file: %s", item.SourcePath)
			copyCmd := fmt.Sprintf("cp %s %s", item.SourcePath, tempFile)
			timeout = 60
			copyOutput, copyExit := b.pctService.Execute(item.SourceContainerID, copyCmd, &timeout)
			if copyExit == nil || *copyExit != 0 {
				logger.Info("  Failed to copy file for %s: %s", item.Name, copyOutput)
				return &BackupError{Message: fmt.Sprintf("Failed to copy file for %s", item.Name)}
			}
			backupFiles = append(backupFiles, backupFile{
				sourceID: item.SourceContainerID,
				tempFile: tempFile,
				destName: item.Name,
			})
		}
	}
	logger.Info("Copying backup files to backup container...")
	var backupFileNames []string
	for _, bf := range backupFiles {
		lxcTemp := fmt.Sprintf("/tmp/%s-%s", backupName, bf.destName)
		pullCmd := fmt.Sprintf("pct pull %d %s %s", bf.sourceID, bf.tempFile, lxcTemp)
		timeout = 300
		pullOutput, pullExit := b.lxcService.Execute(pullCmd, &timeout)
		if pullExit == nil || *pullExit != 0 {
			logger.Error("Failed to pull file from container %d: %s", bf.sourceID, pullOutput)
			return &BackupError{Message: fmt.Sprintf("Failed to pull file from container %d", bf.sourceID)}
		}
		backupFilePath := fmt.Sprintf("%s/%s-%s", b.cfg.Backup.BackupDir, backupName, bf.destName)
		pushCmd := fmt.Sprintf("pct push %d %s %s", backupContainer.ID, lxcTemp, backupFilePath)
		timeout = 300
		pushOutput, pushExit := b.lxcService.Execute(pushCmd, &timeout)
		if pushExit == nil || *pushExit != 0 {
			logger.Error("Failed to push file to backup container: %s", pushOutput)
			return &BackupError{Message: "Failed to push file to backup container"}
		}
		timeout = 30
		b.lxcService.Execute(fmt.Sprintf("rm -f %s", lxcTemp), &timeout)
		timeout = 30
		b.pctService.Execute(bf.sourceID, fmt.Sprintf("rm -f %s %s.tar.gz || true", bf.tempFile, bf.tempFile), &timeout)
		backupFileNames = append(backupFileNames, fmt.Sprintf("%s-%s", backupName, bf.destName))
	}
	logger.Info("Creating final backup tarball...")
	finalTarball := fmt.Sprintf("%s/%s.tar.gz", b.cfg.Backup.BackupDir, backupName)
	tarFilesList := strings.Join(backupFileNames, " ")
	tarCmd := fmt.Sprintf("cd %s && tar -czvf %s.tar.gz %s", b.cfg.Backup.BackupDir, backupName, tarFilesList)
	if customTimeout != nil && *customTimeout > 0 {
		timeout = *customTimeout
	} else {
		timeout = 300
	}
	tarOutput, tarExit := b.pctService.Execute(backupContainer.ID, tarCmd, &timeout)
	if tarExit == nil || *tarExit != 0 {
		logger.Error("Failed to create final tarball: %s", tarOutput)
		return &BackupError{Message: "Failed to create final tarball"}
	}
	cleanupCmd := fmt.Sprintf("cd %s && rm -f %s || true", b.cfg.Backup.BackupDir, tarFilesList)
	timeout = 30
	b.pctService.Execute(backupContainer.ID, cleanupCmd, &timeout)
	logger.InfoBannerStart()
	logger.Info("Backup completed successfully!")
	logger.Info("Backup name: %s", backupName)
	logger.Info("Backup location: %s on container %d", finalTarball, backupContainer.ID)
	logger.InfoBannerEnd()
	return nil
}

// restartK3sNodes restarts all k3s nodes (containers) to ensure clean state
func (b *Backup) restartK3sNodes() error {
	logger := libs.GetLogger("backup")

	// Find k3s control and worker nodes from backup items
	var controlNodeID *int
	var workerNodeIDs []int
	seenContainers := make(map[int]bool)

	for _, item := range b.cfg.Backup.Items {
		if strings.HasPrefix(item.Name, "k3s-") {
			containerID := item.SourceContainerID
			if seenContainers[containerID] {
				continue
			}
			seenContainers[containerID] = true

			if strings.Contains(item.Name, "control") {
				controlNodeID = &containerID
			} else if strings.Contains(item.Name, "worker") {
				workerNodeIDs = append(workerNodeIDs, containerID)
			}
		}
	}

	// Restart control node first
	if controlNodeID != nil {
		logger.Info("Restarting control node container %d...", *controlNodeID)
		// Stop container
		_, stopExit := b.pctService.Stop(*controlNodeID, false)
		if stopExit != nil && *stopExit != 0 {
			logger.Info("Container %d may not have been running, continuing...", *controlNodeID)
		}
		// Wait a bit for clean shutdown
		time.Sleep(5 * time.Second)
		// Start container
		_, startExit := b.pctService.Start(*controlNodeID)
		if startExit != nil && *startExit != 0 {
			return fmt.Errorf("failed to start control node container %d", *controlNodeID)
		}
		logger.Info("Control node container %d restarted, waiting for it to be ready...", *controlNodeID)
		// Wait for container to be ready
		maxWait := 120
		waitTime := 0
		for waitTime < maxWait {
			testCmd := "echo test"
			testOutput, testExit := b.pctService.Execute(*controlNodeID, testCmd, libs.IntPtr(5))
			if testExit != nil && *testExit == 0 && strings.Contains(testOutput, "test") {
				logger.Info("Control node container %d is ready", *controlNodeID)
				break
			}
			time.Sleep(5 * time.Second)
			waitTime += 5
		}
		if waitTime >= maxWait {
			return fmt.Errorf("control node container %d did not become ready after %d seconds", *controlNodeID, maxWait)
		}
	}

	// Restart worker nodes
	for _, workerID := range workerNodeIDs {
		logger.Info("Restarting worker node container %d...", workerID)
		// Stop container
		_, stopExit := b.pctService.Stop(workerID, false)
		if stopExit != nil && *stopExit != 0 {
			logger.Info("Container %d may not have been running, continuing...", workerID)
		}
		// Wait a bit for clean shutdown
		time.Sleep(5 * time.Second)
		// Start container
		_, startExit := b.pctService.Start(workerID)
		if startExit != nil && *startExit != 0 {
			return fmt.Errorf("failed to start worker node container %d", workerID)
		}
		logger.Info("Worker node container %d restarted, waiting for it to be ready...", workerID)
		// Wait for container to be ready
		maxWait := 120
		waitTime := 0
		for waitTime < maxWait {
			testCmd := "echo test"
			testOutput, testExit := b.pctService.Execute(workerID, testCmd, libs.IntPtr(5))
			if testExit != nil && *testExit == 0 && strings.Contains(testOutput, "test") {
				logger.Info("Worker node container %d is ready", workerID)
				break
			}
			time.Sleep(5 * time.Second)
			waitTime += 5
		}
		if waitTime >= maxWait {
			return fmt.Errorf("worker node container %d did not become ready after %d seconds", workerID, maxWait)
		}
	}

	logger.Info("All k3s node containers restarted successfully")
	return nil
}

// waitForAllPodsReady waits for all pods in the cluster to be ready
func (b *Backup) waitForAllPodsReady() error {
	logger := libs.GetLogger("backup")

	// Find k3s control node
	var controlNodeID *int
	for _, item := range b.cfg.Backup.Items {
		if strings.HasPrefix(item.Name, "k3s-") && strings.Contains(item.Name, "control") {
			controlNodeID = &item.SourceContainerID
			break
		}
	}

	if controlNodeID == nil {
		logger.Info("No k3s control node found in backup items, skipping pod readiness check")
		return nil
	}

	logger.Info("Waiting for k3s control plane to be ready...")
	maxWait := 300 // 5 minutes maximum timeout
	waitTime := 0
	controlPlaneReady := false
	for waitTime < maxWait {
		// Check if k3s service is active
		checkCmd := "systemctl is-active k3s || echo inactive"
		timeout := 30
		checkOutput, checkExit := b.pctService.Execute(*controlNodeID, checkCmd, &timeout)
		if checkExit != nil && *checkExit == 0 && strings.Contains(checkOutput, "active") {
			// Check if kubectl is available and nodes are ready
			nodeCmd := "export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get nodes --no-headers 2>&1 | grep -q Ready || echo not-ready"
			nodeOutput, nodeExit := b.pctService.Execute(*controlNodeID, nodeCmd, &timeout)
			if nodeExit != nil && *nodeExit == 0 && !strings.Contains(nodeOutput, "not-ready") {
				logger.Info("k3s control plane is ready")
				controlPlaneReady = true
				break
			}
		}
		logger.Info("Waiting for k3s control plane to be ready (waited %d/%d seconds)...", waitTime, maxWait)
		time.Sleep(10 * time.Second)
		waitTime += 10
	}
	if !controlPlaneReady {
		return fmt.Errorf("k3s control plane did not become ready after %d seconds", maxWait)
	}

	logger.Info("Waiting until all pods are ready...")
	maxWaitPods := 600 // 10 minutes maximum timeout
	waitTimePods := 0
	allPodsReady := false
	for waitTimePods < maxWaitPods {
		// Get all pods and check if they're all ready
		podsCmd := "export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get pods --all-namespaces --field-selector=status.phase!=Succeeded --no-headers -o jsonpath='{range .items[*]}{.metadata.namespace}{\"/\"}{.metadata.name}{\" \"}{.status.phase}{\" \"}{.status.containerStatuses[*].ready}{\"\\n\"}{end}' 2>&1"
		timeout := 60
		podsOutput, podsExit := b.pctService.Execute(*controlNodeID, podsCmd, &timeout)

		if podsExit != nil && *podsExit == 0 {
			// Parse output to check if all pods are ready
			allReady := true
			lines := strings.Split(strings.TrimSpace(podsOutput), "\n")
			nonSucceededPods := 0
			for _, line := range lines {
				if line == "" {
					continue
				}
				parts := strings.Fields(line)
				if len(parts) >= 3 {
					phase := parts[1]
					ready := parts[2]
					// Skip if phase is Succeeded (completed jobs)
					if phase == "Succeeded" {
						continue
					}
					nonSucceededPods++
					// Check if pod is running and ready
					if phase != "Running" || !strings.Contains(ready, "true") {
						allReady = false
						break
					}
				}
			}

			if allReady && nonSucceededPods > 0 {
				logger.Info("All pods are ready")
				allPodsReady = true
				break
			} else if nonSucceededPods == 0 {
				// No pods yet, keep waiting
				logger.Info("No pods found yet, waiting...")
			}
		}

		logger.Info("Waiting for all pods to be ready (waited %d/%d seconds)...", waitTimePods, maxWaitPods)
		time.Sleep(15 * time.Second)
		waitTimePods += 15
	}

	if !allPodsReady {
		return fmt.Errorf("not all pods are ready after %d seconds", maxWaitPods)
	}

	return nil
}
