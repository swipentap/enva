using System;
using System.Linq;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Actions;

public class ConfigureAptProxyAction : BaseAction, IAction
{
    public ConfigureAptProxyAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "apt cache proxy configuration";
    }

    public bool Execute()
    {
        if (PCTService == null || Cfg == null || string.IsNullOrEmpty(ContainerID))
        {
            Logger.GetLogger("configure_apt_proxy").Printf("PCT service, config, or container ID not available");
            return false;
        }
        ContainerConfig? aptCacheContainer = null;
        foreach (var container in Cfg.Containers)
        {
            if (container.Name == Cfg.APTCacheCT)
            {
                aptCacheContainer = container;
                break;
            }
        }
        if (aptCacheContainer == null)
        {
            return true;
        }
        if (aptCacheContainer.IPAddress == null)
        {
            return false;
        }
        string aptCacheIP = aptCacheContainer.IPAddress;
        int aptCachePort = Cfg.APTCachePort();
        string proxyContent = $"Acquire::http::Proxy \"http://{aptCacheIP}:{aptCachePort}\";\n";
        string proxyCmd = CLI.Files.NewFileOps().Write("/etc/apt/apt.conf.d/01proxy", proxyContent).ToCommand();
        if (!int.TryParse(ContainerID, out int containerIDInt))
        {
            Logger.GetLogger("configure_apt_proxy").Printf("Invalid container ID: {0}", ContainerID);
            return false;
        }
        var (output, exitCode) = PCTService.Execute(containerIDInt, proxyCmd, null);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("configure_apt_proxy").Printf("Failed to configure apt cache proxy: {0}", output);
            return false;
        }
        return true;
    }
}

public static class ConfigureAptProxyActionFactory
{
    public static IAction NewConfigureAptProxyAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new ConfigureAptProxyAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
