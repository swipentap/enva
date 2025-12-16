package actions

import (
	"strconv"
	"enva/cli"
	"enva/libs"
	"enva/services"
	"strings"
)

// FixAptSourcesAction fixes apt sources
type FixAptSourcesAction struct {
	*BaseAction
}

func NewFixAptSourcesAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &FixAptSourcesAction{
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

func (a *FixAptSourcesAction) Description() string {
	return "apt sources fix"
}

func (a *FixAptSourcesAction) Execute() bool {
	sedCmds := []string{
		cli.NewSed().Replace("/etc/apt/sources.list", "oracular", "plucky"),
		cli.NewSed().Replace("/etc/apt/sources.list", "old-releases.ubuntu.com", "archive.ubuntu.com"),
	}
	containerIDInt, err := strconv.Atoi(*a.ContainerID)
	if err != nil {
		libs.GetLogger("fix_apt_sources").Printf("Invalid container ID: %s", *a.ContainerID)
		return false
	}
	allSucceeded := true
	for _, sedCmd := range sedCmds {
		output, exitCode := a.PCTService.Execute(containerIDInt, sedCmd, nil)
		if exitCode != nil && *exitCode != 0 {
			if strings.Contains(output, "No such file or directory") || strings.Contains(output, "can't read") {
				libs.GetLogger("fix_apt_sources").Printf("sources.list not found (may use sources.list.d), skipping fix")
			} else {
				libs.GetLogger("fix_apt_sources").Printf("Failed to fix apt sources: %s", output)
				allSucceeded = false
			}
		}
	}
	return allSucceeded
}

