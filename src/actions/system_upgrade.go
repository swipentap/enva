package actions

import (
	"enva/libs"
	"enva/services"
	"strings"
)

// SystemUpgradeAction runs system upgrade
type SystemUpgradeAction struct {
	*BaseAction
}

func NewSystemUpgradeAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &SystemUpgradeAction{
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

func (a *SystemUpgradeAction) Description() string {
	return "system upgrade"
}

func (a *SystemUpgradeAction) Execute() bool {
	if a.APTService == nil {
		libs.GetLogger("system_upgrade").Printf("APT service not initialized")
		return false
	}

	// Run apt update
	output, exitCode := a.APTService.Update()
	if exitCode == nil || *exitCode != 0 {
		libs.GetLogger("system_upgrade").Printf("apt update failed")
		return false
	}

	// Run distribution upgrade
	output, exitCode = a.APTService.DistUpgrade()
	if exitCode == nil || *exitCode != 0 {
		outputLower := strings.ToLower(output)
		successIndicators := strings.Contains(outputLower, "setting up") ||
			strings.Contains(outputLower, "processing triggers") ||
			strings.Contains(outputLower, "created symlink") ||
			strings.Contains(outputLower, "0 upgraded") ||
			strings.Contains(outputLower, "0 newly installed")
		if !successIndicators {
			libs.GetLogger("system_upgrade").Printf("distribution upgrade failed: %s", output)
			return false
		}
	}
	return true
}

