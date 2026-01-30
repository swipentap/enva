package actions

import (
	"enva/cli"
	"enva/libs"
	"enva/services"
	"strings"
	"time"
)

// EnableSinsServiceAction enables and starts SiNS DNS service
type EnableSinsServiceAction struct {
	*BaseAction
}

func NewEnableSinsServiceAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &EnableSinsServiceAction{
		BaseAction: &BaseAction{
			SSHService:   sshService,
			APTService:   aptService,
			PCTService:   pctService,
			ContainerID:  containerID,
			Cfg:          cfg,
			ContainerCfg: containerCfg,
		},
	}
}

func (a *EnableSinsServiceAction) Description() string {
	return "sins dns service enablement"
}

func (a *EnableSinsServiceAction) Execute() bool {
	if a.SSHService == nil {
		libs.GetLogger("enable_sins_service").Printf("SSH service not initialized")
		return false
	}
	logger := libs.GetLogger("enable_sins_service")

	// First, check what's using port 53
	logger.Printf("Checking what's using port 53...")
	portCheckCmd := "ss -tulnp | grep ':53' || echo 'port_free'"
	portOutput, _ := a.SSHService.Execute(portCheckCmd, nil, true) // sudo=True
	if !strings.Contains(portOutput, "port_free") {
		logger.Printf("Port 53 is in use: %s", portOutput)
		// Check if systemd-resolved is still running
		resolvedCheckCmd := cli.NewSystemCtl().Service("systemd-resolved").IsActive()
		resolvedStatus, _ := a.SSHService.Execute(resolvedCheckCmd, nil, true) // sudo=True
		if cli.ParseIsActive(resolvedStatus) {
			logger.Printf("systemd-resolved is still active, stopping it...")
			stopResolvedCmd := cli.NewSystemCtl().Service("systemd-resolved").Stop()
			a.SSHService.Execute(stopResolvedCmd, nil, true) // sudo=True
			disableResolvedCmd := cli.NewSystemCtl().Service("systemd-resolved").Disable()
			a.SSHService.Execute(disableResolvedCmd, nil, true) // sudo=True
			time.Sleep(2 * time.Second)
		}
		// Kill whatever is using port 53 (except sins itself)
		killPort53Cmd := "fuser -k 53/udp 53/tcp || true"
		a.SSHService.Execute(killPort53Cmd, nil, true) // sudo=True
		time.Sleep(1 * time.Second)
	}

	logger.Printf("Enabling and starting SiNS service...")
	enableCmd := cli.NewSystemCtl().Service("sins").Enable()
	startCmd := cli.NewSystemCtl().Service("sins").Start()
	a.SSHService.Execute(enableCmd, nil, true)                    // sudo=True
	output, exitCode := a.SSHService.Execute(startCmd, nil, true) // sudo=True
	time.Sleep(5 * time.Second)                                   // Give it more time to start
	if exitCode != nil && *exitCode != 0 {
		statusCmd := cli.NewSystemCtl().Service("sins").Status()
		statusOutput, _ := a.SSHService.Execute(statusCmd, nil, true) // sudo=True
		if strings.Contains(statusOutput, "activating (auto-restart)") || strings.Contains(statusOutput, "auto-restart") {
			journalCmd := "journalctl -u sins.service -n 10 --no-pager | grep -i 'postgres\\|connection refused' || true"
			journalOutput, _ := a.SSHService.Execute(journalCmd, nil, true) // sudo=True
			if strings.Contains(strings.ToLower(journalOutput), "postgres") || strings.Contains(strings.ToLower(journalOutput), "connection refused") {
				libs.GetLogger("enable_sins_service").Printf("SiNS service is restarting due to PostgreSQL connection failure. This is expected if PostgreSQL is not yet available. Service will retry automatically.")
				return true
			}
			journalCmd2 := "journalctl -u sins.service -n 10 --no-pager | grep -i 'address already in use\\|port.*53' || true"
			journalOutput2, _ := a.SSHService.Execute(journalCmd2, nil, true) // sudo=True
			if strings.Contains(strings.ToLower(journalOutput2), "address already in use") || strings.Contains(strings.ToLower(journalOutput2), "port") {
				libs.GetLogger("enable_sins_service").Printf("SiNS service cannot bind to port 53 - port is already in use. Ensure systemd-resolved is disabled.")
				return false
			}
		}
		libs.GetLogger("enable_sins_service").Printf("Failed to start SiNS service: %s", output)
		libs.GetLogger("enable_sins_service").Printf("Service status:\n%s", statusOutput)
		journalCmd := "journalctl -u sins.service -n 50 --no-pager"
		journalOutput, _ := a.SSHService.Execute(journalCmd, nil, true) // sudo=True
		libs.GetLogger("enable_sins_service").Printf("Service journal logs:\n%s", journalOutput)
		return false
	}
	statusCmd := cli.NewSystemCtl().Service("sins").IsActive()
	status, exitCode := a.SSHService.Execute(statusCmd, nil, true) // sudo=True
	if exitCode != nil && *exitCode == 0 && cli.ParseIsActive(status) {
		portCheckCmd := "ss -tulnp | grep -E ':53.*sins|:53.*dotnet' || echo 'not_listening'"
		portOutput, _ := a.SSHService.Execute(portCheckCmd, nil, true) // sudo=True
		if !strings.Contains(portOutput, "not_listening") && strings.Contains(portOutput, ":53") {
			libs.GetLogger("enable_sins_service").Printf("SiNS DNS server is running and listening on port 53")
			return true
		}
		libs.GetLogger("enable_sins_service").Printf("SiNS service is active but not listening on port 53")
		// Check if it's restarting due to PostgreSQL connection issues
		statusCmd2 := cli.NewSystemCtl().Service("sins").Status()
		statusOutput2, _ := a.SSHService.Execute(statusCmd2, nil, true) // sudo=True
		if strings.Contains(statusOutput2, "activating (auto-restart)") || strings.Contains(statusOutput2, "auto-restart") {
			journalCmd := "journalctl -u sins.service -n 20 --no-pager | grep -i 'postgres\\|connection refused' || true"
			journalOutput, _ := a.SSHService.Execute(journalCmd, nil, true) // sudo=True
			if strings.Contains(strings.ToLower(journalOutput), "postgres") || strings.Contains(strings.ToLower(journalOutput), "connection refused") {
				libs.GetLogger("enable_sins_service").Printf("SiNS service is restarting due to PostgreSQL connection failure. This is expected if PostgreSQL is not yet available. Service will retry automatically.")
				return true
			}
			journalCmd2 := "journalctl -u sins.service -n 20 --no-pager | grep -i 'address already in use\\|port.*53\\|bind.*53' || true"
			journalOutput2, _ := a.SSHService.Execute(journalCmd2, nil, true) // sudo=True
			if strings.Contains(strings.ToLower(journalOutput2), "address already in use") || strings.Contains(strings.ToLower(journalOutput2), "port") || strings.Contains(strings.ToLower(journalOutput2), "bind") {
				libs.GetLogger("enable_sins_service").Printf("SiNS service cannot bind to port 53. Attempting to fix...")
				// Kill whatever is using port 53
				killPort53Cmd := "fuser -k 53/udp 53/tcp || true"
				a.SSHService.Execute(killPort53Cmd, nil, true) // sudo=True
				time.Sleep(2 * time.Second)
				// Restart the service
				restartCmd := cli.NewSystemCtl().Service("sins").Restart()
				a.SSHService.Execute(restartCmd, nil, true) // sudo=True
				time.Sleep(5 * time.Second)
				// Check again
				portCheckCmd2 := "ss -tulnp | grep -E ':53.*sins|:53.*dotnet' || echo 'not_listening'"
				portOutput2, _ := a.SSHService.Execute(portCheckCmd2, nil, true) // sudo=True
				if !strings.Contains(portOutput2, "not_listening") && strings.Contains(portOutput2, ":53") {
					libs.GetLogger("enable_sins_service").Printf("SiNS DNS server is now listening on port 53 after restart")
					return true
				}
				libs.GetLogger("enable_sins_service").Printf("SiNS service cannot bind to port 53 - port is already in use. Ensure systemd-resolved is disabled.")
				return false
			}
		}
		// Service is active but not listening - try to diagnose and fix
		libs.GetLogger("enable_sins_service").Printf("Diagnosing why SiNS is not listening on port 53...")
		// Check journal logs for errors
		journalCmd := "journalctl -u sins.service -n 50 --no-pager"
		journalOutput, _ := a.SSHService.Execute(journalCmd, nil, true) // sudo=True
		libs.GetLogger("enable_sins_service").Printf("Service journal logs:\n%s", journalOutput)

		// Check if config file exists
		configCheckCmd := "test -f /etc/sins/appsettings.json && echo 'exists' || test -f /opt/sins/appsettings.json && echo 'exists' || test -f /opt/sins/app/appsettings.json && echo 'exists' || echo 'missing'"
		configOutput, _ := a.SSHService.Execute(configCheckCmd, nil, true) // sudo=True
		if strings.Contains(configOutput, "missing") {
			libs.GetLogger("enable_sins_service").Printf("ERROR: SiNS appsettings.json is missing. Service cannot start properly.")
			return false
		}

		// Try restarting the service
		libs.GetLogger("enable_sins_service").Printf("Attempting to restart SiNS service...")
		restartCmd := cli.NewSystemCtl().Service("sins").Restart()
		a.SSHService.Execute(restartCmd, nil, true) // sudo=True
		time.Sleep(5 * time.Second)

		// Check again if it's listening
		portCheckCmd3 := "ss -tulnp | grep -E ':53.*sins|:53.*dotnet' || echo 'not_listening'"
		portOutput3, _ := a.SSHService.Execute(portCheckCmd3, nil, true) // sudo=True
		if !strings.Contains(portOutput3, "not_listening") && strings.Contains(portOutput3, ":53") {
			libs.GetLogger("enable_sins_service").Printf("SiNS DNS server is now listening on port 53 after restart")
			return true
		}

		// Still not listening - check what's using port 53
		portCheckCmd4 := "ss -tulnp | grep ':53' || echo 'port_free'"
		portOutput4, _ := a.SSHService.Execute(portCheckCmd4, nil, true) // sudo=True
		libs.GetLogger("enable_sins_service").Printf("Port 53 status: %s", portOutput4)

		return false
	}
	statusCmd2 := cli.NewSystemCtl().Service("sins").Status()
	statusOutput2, _ := a.SSHService.Execute(statusCmd2, nil, true) // sudo=True
	if strings.Contains(statusOutput2, "activating (auto-restart)") || strings.Contains(statusOutput2, "auto-restart") {
		journalCmd := "journalctl -u sins.service -n 10 --no-pager | grep -i 'postgres\\|connection refused' || true"
		journalOutput, _ := a.SSHService.Execute(journalCmd, nil, true) // sudo=True
		if strings.Contains(strings.ToLower(journalOutput), "postgres") || strings.Contains(strings.ToLower(journalOutput), "connection refused") {
			libs.GetLogger("enable_sins_service").Printf("SiNS service is restarting due to PostgreSQL connection failure. This is expected if PostgreSQL is not yet available. Service will retry automatically.")
			return true
		}
		journalCmd2 := "journalctl -u sins.service -n 10 --no-pager | grep -i 'address already in use\\|port.*53' || true"
		journalOutput2, _ := a.SSHService.Execute(journalCmd2, nil, true) // sudo=True
		if strings.Contains(strings.ToLower(journalOutput2), "address already in use") || strings.Contains(strings.ToLower(journalOutput2), "port") {
			libs.GetLogger("enable_sins_service").Printf("SiNS service cannot bind to port 53 - port is already in use. Ensure systemd-resolved is disabled.")
			return false
		}
	}
	processCheckCmd := "pgrep -f 'sins\\.dll|^sins '  && echo running || echo not_running"
	processOutput, _ := a.SSHService.Execute(processCheckCmd, nil, true) // sudo=True
	if strings.Contains(processOutput, "running") {
		// Even if process is running, verify it's listening on port 53
		portCheckCmd := "ss -tulnp | grep -E ':53.*sins|:53.*dotnet' || echo 'not_listening'"
		portOutput, _ := a.SSHService.Execute(portCheckCmd, nil, true) // sudo=True
		if !strings.Contains(portOutput, "not_listening") && strings.Contains(portOutput, ":53") {
			libs.GetLogger("enable_sins_service").Printf("SiNS process is running and listening on port 53")
			return true
		}
		libs.GetLogger("enable_sins_service").Printf("SiNS process is running but not listening on port 53")
		return false
	}
	libs.GetLogger("enable_sins_service").Printf("SiNS DNS server is not running")
	return false
}
