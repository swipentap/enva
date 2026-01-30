using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Actions;

public class InstallSinsDnsAction : BaseAction, IAction
{
    public InstallSinsDnsAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "sins dns installation";
    }

    public bool Execute()
    {
        if (SSHService == null || APTService == null)
        {
            Logger.GetLogger("install_sins_dns").Error("SSH service or APT service not initialized");
            return false;
        }
        Logger.GetLogger("install_sins_dns").Info("Adding Gemfury APT repository...");
        string repoSource = "deb [trusted=yes] https://judyalvarez@apt.fury.io/judyalvarez /";
        string addSourceCmd = $"echo '{repoSource}' | tee /etc/apt/sources.list.d/fury.list";
        int timeout = 30;
        var (output, exitCode) = SSHService.Execute(addSourceCmd, timeout, true);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            int outputLen = output.Length;
            int start = outputLen > 200 ? outputLen - 200 : 0;
            Logger.GetLogger("install_sins_dns").Error("Failed to add Gemfury repository source: {0}", output.Substring(start));
            return false;
        }
        Logger.GetLogger("install_sins_dns").Info("Installing SiNS DNS server from APT repository...");
        string installCmd = "apt-get update && apt-get install -y sins";
        timeout = 300;
        (output, exitCode) = SSHService.Execute(installCmd, timeout, true);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            int outputLen = output.Length;
            int start = outputLen > 200 ? outputLen - 200 : 0;
            Logger.GetLogger("install_sins_dns").Error("Failed to install sins package: {0}", output.Substring(start));
            return false;
        }
        string verifyCmd = "command -v sins  && echo installed || echo not_installed";
        var (verifyOutput, verifyExitCode) = SSHService.Execute(verifyCmd, null, true);
        if (!verifyExitCode.HasValue || verifyExitCode.Value != 0 || !verifyOutput.Contains("installed"))
        {
            Logger.GetLogger("install_sins_dns").Error("SiNS package was not installed correctly");
            return false;
        }
        var @params = ContainerCfg?.Params ?? new Dictionary<string, object>();
        string postgresHost = Cfg?.PostgresHost ?? "10.11.3.18";
        if (@params.TryGetValue("postgres_host", out object? pgHostObj) && pgHostObj is string ph)
        {
            postgresHost = ph;
        }
        int postgresPort = 5432;
        if (@params.TryGetValue("postgres_port", out object? pgPortObj) && pgPortObj is int pp)
        {
            postgresPort = pp;
        }
        string postgresDB = "dns_server";
        if (@params.TryGetValue("postgres_db", out object? pgDbObj) && pgDbObj is string pdb)
        {
            postgresDB = pdb;
        }
        string postgresUser = "postgres";
        if (@params.TryGetValue("postgres_user", out object? pgUserObj) && pgUserObj is string pu)
        {
            postgresUser = pu;
        }
        string postgresPassword = "postgres";
        if (@params.TryGetValue("postgres_password", out object? pgPassObj) && pgPassObj is string ppass)
        {
            postgresPassword = ppass;
        }
        int dnsPort = 53;
        if (@params.TryGetValue("dns_port", out object? dnsPortObj) && dnsPortObj is int dp)
        {
            dnsPort = dp;
        }
        int webPort = 80;
        if (@params.TryGetValue("web_port", out object? webPortObj) && webPortObj is int wp)
        {
            webPort = wp;
        }
        Logger.GetLogger("install_sins_dns").Info("Ensuring PostgreSQL database '{0}' exists...", postgresDB);
        string installPgClientCmd = "command -v psql  || apt-get install -y postgresql-client";
        timeout = 60;
        SSHService.Execute(installPgClientCmd, timeout, true);
        string createDBCmd = $"PGPASSWORD={postgresPassword} psql -h {postgresHost} -p {postgresPort} -U {postgresUser} -d postgres -tc \"SELECT 1 FROM pg_database WHERE datname = '{postgresDB}'\" | grep -q 1 || PGPASSWORD={postgresPassword} psql -h {postgresHost} -p {postgresPort} -U {postgresUser} -d postgres -c \"CREATE DATABASE {postgresDB};\"";
        timeout = 30;
        (output, exitCode) = SSHService.Execute(createDBCmd, timeout, true);
        if (exitCode.HasValue && exitCode.Value == 0)
        {
            Logger.GetLogger("install_sins_dns").Info("PostgreSQL database '{0}' is ready", postgresDB);
        }
        else
        {
            int outputLen = output.Length;
            int start = outputLen > 100 ? outputLen - 100 : 0;
            Logger.GetLogger("install_sins_dns").Warning("Could not verify/create PostgreSQL database (may already exist): {0}", output.Substring(start));
        }
        Logger.GetLogger("install_sins_dns").Info("Configuring SiNS application settings...");
        string jwtSecret = GenerateJWTSecret();
        var appsettings = new
        {
            ConnectionStrings = new
            {
                DefaultConnection = $"Host={postgresHost};Port={postgresPort};Database={postgresDB};Username={postgresUser};Password={postgresPassword}"
            },
            DnsSettings = new
            {
                Port = dnsPort
            },
            WebSettings = new
            {
                Port = webPort
            },
            Jwt = new
            {
                Key = jwtSecret,
                Issuer = "SiNS-DNS-Server",
                Audience = "SiNS-DNS-Client",
                ExpirationMinutes = 1440
            }
        };
        string appsettingsJson = JsonSerializer.Serialize(appsettings, new JsonSerializerOptions { WriteIndented = true });
        byte[] appsettingsBytes = Encoding.UTF8.GetBytes(appsettingsJson);
        string appsettingsB64 = Convert.ToBase64String(appsettingsBytes);
        var configLocations = new[] { "/etc/sins/appsettings.json", "/opt/sins/appsettings.json", "/opt/sins/app/appsettings.json" };
        Logger.GetLogger("install_sins_dns").Info("Writing SiNS appsettings.json to all config locations...");
        bool configWritten = false;
        foreach (string configPath in configLocations)
        {
            var parts = configPath.Split('/');
            string configDir = string.Join("/", parts.Take(parts.Length - 1));
            string mkdirCmd = $"mkdir -p {configDir}";
            SSHService.Execute(mkdirCmd, null, true);
            string appsettingsCmd = $"echo {appsettingsB64} | base64 -d > {configPath}";
            (output, exitCode) = SSHService.Execute(appsettingsCmd, null, true);
            if (exitCode.HasValue && exitCode.Value == 0)
            {
                Logger.GetLogger("install_sins_dns").Info("SiNS appsettings.json written to {0}", configPath);
                configWritten = true;
            }
            else
            {
                int outputLen = output.Length;
                int start = outputLen > 100 ? outputLen - 100 : 0;
                Logger.GetLogger("install_sins_dns").Warning("Failed to write appsettings.json to {0}: {1}", configPath, output.Substring(start));
            }
        }
        if (!configWritten)
        {
            Logger.GetLogger("install_sins_dns").Error("Failed to write appsettings.json to any location");
            return false;
        }
        return true;
    }

    private static string GenerateJWTSecret()
    {
        byte[] b = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
        {
            rng.GetBytes(b);
        }
        return Convert.ToBase64String(b);
    }
}

public static class InstallSinsDnsActionFactory
{
    public static IAction NewInstallSinsDnsAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallSinsDnsAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
