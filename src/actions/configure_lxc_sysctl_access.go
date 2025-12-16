package actions

import (
	"fmt"
	"strings"
	"time"
	"enva/libs"
	"enva/services"
)

// ConfigureLxcSysctlAccessAction configures LXC container for sysctl access
type ConfigureLxcSysctlAccessAction struct {
	*BaseAction
}

func NewConfigureLxcSysctlAccessAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &ConfigureLxcSysctlAccessAction{
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

func (a *ConfigureLxcSysctlAccessAction) Description() string {
	return "lxc sysctl access configuration"
}

func (a *ConfigureLxcSysctlAccessAction) Execute() bool {
	if a.PCTService == nil || a.ContainerID == nil {
		libs.GetLogger("configure_lxc_sysctl_access").Printf("PCT service or container ID not available")
		return false
	}
	libs.GetLogger("configure_lxc_sysctl_access").Printf("Configuring LXC container for sysctl access...")
	configFile := fmt.Sprintf("/etc/pve/lxc/%s.conf", *a.ContainerID)
	
	// Get LXC service from PCT service
	lxcService := a.PCTService.GetLXCService()
	if lxcService != nil {
		checkCmd := fmt.Sprintf("grep -E '^lxc.(mount.auto|apparmor.profile|cap.drop|cgroup2.devices.allow)' %s 2>/dev/null || echo 'not_found'", configFile)
		checkOutput, _ := lxcService.Execute(checkCmd, nil)
		
		needsRestart := false
		configsToAdd := []string{}
		
		if !strings.Contains(checkOutput, "lxc.apparmor.profile") {
			configsToAdd = append(configsToAdd, "lxc.apparmor.profile: unconfined")
			needsRestart = true
		}
		if !strings.Contains(checkOutput, "lxc.cap.drop:") {
			configsToAdd = append(configsToAdd, "lxc.cap.drop:")
			needsRestart = true
		}
		if !strings.Contains(checkOutput, "lxc.mount.auto") {
			configsToAdd = append(configsToAdd, "lxc.mount.auto: proc:rw sys:rw")
			needsRestart = true
		}
		if !strings.Contains(checkOutput, "lxc.cgroup2.devices.allow") {
			configsToAdd = append(configsToAdd, "lxc.cgroup2.devices.allow: c 1:11 rwm")
			needsRestart = true
		}
		
		if len(configsToAdd) > 0 {
			libs.GetLogger("configure_lxc_sysctl_access").Printf("Adding k3s LXC configuration requirements...")
			for _, configLine := range configsToAdd {
				addCmd := fmt.Sprintf("echo '%s' >> %s", configLine, configFile)
				addOutput, addExit := lxcService.Execute(addCmd, nil)
				if addExit != nil && *addExit != 0 {
					outputLen := len(addOutput)
					start := 0
					if outputLen > 200 {
						start = outputLen - 200
					}
					libs.GetLogger("configure_lxc_sysctl_access").Printf("Failed to add %s: %s", configLine, addOutput[start:])
					return false
				}
				libs.GetLogger("configure_lxc_sysctl_access").Printf("Added: %s", configLine)
			}
			
			if needsRestart {
				libs.GetLogger("configure_lxc_sysctl_access").Printf("Container needs to be restarted for k3s LXC configuration to take effect...")
				restartCmd := fmt.Sprintf("pct stop %s && sleep 2 && pct start %s", *a.ContainerID, *a.ContainerID)
				restartOutput, restartExit := lxcService.Execute(restartCmd, nil)
				if restartExit != nil && *restartExit != 0 {
					outputLen := len(restartOutput)
					start := 0
					if outputLen > 200 {
						start = outputLen - 200
					}
					libs.GetLogger("configure_lxc_sysctl_access").Printf("Container restart had issues: %s", restartOutput[start:])
				} else {
					libs.GetLogger("configure_lxc_sysctl_access").Printf("Container restarted to apply k3s LXC configuration")
					time.Sleep(5 * time.Second)
				}
			}
		} else {
			libs.GetLogger("configure_lxc_sysctl_access").Printf("All k3s LXC configuration requirements already present")
		}
		
		libs.GetLogger("configure_lxc_sysctl_access").Printf("Ensuring /dev/kmsg is configured as symlink to /dev/console (k3s LXC requirement)...")
		kmsgCmd := fmt.Sprintf("pct exec %s -- bash -c 'rm -f /dev/kmsg && ln -sf /dev/console /dev/kmsg && ls -l /dev/kmsg'", *a.ContainerID)
		kmsgOutput, kmsgExit := lxcService.Execute(kmsgCmd, nil)
		if kmsgExit != nil && *kmsgExit != 0 {
			outputLen := len(kmsgOutput)
			start := 0
			if outputLen > 200 {
				start = outputLen - 200
			}
			libs.GetLogger("configure_lxc_sysctl_access").Printf("Failed to create /dev/kmsg symlink: %s", kmsgOutput[start:])
		} else {
			libs.GetLogger("configure_lxc_sysctl_access").Printf("/dev/kmsg symlink configured successfully")
		}
	} else {
		libs.GetLogger("configure_lxc_sysctl_access").Printf("LXC service not available in PCT service")
		return false
	}
	return true
}

