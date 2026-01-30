package actions

import (
	"strings"
	"time"
	"enva/cli"
	"enva/libs"
	"enva/services"
)

// EnableHaproxyServiceAction enables and starts HAProxy service
type EnableHaproxyServiceAction struct {
	*BaseAction
}

func NewEnableHaproxyServiceAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &EnableHaproxyServiceAction{
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

func (a *EnableHaproxyServiceAction) Description() string {
	return "haproxy service enablement"
}

func (a *EnableHaproxyServiceAction) Execute() bool {
	if a.SSHService == nil {
		libs.GetLogger("enable_haproxy_service").Error("SSH service not initialized")
		return false
	}
	validateCmd := "haproxy -c -f /etc/haproxy/haproxy.cfg"
	validateOutput, exitCode := a.SSHService.Execute(validateCmd, nil, true) // sudo=True
	if exitCode == nil || *exitCode != 0 {
		libs.GetLogger("enable_haproxy_service").Error("HAProxy config validation command failed")
		return false
	}
	if validateOutput != "" && (strings.Contains(validateOutput, "Fatal errors found") || strings.Contains(validateOutput, "[ALERT]")) {
		libs.GetLogger("enable_haproxy_service").Error("HAProxy config validation failed: %s", validateOutput)
		return false
	}
	restartCmd := cli.NewSystemCtl().Service("haproxy").Restart()
	output, exitCode := a.SSHService.Execute(restartCmd, nil, true) // sudo=True
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("enable_haproxy_service").Error("restart haproxy service failed with exit code %d", *exitCode)
		if output != "" {
			lines := strings.Split(output, "\n")
			if len(lines) > 0 {
				libs.GetLogger("enable_haproxy_service").Error("restart haproxy service output: %s", lines[len(lines)-1])
			}
		}
		statusCmd := "systemctl status haproxy.service --no-pager -l | head -20"
		statusOutput, _ := a.SSHService.Execute(statusCmd, nil, true) // sudo=True
		libs.GetLogger("enable_haproxy_service").Error("HAProxy service restart failed. Status: %s", statusOutput)
		// Even if restart failed, check if service is actually running (matching Python behavior)
		time.Sleep(2 * time.Second)
		statusCheckCmd := cli.NewSystemCtl().Service("haproxy").IsActive()
		statusCheck, statusExitCode := a.SSHService.Execute(statusCheckCmd, nil, true) // sudo=True
		if statusExitCode != nil && *statusExitCode == 0 && cli.ParseIsActive(statusCheck) {
			libs.GetLogger("enable_haproxy_service").Warning("HAProxy service is active despite restart failure, treating as success")
			return true
		}
		return false
	}
	enableCmd := cli.NewSystemCtl().Service("haproxy").Enable()
	a.SSHService.Execute(enableCmd, nil, true) // sudo=True
	time.Sleep(2 * time.Second)
	statusCmd := cli.NewSystemCtl().Service("haproxy").IsActive()
	status, exitCode := a.SSHService.Execute(statusCmd, nil, true) // sudo=True
	if exitCode != nil && *exitCode == 0 && cli.ParseIsActive(status) {
		return true
	}
	libs.GetLogger("enable_haproxy_service").Error("HAProxy service is not active after restart")
	return false
}

