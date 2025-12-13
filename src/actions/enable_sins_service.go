package actions

import (
	"strings"
	"time"
	"enva/cli"
	"enva/libs"
	"enva/services"
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
			ContainerID: containerID,
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
	libs.GetLogger("enable_sins_service").Printf("Enabling and starting SiNS service...")
	enableCmd := cli.NewSystemCtl().Service("sins").Enable()
	startCmd := cli.NewSystemCtl().Service("sins").Start()
	a.SSHService.Execute(enableCmd, nil)
	output, exitCode := a.SSHService.Execute(startCmd, nil)
	time.Sleep(3 * time.Second)
	if exitCode != nil && *exitCode != 0 {
		statusCmd := cli.NewSystemCtl().Service("sins").Status()
		statusOutput, _ := a.SSHService.Execute(statusCmd, nil)
		if strings.Contains(statusOutput, "activating (auto-restart)") || strings.Contains(statusOutput, "auto-restart") {
			journalCmd := "journalctl -u sins.service -n 10 --no-pager | grep -i 'postgres\\|connection refused' || true"
			journalOutput, _ := a.SSHService.Execute(journalCmd, nil)
			if strings.Contains(strings.ToLower(journalOutput), "postgres") || strings.Contains(strings.ToLower(journalOutput), "connection refused") {
				libs.GetLogger("enable_sins_service").Printf("SiNS service is restarting due to PostgreSQL connection failure. This is expected if PostgreSQL is not yet available. Service will retry automatically.")
				return true
			}
			journalCmd2 := "journalctl -u sins.service -n 10 --no-pager | grep -i 'address already in use\\|port.*53' || true"
			journalOutput2, _ := a.SSHService.Execute(journalCmd2, nil)
			if strings.Contains(strings.ToLower(journalOutput2), "address already in use") || strings.Contains(strings.ToLower(journalOutput2), "port") {
				libs.GetLogger("enable_sins_service").Printf("SiNS service cannot bind to port 53 - port is already in use. Ensure systemd-resolved is disabled.")
				return false
			}
		}
		libs.GetLogger("enable_sins_service").Printf("Failed to start SiNS service: %s", output)
		libs.GetLogger("enable_sins_service").Printf("Service status:\n%s", statusOutput)
		journalCmd := "journalctl -u sins.service -n 50 --no-pager"
		journalOutput, _ := a.SSHService.Execute(journalCmd, nil)
		libs.GetLogger("enable_sins_service").Printf("Service journal logs:\n%s", journalOutput)
		return false
	}
	statusCmd := cli.NewSystemCtl().Service("sins").IsActive()
	status, exitCode := a.SSHService.Execute(statusCmd, nil)
	if exitCode != nil && *exitCode == 0 && cli.ParseIsActive(status) {
		portCheckCmd := "ss -tulnp | grep -E ':53.*sins|:53.*dotnet' || echo 'not_listening'"
		portOutput, _ := a.SSHService.Execute(portCheckCmd, nil)
		if !strings.Contains(portOutput, "not_listening") && strings.Contains(portOutput, ":53") {
			libs.GetLogger("enable_sins_service").Printf("SiNS DNS server is running and listening on port 53")
			return true
		}
		libs.GetLogger("enable_sins_service").Printf("SiNS service is active but not listening on port 53")
	}
	statusCmd2 := cli.NewSystemCtl().Service("sins").Status()
	statusOutput2, _ := a.SSHService.Execute(statusCmd2, nil)
	if strings.Contains(statusOutput2, "activating (auto-restart)") || strings.Contains(statusOutput2, "auto-restart") {
		journalCmd := "journalctl -u sins.service -n 10 --no-pager | grep -i 'postgres\\|connection refused' || true"
		journalOutput, _ := a.SSHService.Execute(journalCmd, nil)
		if strings.Contains(strings.ToLower(journalOutput), "postgres") || strings.Contains(strings.ToLower(journalOutput), "connection refused") {
			libs.GetLogger("enable_sins_service").Printf("SiNS service is restarting due to PostgreSQL connection failure. This is expected if PostgreSQL is not yet available. Service will retry automatically.")
			return true
		}
		journalCmd2 := "journalctl -u sins.service -n 10 --no-pager | grep -i 'address already in use\\|port.*53' || true"
		journalOutput2, _ := a.SSHService.Execute(journalCmd2, nil)
		if strings.Contains(strings.ToLower(journalOutput2), "address already in use") || strings.Contains(strings.ToLower(journalOutput2), "port") {
			libs.GetLogger("enable_sins_service").Printf("SiNS service cannot bind to port 53 - port is already in use. Ensure systemd-resolved is disabled.")
			return false
		}
	}
	processCheckCmd := "pgrep -f 'sins\\.dll|^sins ' >/dev/null && echo running || echo not_running"
	processOutput, _ := a.SSHService.Execute(processCheckCmd, nil)
	if strings.Contains(processOutput, "running") {
		libs.GetLogger("enable_sins_service").Printf("SiNS process is running despite inactive systemd status")
		return true
	}
	libs.GetLogger("enable_sins_service").Printf("SiNS DNS server is not running")
	return false
}

