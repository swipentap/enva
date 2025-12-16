package actions

import (
	"fmt"
	"strings"
	"time"
	"enva/cli"
	"enva/libs"
	"enva/services"
)

// ConfigurePostgresFilesAction configures PostgreSQL configuration files
type ConfigurePostgresFilesAction struct {
	*BaseAction
}

func NewConfigurePostgresFilesAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &ConfigurePostgresFilesAction{
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

func (a *ConfigurePostgresFilesAction) Description() string {
	return "postgresql files configuration"
}

func (a *ConfigurePostgresFilesAction) Execute() bool {
	if a.SSHService == nil {
		libs.GetLogger("configure_postgres_files").Error("SSH service not initialized")
		return false
	}
	version := "17"
	port := 5432
	allowCIDR := "10.11.3.0/24"
	if a.Cfg != nil && a.Cfg.Network != "" {
		allowCIDR = a.Cfg.Network
	}
	if a.ContainerCfg != nil && a.ContainerCfg.Params != nil {
		if v, ok := a.ContainerCfg.Params["version"].(string); ok {
			version = v
		}
		if p, ok := a.ContainerCfg.Params["port"].(int); ok {
			port = p
		}
		if a.Cfg == nil || a.Cfg.Network == "" {
			if cidr, ok := a.ContainerCfg.Params["cidr"].(string); ok {
				allowCIDR = cidr
			}
		}
	}
	configPath := fmt.Sprintf("/etc/postgresql/%s/main/postgresql.conf", version)
	// Remove all existing listen_addresses lines (matching Python: sudo=True)
	removeCmd := fmt.Sprintf("sed -i '/^#*listen_addresses.*/d' %s", configPath)
	output, exitCode := a.SSHService.Execute(removeCmd, nil, true) // sudo=True
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("configure_postgres_files").Error("remove listen_addresses failed with exit code %d", *exitCode)
		return false
	}
	// Check if listen_addresses already exists (matching Python: sudo=True)
	checkCmd := fmt.Sprintf("grep -q '^listen_addresses =' %s && echo exists || echo not_exists", configPath)
	checkOutput, _ := a.SSHService.Execute(checkCmd, nil, true) // sudo=True
	if strings.Contains(checkOutput, "not_exists") {
		// Find the line number of CONNECTIONS section (matching Python: sudo=True)
		lineNumCmd := fmt.Sprintf("grep -n '^# CONNECTIONS AND AUTHENTICATION' %s | cut -d: -f1", configPath)
		lineNumOutput, _ := a.SSHService.Execute(lineNumCmd, nil, true) // sudo=True
		if lineNumOutput != "" && strings.TrimSpace(lineNumOutput) != "" {
			insertScript := fmt.Sprintf("awk -v n=%s -v s=\"listen_addresses = '*'\" 'NR==n {print; print s; next}1' %s > %s.tmp && mv %s.tmp %s", strings.TrimSpace(lineNumOutput), configPath, configPath, configPath, configPath)
			output, exitCode = a.SSHService.Execute(insertScript, nil, true) // sudo=True
		} else {
			// Fallback: append at end of file (matching Python: sudo=True)
			insertScript := fmt.Sprintf("echo \"listen_addresses = '*'\" >> %s", configPath)
			output, exitCode = a.SSHService.Execute(insertScript, nil, true) // sudo=True
		}
		if exitCode != nil && *exitCode != 0 {
			libs.GetLogger("configure_postgres_files").Error("insert listen_addresses failed with exit code %d", *exitCode)
			if output != "" {
				libs.GetLogger("configure_postgres_files").Error("insert listen_addresses output: %s", output)
			}
			return false
		}
	}
	portCmd := cli.NewSed().Flags("").Replace(configPath, "^#?port.*", fmt.Sprintf("port = %d", port))
	pgHbaContent := fmt.Sprintf("local all all peer\nhost all all %s md5\n", allowCIDR)
	pgHbaCmd := cli.NewFileOps().Write(fmt.Sprintf("/etc/postgresql/%s/main/pg_hba.conf", version), pgHbaContent)
	results := []bool{}
	for _, item := range []struct {
		cmd string
		desc string
	}{
		{portCmd, "configure postgres port"},
		{pgHbaCmd, "write pg_hba rule"},
	} {
		// Matching Python: sudo=True for all commands
		output, exitCode := a.SSHService.Execute(item.cmd, nil, true) // sudo=True
		if exitCode != nil && *exitCode != 0 {
			libs.GetLogger("configure_postgres_files").Error("%s failed with exit code %d", item.desc, *exitCode)
			if output != "" {
				lines := strings.Split(output, "\n")
				if len(lines) > 0 {
					libs.GetLogger("configure_postgres_files").Error("%s output: %s", item.desc, lines[len(lines)-1])
				}
			}
		}
		results = append(results, exitCode == nil || *exitCode == 0)
	}
	if results[0] && results[1] {
		libs.GetLogger("configure_postgres_files").Info("Restarting PostgreSQL to apply configuration changes...")
		clusterService := fmt.Sprintf("postgresql@%s-main", version)
		restartCmd := cli.NewSystemCtl().Service(clusterService).Restart()
		output, exitCode = a.SSHService.Execute(restartCmd, nil, true) // sudo=True
		if exitCode != nil && *exitCode != 0 {
			libs.GetLogger("configure_postgres_files").Error("restart postgresql failed with exit code %d", *exitCode)
			return false
		}
		time.Sleep(3 * time.Second)
		// Verify it's listening on all interfaces (matching Python: sudo=True)
		portCheckCmd := "ss -tlnp | grep :5432 | grep -v '127.0.0.1' || echo 'not_listening'"
		portOutput, _ := a.SSHService.Execute(portCheckCmd, nil, true) // sudo=True
		if !strings.Contains(portOutput, "not_listening") && strings.Contains(portOutput, ":5432") {
			libs.GetLogger("configure_postgres_files").Info("PostgreSQL is listening on all interfaces after restart")
		} else {
			libs.GetLogger("configure_postgres_files").Warning("PostgreSQL may not be listening on external interfaces after restart")
		}
	}
	return results[0] && results[1]
}

