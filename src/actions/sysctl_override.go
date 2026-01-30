package actions

import (
	"strconv"
	"strings"
	"enva/cli"
	"enva/libs"
	"enva/services"
)

// SysctlOverrideAction configures systemd sysctl override
type SysctlOverrideAction struct {
	*BaseAction
}

func NewSysctlOverrideAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &SysctlOverrideAction{
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

func (a *SysctlOverrideAction) Description() string {
	return "systemd sysctl override"
}

func (a *SysctlOverrideAction) Execute() bool {
	if a.PCTService == nil || a.ContainerID == nil {
		libs.GetLogger("sysctl_override").Printf("PCT service or container ID not available")
		return false
	}
	containerIDInt, err := strconv.Atoi(*a.ContainerID)
	if err != nil {
		libs.GetLogger("sysctl_override").Printf("Invalid container ID: %s", *a.ContainerID)
		return false
	}
	mkdirCmd := cli.NewFileOps().Mkdir("/etc/systemd/system/systemd-sysctl.service.d", true)
	output, exitCode := a.PCTService.Execute(containerIDInt, mkdirCmd, nil)
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("sysctl_override").Printf("create sysctl override directory failed with exit code %d", *exitCode)
		if output != "" {
			lines := strings.Split(output, "\n")
			if len(lines) > 0 {
				libs.GetLogger("sysctl_override").Printf("create sysctl override directory output: %s", lines[len(lines)-1])
			}
		}
		return false
	}
	overrideCmd := cli.NewFileOps().Write("/etc/systemd/system/systemd-sysctl.service.d/override.conf", "[Service]\nImportCredential=\n")
	output, exitCode = a.PCTService.Execute(containerIDInt, overrideCmd, nil)
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("sysctl_override").Printf("write sysctl override failed with exit code %d", *exitCode)
		if output != "" {
			lines := strings.Split(output, "\n")
			if len(lines) > 0 {
				libs.GetLogger("sysctl_override").Printf("write sysctl override output: %s", lines[len(lines)-1])
			}
		}
		return false
	}
	reloadCmd := "systemctl daemon-reload && systemctl stop systemd-sysctl.service || true && systemctl start systemd-sysctl.service || true"
	output, exitCode = a.PCTService.Execute(containerIDInt, reloadCmd, nil)
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("sysctl_override").Printf("reload systemd-sysctl failed with exit code %d", *exitCode)
		if output != "" {
			lines := strings.Split(output, "\n")
			if len(lines) > 0 {
				libs.GetLogger("sysctl_override").Printf("reload systemd-sysctl output: %s", lines[len(lines)-1])
			}
		}
		return false
	}
	return true
}

