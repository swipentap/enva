using System;
using System.Linq;
using System.Threading;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Actions;

public class StartDockerServiceAction : BaseAction, IAction
{
    public StartDockerServiceAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "docker service start";
    }

    public bool Execute()
    {
        if (SSHService == null || Cfg == null)
        {
            Logger.GetLogger("start_docker_service").Printf("SSH service or config not initialized");
            return false;
        }
        Logger.GetLogger("start_docker_service").Printf("Ensuring Docker service is running...");
        string isActiveCmd = CLI.SystemCtl.NewSystemCtl().Service("docker").IsActive();
        var (currentStatus, currentExit) = SSHService.Execute(isActiveCmd, null);
        if (currentExit.HasValue && currentExit.Value == 0 && CLI.SystemCtl.ParseIsActive(currentStatus))
        {
            Logger.GetLogger("start_docker_service").Printf("Docker service already active, skipping start");
            return true;
        }
        Logger.GetLogger("start_docker_service").Printf("Docker service not active, starting socket and triggering activation...");
        string enableCmd = CLI.SystemCtl.NewSystemCtl().Service("docker.socket").Enable();
        string startCmd = CLI.SystemCtl.NewSystemCtl().Service("docker.socket").Start();
        SSHService.Execute(enableCmd, null);
        var (socketOutput, socketExit) = SSHService.Execute(startCmd, null);
        if (!socketExit.HasValue || socketExit.Value != 0)
        {
            Logger.GetLogger("start_docker_service").Printf("Failed to start docker.socket with exit code {0}", socketExit?.ToString() ?? "null");
            int outputLen = socketOutput.Length;
            int start = outputLen > 500 ? outputLen - 500 : 0;
            Logger.GetLogger("start_docker_service").Printf("docker.socket start output: {0}", socketOutput.Substring(start));
            return false;
        }
        Thread.Sleep(2000);
        string enableDockerCmd = CLI.SystemCtl.NewSystemCtl().Service("docker").Enable();
        SSHService.Execute(enableDockerCmd, null);
        Logger.GetLogger("start_docker_service").Printf("Triggering Docker service via socket activation...");
        string triggerCmd = "docker version  || true";
        SSHService.Execute(triggerCmd, null);
        Thread.Sleep(3000);
        var (status, exitCode) = SSHService.Execute(isActiveCmd, null);
        if (exitCode.HasValue && exitCode.Value == 0 && CLI.SystemCtl.ParseIsActive(status))
        {
            Logger.GetLogger("start_docker_service").Printf("Docker service is running");
            return true;
        }
        Logger.GetLogger("start_docker_service").Printf("Docker service failed to start via socket activation");
        string statusCmd = CLI.SystemCtl.NewSystemCtl().Service("docker").Status();
        var (statusOutput, _) = SSHService.Execute(statusCmd, null);
        Logger.GetLogger("start_docker_service").Printf("Docker service status:\n{0}", statusOutput);
        string journalCmd = "journalctl -u docker.service -n 50 --no-pager";
        var (journalOutput, _) = SSHService.Execute(journalCmd, null);
        Logger.GetLogger("start_docker_service").Printf("Docker service journal logs:\n{0}", journalOutput);
        return false;
    }
}

public static class StartDockerServiceActionFactory
{
    public static IAction NewStartDockerServiceAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new StartDockerServiceAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
