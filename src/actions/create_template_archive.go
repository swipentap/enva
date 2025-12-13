package actions

import (
	"fmt"
	"strconv"
	"strings"
	"time"
	"enva/cli"
	"enva/libs"
	"enva/services"
)

// CreateTemplateArchiveAction creates template archive from container
type CreateTemplateArchiveAction struct {
	*BaseAction
}

func NewCreateTemplateArchiveAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &CreateTemplateArchiveAction{
		BaseAction: &BaseAction{
			SSHService:   sshService,
			APTService:   aptService,
			PCTService:   pctService,
			ContainerID: containerID,
			Cfg:          cfg,
			ContainerCfg: containerCfg,
		},
	}
}

func (a *CreateTemplateArchiveAction) Description() string {
	return "template archive creation"
}

func (a *CreateTemplateArchiveAction) Execute() bool {
	proxmoxHost := a.Cfg.LXCHost()
	containerID := *a.ContainerID
	templateDir := a.Cfg.LXC.TemplateDir
	
	lxcService := services.NewLXCService(proxmoxHost, &a.Cfg.SSH)
	if !lxcService.Connect() {
		libs.GetLogger("create_template_archive").Printf("Failed to connect to Proxmox host")
		return false
	}
	defer lxcService.Disconnect()
	
	// Stop container
	libs.GetLogger("create_template_archive").Printf("Stopping container...")
	stopCmd := cli.NewPCT().ContainerID(containerID).Stop()
	stopOutput, _ := lxcService.Execute(stopCmd, nil)
	if stopOutput == "" {
		libs.GetLogger("create_template_archive").Printf("Stop container had issues, trying force stop")
		forceStopCmd := cli.NewPCT().ContainerID(containerID).Force().Stop()
		lxcService.Execute(forceStopCmd, nil)
	}
	time.Sleep(2 * time.Second)
	
	// Create template archive
	libs.GetLogger("create_template_archive").Printf("Creating template archive for container %s in directory %s", containerID, templateDir)
	vzdumpCmd := cli.NewVzdump().Compress("zstd").Mode("stop").CreateTemplate(containerID, templateDir)
	libs.GetLogger("create_template_archive").Printf("Executing vzdump command: %s", vzdumpCmd)
	vzdumpOutput, _ := lxcService.Execute(vzdumpCmd, nil)
	if vzdumpOutput == "" {
		libs.GetLogger("create_template_archive").Printf("vzdump produced no output - command may have failed silently")
		return false
	}
	if len(vzdumpOutput) > 500 {
		libs.GetLogger("create_template_archive").Printf("vzdump output (first 500 chars): %s", vzdumpOutput[:500])
	}
	
	// Wait for archive file to be created and stable
	libs.GetLogger("create_template_archive").Printf("Waiting for template archive to be ready (max 120 seconds)...")
	backupFile := waitForArchiveFile(proxmoxHost, containerID, templateDir, a.Cfg, lxcService, 120)
	if backupFile == "" {
		libs.GetLogger("create_template_archive").Printf("Template archive file not found after vzdump in directory %s", templateDir)
		checkCmd := fmt.Sprintf("ls -la %s/*vzdump* 2>&1 | head -10", templateDir)
		checkOutput, _ := lxcService.Execute(checkCmd, nil)
		libs.GetLogger("create_template_archive").Printf("Files in template directory: %s", checkOutput)
		return false
	}
	libs.GetLogger("create_template_archive").Printf("Template archive file found: %s", backupFile)
	
	// Verify archive is not empty
	sizeCmd := cli.NewVzdump().GetArchiveSize(backupFile)
	sizeCheck, _ := lxcService.Execute(sizeCmd, nil)
	if sizeCheck == "" {
		libs.GetLogger("create_template_archive").Printf("Failed to get archive file size")
		return false
	}
	fileSize := cli.ParseArchiveSize(sizeCheck)
	if fileSize == nil || *fileSize < 10485760 {
		libs.GetLogger("create_template_archive").Printf("Template archive is too small (%d bytes if found), likely corrupted", *fileSize)
		return false
	}
	libs.GetLogger("create_template_archive").Printf("Template archive size: %.2f MB", float64(*fileSize)/1048576)
	
	// Rename template and move to storage location
	templateName := a.ContainerCfg.Name
	if templateName == "" {
		templateName = "template"
	}
	dateStr := time.Now().Format("20060102")
	finalTemplateName := fmt.Sprintf("%s_%s_amd64.tar.zst", templateName, dateStr)
	libs.GetLogger("create_template_archive").Printf("Final template name: %s", finalTemplateName)
	
	storageTemplateDir := a.Cfg.LXC.TemplateDir
	storageTemplatePath := fmt.Sprintf("%s/%s", storageTemplateDir, finalTemplateName)
	libs.GetLogger("create_template_archive").Printf("Moving template from %s to %s", backupFile, storageTemplatePath)
	moveCmd := fmt.Sprintf("mv '%s' %s 2>&1", backupFile, storageTemplatePath)
	moveOutput, _ := lxcService.Execute(moveCmd, nil)
	if moveOutput != "" {
		libs.GetLogger("create_template_archive").Printf("Move command output: %s", moveOutput)
	}
	libs.GetLogger("create_template_archive").Printf("Template moved to storage location: %s", storageTemplatePath)
	
	// Update template list
	pveamCmd := "pveam update 2>&1"
	pveamOutput, _ := lxcService.Execute(pveamCmd, nil)
	if pveamOutput == "" {
		libs.GetLogger("create_template_archive").Printf("pveam update had issues")
	}
	
	// Cleanup other templates
	libs.GetLogger("create_template_archive").Printf("Cleaning up other template archives...")
	preservePatterns := strings.Join(a.Cfg.TemplateConfig.Preserve, " ")
	cleanupCacheCmd := fmt.Sprintf("find %s -maxdepth 1 -type f -name '*.tar.zst' ! -name '%s' %s -delete 2>&1", templateDir, finalTemplateName, preservePatterns)
	lxcService.Execute(cleanupCacheCmd, nil)
	cleanupStorageCmd := fmt.Sprintf("find %s -maxdepth 1 -type f -name '*.tar.zst' ! -name '%s' %s ! -name 'ubuntu-24.10-standard_24.10-1_amd64.tar.zst' -delete 2>&1", storageTemplateDir, finalTemplateName, preservePatterns)
	lxcService.Execute(cleanupStorageCmd, nil)
	
	// Destroy container after archive is created
	containerIDInt, err := strconv.Atoi(containerID)
	if err == nil {
		libs.DestroyContainer(proxmoxHost, containerIDInt, a.Cfg, lxcService)
		libs.GetLogger("create_template_archive").Printf("Container %s destroyed after template archive creation", containerID)
	}
	return true
}

func waitForArchiveFile(proxmoxHost, containerID, dumpdir string, cfg *libs.LabConfig, lxcService libs.LXCServiceInterface, maxWait int) string {
	waitCount := 0
	lastSize := 0
	stableCount := 0
	backupFile := ""
	for waitCount < maxWait {
		time.Sleep(2 * time.Second)
		waitCount += 2
		findArchiveCmd := cli.NewVzdump().FindArchive(dumpdir, containerID)
		backupFile, _ = lxcService.Execute(findArchiveCmd, nil)
		if backupFile == "" {
			continue
		}
		backupFile = strings.TrimSpace(backupFile)
		sizeCmd := cli.NewVzdump().GetArchiveSize(backupFile)
		sizeCheck, _ := lxcService.Execute(sizeCmd, nil)
		if sizeCheck == "" {
			continue
		}
		currentSize := cli.ParseArchiveSize(sizeCheck)
		if currentSize == nil || *currentSize <= 0 {
			continue
		}
		if *currentSize == lastSize {
			stableCount++
			if stableCount >= 3 {
				break
			}
		} else {
			stableCount = 0
			lastSize = *currentSize
		}
	}
	return backupFile
}

