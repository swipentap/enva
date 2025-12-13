package actions

import (
	"strings"
	"enva/cli"
	"enva/libs"
	"enva/services"
)

// ConfigureHaproxySystemdAction configures systemd override for HAProxy
type ConfigureHaproxySystemdAction struct {
	*BaseAction
}

func NewConfigureHaproxySystemdAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &ConfigureHaproxySystemdAction{
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

func (a *ConfigureHaproxySystemdAction) Description() string {
	return "haproxy systemd override"
}

func (a *ConfigureHaproxySystemdAction) Execute() bool {
	if a.SSHService == nil {
		libs.GetLogger("configure_haproxy_systemd").Error("SSH service not initialized")
		return false
	}
	mkdirCmd := cli.NewFileOps().Mkdir("/etc/systemd/system/haproxy.service.d", true)
	output, exitCode := a.SSHService.Execute(mkdirCmd, nil, true) // sudo=True
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("configure_haproxy_systemd").Error("create haproxy systemd override directory failed with exit code %d", *exitCode)
		if output != "" {
			lines := strings.Split(output, "\n")
			if len(lines) > 0 {
				libs.GetLogger("configure_haproxy_systemd").Error("create haproxy systemd override directory output: %s", lines[len(lines)-1])
			}
		}
		return false
	}
	overrideContent := `[Service]
Type=notify
PrivateNetwork=no
ProtectSystem=no
ProtectHome=no
ExecStart=
ExecStart=/usr/sbin/haproxy -Ws -f $CONFIG -p $PIDFILE $EXTRAOPTS
`
	overrideCmd := cli.NewFileOps().Write("/etc/systemd/system/haproxy.service.d/override.conf", overrideContent)
	output, exitCode = a.SSHService.Execute(overrideCmd, nil, true) // sudo=True
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("configure_haproxy_systemd").Error("write haproxy systemd override failed with exit code %d", *exitCode)
		if output != "" {
			lines := strings.Split(output, "\n")
			if len(lines) > 0 {
				libs.GetLogger("configure_haproxy_systemd").Error("write haproxy systemd override output: %s", lines[len(lines)-1])
			}
		}
		return false
	}
	reloadCmd := "systemctl daemon-reload"
	output, exitCode = a.SSHService.Execute(reloadCmd, nil, true) // sudo=True
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("configure_haproxy_systemd").Error("reload systemd daemon failed with exit code %d", *exitCode)
		if output != "" {
			lines := strings.Split(output, "\n")
			if len(lines) > 0 {
				libs.GetLogger("configure_haproxy_systemd").Error("reload systemd daemon output: %s", lines[len(lines)-1])
			}
		}
		return false
	}
	return true
}

