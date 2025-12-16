package actions

import (
	"enva/libs"
	"enva/services"
)

// InstallHaproxyAction installs HAProxy package
type InstallHaproxyAction struct {
	*BaseAction
}

func NewInstallHaproxyAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &InstallHaproxyAction{
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

func (a *InstallHaproxyAction) Description() string {
	return "haproxy installation"
}

func (a *InstallHaproxyAction) Execute() bool {
	if a.APTService == nil {
		libs.GetLogger("install_haproxy").Printf("APT service not initialized")
		return false
	}
	libs.GetLogger("install_haproxy").Printf("Installing haproxy package...")
	output, exitCode := a.APTService.Install([]string{"haproxy"})
	if exitCode == nil || *exitCode != 0 {
		libs.GetLogger("install_haproxy").Printf("haproxy installation failed: %s", output)
		return false
	}
	return true
}

