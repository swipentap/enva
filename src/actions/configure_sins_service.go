package actions

import (
	"encoding/base64"
	"enva/libs"
	"enva/services"
	"fmt"
	"strings"
)

// ConfigureSinsServiceAction configures SiNS DNS systemd service
type ConfigureSinsServiceAction struct {
	*BaseAction
}

func NewConfigureSinsServiceAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &ConfigureSinsServiceAction{
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

func (a *ConfigureSinsServiceAction) Description() string {
	return "sins dns service configuration"
}

func (a *ConfigureSinsServiceAction) Execute() bool {
	if a.SSHService == nil {
		libs.GetLogger("configure_sins_service").Printf("SSH service not initialized")
		return false
	}
	webPort := 80
	if a.ContainerCfg != nil && a.ContainerCfg.Params != nil {
		if p, ok := a.ContainerCfg.Params["web_port"].(int); ok {
			webPort = p
		}
	}
	checkServiceCmd := "test -f /etc/systemd/system/sins.service && echo exists || echo missing"
	serviceExists, _ := a.SSHService.Execute(checkServiceCmd, nil)
	if strings.Contains(serviceExists, "exists") {
		libs.GetLogger("configure_sins_service").Printf("SiNS service file already exists (provided by Debian package), updating web port configuration and timeout...")
		readServiceCmd := "cat /etc/systemd/system/sins.service"
		existingService, _ := a.SSHService.Execute(readServiceCmd, nil)
		needsUpdate := false
		if !strings.Contains(existingService, fmt.Sprintf("ASPNETCORE_URLS=http://+:%d", webPort)) && !strings.Contains(existingService, fmt.Sprintf("ASPNETCORE_URLS=http://0.0.0.0:%d", webPort)) {
			needsUpdate = true
		}
		if strings.Contains(existingService, "Type=notify") {
			needsUpdate = true
		}
		if !strings.Contains(existingService, "TimeoutStartSec") {
			needsUpdate = true
		}
		if !needsUpdate {
			libs.GetLogger("configure_sins_service").Printf("SiNS service file already configured with correct web port and timeout")
			reloadCmd := "systemctl daemon-reload || true"
			a.SSHService.Execute(reloadCmd, nil, true)
			return true
		}
		updateCmd := fmt.Sprintf("sed -i 's|^Type=notify|Type=simple|' /etc/systemd/system/sins.service && sed -i 's|Environment=ASPNETCORE_URLS=.*|Environment=ASPNETCORE_URLS=http://0.0.0.0:%d|' /etc/systemd/system/sins.service && grep -q '^TimeoutStartSec=' /etc/systemd/system/sins.service || sed -i '/^\\[Service\\]/a TimeoutStartSec=300' /etc/systemd/system/sins.service && sed -i 's|^TimeoutStartSec=.*|TimeoutStartSec=300|' /etc/systemd/system/sins.service", webPort)
		_, exitCode := a.SSHService.Execute(updateCmd, nil, true)
		if exitCode != nil && *exitCode == 0 {
			libs.GetLogger("configure_sins_service").Printf("Updated SiNS service file: changed Type=notify to Type=simple, ASPNETCORE_URLS=http://0.0.0.0:%d, TimeoutStartSec=300", webPort)
			reloadCmd := "systemctl daemon-reload || true"
			a.SSHService.Execute(reloadCmd, nil, true)
			return true
		}
		libs.GetLogger("configure_sins_service").Printf("Failed to update service file, will create new one")
	}
	libs.GetLogger("configure_sins_service").Printf("Creating SiNS systemd service...")
	findBinaryCmd := "test -f /opt/sins/sins && echo /opt/sins/sins || (which sins || find /usr /opt -name 'sins.dll' -o -name 'sins' -type f | head -1)"
	binaryPath, _ := a.SSHService.Execute(findBinaryCmd, nil)
	if binaryPath == "" || strings.TrimSpace(binaryPath) == "" {
		libs.GetLogger("configure_sins_service").Printf("Could not find SiNS binary")
		return false
	}
	binaryPath = strings.TrimSpace(binaryPath)
	workingDir := "/opt/sins"
	execStart := "/opt/sins/sins"
	if strings.HasSuffix(binaryPath, ".dll") {
		parts := strings.Split(binaryPath, "/")
		workingDir = strings.Join(parts[:len(parts)-1], "/")
		execStart = fmt.Sprintf("/usr/bin/dotnet %s", binaryPath)
	} else if binaryPath != "/opt/sins/sins" {
		parts := strings.Split(binaryPath, "/")
		if len(parts) > 1 {
			workingDir = strings.Join(parts[:len(parts)-1], "/")
		} else {
			workingDir = "/usr/bin"
		}
		execStart = binaryPath
	}
	appsettingsLocations := []string{
		"/etc/sins/appsettings.json",
		fmt.Sprintf("%s/appsettings.json", workingDir),
		"/opt/sins/app/appsettings.json",
	}
	appsettingsPath := "/etc/sins/appsettings.json"
	for _, loc := range appsettingsLocations {
		checkCmd := fmt.Sprintf("test -f %s && echo exists || echo missing", loc)
		checkOutput, _ := a.SSHService.Execute(checkCmd, nil)
		if strings.Contains(checkOutput, "exists") {
			appsettingsPath = loc
			break
		}
	}
	_ = appsettingsPath // May be used in future service configuration
	serviceContent := fmt.Sprintf(`[Unit]
Description=SiNS DNS Server
After=network.target

[Service]
Type=simple
User=root
WorkingDirectory=%s
Environment=ASPNETCORE_URLS=http://0.0.0.0:%d
Environment=ASPNETCORE_ENVIRONMENT=Production
ExecStart=%s
Restart=always
RestartSec=10
TimeoutStartSec=300

[Install]
WantedBy=multi-user.target
`, workingDir, webPort, execStart)
	serviceB64 := base64.StdEncoding.EncodeToString([]byte(serviceContent))
	serviceCmd := fmt.Sprintf("systemctl stop sins || true; echo %s | base64 -d > /etc/systemd/system/sins.service && systemctl daemon-reload || true", serviceB64)
	output, exitCode := a.SSHService.Execute(serviceCmd, nil, true)
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("configure_sins_service").Printf("Failed to create SiNS service file: %s", output)
		return false
	}
	return true
}
