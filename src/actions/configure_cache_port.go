package actions

import (
	"fmt"
	"strings"
	"time"
	"enva/cli"
	"enva/libs"
	"enva/services"
)

// ConfigureCachePortAction configures apt-cacher-ng port
type ConfigureCachePortAction struct {
	*BaseAction
}

func NewConfigureCachePortAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &ConfigureCachePortAction{
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

func (a *ConfigureCachePortAction) Description() string {
	return "apt-cacher-ng port configuration"
}

func (a *ConfigureCachePortAction) Execute() bool {
	if a.SSHService == nil {
		libs.GetLogger("configure_cache_port").Error("SSH service not initialized")
		return false
	}
	port := a.Cfg.APTCachePort()
	configFile := "/etc/apt-cacher-ng/acng.conf"
	configDir := "/etc/apt-cacher-ng"
	
	// Ensure directory exists (apt-cacher-ng installation should create it, but be safe)
	mkdirCmd := cli.NewFileOps().Mkdir(configDir, true) // parents=true
	mkdirOutput, mkdirExitCode := a.SSHService.Execute(mkdirCmd, nil, true) // sudo=True
	if mkdirExitCode != nil && *mkdirExitCode != 0 {
		libs.GetLogger("configure_cache_port").Warning("Failed to create directory %s: %s", configDir, mkdirOutput)
		// Continue anyway, as the directory might already exist
	}
	
	// Try to uncomment and replace if Port line is commented
	uncommentCmd := cli.NewSed().Flags("").Replace(configFile, "^#\\s*Port:.*", fmt.Sprintf("Port: %d", port))
	output, exitCode := a.SSHService.Execute(uncommentCmd, nil, true) // sudo=True
	
	// If that didn't work, try replacing uncommented Port line
	// Python: if exit_code is not None and exit_code != 0:
	if exitCode != nil && *exitCode != 0 {
		replaceCmd := cli.NewSed().Flags("").Replace(configFile, "^Port:.*", fmt.Sprintf("Port: %d", port))
		output, exitCode = a.SSHService.Execute(replaceCmd, nil, true) // sudo=True
	}
	
	// If still no match, append the Port line
	// Python: if exit_code is not None and exit_code != 0:
	if exitCode != nil && *exitCode != 0 {
		appendCmd := cli.NewFileOps().Append().Write(configFile, fmt.Sprintf("Port: %d\n", port))
		output, exitCode = a.SSHService.Execute(appendCmd, nil, true) // sudo=True
		// Python: if exit_code is not None and exit_code != 0:
		if exitCode != nil && *exitCode != 0 {
			libs.GetLogger("configure_cache_port").Error("append apt-cacher-ng port failed with exit code %d", *exitCode)
			if output != "" {
				libs.GetLogger("configure_cache_port").Error("append output: %s", output)
			}
			return false
		}
	}
	
	// Configure timeout and retry settings
	libs.GetLogger("configure_cache_port").Printf("Configuring apt-cacher-ng timeout and retry settings...")
	timeoutSettings := map[string]string{
		"DlMaxRetries":   "5",
		"NetworkTimeout": "120",
		"DisconnectTimeout": "30",
	}
	for settingName, settingValue := range timeoutSettings {
		checkCmd := fmt.Sprintf("grep -E '^#?%s:' %s || echo 'not_found'", settingName, configFile)
		checkOutput, _ := a.SSHService.Execute(checkCmd, nil, true) // sudo=True
		if strings.Contains(checkOutput, "not_found") {
			appendCmd := cli.NewFileOps().Append().Write(configFile, fmt.Sprintf("%s: %s\n", settingName, settingValue))
			output, exitCode = a.SSHService.Execute(appendCmd, nil, true) // sudo=True
			if exitCode != nil && *exitCode != 0 {
				libs.GetLogger("configure_cache_port").Warning("Failed to add %s: %s", settingName, output)
			}
		} else {
			replaceCmd := cli.NewSed().Flags("").Replace(configFile, fmt.Sprintf("^#?%s:.*", settingName), fmt.Sprintf("%s: %s", settingName, settingValue))
			output, exitCode = a.SSHService.Execute(replaceCmd, nil, true) // sudo=True
			if exitCode != nil && *exitCode != 0 {
				libs.GetLogger("configure_cache_port").Warning("Failed to update %s: %s", settingName, output)
			}
		}
	}
	
	// Restart service to apply configuration changes
	libs.GetLogger("configure_cache_port").Info("Restarting apt-cacher-ng service to apply configuration changes...")
	restartCmd := cli.NewSystemCtl().Service("apt-cacher-ng").Restart()
	output, exitCode = a.SSHService.Execute(restartCmd, nil, true) // sudo=True
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("configure_cache_port").Error("restart apt-cacher-ng service failed with exit code %d", *exitCode)
		if output != "" {
			lines := strings.Split(output, "\n")
			if len(lines) > 0 {
				libs.GetLogger("configure_cache_port").Error("restart output: %s", lines[len(lines)-1])
			}
		}
		return false
	}
	
	// Wait for service to be ready
	time.Sleep(2 * time.Second)
	
	// Verify service is active after restart
	isActiveCmd := cli.NewSystemCtl().Service("apt-cacher-ng").IsActive()
	status, exitCode := a.SSHService.Execute(isActiveCmd, nil, true) // sudo=True
	if exitCode != nil && *exitCode == 0 && cli.ParseIsActive(status) {
		return true
	}
	libs.GetLogger("configure_cache_port").Error("apt-cacher-ng service is not active after restart")
	return false
}

