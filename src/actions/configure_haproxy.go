package actions

import (
	"fmt"
	"strings"
	"enva/cli"
	"enva/libs"
	"enva/services"
)

// ConfigureHaproxyAction configures HAProxy
type ConfigureHaproxyAction struct {
	*BaseAction
}

func NewConfigureHaproxyAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &ConfigureHaproxyAction{
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

func (a *ConfigureHaproxyAction) Description() string {
	return "haproxy configuration"
}

func (a *ConfigureHaproxyAction) Execute() bool {
	if a.SSHService == nil || a.Cfg == nil {
		libs.GetLogger("configure_haproxy").Error("SSH service or config not initialized")
		return false
	}
	httpPort := 80
	httpsPort := 443
	statsPort := 8404
	if a.ContainerCfg != nil && a.ContainerCfg.Params != nil {
		if p, ok := a.ContainerCfg.Params["http_port"].(int); ok {
			httpPort = p
		}
		if p, ok := a.ContainerCfg.Params["https_port"].(int); ok {
			httpsPort = p
		}
		if p, ok := a.ContainerCfg.Params["stats_port"].(int); ok {
			statsPort = p
		}
	}
	backendServers := []string{}
	// Note: Docker Swarm configuration removed - HAProxy backend servers should be configured via container params
	// For now, use dummy backend
	serversText := strings.Join(backendServers, "\n")
	if serversText == "" {
		serversText = "    server dummy 127.0.0.1:80 check"
	}
	configText := fmt.Sprintf(`global
    log /dev/log local0
    log /dev/log local1 notice
    maxconn 2048
    daemon
defaults
    log     global
    mode    http
    option  httplog
    option  dontlognull
    timeout connect 5s
    timeout client  50s
    timeout server  50s
frontend http-in
    bind *:%d
    default_backend nodes
frontend https-in
    bind *:%d
    mode http
    default_backend nodes
backend nodes
%s
listen stats
    bind *:%d
    mode http
    stats enable
    stats uri /
    stats refresh 10s
`, httpPort, httpsPort, serversText, statsPort)
	writeCmd := cli.NewFileOps().Write("/etc/haproxy/haproxy.cfg", configText)
	output, exitCode := a.SSHService.Execute(writeCmd, nil, true) // sudo=True
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("configure_haproxy").Error("write haproxy configuration failed with exit code %d", *exitCode)
		if output != "" {
			lines := strings.Split(output, "\n")
			if len(lines) > 0 {
				libs.GetLogger("configure_haproxy").Error("write haproxy configuration output: %s", lines[len(lines)-1])
			}
		}
		return false
	}
	return true
}

