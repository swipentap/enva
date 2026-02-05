using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Enva.Libs;
using Enva.Services;

namespace Enva.Actions;

public class ConfigureLxcSysctlAccessAction : BaseAction, IAction
{
    public ConfigureLxcSysctlAccessAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "lxc sysctl access configuration";
    }

    public bool Execute()
    {
        if (PCTService == null || string.IsNullOrEmpty(ContainerID))
        {
            Logger.GetLogger("configure_lxc_sysctl_access").Printf("PCT service or container ID not available");
            return false;
        }
        Logger.GetLogger("configure_lxc_sysctl_access").Printf("Configuring LXC container for sysctl access...");
        string configFile = $"/etc/pve/lxc/{ContainerID}.conf";

        if (PCTService == null)
        {
            Logger.GetLogger("configure_lxc_sysctl_access").Printf("PCT service not available");
            return false;
        }
        ILXCService? lxcService = PCTService.GetLXCService();
        if (lxcService == null)
        {
            Logger.GetLogger("configure_lxc_sysctl_access").Printf("LXC service not available in PCT service");
            return false;
        }

        string checkCmd = $"grep -E '^lxc.(mount.auto|apparmor.profile|cap.drop|cgroup2.devices.allow)' {configFile} || echo 'not_found'";
        var (checkOutput, _) = lxcService.Execute(checkCmd, null);

        bool needsRestart = false;
        List<string> configsToAdd = new List<string>();

        if (!checkOutput.Contains("lxc.apparmor.profile"))
        {
            configsToAdd.Add("lxc.apparmor.profile: unconfined");
            needsRestart = true;
        }
        if (!checkOutput.Contains("lxc.cap.drop:"))
        {
            configsToAdd.Add("lxc.cap.drop:");
            needsRestart = true;
        }
        if (!checkOutput.Contains("lxc.mount.auto"))
        {
            configsToAdd.Add("lxc.mount.auto: proc:rw sys:rw");
            needsRestart = true;
        }
        if (!checkOutput.Contains("lxc.cgroup2.devices.allow"))
        {
            configsToAdd.Add("lxc.cgroup2.devices.allow: c 1:11 rwm");
            needsRestart = true;
        }

        if (configsToAdd.Count > 0)
        {
            Logger.GetLogger("configure_lxc_sysctl_access").Printf("Adding k3s LXC configuration requirements...");
            foreach (string configLine in configsToAdd)
            {
                string addCmd = $"echo '{configLine}' >> {configFile}";
                var (addOutput, addExit) = lxcService.Execute(addCmd, null);
                if (!addExit.HasValue || addExit.Value != 0)
                {
                    int outputLen = addOutput.Length;
                    int start = outputLen > 200 ? outputLen - 200 : 0;
                    Logger.GetLogger("configure_lxc_sysctl_access").Printf("Failed to add {0}: {1}", configLine, addOutput.Substring(start));
                    return false;
                }
                Logger.GetLogger("configure_lxc_sysctl_access").Printf("Added: {0}", configLine);
            }

            if (needsRestart)
            {
                Logger.GetLogger("configure_lxc_sysctl_access").Printf("Container needs to be restarted for k3s LXC configuration to take effect...");
                string restartCmd = $"pct stop {ContainerID} && sleep 2 && pct start {ContainerID}";
                var (restartOutput, restartExit) = lxcService.Execute(restartCmd, null);
                if (!restartExit.HasValue || restartExit.Value != 0)
                {
                    int outputLen = restartOutput.Length;
                    int start = outputLen > 200 ? outputLen - 200 : 0;
                    Logger.GetLogger("configure_lxc_sysctl_access").Printf("Container restart had issues: {0}", restartOutput.Substring(start));
                }
                else
                {
                    Logger.GetLogger("configure_lxc_sysctl_access").Printf("Container restarted to apply k3s LXC configuration");
                    Thread.Sleep(5000);
                }
            }
        }
        else
        {
            Logger.GetLogger("configure_lxc_sysctl_access").Printf("All k3s LXC configuration requirements already present");
        }

        Logger.GetLogger("configure_lxc_sysctl_access").Printf("Ensuring /dev/kmsg is configured as symlink to /dev/console (k3s LXC requirement)...");
        string kmsgCmd = $"pct exec {ContainerID} -- bash -c 'rm -f /dev/kmsg && ln -sf /dev/console /dev/kmsg && ls -l /dev/kmsg'";
        var (kmsgOutput, kmsgExit) = lxcService.Execute(kmsgCmd, null);
        if (!kmsgExit.HasValue || kmsgExit.Value != 0)
        {
            int outputLen = kmsgOutput.Length;
            int start = outputLen > 200 ? outputLen - 200 : 0;
            Logger.GetLogger("configure_lxc_sysctl_access").Printf("Failed to create /dev/kmsg symlink: {0}", kmsgOutput.Substring(start));
        }
        else
        {
            Logger.GetLogger("configure_lxc_sysctl_access").Printf("/dev/kmsg symlink configured successfully");
        }
        return true;
    }
}

public static class ConfigureLxcSysctlAccessActionFactory
{
    public static IAction NewConfigureLxcSysctlAccessAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new ConfigureLxcSysctlAccessAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
