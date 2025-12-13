package actions

import (
	"strconv"
	"enva/cli"
	"enva/libs"
	"enva/services"
)

// EnableSshServiceAction enables and starts SSH service
type EnableSshServiceAction struct {
	*BaseAction
}

func NewEnableSshServiceAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &EnableSshServiceAction{
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

func (a *EnableSshServiceAction) Description() string {
	return "SSH service enablement"
}

func (a *EnableSshServiceAction) Execute() bool {
	containerIDInt, err := strconv.Atoi(*a.ContainerID)
	if err != nil {
		libs.GetLogger("enable_ssh_service").Printf("Invalid container ID: %s", *a.ContainerID)
		return false
	}
	enableCmd := cli.NewSystemCtl().Service("ssh").Enable()
	output, exitCode := a.PCTService.Execute(containerIDInt, enableCmd, nil)
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("enable_ssh_service").Printf("Failed to enable SSH service: %s", output)
		return false
	}
	startCmd := cli.NewSystemCtl().Service("ssh").Start()
	output, exitCode = a.PCTService.Execute(containerIDInt, startCmd, nil)
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("enable_ssh_service").Printf("Failed to start SSH service: %s", output)
		return false
	}
	return true
}

