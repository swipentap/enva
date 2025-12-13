package actions

import (
	"enva/cli"
	"enva/libs"
	"enva/services"
)

// DisableSystemdResolvedAction disables systemd-resolved and configures resolv.conf
type DisableSystemdResolvedAction struct {
	*BaseAction
}

func NewDisableSystemdResolvedAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &DisableSystemdResolvedAction{
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

func (a *DisableSystemdResolvedAction) Description() string {
	return "disable systemd resolved"
}

func (a *DisableSystemdResolvedAction) Execute() bool {
	if a.SSHService == nil {
		libs.GetLogger("disable_systemd_resolved").Error("SSH service not initialized")
		return false
	}
	libs.GetLogger("disable_systemd_resolved").Info("Disabling systemd-resolved to free port 53...")
	stopCmd := cli.NewSystemCtl().Service("systemd-resolved").Stop()
	output, exitCode := a.SSHService.Execute(stopCmd, nil, true) // sudo=True
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("disable_systemd_resolved").Warning("Failed to stop systemd-resolved: %s", output)
	}
	disableCmd := cli.NewSystemCtl().Service("systemd-resolved").Disable()
	output, exitCode = a.SSHService.Execute(disableCmd, nil, true) // sudo=True
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("disable_systemd_resolved").Warning("Failed to disable systemd-resolved: %s", output)
	}
	libs.GetLogger("disable_systemd_resolved").Info("Configuring static resolv.conf...")
	removeSymlinkCmd := "rm -f /etc/resolv.conf"
	a.SSHService.Execute(removeSymlinkCmd, nil, true) // sudo=True
	resolvContent := "nameserver 8.8.8.8\nnameserver 1.1.1.1\nnameserver 8.8.4.4\n"
	writeResolvCmd := cli.NewFileOps().Write("/etc/resolv.conf", resolvContent)
	output, exitCode = a.SSHService.Execute(writeResolvCmd, nil, true) // sudo=True
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("disable_systemd_resolved").Error("Failed to write resolv.conf: %s", output)
		return false
	}
	statusCmd := cli.NewSystemCtl().Service("systemd-resolved").IsActive()
	status, exitCode := a.SSHService.Execute(statusCmd, nil, true) // sudo=True
	if exitCode != nil && *exitCode == 0 && cli.ParseIsActive(status) {
		libs.GetLogger("disable_systemd_resolved").Warning("systemd-resolved is still active")
	} else {
		libs.GetLogger("disable_systemd_resolved").Info("systemd-resolved is stopped and disabled")
	}
	return true
}

