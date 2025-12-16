package actions

import (
	"fmt"
	"enva/libs"
	"enva/services"
)

// InstallPostgresqlAction installs PostgreSQL package
type InstallPostgresqlAction struct {
	*BaseAction
}

func NewInstallPostgresqlAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &InstallPostgresqlAction{
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

func (a *InstallPostgresqlAction) Description() string {
	return "postgresql installation"
}

func (a *InstallPostgresqlAction) Execute() bool {
	if a.APTService == nil {
		libs.GetLogger("install_postgresql").Printf("APT service not initialized")
		return false
	}
	version := "17"
	if a.ContainerCfg != nil && a.ContainerCfg.Params != nil {
		if v, ok := a.ContainerCfg.Params["version"].(string); ok {
			version = v
		}
	}
	libs.GetLogger("install_postgresql").Printf("Installing PostgreSQL %s package...", version)
	output, exitCode := a.APTService.Install([]string{fmt.Sprintf("postgresql-%s", version), "postgresql-contrib"})
	if exitCode == nil || *exitCode != 0 {
		libs.GetLogger("install_postgresql").Printf("PostgreSQL installation failed: %s", output)
		return false
	}
	return true
}

