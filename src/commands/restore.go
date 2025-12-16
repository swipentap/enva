package commands

import (
	"enva/libs"
	"enva/services"
	"fmt"
	"os"
	"sort"
	"strings"
	"time"
)

// RestoreError is raised when restore fails
type RestoreError struct {
	Message string
}

func (e *RestoreError) Error() string {
	return e.Message
}

// Restore handles restore operations
type Restore struct {
	cfg        *libs.LabConfig
	lxcService *services.LXCService
	pctService *services.PCTService
}

// NewRestore creates a new Restore command
func NewRestore(cfg *libs.LabConfig, lxcService *services.LXCService, pctService *services.PCTService) *Restore {
	return &Restore{
		cfg:        cfg,
		lxcService: lxcService,
		pctService: pctService,
	}
}

// Run executes the restore
func (r *Restore) Run(backupName string) error {
	logger := libs.GetLogger("restore")
	defer func() {
		if rec := recover(); rec != nil {
			if err, ok := rec.(error); ok {
				logger.Error("Error during restore: %s", err.Error())
				logger.LogTraceback(err)
			} else {
				logger.Error("Error during restore: %v", rec)
			}
			os.Exit(1)
		}
	}()
	logger.Info("==================================================")
	logger.Info("Restoring Cluster from Backup")
	logger.Info("==================================================")
	if backupName == "" {
		logger.Error("Backup name is required. Use --backup-name <name>")
		return &RestoreError{Message: "Backup name is required"}
	}
	if r.cfg.Backup == nil {
		logger.Error("Backup configuration not found in enva.yaml")
		return &RestoreError{Message: "Backup configuration not found"}
	}
	if !r.lxcService.Connect() {
		logger.Error("Failed to connect to LXC host %s", r.cfg.LXCHost())
		return &RestoreError{Message: "Failed to connect to LXC host"}
	}
	defer r.lxcService.Disconnect()
	var backupContainer *libs.ContainerConfig
	for i := range r.cfg.Containers {
		if r.cfg.Containers[i].ID == r.cfg.Backup.ContainerID {
			backupContainer = &r.cfg.Containers[i]
			break
		}
	}
	if backupContainer == nil {
		logger.Error("Backup container with ID %d not found", r.cfg.Backup.ContainerID)
		return &RestoreError{Message: fmt.Sprintf("Backup container with ID %d not found", r.cfg.Backup.ContainerID)}
	}
	backupTarball := fmt.Sprintf("%s/%s.tar.gz", r.cfg.Backup.BackupDir, backupName)
	logger.Info("Restoring from backup: %s", backupName)
	logger.Info("Backup location: %s", backupTarball)
	logger.Info("Verifying backup exists...")
	checkCmd := fmt.Sprintf("test -f %s && echo exists || echo missing", backupTarball)
	timeout := 30
	checkOutput, checkExit := r.pctService.Execute(backupContainer.ID, checkCmd, &timeout)
	if checkExit == nil || *checkExit != 0 || checkOutput == "" || strings.Contains(checkOutput, "missing") {
		logger.Error("Backup not found at %s", backupTarball)
		return &RestoreError{Message: fmt.Sprintf("Backup not found: %s", backupName)}
	}
	logger.Info("Extracting backup tarball...")
	extractDir := fmt.Sprintf("%s/%s", r.cfg.Backup.BackupDir, backupName)
	extractCmd := fmt.Sprintf("mkdir -p %s && cd %s && tar -xzf %s.tar.gz -C %s 2>&1", extractDir, r.cfg.Backup.BackupDir, backupName, extractDir)
	timeout = 300
	extractOutput, extractExit := r.pctService.Execute(backupContainer.ID, extractCmd, &timeout)
	if extractExit == nil || *extractExit != 0 {
		logger.Error("Failed to extract backup: %s", extractOutput)
		return &RestoreError{Message: "Failed to extract backup"}
	}
	var controlNodeID *int
	var workerNodeIDs []int
	var k3sItems []libs.BackupItemConfig
	for _, item := range r.cfg.Backup.Items {
		if strings.HasPrefix(item.Name, "k3s-") {
			k3sItems = append(k3sItems, item)
		}
	}
	if len(k3sItems) > 0 {
		if r.cfg.Kubernetes != nil && len(r.cfg.Kubernetes.Control) > 0 {
			id := r.cfg.Kubernetes.Control[0]
			controlNodeID = &id
		}
		if r.cfg.Kubernetes != nil && len(r.cfg.Kubernetes.Workers) > 0 {
			workerNodeIDs = r.cfg.Kubernetes.Workers
		}
		logger.Info("Stopping all k3s services before restore...")
		if controlNodeID != nil {
			logger.Info("Stopping k3s service on control node %d...", *controlNodeID)
			stopCmd := "systemctl stop k3s"
			timeout = 60
			stopOutput, stopExit := r.pctService.Execute(*controlNodeID, stopCmd, &timeout)
			if stopExit == nil || *stopExit != 0 {
				logger.Error("Failed to stop k3s on control node (may not be running): %s", stopOutput)
			} else {
				logger.Info("k3s service stopped on control node")
			}
		}
		for _, workerID := range workerNodeIDs {
			logger.Info("Stopping k3s-agent service on worker node %d...", workerID)
			stopAgentCmd := "systemctl stop k3s-agent 2>&1 || true"
			timeout = 60
			stopAgentOutput, stopAgentExit := r.pctService.Execute(workerID, stopAgentCmd, &timeout)
			if stopAgentExit == nil || *stopAgentExit != 0 {
				logger.Error("Failed to stop k3s-agent on worker %d (may not be running): %s", workerID, stopAgentOutput)
			} else {
				logger.Info("k3s-agent service stopped on worker %d", workerID)
			}
		}
		logger.Info("All k3s services stopped")
		var controlDataItem *libs.BackupItemConfig
		for i := range r.cfg.Backup.Items {
			if r.cfg.Backup.Items[i].Name == "k3s-control-data" {
				controlDataItem = &r.cfg.Backup.Items[i]
				break
			}
		}
		if controlNodeID != nil && controlDataItem != nil {
			logger.Info("Clearing existing k3s state on control node to ensure clean restore...")
			removeCmd := fmt.Sprintf("rm -rf %s && mkdir -p %s && chmod 700 %s 2>&1 || true", controlDataItem.SourcePath, controlDataItem.SourcePath, controlDataItem.SourcePath)
			timeout = 30
			removeOutput, removeExit := r.pctService.Execute(*controlNodeID, removeCmd, &timeout)
			if removeExit == nil || *removeExit != 0 {
				logger.Error("Failed to clear k3s state (may not exist): %s", removeOutput)
			} else {
				logger.Info("k3s state cleared on control node")
			}
		}
	}
	itemsToRestore := make([]libs.BackupItemConfig, len(r.cfg.Backup.Items))
	copy(itemsToRestore, r.cfg.Backup.Items)
	sort.Slice(itemsToRestore, func(i, j int) bool {
		itemI := itemsToRestore[i]
		itemJ := itemsToRestore[j]
		priorityI := getRestorePriority(itemI.Name)
		priorityJ := getRestorePriority(itemJ.Name)
		return priorityI < priorityJ
	})
	for _, item := range itemsToRestore {
		logger.Info("Restoring item: %s to container %d", item.Name, item.SourceContainerID)
		var sourceContainer *libs.ContainerConfig
		for i := range r.cfg.Containers {
			if r.cfg.Containers[i].ID == item.SourceContainerID {
				sourceContainer = &r.cfg.Containers[i]
				break
			}
		}
		if sourceContainer == nil {
			logger.Error("Source container %d not found for restore item %s", item.SourceContainerID, item.Name)
			return &RestoreError{Message: fmt.Sprintf("Source container %d not found", item.SourceContainerID)}
		}
		isArchive := item.ArchiveBase != nil && item.ArchivePath != nil
		backupFileName := fmt.Sprintf("%s-%s", backupName, item.Name)
		if isArchive {
			backupFileName += ".tar.gz"
		}
		backupFilePath := fmt.Sprintf("%s/%s", extractDir, backupFileName)
		checkFileCmd := fmt.Sprintf("test -f %s && echo exists || echo missing", backupFilePath)
		timeout = 30
		checkFileOutput, checkFileExit := r.pctService.Execute(backupContainer.ID, checkFileCmd, &timeout)
		if checkFileExit == nil || *checkFileExit != 0 || checkFileOutput == "" || strings.Contains(checkFileOutput, "missing") {
			logger.Error("Backup file not found for item %s, skipping...", item.Name)
			continue
		}
		lxcTemp := fmt.Sprintf("/tmp/%s", backupFileName)
		pullCmd := fmt.Sprintf("pct pull %d %s %s", backupContainer.ID, backupFilePath, lxcTemp)
		timeout = 300
		pullOutput, pullExit := r.lxcService.Execute(pullCmd, &timeout)
		if pullExit == nil || *pullExit != 0 {
			logger.Error("Failed to pull backup file: %s", pullOutput)
			return &RestoreError{Message: "Failed to pull backup file"}
		}
		sourceTemp := fmt.Sprintf("/tmp/%s", backupFileName)
		pushCmd := fmt.Sprintf("pct push %d %s %s", item.SourceContainerID, lxcTemp, sourceTemp)
		timeout = 300
		pushOutput, pushExit := r.lxcService.Execute(pushCmd, &timeout)
		if pushExit == nil || *pushExit != 0 {
			logger.Error("Failed to push backup file to source container: %s", pushOutput)
			return &RestoreError{Message: "Failed to push backup file to source container"}
		}
		timeout = 30
		r.lxcService.Execute(fmt.Sprintf("rm -f %s", lxcTemp), &timeout)
		if isArchive {
			logger.Info("  Extracting archive to: %s", item.SourcePath)
			if item.Name == "k3s-etcd" {
				dbDir := item.SourcePath
				removeDBCmd := fmt.Sprintf("rm -rf %s/* 2>&1 || true", dbDir)
				timeout = 30
				r.pctService.Execute(item.SourceContainerID, removeDBCmd, &timeout)
			} else {
				backupExistingCmd := fmt.Sprintf("mv %s %s.backup.$(date +%%s) 2>&1 || true", item.SourcePath, item.SourcePath)
				timeout = 30
				r.pctService.Execute(item.SourceContainerID, backupExistingCmd, &timeout)
			}
			extractCmd := fmt.Sprintf("mkdir -p %s && tar -xzf %s -C %s 2>&1", *item.ArchiveBase, sourceTemp, *item.ArchiveBase)
			timeout = 300
			extractOutput2, extractExit2 := r.pctService.Execute(item.SourceContainerID, extractCmd, &timeout)
			if extractExit2 == nil || *extractExit2 != 0 {
				logger.Info("  Failed to extract archive: %s", extractOutput2)
				return &RestoreError{Message: fmt.Sprintf("Failed to extract archive for %s", item.Name)}
			}
		} else {
			logger.Info("  Copying file to: %s", item.SourcePath)
			backupExistingCmd := fmt.Sprintf("mv %s %s.backup.$(date +%%s) 2>&1 || true", item.SourcePath, item.SourcePath)
			timeout = 30
			r.pctService.Execute(item.SourceContainerID, backupExistingCmd, &timeout)
			copyCmd := fmt.Sprintf("cp %s %s 2>&1", sourceTemp, item.SourcePath)
			timeout = 60
			copyOutput, copyExit := r.pctService.Execute(item.SourceContainerID, copyCmd, &timeout)
			if copyExit == nil || *copyExit != 0 {
				logger.Info("  Failed to copy file: %s", copyOutput)
				return &RestoreError{Message: fmt.Sprintf("Failed to copy file for %s", item.Name)}
			}
			if item.Name == "k3s-token" {
				tokenCopyCmd := fmt.Sprintf("cp %s /var/lib/rancher/k3s/server/token 2>&1", item.SourcePath)
				timeout = 30
				tokenCopyOutput, tokenCopyExit := r.pctService.Execute(item.SourceContainerID, tokenCopyCmd, &timeout)
				if tokenCopyExit == nil || *tokenCopyExit != 0 {
					logger.Info("  Failed to copy token to server/token location: %s", tokenCopyOutput)
				} else {
					logger.Info("  Token also copied to /var/lib/rancher/k3s/server/token")
				}
			}
			if strings.Contains(item.Name, "service-env") {
				dirPath := "/etc/systemd/system"
				mkdirCmd := fmt.Sprintf("mkdir -p %s 2>&1 || true", dirPath)
				timeout = 30
				r.pctService.Execute(item.SourceContainerID, mkdirCmd, &timeout)
				reloadCmd := "systemctl daemon-reload 2>&1"
				timeout = 30
				reloadOutput, reloadExit := r.pctService.Execute(item.SourceContainerID, reloadCmd, &timeout)
				if reloadExit == nil || *reloadExit != 0 {
					logger.Info("  Failed to reload systemd daemon: %s", reloadOutput)
				} else {
					logger.Info("  Systemd daemon reloaded to pick up restored service.env")
				}
			}
		}
		timeout = 30
		r.pctService.Execute(item.SourceContainerID, fmt.Sprintf("rm -f %s 2>&1 || true", sourceTemp), &timeout)
	}
	cleanupCmd := fmt.Sprintf("rm -rf %s 2>&1 || true", extractDir)
	timeout = 30
	r.pctService.Execute(backupContainer.ID, cleanupCmd, &timeout)
	if len(k3sItems) > 0 {
		if controlNodeID != nil {
			logger.Info("Removing credential files that may conflict with restored datastore...")
			credCleanupCmd := "rm -f /var/lib/rancher/k3s/server/cred/passwd /var/lib/rancher/k3s/server/cred/ipsec.psk 2>&1 || true"
			timeout = 30
			r.pctService.Execute(*controlNodeID, credCleanupCmd, &timeout)
			logger.Info("Starting k3s service on control node with restored data...")
			startCmd := "systemctl start k3s"
			timeout = 60
			startOutput, startExit := r.pctService.Execute(*controlNodeID, startCmd, &timeout)
			if startExit == nil || *startExit != 0 {
				logger.Error("Failed to start k3s on control node: %s", startOutput)
				return &RestoreError{Message: "Failed to start k3s after restore"}
			}
			logger.Info("k3s service started on control node, waiting for it to be ready...")
			maxWait := 120
			waitTime := 0
			for waitTime < maxWait {
				checkCmd2 := "systemctl is-active k3s && kubectl get nodes 2>&1 | grep -q Ready || echo not-ready"
				timeout = 30
				checkOutput2, checkExit2 := r.pctService.Execute(*controlNodeID, checkCmd2, &timeout)
				if checkExit2 != nil && *checkExit2 == 0 && !strings.Contains(checkOutput2, "not-ready") {
					logger.Info("k3s control plane is ready")
					break
				}
				logger.Info("Waiting for k3s control plane to be ready (waited %d/%d seconds)...", waitTime, maxWait)
				time.Sleep(5 * time.Second)
				waitTime += 5
			}
			if waitTime >= maxWait {
				logger.Info("k3s control plane may not be fully ready after restore, but continuing...")
			}
		}
		if len(workerNodeIDs) > 0 {
			logger.Info("Starting k3s-agent services on worker nodes...")
			for _, workerID := range workerNodeIDs {
				logger.Info("Starting k3s-agent service on worker node %d...", workerID)
				startAgentCmd := "systemctl start k3s-agent"
				timeout = 60
				startAgentOutput, startAgentExit := r.pctService.Execute(workerID, startAgentCmd, &timeout)
				if startAgentExit == nil || *startAgentExit != 0 {
					logger.Error("Failed to start k3s-agent on worker %d: %s", workerID, startAgentOutput)
				} else {
					logger.Info("k3s-agent service started on worker %d", workerID)
				}
			}
			logger.Info("Waiting for worker nodes to connect to control plane...")
			time.Sleep(10 * time.Second)
			if controlNodeID != nil {
				verifyCmd := "kubectl get nodes 2>&1"
				timeout = 30
				verifyOutput, verifyExit := r.pctService.Execute(*controlNodeID, verifyCmd, &timeout)
				if verifyExit != nil && *verifyExit == 0 && verifyOutput != "" {
					logger.Info("Current node status:\n%s", verifyOutput)
				}
			}
		}
		if controlNodeID != nil {
			logger.Info("Checking Rancher first-login setting consistency...")
			time.Sleep(10 * time.Second)
			checkFirstLoginCmd := "kubectl get settings.management.cattle.io first-login -o jsonpath='{.value}' 2>&1 || echo 'not-found'"
			timeout = 30
			firstLoginValue, _ := r.pctService.Execute(*controlNodeID, checkFirstLoginCmd, &timeout)
			checkUserCmd := "kubectl get users.management.cattle.io -o jsonpath='{range .items[*]}{.username}{\"\\n\"}{end}' 2>&1 | grep -q '^admin$' && echo 'exists' || echo 'not-found'"
			userExists, _ := r.pctService.Execute(*controlNodeID, checkUserCmd, &timeout)
			if firstLoginValue != "" && strings.TrimSpace(firstLoginValue) == "" && userExists != "" && strings.Contains(userExists, "exists") {
				logger.Info("Detected inconsistent backup state: admin user exists but first-login is empty")
				logger.Info("Fixing first-login setting to 'false'...")
				patchCmd := "kubectl patch settings.management.cattle.io first-login --type json -p '[{\"op\": \"replace\", \"path\": \"/value\", \"value\": \"false\"}]' 2>&1"
				timeout = 30
				patchOutput, patchExit := r.pctService.Execute(*controlNodeID, patchCmd, &timeout)
				if patchExit != nil && *patchExit == 0 {
					logger.Info("first-login setting fixed to 'false'")
					restartCmd := "kubectl rollout restart deployment rancher -n cattle-system 2>&1"
					timeout = 30
					r.pctService.Execute(*controlNodeID, restartCmd, &timeout)
				} else {
					logger.Error("Failed to fix first-login setting: %s", patchOutput)
				}
			}
		}
	}
	logger.Info("==================================================")
	logger.Info("Restore completed successfully!")
	logger.Info("Backup restored: %s", backupName)
	logger.Info("==================================================")
	return nil
}

func getRestorePriority(name string) int {
	if name == "k3s-control-data" {
		return 0
	}
	if name == "k3s-control-config" {
		return 1
	}
	if strings.Contains(name, "k3s-worker") && strings.Contains(name, "data") {
		return 2
	}
	if strings.Contains(name, "k3s-worker") && strings.Contains(name, "config") {
		return 3
	}
	return 4
}
