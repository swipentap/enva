package actions

import (
	"strconv"
	"enva/cli"
	"enva/libs"
	"enva/services"
)

// InstallGlusterfsAction installs GlusterFS server packages
type InstallGlusterfsAction struct {
	*BaseAction
}

func NewInstallGlusterfsAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &InstallGlusterfsAction{
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

func (a *InstallGlusterfsAction) Description() string {
	return "glusterfs server installation"
}

func (a *InstallGlusterfsAction) Execute() bool {
	containerIDInt, err := strconv.Atoi(*a.ContainerID)
	if err != nil {
		libs.GetLogger("install_glusterfs").Printf("Invalid container ID: %s", *a.ContainerID)
		return false
	}
	libs.GetLogger("install_glusterfs").Printf("Installing GlusterFS server and client packages...")
	updateCmd := cli.NewApt().Update()
	updateOutput, exitCode := a.PCTService.Execute(containerIDInt, updateCmd, libs.IntPtr(600))
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("install_glusterfs").Printf("Failed to update apt: %s", updateOutput)
		return false
	}
	installCmd := cli.NewApt().Install([]string{"glusterfs-server", "glusterfs-client"})
	installOutput, exitCode := a.PCTService.Execute(containerIDInt, installCmd, libs.IntPtr(300))
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("install_glusterfs").Printf("Failed to install GlusterFS: %s", installOutput)
		return false
	}
	enableCmd := "systemctl enable glusterd && systemctl start glusterd"
	enableOutput, exitCode := a.PCTService.Execute(containerIDInt, enableCmd, libs.IntPtr(30))
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("install_glusterfs").Printf("Failed to start glusterd: %s", enableOutput)
		return false
	}
	libs.GetLogger("install_glusterfs").Printf("GlusterFS server installed and started successfully")
	return true
}


