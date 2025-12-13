package actions

import (
	"enva/libs"
	"enva/services"
)

// ConfigureDockerSysctlAction configures sysctl for Docker containers
type ConfigureDockerSysctlAction struct {
	*BaseAction
}

func NewConfigureDockerSysctlAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &ConfigureDockerSysctlAction{
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

func (a *ConfigureDockerSysctlAction) Description() string {
	return "docker sysctl configuration"
}

func (a *ConfigureDockerSysctlAction) Execute() bool {
	if a.SSHService == nil {
		libs.GetLogger("configure_docker_sysctl").Printf("SSH service not initialized")
		return false
	}
	libs.GetLogger("configure_docker_sysctl").Printf("Configuring sysctl for Docker containers...")
	sysctlCmd := "sysctl -w net.ipv4.ip_unprivileged_port_start=0 2>/dev/null || true; echo 'net.ipv4.ip_unprivileged_port_start=0' >> /etc/sysctl.conf 2>/dev/null || true"
	output, exitCode := a.SSHService.Execute(sysctlCmd, nil)
	if exitCode != nil && *exitCode != 0 {
		outputLen := len(output)
		start := 0
		if outputLen > 200 {
			start = outputLen - 200
		}
		libs.GetLogger("configure_docker_sysctl").Printf("Sysctl configuration had issues: %s", output[start:])
	}
	return true
}

