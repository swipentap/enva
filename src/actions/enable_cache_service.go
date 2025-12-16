package actions

import (
	"strings"
	"time"
	"enva/cli"
	"enva/libs"
	"enva/services"
)

// EnableCacheServiceAction enables and starts apt-cacher-ng service
type EnableCacheServiceAction struct {
	*BaseAction
}

func NewEnableCacheServiceAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &EnableCacheServiceAction{
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

func (a *EnableCacheServiceAction) Description() string {
	return "apt-cacher-ng service enablement"
}

func (a *EnableCacheServiceAction) Execute() bool {
	if a.SSHService == nil {
		libs.GetLogger("enable_cache_service").Error("SSH service not initialized")
		return false
	}
	
	// Enable the service first (matching Python: sudo=True)
	enableCmd := cli.NewSystemCtl().Service("apt-cacher-ng").Enable()
	output, exitCode := a.SSHService.Execute(enableCmd, nil, true) // sudo=True
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("enable_cache_service").Warning("enable apt-cacher-ng service had issues: %s", output)
	}
	
	// Use restart to ensure config changes are applied (if service was already running) (matching Python: sudo=True)
	restartCmd := cli.NewSystemCtl().Service("apt-cacher-ng").Restart()
	output, exitCode = a.SSHService.Execute(restartCmd, nil, true) // sudo=True
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("enable_cache_service").Error("restart apt-cacher-ng service failed with exit code %d", *exitCode)
		if output != "" {
			lines := strings.Split(output, "\n")
			if len(lines) > 0 {
				libs.GetLogger("enable_cache_service").Error("restart output: %s", lines[len(lines)-1])
			}
		}
		return false
	}
	
	// Wait a moment for service to start
	time.Sleep(2 * time.Second)
	
	// Verify service is active (matching Python: sudo=True)
	isActiveCmd := cli.NewSystemCtl().Service("apt-cacher-ng").IsActive()
	status, exitCode := a.SSHService.Execute(isActiveCmd, nil, true) // sudo=True
	if exitCode != nil && *exitCode == 0 && cli.ParseIsActive(status) {
		// Verify service stays active
		time.Sleep(2 * time.Second)
		status2, exitCode2 := a.SSHService.Execute(isActiveCmd, nil, true) // sudo=True
		if exitCode2 != nil && *exitCode2 == 0 && cli.ParseIsActive(status2) {
			return true
		}
		// Service started but stopped - check why (matching Python: sudo=True)
		statusCmd := "systemctl status apt-cacher-ng --no-pager -l 2>&1 | head -20"
		statusOutput, _ := a.SSHService.Execute(statusCmd, nil, true) // sudo=True
		libs.GetLogger("enable_cache_service").Error("apt-cacher-ng service started but stopped. Status: %s", statusOutput)
		return false
	}
	
	// Service didn't start - check why (matching Python: sudo=True)
	statusCmd := "systemctl status apt-cacher-ng --no-pager -l 2>&1 | head -20"
	statusOutput, _ := a.SSHService.Execute(statusCmd, nil, true) // sudo=True
	libs.GetLogger("enable_cache_service").Error("apt-cacher-ng service failed to start. Status: %s", statusOutput)
	return false
}

