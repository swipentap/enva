package actions

import (
	"fmt"
	"strconv"
	"enva/cli"
	"enva/libs"
	"enva/services"
)

// TemplateCleanupAction cleans up template before archiving
type TemplateCleanupAction struct {
	*BaseAction
}

func NewTemplateCleanupAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &TemplateCleanupAction{
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

func (a *TemplateCleanupAction) Description() string {
	return "template cleanup"
}

func (a *TemplateCleanupAction) Execute() bool {
	containerIDInt, err := strconv.Atoi(*a.ContainerID)
	if err != nil {
		libs.GetLogger("template_cleanup").Printf("Invalid container ID: %s", *a.ContainerID)
		return false
	}
	defaultUser := a.Cfg.Users.DefaultUser()
	cleanupCommands := []struct {
		desc string
		cmd  string
	}{
		{"Remove apt proxy configuration", cli.NewFileOps().Force(true).Remove("/etc/apt/apt.conf.d/01proxy")},
		{"Remove SSH host keys", cli.NewFileOps().Force(true).AllowGlob().Remove("/etc/ssh/ssh_host_*")},
		{"Truncate machine-id", cli.NewFileOps().Truncate("/etc/machine-id")},
		{"Remove DBus machine-id", cli.NewFileOps().Force(true).Remove("/var/lib/dbus/machine-id")},
		{"Recreate DBus machine-id symlink", cli.NewFileOps().Symlink("/etc/machine-id", "/var/lib/dbus/machine-id")},
		{"Remove apt lists", cli.NewFileOps().Force(true).Recursive().AllowGlob().Remove("/var/lib/apt/lists/*")},
		{"Remove log files", cli.NewFileOps().SuppressErrors().FindDelete("/var/log", "*.log", "f")},
		{"Remove compressed logs", cli.NewFileOps().SuppressErrors().FindDelete("/var/log", "*.gz", "f")},
		{"Clear root history", cli.NewFileOps().SuppressErrors().Truncate("/root/.bash_history")},
		{fmt.Sprintf("Clear %s history", defaultUser), cli.NewFileOps().SuppressErrors().Truncate(fmt.Sprintf("/home/%s/.bash_history", defaultUser))},
	}
	for _, item := range cleanupCommands {
		output, exitCode := a.PCTService.Execute(containerIDInt, item.cmd, nil)
		if exitCode != nil && *exitCode != 0 {
			libs.GetLogger("template_cleanup").Printf("Failed to %s: %s", item.desc, output)
		}
	}
	// Clean apt cache
	cleanCmd := "apt-get clean 2>&1"
	output, exitCode := a.PCTService.Execute(containerIDInt, cleanCmd, nil)
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("template_cleanup").Printf("Failed to clean apt cache: %s", output)
	}
	return true
}

