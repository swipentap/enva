using System;
using System.Linq;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Actions;

public class ConfigureDockerSysctlAction : BaseAction, IAction
{
    public ConfigureDockerSysctlAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "docker sysctl configuration";
    }

    public bool Execute()
    {
        if (SSHService == null)
        {
            Logger.GetLogger("configure_docker_sysctl").Printf("SSH service not initialized");
            return false;
        }
        string command = "echo 'net.bridge.bridge-nf-call-iptables = 1' | sudo tee /etc/sysctl.d/99-docker.conf && sudo sysctl -p /etc/sysctl.d/99-docker.conf || true";
        var (output, exitCode) = SSHService.Execute(command, null, true);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("configure_docker_sysctl").Printf("docker sysctl configuration failed with exit code {0}", exitCode?.ToString() ?? "null");
            if (!string.IsNullOrEmpty(output))
            {
                var lines = output.Split('\n');
                if (lines.Length > 0)
                {
                    Logger.GetLogger("configure_docker_sysctl").Printf("docker sysctl configuration output: {0}", lines[lines.Length - 1]);
                }
            }
            return false;
        }
        return true;
    }
}

public static class ConfigureDockerSysctlActionFactory
{
    public static IAction NewConfigureDockerSysctlAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new ConfigureDockerSysctlAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
