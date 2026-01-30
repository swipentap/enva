package actions

import (
	"strings"
	"enva/libs"
	"enva/services"
)

// DisableAptUnitsAction disables automatic apt units
type DisableAptUnitsAction struct {
	*BaseAction
}

func NewDisableAptUnitsAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &DisableAptUnitsAction{
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

func (a *DisableAptUnitsAction) Description() string {
	return "disable automatic apt units"
}

func (a *DisableAptUnitsAction) Execute() bool {
	command := `for unit in apt-daily.service apt-daily.timer apt-daily-upgrade.service apt-daily-upgrade.timer; do systemctl stop "$unit" || true; systemctl disable "$unit" || true; systemctl mask "$unit" || true; done`
	output, exitCode := a.SSHService.Execute(command, nil)
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("disable_apt_units").Printf("disable automatic apt units failed with exit code %d", *exitCode)
		if output != "" {
			lines := strings.Split(output, "\n")
			if len(lines) > 0 {
				libs.GetLogger("disable_apt_units").Printf("disable automatic apt units output: %s", lines[len(lines)-1])
			}
		}
		return false
	}
	return true
}

