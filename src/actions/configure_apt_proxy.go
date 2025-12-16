package actions

import (
	"fmt"
	"strconv"
	"enva/cli"
	"enva/libs"
	"enva/services"
)

// ConfigureAptProxyAction configures apt cache proxy
type ConfigureAptProxyAction struct {
	*BaseAction
}

func NewConfigureAptProxyAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &ConfigureAptProxyAction{
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

func (a *ConfigureAptProxyAction) Description() string {
	return "apt cache proxy configuration"
}

func (a *ConfigureAptProxyAction) Execute() bool {
	// Find apt-cache container
	var aptCacheContainer *libs.ContainerConfig
	for i := range a.Cfg.Containers {
		if a.Cfg.Containers[i].Name == a.Cfg.APTCacheCT {
			aptCacheContainer = &a.Cfg.Containers[i]
			break
		}
	}
	if aptCacheContainer == nil {
		return true // No apt-cache, skip
	}
	if aptCacheContainer.IPAddress == nil {
		return false
	}
	aptCacheIP := *aptCacheContainer.IPAddress
	aptCachePort := a.Cfg.APTCachePort()
	proxyContent := fmt.Sprintf("Acquire::http::Proxy \"http://%s:%d\";\n", aptCacheIP, aptCachePort)
	proxyCmd := cli.NewFileOps().Write("/etc/apt/apt.conf.d/01proxy", proxyContent)
	containerIDInt, err := strconv.Atoi(*a.ContainerID)
	if err != nil {
		libs.GetLogger("configure_apt_proxy").Printf("Invalid container ID: %s", *a.ContainerID)
		return false
	}
	output, exitCode := a.PCTService.Execute(containerIDInt, proxyCmd, nil)
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("configure_apt_proxy").Printf("Failed to configure apt cache proxy: %s", output)
		return false
	}
	return true
}

