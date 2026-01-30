using System;
using System.Linq;
using System.Threading;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Actions;

public class ConfigurePostgresServiceAction : BaseAction, IAction
{
    public ConfigurePostgresServiceAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "postgresql service configuration";
    }

    public bool Execute()
    {
        if (SSHService == null || Cfg == null)
        {
            Logger.GetLogger("configure_postgres_service").Printf("SSH service or config not initialized");
            return false;
        }
        string version = "17";
        if (ContainerCfg?.Params != null && ContainerCfg.Params.ContainsKey("version"))
        {
            if (ContainerCfg.Params["version"] is string v)
            {
                version = v;
            }
        }
        string clusterService = $"postgresql@{version}-main";
        string enableCmd = CLI.SystemCtl.NewSystemCtl().Service(clusterService).Enable();
        string startCmd = CLI.SystemCtl.NewSystemCtl().Service(clusterService).Start();
        SSHService.Execute(enableCmd, null, true);
        var (output, exitCode) = SSHService.Execute(startCmd, null, true);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("configure_postgres_service").Printf("start postgresql cluster service failed with exit code {0}", exitCode?.ToString() ?? "null");
            if (!string.IsNullOrEmpty(output))
            {
                var lines = output.Split('\n');
                if (lines.Length > 0)
                {
                    Logger.GetLogger("configure_postgres_service").Printf("start postgresql cluster service output: {0}", lines[lines.Length - 1]);
                }
            }
            return false;
        }
        Thread.Sleep(Cfg.Waits.ServiceStart * 1000);
        string isActiveCmd = CLI.SystemCtl.NewSystemCtl().Service(clusterService).IsActive();
        var (status, statusExitCode) = SSHService.Execute(isActiveCmd, null, true);
        if (statusExitCode.HasValue && statusExitCode.Value == 0 && CLI.SystemCtl.ParseIsActive(status))
        {
            string portCheckCmd = "ss -tlnp | grep :5432 | grep -v '127.0.0.1' || echo 'not_listening'";
            var (portOutput, _) = SSHService.Execute(portCheckCmd, null, true);
            if (!portOutput.Contains("not_listening") && portOutput.Contains(":5432"))
            {
                Logger.GetLogger("configure_postgres_service").Printf("PostgreSQL is listening on all interfaces");
            }
            else
            {
                Logger.GetLogger("configure_postgres_service").Printf("PostgreSQL may not be listening on external interfaces");
            }
            return true;
        }
        Logger.GetLogger("configure_postgres_service").Printf("PostgreSQL cluster service is not active");
        return false;
    }
}

public static class ConfigurePostgresServiceActionFactory
{
    public static IAction NewConfigurePostgresServiceAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new ConfigurePostgresServiceAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
