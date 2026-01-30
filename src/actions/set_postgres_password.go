package actions

import (
	"fmt"
	"strings"
	"enva/libs"
	"enva/services"
)

// SetPostgresPasswordAction sets PostgreSQL password
type SetPostgresPasswordAction struct {
	*BaseAction
}

func NewSetPostgresPasswordAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &SetPostgresPasswordAction{
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

func (a *SetPostgresPasswordAction) Description() string {
	return "postgresql password setup"
}

func (a *SetPostgresPasswordAction) Execute() bool {
	if a.SSHService == nil {
		libs.GetLogger("set_postgres_password").Printf("SSH service not initialized")
		return false
	}
	password := "postgres"
	if a.ContainerCfg != nil && a.ContainerCfg.Params != nil {
		if p, ok := a.ContainerCfg.Params["password"].(string); ok {
			password = p
		}
	}
	command := fmt.Sprintf("sudo -n -u postgres psql -c \"ALTER USER postgres WITH PASSWORD '%s';\"", password)
	output, exitCode := a.SSHService.Execute(command, nil)
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("set_postgres_password").Printf("set postgres password failed with exit code %d", *exitCode)
		if output != "" {
			lines := strings.Split(output, "\n")
			if len(lines) > 0 {
				libs.GetLogger("set_postgres_password").Printf("set postgres password output: %s", lines[len(lines)-1])
			}
		}
		return false
	}
	return true
}

