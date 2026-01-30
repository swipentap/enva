package actions

import (
	"enva/libs"
	"enva/services"
	"fmt"
	"strings"
)

// ConfigureDnsRecordsAction adds DNS A records for services to sins DNS
type ConfigureDnsRecordsAction struct {
	*BaseAction
}

func NewConfigureDnsRecordsAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &ConfigureDnsRecordsAction{
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

func (a *ConfigureDnsRecordsAction) Description() string {
	return "configure dns records"
}

func (a *ConfigureDnsRecordsAction) Execute() bool {
	if a.SSHService == nil || a.Cfg == nil {
		libs.GetLogger("configure_dns_records").Error("SSH service or config not initialized")
		return false
	}

	// Get DNS server container (dns container)
	var dnsContainer *libs.ContainerConfig
	for _, ct := range a.Cfg.Containers {
		if ct.Name == "dns" {
			dnsContainer = &ct
			break
		}
	}
	if dnsContainer == nil {
		libs.GetLogger("configure_dns_records").Error("DNS container not found")
		return false
	}

	// Load full config to access all environments
	// Try common config file locations
	configFile := "enva.yaml"
	rawConfig, err := libs.LoadConfig(configFile)
	if err != nil {
		libs.GetLogger("configure_dns_records").Warning("Failed to load config file %s, trying ./enva.yaml: %s", configFile, err)
		configFile = "./enva.yaml"
		rawConfig, err = libs.LoadConfig(configFile)
		if err != nil {
			libs.GetLogger("configure_dns_records").Error("Failed to load config file: %s", err)
			return false
		}
	}

	// Get all environments
	environments, ok := rawConfig["environments"].(map[string]interface{})
	if !ok {
		libs.GetLogger("configure_dns_records").Error("No environments section found in config")
		return false
	}

	// Get services config (shared across environments)
	servicesData, ok := rawConfig["services"].(map[string]interface{})
	if !ok {
		libs.GetLogger("configure_dns_records").Error("No services section found in config")
		return false
	}

	// Get containers config (shared across environments)
	containersData, ok := rawConfig["ct"].([]interface{})
	if !ok {
		libs.GetLogger("configure_dns_records").Error("No containers (ct) section found in config")
		return false
	}

	libs.GetLogger("configure_dns_records").Info("Configuring DNS records for all environments: dev, test, prod")

	// Get PostgreSQL connection details from DNS container params
	params := make(map[string]interface{})
	if dnsContainer.Params != nil {
		params = dnsContainer.Params
	}
	// Use environment-specific PostgreSQL host (from config or DNS container params)
	postgresHost := ""
	if a.Cfg.PostgresHost != nil {
		postgresHost = *a.Cfg.PostgresHost
	} else if p, ok := params["postgres_host"].(string); ok {
		postgresHost = p
	}
	// Fallback to default only if not set (should not happen in normal deployment)
	if postgresHost == "" {
		libs.GetLogger("configure_dns_records").Warning("PostgreSQL host not configured, using default")
		postgresHost = "10.11.3.18" // fallback default
	}
	libs.GetLogger("configure_dns_records").Info("Using PostgreSQL host: %s for DNS database", postgresHost)
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

	// Install postgresql-client if needed
	installPgClientCmd := "command -v psql  || apt-get install -y postgresql-client"
	timeout := 60
	a.PCTService.Execute(dnsContainer.ID, installPgClientCmd, &timeout)

	// Helper function to compute IP from network and IP offset
	computeIP := func(network string, ipOffset int) string {
		// Extract network base (e.g., "10.11.2.0/24" -> "10.11.2")
		slashIdx := strings.Index(network, "/")
		if slashIdx >= 0 {
			network = network[:slashIdx]
		}
		parts := strings.Split(network, ".")
		if len(parts) >= 3 {
			return fmt.Sprintf("%s.%s.%s.%d", parts[0], parts[1], parts[2], ipOffset)
		}
		return ""
	}

	// Helper function to get container IP offset from containers list
	getContainerIPOffset := func(containerName string, containers []interface{}) (int, bool) {
		for _, ctInterface := range containers {
			ct, ok := ctInterface.(map[string]interface{})
			if !ok {
				continue
			}
			if name, ok := ct["name"].(string); ok && name == containerName {
				if ip, ok := ct["ip"].(int); ok {
					return ip, true
				}
			}
		}
		return 0, false
	}

	// Get service names from services config
	aptCacheName := ""
	if aptCacheData, ok := servicesData["apt_cache"].(map[string]interface{}); ok {
		if name, ok := aptCacheData["name"].(string); ok {
			aptCacheName = name
		}
	}
	postgresqlName := ""
	if pgData, ok := servicesData["postgresql"].(map[string]interface{}); ok {
		if name, ok := pgData["name"].(string); ok {
			postgresqlName = name
		}
	}

	haproxyName := ""
	if haproxyData, ok := servicesData["haproxy"].(map[string]interface{}); ok {
		if name, ok := haproxyData["name"].(string); ok {
			haproxyName = name
		}
	}

	rancherName := ""
	if rancherData, ok := servicesData["rancher"].(map[string]interface{}); ok {
		if name, ok := rancherData["name"].(string); ok {
			rancherName = name
		}
	}

	cockroachName := ""
	if cockroachData, ok := servicesData["cockroachdb"].(map[string]interface{}); ok {
		if name, ok := cockroachData["name"].(string); ok {
			cockroachName = name
		}
	}

	certaName := ""
	if certaData, ok := servicesData["certa"].(map[string]interface{}); ok {
		if name, ok := certaData["name"].(string); ok {
			certaName = name
		}
	}

	// Collect all DNS records to add (for all environments)
	type DNSRecord struct {
		fqdn string
		ip   string
	}
	allRecords := []DNSRecord{}

	// Process each environment
	for envName, envInterface := range environments {
		env, ok := envInterface.(map[string]interface{})
		if !ok {
			continue
		}

		// Get domain for this environment
		domain, ok := env["domain"].(string)
		if !ok || domain == "" {
			libs.GetLogger("configure_dns_records").Warning("Domain not configured for environment %s, skipping", envName)
			continue
		}

		// Get network for this environment
		network, ok := env["network"].(string)
		if !ok || network == "" {
			libs.GetLogger("configure_dns_records").Warning("Network not configured for environment %s, skipping", envName)
			continue
		}

		libs.GetLogger("configure_dns_records").Info("Processing environment %s (domain: %s, network: %s)", envName, domain, network)

		// Add apt_cache DNS record - points to haproxy (not apt-cache directly)
		if aptCacheName != "" {
			if ipOffset, found := getContainerIPOffset("haproxy", containersData); found {
				ip := computeIP(network, ipOffset)
				if ip != "" {
					fqdn := fmt.Sprintf("%s.%s", aptCacheName, domain)
					allRecords = append(allRecords, DNSRecord{fqdn: fqdn, ip: ip})
					libs.GetLogger("configure_dns_records").Info("  Will add: %s -> %s (via haproxy)", fqdn, ip)
				}
			}
		}

		// Add postgresql DNS record - points to haproxy (not pgsql directly)
		if postgresqlName != "" {
			if ipOffset, found := getContainerIPOffset("haproxy", containersData); found {
				ip := computeIP(network, ipOffset)
				if ip != "" {
					fqdn := fmt.Sprintf("%s.%s", postgresqlName, domain)
					allRecords = append(allRecords, DNSRecord{fqdn: fqdn, ip: ip})
					libs.GetLogger("configure_dns_records").Info("  Will add: %s -> %s (via haproxy)", fqdn, ip)
				}
			}
		}

		// Add haproxy DNS record
		if haproxyName != "" {
			if ipOffset, found := getContainerIPOffset("haproxy", containersData); found {
				ip := computeIP(network, ipOffset)
				if ip != "" {
					fqdn := fmt.Sprintf("%s.%s", haproxyName, domain)
					allRecords = append(allRecords, DNSRecord{fqdn: fqdn, ip: ip})
					libs.GetLogger("configure_dns_records").Info("  Will add: %s -> %s", fqdn, ip)
				}
			}
		}

		// Add rancher DNS record - points to haproxy (not k3s-control directly)
		if rancherName != "" {
			if ipOffset, found := getContainerIPOffset("haproxy", containersData); found {
				ip := computeIP(network, ipOffset)
				if ip != "" {
					fqdn := fmt.Sprintf("%s.%s", rancherName, domain)
					allRecords = append(allRecords, DNSRecord{fqdn: fqdn, ip: ip})
					libs.GetLogger("configure_dns_records").Info("  Will add: %s -> %s (via haproxy)", fqdn, ip)
				}
			}
		}

		// Add cockroachdb DNS record (points to haproxy, which routes to k3s worker nodes)
		if cockroachName != "" {
			if ipOffset, found := getContainerIPOffset("haproxy", containersData); found {
				ip := computeIP(network, ipOffset)
				if ip != "" {
					fqdn := fmt.Sprintf("%s.%s", cockroachName, domain)
					allRecords = append(allRecords, DNSRecord{fqdn: fqdn, ip: ip})
					libs.GetLogger("configure_dns_records").Info("  Will add: %s -> %s (via haproxy)", fqdn, ip)
				}
			}
		}

		// Add certa DNS record (points to haproxy, which routes to k3s worker nodes)
		if certaName != "" {
			if ipOffset, found := getContainerIPOffset("haproxy", containersData); found {
				ip := computeIP(network, ipOffset)
				if ip != "" {
					fqdn := fmt.Sprintf("%s.%s", certaName, domain)
					allRecords = append(allRecords, DNSRecord{fqdn: fqdn, ip: ip})
					libs.GetLogger("configure_dns_records").Info("  Will add: %s -> %s (via haproxy)", fqdn, ip)
				}
			}
		}
	}

	// Add DNS records to sins DNS database for all environments
	// SiNS DNS schema uses DnsRecords table with columns: Id, Name, Type, Value, Ttl, CreatedAt, UpdatedAt
	// There's a unique constraint on (Name, Type), so we use INSERT ... ON CONFLICT to update
	for _, record := range allRecords {
		libs.GetLogger("configure_dns_records").Info("Adding DNS record: %s -> %s", record.fqdn, record.ip)

		// Escape single quotes in SQL values
		escapedFqdn := strings.ReplaceAll(record.fqdn, "'", "''")
		escapedIP := strings.ReplaceAll(record.ip, "'", "''")

		// Use INSERT ... ON CONFLICT to update existing records or insert new ones
		// The unique constraint is on (Name, Type), so we update Value and Ttl on conflict
		addRecordSQL := fmt.Sprintf(`
INSERT INTO "DnsRecords" ("Name", "Type", "Value", "Ttl", "CreatedAt", "UpdatedAt")
VALUES ('%s', 'A', '%s', 3600, NOW(), NOW())
ON CONFLICT ("Name", "Type") 
DO UPDATE SET 
    "Value" = EXCLUDED."Value",
    "Ttl" = EXCLUDED."Ttl",
    "UpdatedAt" = NOW();
`, escapedFqdn, escapedIP)

		// Write SQL to temp file to avoid shell escaping issues
		sqlFile := "/tmp/add_dns_record.sql"
		writeSQLCmd := fmt.Sprintf("cat > %s << 'SQL_EOF'\n%s\nSQL_EOF", sqlFile, addRecordSQL)
		_, _ = a.PCTService.Execute(dnsContainer.ID, writeSQLCmd, nil)

		psqlCmd := fmt.Sprintf("PGPASSWORD=%s psql -h %s -p %d -U %s -d %s -f %s",
			postgresPassword, postgresHost, postgresPort, postgresUser, postgresDB, sqlFile)

		output, exitCode := a.PCTService.Execute(dnsContainer.ID, psqlCmd, &timeout)
		if exitCode != nil && *exitCode != 0 {
			libs.GetLogger("configure_dns_records").Warning("Failed to add DNS record for %s: %s", record.fqdn, output)
			// Continue with other records even if one fails
		} else {
			libs.GetLogger("configure_dns_records").Info("Successfully added DNS record: %s -> %s", record.fqdn, record.ip)
		}
	}

	return true
}
