package actions

import (
	"enva/libs"
	"enva/services"
)

// InstallDotnetAction installs .NET SDK
type InstallDotnetAction struct {
	*BaseAction
}

func NewInstallDotnetAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &InstallDotnetAction{
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

func (a *InstallDotnetAction) Description() string {
	return "dotnet installation"
}

func (a *InstallDotnetAction) Execute() bool {
	if a.APTService == nil {
		libs.GetLogger("install_dotnet").Printf("APT service not initialized")
		return false
	}
	libs.GetLogger("install_dotnet").Printf("Installing .NET SDK...")
	output, exitCode := a.APTService.Install([]string{"dotnet-sdk-8.0"})
	if exitCode == nil || *exitCode != 0 {
		libs.GetLogger("install_dotnet").Printf("dotnet SDK installation failed: %s", output)
		return false
	}
	return true
}

