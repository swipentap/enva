package commands

import (
	"enva/libs"
	"enva/services"
	"fmt"
	"os"
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

// NewBackup creates a new Backup command
func NewBackup(cfg *libs.LabConfig, lxcService *services.LXCService, pctService *services.PCTService) *Backup {
	return &Backup{
		cfg:        cfg,
		lxcService: lxcService,
		pctService: pctService,
	}
}

// Run executes the backup
func (b *Backup) Run() error {
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
	logger.Info("==================================================")
	logger.Info("Backing Up Cluster")
	logger.Info("==================================================")
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
				checkpointCmd := fmt.Sprintf("python3 -c \"import sqlite3; conn = sqlite3.connect('%s'); result = conn.execute('PRAGMA wal_checkpoint(TRUNCATE)').fetchone(); conn.close(); print(f'Checkpoint: {result}')\" 2>&1", dbPath)
				timeout = 60
				checkpointOutput, checkpointExit := b.pctService.Execute(item.SourceContainerID, checkpointCmd, &timeout)
				if checkpointExit != nil && *checkpointExit == 0 {
					output := checkpointOutput
					if output == "" {
						output = "OK"
					}
					logger.Info("  SQLite WAL checkpoint completed: %s", strings.TrimSpace(output))
				} else {
					logger.Info("  SQLite WAL checkpoint failed (may not be in WAL mode): %s", checkpointOutput)
				}
			}
			archiveCmd := fmt.Sprintf("tar --warning=no-file-changed -czf %s -C %s %s 2>&1", archiveFile, *item.ArchiveBase, *item.ArchivePath)
			timeout = 300
			archiveOutput, archiveExit := b.pctService.Execute(item.SourceContainerID, archiveCmd, &timeout)
			verifyCmd := fmt.Sprintf("test -f %s && echo exists || echo missing", archiveFile)
			timeout = 10
			verifyOutput, verifyExit := b.pctService.Execute(item.SourceContainerID, verifyCmd, &timeout)
			if verifyExit == nil || *verifyExit != 0 || verifyOutput == "" || strings.Contains(verifyOutput, "missing") {
				logger.Info("  Failed to create archive for %s: %s", item.Name, archiveOutput)
				return &BackupError{Message: fmt.Sprintf("Failed to create archive for %s", item.Name)}
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
			copyCmd := fmt.Sprintf("cp %s %s 2>&1", item.SourcePath, tempFile)
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
		b.pctService.Execute(bf.sourceID, fmt.Sprintf("rm -f %s %s.tar.gz 2>&1 || true", bf.tempFile, bf.tempFile), &timeout)
		backupFileNames = append(backupFileNames, fmt.Sprintf("%s-%s", backupName, bf.destName))
	}
	logger.Info("Creating final backup tarball...")
	finalTarball := fmt.Sprintf("%s/%s.tar.gz", b.cfg.Backup.BackupDir, backupName)
	tarFilesList := strings.Join(backupFileNames, " ")
	tarCmd := fmt.Sprintf("cd %s && tar -czf %s.tar.gz %s 2>&1", b.cfg.Backup.BackupDir, backupName, tarFilesList)
	timeout = 300
	tarOutput, tarExit := b.pctService.Execute(backupContainer.ID, tarCmd, &timeout)
	if tarExit == nil || *tarExit != 0 {
		logger.Error("Failed to create final tarball: %s", tarOutput)
		return &BackupError{Message: "Failed to create final tarball"}
	}
	cleanupCmd := fmt.Sprintf("cd %s && rm -f %s 2>&1 || true", b.cfg.Backup.BackupDir, tarFilesList)
	timeout = 30
	b.pctService.Execute(backupContainer.ID, cleanupCmd, &timeout)
	logger.Info("==================================================")
	logger.Info("Backup completed successfully!")
	logger.Info("Backup name: %s", backupName)
	logger.Info("Backup location: %s on container %d", finalTarball, backupContainer.ID)
	logger.Info("==================================================")
	return nil
}
