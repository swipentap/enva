package actions

import (
	"fmt"
	"strings"
	"time"
	"enva/libs"
	"enva/services"
)

// WaitAptCacheReadyAction waits for apt-cache service to be ready
type WaitAptCacheReadyAction struct {
	*BaseAction
}

func NewWaitAptCacheReadyAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &WaitAptCacheReadyAction{
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

func (a *WaitAptCacheReadyAction) Description() string {
	return "wait apt cache ready"
}

func (a *WaitAptCacheReadyAction) Execute() bool {
	if a.Cfg == nil || a.ContainerCfg == nil {
		libs.GetLogger("wait_apt_cache_ready").Printf("Configuration not initialized")
		return false
	}
	
	libs.GetLogger("wait_apt_cache_ready").Printf("Verifying apt-cache service is ready...")
	maxAttempts := 20
	proxmoxHost := a.Cfg.LXCHost()
	aptCachePort := a.Cfg.APTCachePort()
	
	lxcService := services.NewLXCService(proxmoxHost, &a.Cfg.SSH)
	if !lxcService.Connect() {
		libs.GetLogger("wait_apt_cache_ready").Printf("Failed to connect to Proxmox host for apt-cache verification")
		return false
	}
	defer lxcService.Disconnect()
	
	pctService := services.NewPCTService(lxcService)
	for attempt := 1; attempt <= maxAttempts; attempt++ {
		serviceCheck, _ := pctService.Execute(a.ContainerCfg.ID, "systemctl is-active apt-cacher-ng 2>/dev/null || echo 'inactive'", libs.IntPtr(10))
		if strings.Contains(serviceCheck, "active") {
			portCheckCmd := fmt.Sprintf("nc -z localhost %d 2>/dev/null && echo 'port_open' || echo 'port_closed'", aptCachePort)
			portCheck, _ := pctService.Execute(a.ContainerCfg.ID, portCheckCmd, libs.IntPtr(10))
			if strings.Contains(portCheck, "port_open") {
				testCmd := fmt.Sprintf("timeout 10 wget -qO- 'http://127.0.0.1:%d/acng-report.html' 2>&1 | grep -q 'Apt-Cacher NG' && echo 'working' || echo 'not_working'", aptCachePort)
				functionalityTest, _ := pctService.Execute(a.ContainerCfg.ID, testCmd, libs.IntPtr(15))
				if strings.Contains(functionalityTest, "working") {
					if a.ContainerCfg.IPAddress != nil {
						libs.GetLogger("wait_apt_cache_ready").Printf("apt-cache service is ready on %s:%d", *a.ContainerCfg.IPAddress, aptCachePort)
					}
					return true
				} else if attempt < maxAttempts {
					libs.GetLogger("wait_apt_cache_ready").Printf("apt-cache service not fully ready yet (attempt %d/%d), waiting...", attempt, maxAttempts)
					time.Sleep(2 * time.Second)
					continue
				}
			}
		} else {
			if attempt == 1 {
				startCmd := "systemctl start apt-cacher-ng 2>&1"
				startOutput, _ := pctService.Execute(a.ContainerCfg.ID, startCmd, libs.IntPtr(10))
				if startOutput != "" {
					libs.GetLogger("wait_apt_cache_ready").Printf("Service start attempt output: %s", startOutput)
				}
				statusCmd := "systemctl status apt-cacher-ng --no-pager -l 2>&1 | head -15"
				statusOutput, _ := pctService.Execute(a.ContainerCfg.ID, statusCmd, libs.IntPtr(10))
				if statusOutput != "" {
					libs.GetLogger("wait_apt_cache_ready").Printf("Service status: %s", statusOutput)
				}
			}
		}
		if attempt < maxAttempts {
			libs.GetLogger("wait_apt_cache_ready").Printf("Waiting for apt-cache service... (%d/%d)", attempt, maxAttempts)
			time.Sleep(3 * time.Second)
		} else {
			statusCmd := "systemctl status apt-cacher-ng --no-pager -l 2>&1"
			statusOutput, _ := pctService.Execute(a.ContainerCfg.ID, statusCmd, libs.IntPtr(10))
			journalCmd := "journalctl -u apt-cacher-ng --no-pager -n 30 2>&1"
			journalOutput, _ := pctService.Execute(a.ContainerCfg.ID, journalCmd, libs.IntPtr(10))
			errorMsg := "apt-cache service did not become ready in time"
			if statusOutput != "" {
				errorMsg += fmt.Sprintf("\nService status: %s", statusOutput)
			}
			if journalOutput != "" {
				errorMsg += fmt.Sprintf("\nService logs: %s", journalOutput)
			}
			libs.GetLogger("wait_apt_cache_ready").Printf(errorMsg)
			return false
		}
	}
	return false
}


