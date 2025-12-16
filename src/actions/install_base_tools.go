package actions

import (
	"enva/libs"
	"enva/services"
)

// InstallBaseToolsAction installs minimal base tools
type InstallBaseToolsAction struct {
	*BaseAction
}

func NewInstallBaseToolsAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &InstallBaseToolsAction{
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

func (a *InstallBaseToolsAction) Description() string {
	return "base tools installation"
}

func (a *InstallBaseToolsAction) Execute() bool {
	output, exitCode := a.APTService.Install([]string{"ca-certificates", "curl"})
	if exitCode == nil || *exitCode != 0 {
		libs.GetLogger("install_base_tools").Printf("Failed to install base tools: %s", output)
		return false
	}
	return true
}

