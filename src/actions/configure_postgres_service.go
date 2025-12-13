package actions

import (
	"fmt"
	"strings"
	"time"
	"enva/cli"
	"enva/libs"
	"enva/services"
)

// ConfigurePostgresServiceAction configures and starts PostgreSQL service
type ConfigurePostgresServiceAction struct {
	*BaseAction
}

func NewConfigurePostgresServiceAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &ConfigurePostgresServiceAction{
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

func (a *ConfigurePostgresServiceAction) Description() string {
	return "postgresql service configuration"
}

func (a *ConfigurePostgresServiceAction) Execute() bool {
	if a.SSHService == nil || a.Cfg == nil {
		libs.GetLogger("configure_postgres_service").Error("SSH service or config not initialized")
		return false
	}
	version := "17"
	if a.ContainerCfg != nil && a.ContainerCfg.Params != nil {
		if v, ok := a.ContainerCfg.Params["version"].(string); ok {
			version = v
		}
	}
	clusterService := fmt.Sprintf("postgresql@%s-main", version)
	// Start the cluster service (matching Python: sudo=True)
	enableCmd := cli.NewSystemCtl().Service(clusterService).Enable()
	startCmd := cli.NewSystemCtl().Service(clusterService).Start()
	a.SSHService.Execute(enableCmd, nil, true) // sudo=True
	output, exitCode := a.SSHService.Execute(startCmd, nil, true) // sudo=True
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("configure_postgres_service").Error("start postgresql cluster service failed with exit code %d", *exitCode)
		if output != "" {
			lines := strings.Split(output, "\n")
			if len(lines) > 0 {
				libs.GetLogger("configure_postgres_service").Error("start postgresql cluster service output: %s", lines[len(lines)-1])
			}
		}
		return false
	}
	time.Sleep(time.Duration(a.Cfg.Waits.ServiceStart) * time.Second)
	isActiveCmd := cli.NewSystemCtl().Service(clusterService).IsActive()
	status, exitCode := a.SSHService.Execute(isActiveCmd, nil, true) // sudo=True
	if exitCode != nil && *exitCode == 0 && cli.ParseIsActive(status) {
		// Verify PostgreSQL is listening on all interfaces (matching Python: sudo=True)
		portCheckCmd := "ss -tlnp | grep :5432 | grep -v '127.0.0.1' || echo 'not_listening'"
		portOutput, _ := a.SSHService.Execute(portCheckCmd, nil, true) // sudo=True
		if !strings.Contains(portOutput, "not_listening") && strings.Contains(portOutput, ":5432") {
			libs.GetLogger("configure_postgres_service").Info("PostgreSQL is listening on all interfaces")
		} else {
			libs.GetLogger("configure_postgres_service").Warning("PostgreSQL may not be listening on external interfaces")
		}
		return true
	}
	libs.GetLogger("configure_postgres_service").Error("PostgreSQL cluster service is not active")
	return false
}

