using System;
using System.Linq;
using System.Threading;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Actions;

public class ConfigurePostgresFilesAction : BaseAction, IAction
{
    public ConfigurePostgresFilesAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "postgresql files configuration";
    }

    public bool Execute()
    {
        if (SSHService == null)
        {
            Logger.GetLogger("configure_postgres_files").Printf("SSH service not initialized");
            return false;
        }
        string version = "17";
        int port = 5432;
        string allowCIDR = "10.11.3.0/24";
        if (Cfg != null && !string.IsNullOrEmpty(Cfg.Network))
        {
            allowCIDR = Cfg.Network;
        }
        if (ContainerCfg?.Params != null)
        {
            if (ContainerCfg.Params.ContainsKey("version") && ContainerCfg.Params["version"] is string v)
            {
                version = v;
            }
            if (ContainerCfg.Params.ContainsKey("port") && ContainerCfg.Params["port"] is int p)
            {
                port = p;
            }
            if (Cfg == null || string.IsNullOrEmpty(Cfg.Network))
            {
                if (ContainerCfg.Params.ContainsKey("cidr") && ContainerCfg.Params["cidr"] is string cidr)
                {
                    allowCIDR = cidr;
                }
            }
        }
        string configPath = $"/etc/postgresql/{version}/main/postgresql.conf";
        string removeCmd = $"sed -i '/^#*listen_addresses.*/d' {configPath}";
        var (output, exitCode) = SSHService.Execute(removeCmd, null, true);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("configure_postgres_files").Printf("remove listen_addresses failed with exit code {0}", exitCode?.ToString() ?? "null");
            return false;
        }
        string checkCmd = $"grep -q '^listen_addresses =' {configPath} && echo exists || echo not_exists";
        var (checkOutput, _) = SSHService.Execute(checkCmd, null, true);
        if (checkOutput.Contains("not_exists"))
        {
            string lineNumCmd = $"grep -n '^# CONNECTIONS AND AUTHENTICATION' {configPath} | cut -d: -f1";
            var (lineNumOutput, _) = SSHService.Execute(lineNumCmd, null, true);
            if (!string.IsNullOrEmpty(lineNumOutput) && !string.IsNullOrWhiteSpace(lineNumOutput))
            {
                string insertScript = $"awk -v n={lineNumOutput.Trim()} -v s=\"listen_addresses = '*'\" 'NR==n {{print; print s; next}}1' {configPath} > {configPath}.tmp && mv {configPath}.tmp {configPath}";
                (output, exitCode) = SSHService.Execute(insertScript, null, true);
            }
            else
            {
                string insertScript = $"echo \"listen_addresses = '*'\" >> {configPath}";
                (output, exitCode) = SSHService.Execute(insertScript, null, true);
            }
            if (!exitCode.HasValue || exitCode.Value != 0)
            {
                Logger.GetLogger("configure_postgres_files").Printf("insert listen_addresses failed with exit code {0}", exitCode?.ToString() ?? "null");
                if (!string.IsNullOrEmpty(output))
                {
                    Logger.GetLogger("configure_postgres_files").Printf("insert listen_addresses output: {0}", output);
                }
                return false;
            }
        }
        string portCmd = CLI.Sed.NewSed().Flags("").Replace(configPath, "^#?port.*", $"port = {port}");
        string pgHbaContent = $"local all all peer\nhost all all {allowCIDR} md5\n";
        string pgHbaCmd = CLI.Files.NewFileOps().Write($"/etc/postgresql/{version}/main/pg_hba.conf", pgHbaContent).ToCommand();
        bool portSuccess = false;
        bool pgHbaSuccess = false;
        
        var (portOutput, portExitCode) = SSHService.Execute(portCmd, null, true);
        portSuccess = portExitCode.HasValue && portExitCode.Value == 0;
        if (!portSuccess && portExitCode.HasValue)
        {
            Logger.GetLogger("configure_postgres_files").Printf("configure postgres port failed with exit code {0}", portExitCode.Value);
            if (!string.IsNullOrEmpty(portOutput))
            {
                var lines = portOutput.Split('\n');
                if (lines.Length > 0)
                {
                    Logger.GetLogger("configure_postgres_files").Printf("configure postgres port output: {0}", lines[lines.Length - 1]);
                }
            }
        }
        
        var (pgHbaOutput, pgHbaExitCode) = SSHService.Execute(pgHbaCmd, null, true);
        pgHbaSuccess = pgHbaExitCode.HasValue && pgHbaExitCode.Value == 0;
        if (!pgHbaSuccess && pgHbaExitCode.HasValue)
        {
            Logger.GetLogger("configure_postgres_files").Printf("write pg_hba rule failed with exit code {0}", pgHbaExitCode.Value);
            if (!string.IsNullOrEmpty(pgHbaOutput))
            {
                var lines = pgHbaOutput.Split('\n');
                if (lines.Length > 0)
                {
                    Logger.GetLogger("configure_postgres_files").Printf("write pg_hba rule output: {0}", lines[lines.Length - 1]);
                }
            }
        }
        
        if (portSuccess && pgHbaSuccess)
        {
            Logger.GetLogger("configure_postgres_files").Printf("Restarting PostgreSQL to apply configuration changes...");
            string clusterService = $"postgresql@{version}-main";
            string restartCmd = CLI.SystemCtl.NewSystemCtl().Service(clusterService).Restart();
            (output, exitCode) = SSHService.Execute(restartCmd, null, true);
            if (!exitCode.HasValue || exitCode.Value != 0)
            {
                Logger.GetLogger("configure_postgres_files").Printf("restart postgresql failed with exit code {0}", exitCode?.ToString() ?? "null");
                return false;
            }
            Thread.Sleep(3000);
            string portCheckCmd = "ss -tlnp | grep :5432 | grep -v '127.0.0.1' || echo 'not_listening'";
            var (portOutput2, _) = SSHService.Execute(portCheckCmd, null, true);
            if (!portOutput2.Contains("not_listening") && portOutput2.Contains(":5432"))
            {
                Logger.GetLogger("configure_postgres_files").Printf("PostgreSQL is listening on all interfaces after restart");
            }
            else
            {
                Logger.GetLogger("configure_postgres_files").Printf("PostgreSQL may not be listening on external interfaces after restart");
            }
        }
        return portSuccess && pgHbaSuccess;
    }
}

public static class ConfigurePostgresFilesActionFactory
{
    public static IAction NewConfigurePostgresFilesAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new ConfigurePostgresFilesAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
