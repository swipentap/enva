using System;
using Enva.Libs;
using Enva.Services;

namespace Enva.Actions;

public class InstallPostgresqlAction : BaseAction, IAction
{
    public InstallPostgresqlAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "postgresql installation";
    }

    public bool Execute()
    {
        if (APTService == null)
        {
            Logger.GetLogger("install_postgresql").Printf("APT service not initialized");
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
        Logger.GetLogger("install_postgresql").Printf("Installing PostgreSQL {0} package...", version);
        var (output, exitCode) = APTService.Install(new[] { $"postgresql-{version}", "postgresql-contrib" });
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("install_postgresql").Printf("PostgreSQL installation failed: {0}", output);
            return false;
        }
        return true;
    }
}

public static class InstallPostgresqlActionFactory
{
    public static IAction NewInstallPostgresqlAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallPostgresqlAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
