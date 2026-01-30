using System;
using System.Linq;
using System.Threading;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Actions;

public class EnableCacheServiceAction : BaseAction, IAction
{
    public EnableCacheServiceAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "apt-cacher-ng service enablement";
    }

    public bool Execute()
    {
        if (SSHService == null)
        {
            Logger.GetLogger("enable_cache_service").Printf("SSH service not initialized");
            return false;
        }

        string enableCmd = CLI.SystemCtl.NewSystemCtl().Service("apt-cacher-ng").Enable();
        var (output, exitCode) = SSHService.Execute(enableCmd, null, true);
        if (exitCode.HasValue && exitCode.Value != 0)
        {
            Logger.GetLogger("enable_cache_service").Printf("enable apt-cacher-ng service had issues: {0}", output);
        }

        string restartCmd = CLI.SystemCtl.NewSystemCtl().Service("apt-cacher-ng").Restart();
        (output, exitCode) = SSHService.Execute(restartCmd, null, true);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("enable_cache_service").Printf("restart apt-cacher-ng service failed with exit code {0}", exitCode?.ToString() ?? "null");
            if (!string.IsNullOrEmpty(output))
            {
                var lines = output.Split('\n');
                if (lines.Length > 0)
                {
                    Logger.GetLogger("enable_cache_service").Printf("restart output: {0}", lines[lines.Length - 1]);
                }
            }
            return false;
        }

        Thread.Sleep(2000);

        string isActiveCmd = CLI.SystemCtl.NewSystemCtl().Service("apt-cacher-ng").IsActive();
        var (status, statusExitCode) = SSHService.Execute(isActiveCmd, null, true);
        if (statusExitCode.HasValue && statusExitCode.Value == 0 && CLI.SystemCtl.ParseIsActive(status))
        {
            Thread.Sleep(2000);
            var (status2, statusExitCode2) = SSHService.Execute(isActiveCmd, null, true);
            if (statusExitCode2.HasValue && statusExitCode2.Value == 0 && CLI.SystemCtl.ParseIsActive(status2))
            {
                return true;
            }
            string statusCmd = "systemctl status apt-cacher-ng --no-pager -l | head -20";
            var (statusOutput, _) = SSHService.Execute(statusCmd, null, true);
            Logger.GetLogger("enable_cache_service").Printf("apt-cacher-ng service started but stopped. Status: {0}", statusOutput);
            return false;
        }

        string statusCmd2 = "systemctl status apt-cacher-ng --no-pager -l | head -20";
        var (statusOutput2, _) = SSHService.Execute(statusCmd2, null, true);
        Logger.GetLogger("enable_cache_service").Printf("apt-cacher-ng service failed to start. Status: {0}", statusOutput2);
        return false;
    }
}

public static class EnableCacheServiceActionFactory
{
    public static IAction NewEnableCacheServiceAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new EnableCacheServiceAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
