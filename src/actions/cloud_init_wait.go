package actions

import (
	"enva/cli"
	"enva/libs"
	"enva/services"
)

// CloudInitWaitAction waits for cloud-init to complete
type CloudInitWaitAction struct {
	*BaseAction
}

func NewCloudInitWaitAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &CloudInitWaitAction{
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

func (a *CloudInitWaitAction) Description() string {
	return "cloud-init wait"
}

func (a *CloudInitWaitAction) Execute() bool {
	if a.SSHService == nil {
		libs.GetLogger("cloud_init_wait").Printf("SSH service not initialized")
		return false
	}

	// Check if cloud-init exists
	existsCmd := cli.NewCommand().SetCommand("cloud-init").Exists()
	_, exitCode := a.SSHService.Execute(existsCmd, libs.IntPtr(10))

	// Skip if cloud-init is not installed
	if exitCode == nil || *exitCode != 0 {
		libs.GetLogger("cloud_init_wait").Printf("cloud-init not found, skipping")
		return true
	}

	// Wait for cloud-init to complete
	waitCmd := cli.NewCloudInit().LogFile("/tmp/cloud-init-wait.log").Wait(nil)
	timeout := 180
	waitOutput, waitExitCode := a.SSHService.Execute(waitCmd, &timeout)

	if waitExitCode != nil && *waitExitCode != 0 {
		libs.GetLogger("cloud_init_wait").Printf("cloud-init wait failed with exit code %d", *waitExitCode)
		if waitOutput != "" {
			outputLen := len(waitOutput)
			start := 0
			if outputLen > 500 {
				start = outputLen - 500
			}
			libs.GetLogger("cloud_init_wait").Printf("cloud-init wait output: %s", waitOutput[start:])
		}
	}

	// Clean cloud-init logs
	cleanCmd := cli.NewCloudInit().SuppressOutput(true).Clean(true, false, false)
	cleanTimeout := 30
	cleanOutput, cleanExitCode := a.SSHService.Execute(cleanCmd, &cleanTimeout)
	if cleanExitCode != nil && *cleanExitCode != 0 {
		libs.GetLogger("cloud_init_wait").Printf("cloud-init clean failed with exit code %d", *cleanExitCode)
		if cleanOutput != "" {
			outputLen := len(cleanOutput)
			start := 0
			if outputLen > 500 {
				start = outputLen - 500
			}
			libs.GetLogger("cloud_init_wait").Printf("cloud-init clean output: %s", cleanOutput[start:])
		}
	}

	return true
}


