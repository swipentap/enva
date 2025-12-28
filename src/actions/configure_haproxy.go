package actions

import (
	"encoding/base64"
	"enva/cli"
	"enva/libs"
	"enva/services"
	"enva/verification"
	"fmt"
	"strings"
	"time"
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
			ContainerID:  containerID,
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

	// Determine SSL certificate path - use configured path if provided, otherwise default
	certPath := "/etc/haproxy/haproxy.pem"
	useCustomCert := false
	if a.Cfg.CertificatePath != nil && *a.Cfg.CertificatePath != "" {
		certPath = *a.Cfg.CertificatePath
		useCustomCert = true
		libs.GetLogger("configure_haproxy").Info("Using configured SSL certificate path: %s", certPath)
		
		// If custom certificate path is configured, certificate_source_path must also be configured
		if a.Cfg.CertificateSourcePath == nil || *a.Cfg.CertificateSourcePath == "" {
			libs.GetLogger("configure_haproxy").Error("certificate_path is configured but certificate_source_path is missing")
			return false
		}
		
		// Connect to LXC host to read certificate file
		lxcHost := a.Cfg.LXCHost()
		lxcService := services.NewLXCService(lxcHost, &a.Cfg.SSH)
		if !lxcService.Connect() {
			libs.GetLogger("configure_haproxy").Error("Failed to connect to LXC host to read certificate file")
			return false
		}
		defer lxcService.Disconnect()
		
		// Check if certificate source file exists
		checkSourceCmd := fmt.Sprintf("test -f %s && echo 'exists' || echo 'missing'", *a.Cfg.CertificateSourcePath)
		sourceCheck, _ := lxcService.Execute(checkSourceCmd, nil)
		if !strings.Contains(sourceCheck, "exists") {
			libs.GetLogger("configure_haproxy").Error("Certificate source file not found at %s", *a.Cfg.CertificateSourcePath)
			return false
		}
		
		// Read certificate file content from LXC host
		readCertCmd := fmt.Sprintf("cat %s", *a.Cfg.CertificateSourcePath)
		certContent, readExit := lxcService.Execute(readCertCmd, nil)
		if readExit != nil && *readExit != 0 {
			libs.GetLogger("configure_haproxy").Error("Failed to read certificate file from %s: %s", *a.Cfg.CertificateSourcePath, certContent)
			return false
		}
		if certContent == "" {
			libs.GetLogger("configure_haproxy").Error("Certificate file is empty at %s", *a.Cfg.CertificateSourcePath)
			return false
		}
		
		// Write certificate to container using base64 encoding to avoid escaping issues
		encodedCert := base64.StdEncoding.EncodeToString([]byte(certContent))
		writeCertCmd := fmt.Sprintf(`echo %s | base64 -d > %s && chmod 644 %s && echo 'success' || echo 'failed'`, encodedCert, certPath, certPath)
		writeOutput, writeExit := a.SSHService.Execute(writeCertCmd, nil, true) // sudo=True
		if writeExit == nil || *writeExit != 0 || !strings.Contains(writeOutput, "success") {
			libs.GetLogger("configure_haproxy").Error("Failed to write certificate to container at %s: %s", certPath, writeOutput)
			return false
		}
		libs.GetLogger("configure_haproxy").Info("Certificate transferred successfully from %s to %s", *a.Cfg.CertificateSourcePath, certPath)
	} else {
		libs.GetLogger("configure_haproxy").Info("Using default SSL certificate path: %s", certPath)
	}
	
	// For default certificate, check if it exists and generate if needed
	if !useCustomCert {
		checkCertCmd := fmt.Sprintf("test -f %s && echo 'exists' || echo 'missing'", certPath)
		certCheck, _ := a.SSHService.Execute(checkCertCmd, nil, true) // sudo=True
		
		if !strings.Contains(certCheck, "exists") {
			libs.GetLogger("configure_haproxy").Info("Generating self-signed SSL certificate...")
			// Generate certificate with wildcard for domain
			domain := "*"
			if a.Cfg.Domain != nil && *a.Cfg.Domain != "" {
				domain = fmt.Sprintf("*.%s", *a.Cfg.Domain)
			}
			generateCertCmd := fmt.Sprintf(`cd /tmp && openssl req -x509 -newkey rsa:2048 -keyout haproxy.key -out haproxy.crt -days 365 -nodes -subj "/CN=%s" >/dev/null 2>&1 && cat haproxy.key haproxy.crt > %s && chmod 644 %s && rm -f haproxy.key haproxy.crt`, domain, certPath, certPath)
			certOutput, certExitCode := a.SSHService.Execute(generateCertCmd, nil, true) // sudo=True
			if certExitCode != nil && *certExitCode != 0 {
				libs.GetLogger("configure_haproxy").Warning("Failed to generate SSL certificate: %s", certOutput)
				// Continue without SSL - will use HTTP mode only
				certPath = ""
			} else {
				libs.GetLogger("configure_haproxy").Info("SSL certificate generated successfully at %s", certPath)
			}
		} else {
			libs.GetLogger("configure_haproxy").Info("SSL certificate exists at %s", certPath)
		}
	}
	// Build frontends and backends for each service with a name
	frontends := []string{}
	backends := []string{}
	httpACLs := []string{}
	httpRules := []string{}
	httpsACLs := []string{}
	httpsRules := []string{}

	if a.Cfg.Domain != nil && *a.Cfg.Domain != "" {
		domain := *a.Cfg.Domain

		// apt_cache service - mapped on both HTTP and HTTPS
		if a.Cfg.Services.APTcache.Name != nil && *a.Cfg.Services.APTcache.Name != "" {
			var aptCacheCT *libs.ContainerConfig
			for _, ct := range a.Cfg.Containers {
				if ct.Name == a.Cfg.APTCacheCT {
					aptCacheCT = &ct
					break
				}
			}
			if aptCacheCT != nil && aptCacheCT.IPAddress != nil {
				port := 80
				if a.Cfg.Services.APTcache.Port != nil {
					port = *a.Cfg.Services.APTcache.Port
				}
				serviceName := *a.Cfg.Services.APTcache.Name
				fqdn := fmt.Sprintf("%s.%s", serviceName, domain)
				backendName := fmt.Sprintf("backend_%s", serviceName)
				// Add ACLs for HTTP and HTTPS
				aclName := fmt.Sprintf("acl_%s", serviceName)
				httpACLs = append(httpACLs, fmt.Sprintf("    acl %s hdr(host) -i %s", aclName, fqdn))
				httpRules = append(httpRules, fmt.Sprintf("    use_backend %s if %s", backendName, aclName))
				httpsACLs = append(httpsACLs, fmt.Sprintf("    acl %s hdr(host) -i %s", aclName, fqdn))
				httpsRules = append(httpsRules, fmt.Sprintf("    use_backend %s if %s", backendName, aclName))
				backends = append(backends, fmt.Sprintf(`backend %s
    server %s %s:%d check`, backendName, serviceName, *aptCacheCT.IPAddress, port))
			}
		}

		// postgresql service - TCP mode on port 5432
		if a.Cfg.Services.PostgreSQL != nil && a.Cfg.Services.PostgreSQL.Name != nil && *a.Cfg.Services.PostgreSQL.Name != "" {
			var pgsqlCT *libs.ContainerConfig
			for _, ct := range a.Cfg.Containers {
				if ct.Name == "pgsql" {
					pgsqlCT = &ct
					break
				}
			}
			if pgsqlCT != nil && pgsqlCT.IPAddress != nil {
				port := 5432
				if a.Cfg.Services.PostgreSQL.Port != nil {
					port = *a.Cfg.Services.PostgreSQL.Port
				}
				serviceName := *a.Cfg.Services.PostgreSQL.Name
				fqdn := fmt.Sprintf("%s.%s", serviceName, domain)
				backendName := fmt.Sprintf("backend_%s", serviceName)
				// PostgreSQL uses TCP mode on its own port
				frontends = append(frontends, fmt.Sprintf(`frontend %s
    bind *:%d
    mode tcp
    default_backend %s`, fqdn, port, backendName))
				backends = append(backends, fmt.Sprintf(`backend %s
    mode tcp
    server %s %s:%d check`, backendName, serviceName, *pgsqlCT.IPAddress, port))
			}
		}

		// rancher service (runs on k3s control node)
		if a.Cfg.Services.Rancher != nil && a.Cfg.Services.Rancher.Name != nil && *a.Cfg.Services.Rancher.Name != "" {
			var controlCT *libs.ContainerConfig
			for _, ct := range a.Cfg.Containers {
				if ct.Name == "k3s-control" {
					controlCT = &ct
					break
				}
			}
			if controlCT != nil && controlCT.IPAddress != nil {
				port := 30443
				if a.Cfg.Services.Rancher.Port != nil {
					port = *a.Cfg.Services.Rancher.Port
				}
				serviceName := *a.Cfg.Services.Rancher.Name
				fqdn := fmt.Sprintf("%s.%s", serviceName, domain)
				backendName := fmt.Sprintf("backend_%s", serviceName)
				// Rancher uses HTTPS - add ACLs for HTTP and HTTPS frontends
				aclName := fmt.Sprintf("acl_%s", serviceName)
				httpACLs = append(httpACLs, fmt.Sprintf("    acl %s hdr(host) -i %s", aclName, fqdn))
				httpRules = append(httpRules, fmt.Sprintf("    use_backend %s if %s", backendName, aclName))
				httpsACLs = append(httpsACLs, fmt.Sprintf("    acl %s hdr(host) -i %s", aclName, fqdn))
				httpsRules = append(httpsRules, fmt.Sprintf("    use_backend %s if %s", backendName, aclName))
				// Rancher backend - use HTTP mode, but connect to backend with SSL since Rancher serves HTTPS
				backends = append(backends, fmt.Sprintf(`backend %s
    server %s %s:%d check ssl verify none`, backendName, serviceName, *controlCT.IPAddress, port))
			}
		}

		// cockroachdb service (runs on k3s worker nodes)
		if a.Cfg.Services.CockroachDB != nil && a.Cfg.Services.CockroachDB.Name != nil && *a.Cfg.Services.CockroachDB.Name != "" {
			// Get all worker nodes
			workerNodes := []*libs.ContainerConfig{}
			for i := range a.Cfg.Containers {
				ct := &a.Cfg.Containers[i]
				if ct.Name == "k3s-worker-1" || ct.Name == "k3s-worker-2" || ct.Name == "k3s-worker-3" {
					if ct.IPAddress != nil {
						workerNodes = append(workerNodes, ct)
					}
				}
			}
			if len(workerNodes) > 0 {
				port := 32657
				if a.Cfg.Services.CockroachDB.SQLPort != nil {
					port = *a.Cfg.Services.CockroachDB.SQLPort
				}
				serviceName := *a.Cfg.Services.CockroachDB.Name
				fqdn := fmt.Sprintf("%s.%s", serviceName, domain)
				backendName := fmt.Sprintf("backend_%s", serviceName)
				// CockroachDB SQL uses TCP mode on its own port
				frontends = append(frontends, fmt.Sprintf(`frontend %s
    bind *:%d
    mode tcp
    default_backend %s`, fqdn, port, backendName))
				// Add all worker nodes as backend servers with round-robin balancing
				backendServers := []string{}
				for i, worker := range workerNodes {
					backendServers = append(backendServers, fmt.Sprintf("    server %s-%d %s:%d check", serviceName, i+1, *worker.IPAddress, port))
				}
				backends = append(backends, fmt.Sprintf(`backend %s
    mode tcp
    balance roundrobin
%s`, backendName, strings.Join(backendServers, "\n")))
			}
		}

		// certa service (runs on k3s worker nodes via NodePort)
		if a.Cfg.Services.CertA != nil && a.Cfg.Services.CertA.Name != nil && *a.Cfg.Services.CertA.Name != "" {
			// Get all worker nodes
			workerNodes := []*libs.ContainerConfig{}
			for i := range a.Cfg.Containers {
				ct := &a.Cfg.Containers[i]
				if ct.Name == "k3s-worker-1" || ct.Name == "k3s-worker-2" || ct.Name == "k3s-worker-3" {
					if ct.IPAddress != nil {
						workerNodes = append(workerNodes, ct)
					}
				}
			}
			if len(workerNodes) > 0 {
				port := 30081
				if a.Cfg.Services.CertA.Port != nil {
					port = *a.Cfg.Services.CertA.Port
				}
				serviceName := *a.Cfg.Services.CertA.Name
				fqdn := fmt.Sprintf("%s.%s", serviceName, domain)
				backendName := fmt.Sprintf("backend_%s", serviceName)
				// CertA uses HTTP mode - add ACLs for HTTP and HTTPS
				aclName := fmt.Sprintf("acl_%s", serviceName)
				httpACLs = append(httpACLs, fmt.Sprintf("    acl %s hdr(host) -i %s", aclName, fqdn))
				httpRules = append(httpRules, fmt.Sprintf("    use_backend %s if %s", backendName, aclName))
				httpsACLs = append(httpsACLs, fmt.Sprintf("    acl %s hdr(host) -i %s", aclName, fqdn))
				httpsRules = append(httpsRules, fmt.Sprintf("    use_backend %s if %s", backendName, aclName))
				// Add all worker nodes as backend servers with round-robin balancing
				backendServers := []string{}
				for i, worker := range workerNodes {
					backendServers = append(backendServers, fmt.Sprintf("    server %s-%d %s:%d check", serviceName, i+1, *worker.IPAddress, port))
				}
				backends = append(backends, fmt.Sprintf(`backend %s
    balance roundrobin
%s`, backendName, strings.Join(backendServers, "\n")))
			}
		}
	}

	// Build unified HTTP and HTTPS frontends with ACLs
	// For HTTPS, use HTTP mode to support apt-cache (which serves HTTP, not HTTPS)
	// Note: Rancher with SSL passthrough cannot be on the same port in HTTP mode

	if len(httpACLs) > 0 || len(httpsACLs) > 0 {
		// HTTP frontend
		httpFrontend := fmt.Sprintf(`frontend http-in
    bind *:%d`, httpPort)
		if len(httpACLs) > 0 {
			httpFrontend += "\n" + strings.Join(httpACLs, "\n")
			httpFrontend += "\n" + strings.Join(httpRules, "\n")
		}
		httpFrontend += "\n    default_backend nodes"
		frontends = append(frontends, httpFrontend)

		// HTTPS frontend - use HTTP mode with SSL termination at HAProxy
		httpsFrontend := fmt.Sprintf(`frontend https-in
    bind *:%d`, httpsPort)
		if certPath != "" {
			// Add SSL termination if certificate exists
			httpsFrontend += fmt.Sprintf(` ssl crt %s`, certPath)
		}
		httpsFrontend += "\n    mode http"

		if len(httpsACLs) > 0 {
			// Use ACLs for routing (apt-cache, etc.)
			httpsFrontend += "\n" + strings.Join(httpsACLs, "\n")
			httpsFrontend += "\n" + strings.Join(httpsRules, "\n")
		} else {
			// Add apt-cache routing if configured
			aptCacheBackendName := ""
			if a.Cfg.Services.APTcache.Name != nil && *a.Cfg.Services.APTcache.Name != "" {
				aptCacheBackendName = fmt.Sprintf("backend_%s", *a.Cfg.Services.APTcache.Name)
				if a.Cfg.Domain != nil && *a.Cfg.Domain != "" {
					aptCacheFqdn := fmt.Sprintf("%s.%s", *a.Cfg.Services.APTcache.Name, *a.Cfg.Domain)
					httpsFrontend += fmt.Sprintf("\n    acl is_apt_cache hdr(host) -i %s", aptCacheFqdn)
					httpsFrontend += fmt.Sprintf("\n    use_backend %s if is_apt_cache", aptCacheBackendName)
				}
			}
		}

		// Note: Rancher with SSL passthrough needs TCP mode, which conflicts with HTTP mode
		// Rancher should be accessed via its NodePort directly or use SSL termination at HAProxy
		httpsFrontend += "\n    default_backend nodes"
		frontends = append(frontends, httpsFrontend)
	} else {
		// Default frontend/backend if no services configured
		frontends = append(frontends, fmt.Sprintf(`frontend http-in
    bind *:%d
    default_backend nodes`, httpPort))
		frontends = append(frontends, fmt.Sprintf(`frontend https-in
    bind *:%d
    mode tcp
    default_backend nodes`, httpsPort))
	}
	backends = append(backends, `backend nodes
    server dummy 127.0.0.1:80 check`)

	frontendsText := strings.Join(frontends, "\n\n")
	backendsText := strings.Join(backends, "\n\n")

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
%s

%s

listen stats
    bind *:%d
    mode http
    stats enable
    stats uri /
    stats refresh 10s
`, frontendsText, backendsText, statsPort)
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

	// Reload HAProxy to apply new configuration
	reloadCmd := "systemctl reload haproxy 2>&1 || systemctl restart haproxy 2>&1"
	reloadOutput, reloadExit := a.SSHService.Execute(reloadCmd, nil, true) // sudo=True
	if reloadExit != nil && *reloadExit != 0 {
		libs.GetLogger("configure_haproxy").Warning("Failed to reload HAProxy: %s", reloadOutput)
		// Don't fail, HAProxy might already be running with the config
	}

	// Verify HAProxy backends after configuration
	if a.PCTService != nil {
		libs.GetLogger("configure_haproxy").Printf("Verifying HAProxy backends...")
		time.Sleep(5 * time.Second) // Give HAProxy time to start backends
		if !verification.VerifyHAProxyBackends(a.Cfg, a.PCTService) {
			libs.GetLogger("configure_haproxy").Printf("⚠ HAProxy backend verification found issues, but configuration completed")
			// Don't fail deployment, just warn
		}
	}
	return true
}
