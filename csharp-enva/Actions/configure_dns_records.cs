using System;
using System.Collections.Generic;
using System.Linq;
using Enva.Libs;
using Enva.Services;

namespace Enva.Actions;

public class ConfigureDnsRecordsAction : BaseAction, IAction
{
    public ConfigureDnsRecordsAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        SSHService = sshService;
        APTService = aptService;
        PCTService = pctService;
        ContainerID = containerID;
        Cfg = cfg;
        ContainerCfg = containerCfg;
    }

    public override string Description()
    {
        return "configure dns records";
    }

    public bool Execute()
    {
        if (PCTService == null || Cfg == null)
        {
            Logger.GetLogger("configure_dns_records").Printf("PCT service or config not initialized");
            return false;
        }

        // Get DNS server container (dns container)
        ContainerConfig? dnsContainer = null;
        foreach (var ct in Cfg.Containers)
        {
            if (ct.Name == "dns")
            {
                dnsContainer = ct;
                break;
            }
        }
        if (dnsContainer == null)
        {
            Logger.GetLogger("configure_dns_records").Printf("DNS container not found");
            return false;
        }

        // Load full config to access all environments
        // Try common config file locations
        string configFile = "enva.yaml";
        EnvaConfig? rawConfig = null;
        try
        {
            rawConfig = ConfigLoader.LoadConfig(configFile);
        }
        catch (Exception ex)
        {
            Logger.GetLogger("configure_dns_records").Printf("Failed to load config file {0}, trying ./enva.yaml: {1}", configFile, ex.Message);
            configFile = "./enva.yaml";
            try
            {
                rawConfig = ConfigLoader.LoadConfig(configFile);
            }
            catch (Exception ex2)
            {
                Logger.GetLogger("configure_dns_records").Printf("Failed to load config file: {0}", ex2.Message);
                return false;
            }
        }

        if (rawConfig == null || rawConfig.Environments == null)
        {
            Logger.GetLogger("configure_dns_records").Printf("No environments section found in config");
            return false;
        }

        // Get services config (shared across environments)
        if (rawConfig.Services == null)
        {
            Logger.GetLogger("configure_dns_records").Printf("No services section found in config");
            return false;
        }

        // Get containers config (shared across environments)
        if (rawConfig.Containers == null)
        {
            Logger.GetLogger("configure_dns_records").Printf("No containers (ct) section found in config");
            return false;
        }

        Logger.GetLogger("configure_dns_records").Printf("Configuring DNS records for all environments: dev, test, prod");

        // Get PostgreSQL connection details from DNS container params
        Dictionary<string, object>? params_ = dnsContainer.Params;
        // Use environment-specific PostgreSQL host (from config or DNS container params)
        string postgresHost = Cfg.PostgresHost ?? "";
        if (string.IsNullOrEmpty(postgresHost) && params_ != null && params_.TryGetValue("postgres_host", out object? pghost) && pghost is string pghostStr)
        {
            postgresHost = pghostStr;
        }
        // Fallback to default only if not set (should not happen in normal deployment)
        if (string.IsNullOrEmpty(postgresHost))
        {
            Logger.GetLogger("configure_dns_records").Printf("PostgreSQL host not configured, using default");
            postgresHost = "10.11.3.18"; // fallback default
        }
        Logger.GetLogger("configure_dns_records").Printf("Using PostgreSQL host: {0} for DNS database", postgresHost);
        int postgresPort = 5432;
        if (params_ != null && params_.TryGetValue("postgres_port", out object? pgport) && pgport is int pgportInt)
        {
            postgresPort = pgportInt;
        }
        string postgresDB = "dns_server";
        if (params_ != null && params_.TryGetValue("postgres_db", out object? pgdb) && pgdb is string pgdbStr)
        {
            postgresDB = pgdbStr;
        }
        string postgresUser = "postgres";
        if (params_ != null && params_.TryGetValue("postgres_user", out object? pguser) && pguser is string pguserStr)
        {
            postgresUser = pguserStr;
        }
        string postgresPassword = "postgres";
        if (params_ != null && params_.TryGetValue("postgres_password", out object? pgpass) && pgpass is string pgpassStr)
        {
            postgresPassword = pgpassStr;
        }

        // Install postgresql-client if needed
        string installPgClientCmd = "command -v psql  || apt-get install -y postgresql-client";
        int timeout = 60;
        PCTService.Execute(dnsContainer.ID, installPgClientCmd, timeout);

        // Helper function to compute IP from network and IP offset
        string ComputeIP(string network, int ipOffset)
        {
            // Extract network base (e.g., "10.11.2.0/24" -> "10.11.2")
            int slashIdx = network.IndexOf("/");
            if (slashIdx >= 0)
            {
                network = network.Substring(0, slashIdx);
            }
            string[] parts = network.Split('.');
            if (parts.Length >= 3)
            {
                return $"{parts[0]}.{parts[1]}.{parts[2]}.{ipOffset}";
            }
            return "";
        }

        // Helper function to get container IP offset from containers list
        (int, bool) GetContainerIPOffset(string containerName, List<ContainerConfig> containers)
        {
            foreach (var ct in containers)
            {
                if (ct.Name == containerName)
                {
                    return (ct.IP, true);
                }
            }
            return (0, false);
        }

        // Collect all DNS records to add (for all environments)
        List<(string fqdn, string ip)> allRecords = new List<(string, string)>();

        // Process each environment
        foreach (var kvp in rawConfig.Environments)
        {
            string envName = kvp.Key;
            var env = kvp.Value;

            // Get domain for this environment
            string? domain = env.Domain;
            if (string.IsNullOrEmpty(domain))
            {
                Logger.GetLogger("configure_dns_records").Printf("Domain not configured for environment {0}, skipping", envName);
                continue;
            }

            // Get network for this environment
            string? network = env.Network;
            if (string.IsNullOrEmpty(network))
            {
                Logger.GetLogger("configure_dns_records").Printf("Network not configured for environment {0}, skipping", envName);
                continue;
            }

            Logger.GetLogger("configure_dns_records").Printf("Processing environment {0} (domain: {1}, network: {2})", envName, domain, network);

            // Get service names from services config
            string aptCacheName = "";
            if (rawConfig.Services != null && rawConfig.Services.TryGetValue("apt_cache", out var aptCacheService) && aptCacheService.Ports != null && aptCacheService.Ports.Count > 0)
                aptCacheName = aptCacheService.Ports[0].Name;
            string postgresqlName = "";
            if (rawConfig.Services != null && rawConfig.Services.TryGetValue("postgresql", out var postgresqlService) && postgresqlService.Ports != null && postgresqlService.Ports.Count > 0)
                postgresqlName = postgresqlService.Ports[0].Name;
            string haproxyName = "";
            if (rawConfig.Services != null && rawConfig.Services.TryGetValue("haproxy", out var haproxyService) && haproxyService.Ports != null && haproxyService.Ports.Count > 0)
                haproxyName = haproxyService.Ports[0].Name;
            string rancherName = "";
            if (rawConfig.Services != null && rawConfig.Services.TryGetValue("rancher", out var rancherService) && rancherService.Ports != null && rancherService.Ports.Count > 0)
                rancherName = rancherService.Ports[0].Name;
            string cockroachName = "";
            if (rawConfig.Services != null && rawConfig.Services.TryGetValue("cockroachdb", out var cockroachService) && cockroachService.Ports != null && cockroachService.Ports.Count > 0)
                cockroachName = cockroachService.Ports[0].Name;
            string certaName = "";
            if (rawConfig.Services != null && rawConfig.Services.TryGetValue("certa", out var certaService) && certaService.Ports != null && certaService.Ports.Count > 0)
                certaName = certaService.Ports[0].Name;

            // Add apt_cache DNS record - points to haproxy (not apt-cache directly)
            if (!string.IsNullOrEmpty(aptCacheName))
            {
                var (ipOffset, found) = GetContainerIPOffset("haproxy", rawConfig.Containers);
                if (found)
                {
                    string ip = ComputeIP(network, ipOffset);
                    if (!string.IsNullOrEmpty(ip))
                    {
                        string fqdn = $"{aptCacheName}.{domain}";
                        allRecords.Add((fqdn, ip));
                        Logger.GetLogger("configure_dns_records").Printf("  Will add: {0} -> {1} (via haproxy)", fqdn, ip);
                    }
                }
            }

            // Add postgresql DNS record - points to haproxy (not pgsql directly)
            if (!string.IsNullOrEmpty(postgresqlName))
            {
                var (ipOffset, found) = GetContainerIPOffset("haproxy", rawConfig.Containers);
                if (found)
                {
                    string ip = ComputeIP(network, ipOffset);
                    if (!string.IsNullOrEmpty(ip))
                    {
                        string fqdn = $"{postgresqlName}.{domain}";
                        allRecords.Add((fqdn, ip));
                        Logger.GetLogger("configure_dns_records").Printf("  Will add: {0} -> {1} (via haproxy)", fqdn, ip);
                    }
                }
            }

            // Add haproxy DNS record
            if (!string.IsNullOrEmpty(haproxyName))
            {
                var (ipOffset, found) = GetContainerIPOffset("haproxy", rawConfig.Containers);
                if (found)
                {
                    string ip = ComputeIP(network, ipOffset);
                    if (!string.IsNullOrEmpty(ip))
                    {
                        string fqdn = $"{haproxyName}.{domain}";
                        allRecords.Add((fqdn, ip));
                        Logger.GetLogger("configure_dns_records").Printf("  Will add: {0} -> {1}", fqdn, ip);
                    }
                }
            }

            // Add rancher DNS record - points to haproxy (not k3s-control directly)
            if (!string.IsNullOrEmpty(rancherName))
            {
                var (ipOffset, found) = GetContainerIPOffset("haproxy", rawConfig.Containers);
                if (found)
                {
                    string ip = ComputeIP(network, ipOffset);
                    if (!string.IsNullOrEmpty(ip))
                    {
                        string fqdn = $"{rancherName}.{domain}";
                        allRecords.Add((fqdn, ip));
                        Logger.GetLogger("configure_dns_records").Printf("  Will add: {0} -> {1} (via haproxy)", fqdn, ip);
                    }
                }
            }

            // Add cockroachdb DNS record (points to haproxy, which routes to k3s worker nodes)
            if (!string.IsNullOrEmpty(cockroachName))
            {
                var (ipOffset, found) = GetContainerIPOffset("haproxy", rawConfig.Containers);
                if (found)
                {
                    string ip = ComputeIP(network, ipOffset);
                    if (!string.IsNullOrEmpty(ip))
                    {
                        string fqdn = $"{cockroachName}.{domain}";
                        allRecords.Add((fqdn, ip));
                        Logger.GetLogger("configure_dns_records").Printf("  Will add: {0} -> {1} (via haproxy)", fqdn, ip);
                    }
                }
            }

            // Add certa DNS record (points to haproxy, which routes to k3s worker nodes)
            if (!string.IsNullOrEmpty(certaName))
            {
                var (ipOffset, found) = GetContainerIPOffset("haproxy", rawConfig.Containers);
                if (found)
                {
                    string ip = ComputeIP(network, ipOffset);
                    if (!string.IsNullOrEmpty(ip))
                    {
                        string fqdn = $"{certaName}.{domain}";
                        allRecords.Add((fqdn, ip));
                        Logger.GetLogger("configure_dns_records").Printf("  Will add: {0} -> {1} (via haproxy)", fqdn, ip);
                    }
                }
            }
        }

        // Add DNS records to sins DNS database for all environments
        // SiNS DNS schema uses DnsRecords table with columns: Id, Name, Type, Value, Ttl, CreatedAt, UpdatedAt
        // There's a unique constraint on (Name, Type), so we use INSERT ... ON CONFLICT to update
        foreach (var (fqdn, ip) in allRecords)
        {
            Logger.GetLogger("configure_dns_records").Printf("Adding DNS record: {0} -> {1}", fqdn, ip);

            // Escape single quotes in SQL values
            string escapedFqdn = fqdn.Replace("'", "''");
            string escapedIP = ip.Replace("'", "''");

            // Use INSERT ... ON CONFLICT to update existing records or insert new ones
            // The unique constraint is on (Name, Type), so we update Value and Ttl on conflict
            string addRecordSQL = $@"
INSERT INTO ""DnsRecords"" (""Name"", ""Type"", ""Value"", ""Ttl"", ""CreatedAt"", ""UpdatedAt"")
VALUES ('{escapedFqdn}', 'A', '{escapedIP}', 3600, NOW(), NOW())
ON CONFLICT (""Name"", ""Type"") 
DO UPDATE SET 
    ""Value"" = EXCLUDED.""Value"",
    ""Ttl"" = EXCLUDED.""Ttl"",
    ""UpdatedAt"" = NOW();
";

            // Write SQL to temp file to avoid shell escaping issues
            string sqlFile = "/tmp/add_dns_record.sql";
            string writeSQLCmd = $"cat > {sqlFile} << 'SQL_EOF'\n{addRecordSQL}\nSQL_EOF";
            PCTService.Execute(dnsContainer.ID, writeSQLCmd, null);

            string psqlCmd = $"PGPASSWORD={postgresPassword} psql -h {postgresHost} -p {postgresPort} -U {postgresUser} -d {postgresDB} -f {sqlFile}";

            (string output, int? exitCode) = PCTService.Execute(dnsContainer.ID, psqlCmd, timeout);
            if (exitCode.HasValue && exitCode.Value != 0)
            {
                Logger.GetLogger("configure_dns_records").Printf("Failed to add DNS record for {0}: {1}", fqdn, output);
                // Continue with other records even if one fails
            }
            else
            {
                Logger.GetLogger("configure_dns_records").Printf("Successfully added DNS record: {0} -> {1}", fqdn, ip);
            }
        }

        return true;
    }
}

public static class ConfigureDnsRecordsActionFactory
{
    public static IAction NewConfigureDnsRecordsAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new ConfigureDnsRecordsAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}