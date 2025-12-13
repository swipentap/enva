package actions

import (
	"strconv"
	"enva/cli"
	"enva/libs"
	"enva/services"
)

// InstallOpensshServerAction installs openssh-server package
type InstallOpensshServerAction struct {
	*BaseAction
}

func NewInstallOpensshServerAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &InstallOpensshServerAction{
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

func (a *InstallOpensshServerAction) Description() string {
	return "openssh-server installation"
}

func (a *InstallOpensshServerAction) Execute() bool {
	containerIDInt, err := strconv.Atoi(*a.ContainerID)
	if err != nil {
		libs.GetLogger("install_openssh_server").Printf("Invalid container ID: %s", *a.ContainerID)
		return false
	}
	installCmd := cli.NewApt().Install([]string{"openssh-server"})
	output, exitCode := a.PCTService.Execute(containerIDInt, installCmd, nil)
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("install_openssh_server").Printf("Failed to install openssh-server: %s", output)
		return false
	}
	return true
}

