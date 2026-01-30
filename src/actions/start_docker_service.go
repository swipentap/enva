package actions

import (
	"time"
	"enva/cli"
	"enva/libs"
	"enva/services"
)

// StartDockerServiceAction starts Docker service
type StartDockerServiceAction struct {
	*BaseAction
}

func NewStartDockerServiceAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &StartDockerServiceAction{
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

func (a *StartDockerServiceAction) Description() string {
	return "docker service start"
}

func (a *StartDockerServiceAction) Execute() bool {
	if a.SSHService == nil || a.Cfg == nil {
		libs.GetLogger("start_docker_service").Printf("SSH service or config not initialized")
		return false
	}
	libs.GetLogger("start_docker_service").Printf("Ensuring Docker service is running...")
	isActiveCmd := cli.NewSystemCtl().Service("docker").IsActive()
	currentStatus, currentExit := a.SSHService.Execute(isActiveCmd, nil)
	if currentExit != nil && *currentExit == 0 && cli.ParseIsActive(currentStatus) {
		libs.GetLogger("start_docker_service").Printf("Docker service already active, skipping start")
		return true
	}
	libs.GetLogger("start_docker_service").Printf("Docker service not active, starting socket and triggering activation...")
	enableCmd := cli.NewSystemCtl().Service("docker.socket").Enable()
	startCmd := cli.NewSystemCtl().Service("docker.socket").Start()
	a.SSHService.Execute(enableCmd, nil)
	socketOutput, socketExit := a.SSHService.Execute(startCmd, nil)
	if socketExit != nil && *socketExit != 0 {
		libs.GetLogger("start_docker_service").Printf("Failed to start docker.socket with exit code %d", *socketExit)
		outputLen := len(socketOutput)
		start := 0
		if outputLen > 500 {
			start = outputLen - 500
		}
		libs.GetLogger("start_docker_service").Printf("docker.socket start output: %s", socketOutput[start:])
		return false
	}
	time.Sleep(2 * time.Second)
	enableDockerCmd := cli.NewSystemCtl().Service("docker").Enable()
	a.SSHService.Execute(enableDockerCmd, nil)
	libs.GetLogger("start_docker_service").Printf("Triggering Docker service via socket activation...")
	triggerCmd := "docker version  || true"
	a.SSHService.Execute(triggerCmd, nil)
	time.Sleep(3 * time.Second)
	status, exitCode := a.SSHService.Execute(isActiveCmd, nil)
	if exitCode != nil && *exitCode == 0 && cli.ParseIsActive(status) {
		libs.GetLogger("start_docker_service").Printf("Docker service is running")
		return true
	}
	libs.GetLogger("start_docker_service").Printf("Docker service failed to start via socket activation")
	statusCmd := cli.NewSystemCtl().Service("docker").Status()
	statusOutput, _ := a.SSHService.Execute(statusCmd, nil)
	libs.GetLogger("start_docker_service").Printf("Docker service status:\n%s", statusOutput)
	journalCmd := "journalctl -u docker.service -n 50 --no-pager"
	journalOutput, _ := a.SSHService.Execute(journalCmd, nil)
	libs.GetLogger("start_docker_service").Printf("Docker service journal logs:\n%s", journalOutput)
	return false
}

