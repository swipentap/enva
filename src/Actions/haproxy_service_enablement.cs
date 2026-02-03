using System;
using System.Linq;
using System.Threading;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Actions;

public class EnableHaproxyServiceAction : BaseAction, IAction
{
    public EnableHaproxyServiceAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "haproxy service enablement";
    }

    public bool Execute()
    {
        if (SSHService == null)
        {
            Logger.GetLogger("enable_haproxy_service").Printf("SSH service not initialized");
            return false;
        }
        string validateCmd = "haproxy -c -f /etc/haproxy/haproxy.cfg";
        var (validateOutput, validateExitCode) = SSHService.Execute(validateCmd, null, true);
        if (!validateExitCode.HasValue || validateExitCode.Value != 0)
        {
            Logger.GetLogger("enable_haproxy_service").Printf("HAProxy config validation command failed");
            return false;
        }
        if (!string.IsNullOrEmpty(validateOutput) && (validateOutput.Contains("Fatal errors found") || validateOutput.Contains("[ALERT]")))
        {
            Logger.GetLogger("enable_haproxy_service").Printf("HAProxy config validation failed: {0}", validateOutput);
            return false;
        }
        string restartCmd = CLI.SystemCtl.NewSystemCtl().Service("haproxy").Restart();
        var (output, exitCode) = SSHService.Execute(restartCmd, null, true);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("enable_haproxy_service").Printf("restart haproxy service failed with exit code {0}", exitCode?.ToString() ?? "null");
            if (!string.IsNullOrEmpty(output))
            {
                var lines = output.Split('\n');
                if (lines.Length > 0)
                {
                    Logger.GetLogger("enable_haproxy_service").Printf("restart haproxy service output: {0}", lines[lines.Length - 1]);
                }
            }
            string statusCmd = "systemctl status haproxy.service --no-pager -l | head -20";
            var (statusOutput, _) = SSHService.Execute(statusCmd, null, true);
            Logger.GetLogger("enable_haproxy_service").Printf("HAProxy service restart failed. Status: {0}", statusOutput);
            Thread.Sleep(2000);
            string statusCheckCmd = CLI.SystemCtl.NewSystemCtl().Service("haproxy").IsActive();
            var (statusCheck, statusExitCode) = SSHService.Execute(statusCheckCmd, null, true);
            if (statusExitCode.HasValue && statusExitCode.Value == 0 && CLI.SystemCtl.ParseIsActive(statusCheck))
            {
                Logger.GetLogger("enable_haproxy_service").Printf("HAProxy service is active despite restart failure, treating as success");
                return true;
            }
            return false;
        }
        string enableCmd = CLI.SystemCtl.NewSystemCtl().Service("haproxy").Enable();
        SSHService.Execute(enableCmd, null, true);
        Thread.Sleep(2000);
        string statusCmd2 = CLI.SystemCtl.NewSystemCtl().Service("haproxy").IsActive();
        var (status, exitCode2) = SSHService.Execute(statusCmd2, null, true);
        if (exitCode2.HasValue && exitCode2.Value == 0 && CLI.SystemCtl.ParseIsActive(status))
        {
            return true;
        }
        Logger.GetLogger("enable_haproxy_service").Printf("HAProxy service is not active after restart");
        return false;
    }
}

public static class EnableHaproxyServiceActionFactory
{
    public static IAction NewEnableHaproxyServiceAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new EnableHaproxyServiceAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
