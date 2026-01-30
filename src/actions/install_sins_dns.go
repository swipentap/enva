package actions

import (
	"crypto/rand"
	"encoding/base64"
	"encoding/json"
	"fmt"
	"strings"
	"enva/libs"
	"enva/services"
)

// InstallSinsDnsAction installs SiNS DNS server
type InstallSinsDnsAction struct {
	*BaseAction
}

func NewInstallSinsDnsAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &InstallSinsDnsAction{
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

func (a *InstallSinsDnsAction) Description() string {
	return "sins dns installation"
}

func (a *InstallSinsDnsAction) Execute() bool {
	if a.SSHService == nil || a.APTService == nil {
		libs.GetLogger("install_sins_dns").Error("SSH service or APT service not initialized")
		return false
	}
	libs.GetLogger("install_sins_dns").Info("Adding Gemfury APT repository...")
	repoSource := "deb [trusted=yes] https://judyalvarez@apt.fury.io/judyalvarez /"
	addSourceCmd := fmt.Sprintf("echo '%s' | tee /etc/apt/sources.list.d/fury.list", repoSource)
	timeout := 30
	output, exitCode := a.SSHService.Execute(addSourceCmd, &timeout, true) // sudo=True
	if exitCode != nil && *exitCode != 0 {
		outputLen := len(output)
		start := 0
		if outputLen > 200 {
			start = outputLen - 200
		}
		libs.GetLogger("install_sins_dns").Error("Failed to add Gemfury repository source: %s", output[start:])
		return false
	}
	libs.GetLogger("install_sins_dns").Info("Installing SiNS DNS server from APT repository...")
	installCmd := "apt-get update && apt-get install -y sins"
	timeout = 300
	output, exitCode = a.SSHService.Execute(installCmd, &timeout, true) // sudo=True
	if exitCode != nil && *exitCode != 0 {
		outputLen := len(output)
		start := 0
		if outputLen > 200 {
			start = outputLen - 200
		}
		libs.GetLogger("install_sins_dns").Error("Failed to install sins package: %s", output[start:])
		return false
	}
	verifyCmd := "command -v sins  && echo installed || echo not_installed"
	verifyOutput, verifyExitCode := a.SSHService.Execute(verifyCmd, nil, true) // sudo=True
	if verifyExitCode == nil || *verifyExitCode != 0 || !strings.Contains(verifyOutput, "installed") {
		libs.GetLogger("install_sins_dns").Error("SiNS package was not installed correctly")
		return false
	}
	params := make(map[string]interface{})
	if a.ContainerCfg != nil && a.ContainerCfg.Params != nil {
		params = a.ContainerCfg.Params
	}
	postgresHost := "10.11.3.18"
	if a.Cfg != nil && a.Cfg.PostgresHost != nil {
		postgresHost = *a.Cfg.PostgresHost
	} else if p, ok := params["postgres_host"].(string); ok {
		postgresHost = p
	}
	postgresPort := 5432
	if p, ok := params["postgres_port"].(int); ok {
		postgresPort = p
	}
	postgresDB := "dns_server"
	if d, ok := params["postgres_db"].(string); ok {
		postgresDB = d
	}
	postgresUser := "postgres"
	if u, ok := params["postgres_user"].(string); ok {
		postgresUser = u
	}
	postgresPassword := "postgres"
	if p, ok := params["postgres_password"].(string); ok {
		postgresPassword = p
	}
	dnsPort := 53
	if p, ok := params["dns_port"].(int); ok {
		dnsPort = p
	}
	webPort := 80
	if p, ok := params["web_port"].(int); ok {
		webPort = p
	}
	libs.GetLogger("install_sins_dns").Info("Ensuring PostgreSQL database '%s' exists...", postgresDB)
	installPgClientCmd := "command -v psql  || apt-get install -y postgresql-client"
	timeout = 60
	a.SSHService.Execute(installPgClientCmd, &timeout, true) // sudo=True
	createDBCmd := fmt.Sprintf("PGPASSWORD=%s psql -h %s -p %d -U %s -d postgres -tc \"SELECT 1 FROM pg_database WHERE datname = '%s'\" | grep -q 1 || PGPASSWORD=%s psql -h %s -p %d -U %s -d postgres -c \"CREATE DATABASE %s;\"", postgresPassword, postgresHost, postgresPort, postgresUser, postgresDB, postgresPassword, postgresHost, postgresPort, postgresUser, postgresDB)
	timeout = 30
	output, exitCode = a.SSHService.Execute(createDBCmd, &timeout, true) // sudo=True
	if exitCode != nil && *exitCode == 0 {
		libs.GetLogger("install_sins_dns").Info("PostgreSQL database '%s' is ready", postgresDB)
	} else {
		outputLen := len(output)
		start := 0
		if outputLen > 100 {
			start = outputLen - 100
		}
		libs.GetLogger("install_sins_dns").Warning("Could not verify/create PostgreSQL database (may already exist): %s", output[start:])
	}
	libs.GetLogger("install_sins_dns").Info("Configuring SiNS application settings...")
	jwtSecret := generateJWTSecret()
	appsettings := map[string]interface{}{
		"ConnectionStrings": map[string]string{
			"DefaultConnection": fmt.Sprintf("Host=%s;Port=%d;Database=%s;Username=%s;Password=%s", postgresHost, postgresPort, postgresDB, postgresUser, postgresPassword),
		},
		"DnsSettings": map[string]int{
			"Port": dnsPort,
		},
		"WebSettings": map[string]int{
			"Port": webPort,
		},
		"Jwt": map[string]interface{}{
			"Key":              jwtSecret,
			"Issuer":           "SiNS-DNS-Server",
			"Audience":         "SiNS-DNS-Client",
			"ExpirationMinutes": 1440,
		},
	}
	appsettingsJSON, _ := json.MarshalIndent(appsettings, "", "  ")
	appsettingsB64 := base64.StdEncoding.EncodeToString(appsettingsJSON)
	configLocations := []string{
		"/etc/sins/appsettings.json",
		"/opt/sins/appsettings.json",
		"/opt/sins/app/appsettings.json",
	}
	libs.GetLogger("install_sins_dns").Info("Writing SiNS appsettings.json to all config locations...")
	configWritten := false
	for _, configPath := range configLocations {
		parts := strings.Split(configPath, "/")
		configDir := strings.Join(parts[:len(parts)-1], "/")
		mkdirCmd := fmt.Sprintf("mkdir -p %s", configDir)
		a.SSHService.Execute(mkdirCmd, nil, true) // sudo=True
		appsettingsCmd := fmt.Sprintf("echo %s | base64 -d > %s", appsettingsB64, configPath)
		output, exitCode = a.SSHService.Execute(appsettingsCmd, nil, true) // sudo=True
		if exitCode != nil && *exitCode == 0 {
			libs.GetLogger("install_sins_dns").Info("SiNS appsettings.json written to %s", configPath)
			configWritten = true
		} else {
			outputLen := len(output)
			start := 0
			if outputLen > 100 {
				start = outputLen - 100
			}
			libs.GetLogger("install_sins_dns").Warning("Failed to write appsettings.json to %s: %s", configPath, output[start:])
		}
	}
	if !configWritten {
		libs.GetLogger("install_sins_dns").Error("Failed to write appsettings.json to any location")
		return false
	}
	return true
}

func generateJWTSecret() string {
	// Generate a secure 256-bit (32 bytes) JWT secret key
	// In Go, use crypto/rand for secure random generation
	b := make([]byte, 32)
	rand.Read(b)
	return base64.URLEncoding.EncodeToString(b)
}

