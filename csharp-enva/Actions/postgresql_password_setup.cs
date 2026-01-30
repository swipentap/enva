using System;
using System.Linq;
using Enva.Libs;
using Enva.Services;

namespace Enva.Actions;

public class SetPostgresPasswordAction : BaseAction, IAction
{
    public SetPostgresPasswordAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "postgresql password setup";
    }

    public bool Execute()
    {
        if (SSHService == null)
        {
            Logger.GetLogger("set_postgres_password").Printf("SSH service not initialized");
            return false;
        }
        string password = GetProperty<string>("password", "postgres");
        string username = GetProperty<string>("username", "postgres");
        string database = GetProperty<string>("database", "postgres");
        string command = $"sudo -n -u postgres psql -c \"ALTER USER postgres WITH PASSWORD '{password.Replace("'", "''")}';\"";
        var (output, exitCode) = SSHService.Execute(command, null);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("set_postgres_password").Printf("set postgres password failed with exit code {0}", exitCode?.ToString() ?? "null");
            if (!string.IsNullOrEmpty(output))
            {
                var lines = output.Split('\n');
                if (lines.Length > 0)
                {
                    Logger.GetLogger("set_postgres_password").Printf("set postgres password output: {0}", lines[lines.Length - 1]);
                }
            }
            return false;
        }
        return true;
    }
}

public static class SetPostgresPasswordActionFactory
{
    public static IAction NewSetPostgresPasswordAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new SetPostgresPasswordAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
