using System;
using System.Collections.Generic;
using System.Linq;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Actions;

public class ConfigureCachePortAction : BaseAction, IAction
{
    public ConfigureCachePortAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "apt-cacher-ng port configuration";
    }

    public bool Execute()
    {
        if (SSHService == null || Cfg == null)
        {
            Logger.GetLogger("configure_cache_port").Printf("SSH service or config not initialized");
            return false;
        }
        int port = Cfg.APTCachePort();
        string configFile = "/etc/apt-cacher-ng/acng.conf";
        string configDir = "/etc/apt-cacher-ng";

        string mkdirCmd = CLI.Files.NewFileOps().Mkdir(configDir, true).ToCommand();
        var (mkdirOutput, mkdirExitCode) = SSHService.Execute(mkdirCmd, null, true);
        if (mkdirExitCode.HasValue && mkdirExitCode.Value != 0)
        {
            Logger.GetLogger("configure_cache_port").Printf("Failed to create directory {0}: {1}", configDir, mkdirOutput);
        }

        Logger.GetLogger("configure_cache_port").Printf("Configuring apt-cacher-ng timeout and retry settings...");
        var timeoutSettings = new Dictionary<string, string>
        {
            { "DlMaxRetries", "5" },
            { "NetworkTimeout", "120" },
            { "DisconnectTimeout", "30" }
        };
        foreach (var kvp in timeoutSettings)
        {
            string checkCmd = $"grep -E '^#?{kvp.Key}:' {configFile} || echo 'not_found'";
            var (checkOutput, _) = SSHService.Execute(checkCmd, null, true);
            if (checkOutput.Contains("not_found"))
            {
                string appendCmd = CLI.Files.NewFileOps().Append().Write(configFile, $"{kvp.Key}: {kvp.Value}\n").ToCommand();
                var (timeoutOutput, timeoutExitCode) = SSHService.Execute(appendCmd, null, true);
                if (timeoutExitCode.HasValue && timeoutExitCode.Value != 0)
                {
                    Logger.GetLogger("configure_cache_port").Printf("Failed to add {0}: {1}", kvp.Key, timeoutOutput);
                }
            }
            else
            {
                string replaceCmd = CLI.Sed.NewSed().Flags("").Replace(configFile, $"^#?{kvp.Key}:.*", $"{kvp.Key}: {kvp.Value}");
                var (timeoutOutput, timeoutExitCode) = SSHService.Execute(replaceCmd, null, true);
                if (timeoutExitCode.HasValue && timeoutExitCode.Value != 0)
                {
                    Logger.GetLogger("configure_cache_port").Printf("Failed to update {0}: {1}", kvp.Key, timeoutOutput);
                }
            }
        }

        // Configure Port AFTER timeout settings to ensure it's the last thing added before restart
        Logger.GetLogger("configure_cache_port").Printf("Configuring apt-cacher-ng port to {0}...", port);
        // Remove any existing Port lines first to avoid duplicates
        string removePortCmd = $"sed -i '/^#\\?Port:/d' {configFile}";
        SSHService.Execute(removePortCmd, null, true);
        
        // Add Port configuration
        string appendPortCmd = CLI.Files.NewFileOps().Append().Write(configFile, $"Port: {port}\n").ToCommand();
        var (portOutput, portExitCode) = SSHService.Execute(appendPortCmd, null, true);
        if (!portExitCode.HasValue || portExitCode.Value != 0)
        {
            Logger.GetLogger("configure_cache_port").Printf("append apt-cacher-ng port failed with exit code {0}", portExitCode?.ToString() ?? "null");
            if (!string.IsNullOrEmpty(portOutput))
            {
                Logger.GetLogger("configure_cache_port").Printf("append output: {0}", portOutput);
            }
            return false;
        }
        
        // Verify Port was added
        string portCheckCmd = $"grep -E '^Port: {port}' {configFile} || echo 'not_found'";
        var (portCheckOutput, _) = SSHService.Execute(portCheckCmd, null, true);
        if (portCheckOutput.Contains("not_found"))
        {
            Logger.GetLogger("configure_cache_port").Printf("Port configuration failed: Port {0} not found in config file after append", port);
            return false;
        }

        if (port < 1024)
        {
            Logger.GetLogger("configure_cache_port").Printf("Port {0} requires privileged access, setting CAP_NET_BIND_SERVICE capability...", port);
            string setcapCmd = "setcap 'cap_net_bind_service=+ep' /usr/sbin/apt-cacher-ng";
            var (setcapOutput, setcapExitCode) = SSHService.Execute(setcapCmd, null, true);
            if (!setcapExitCode.HasValue || setcapExitCode.Value != 0)
            {
                Logger.GetLogger("configure_cache_port").Printf("Failed to set capability: {0}", setcapOutput);
                return false;
            }
            Logger.GetLogger("configure_cache_port").Printf("Successfully executed setcap command");

            string verifyCmd = "getcap /usr/sbin/apt-cacher-ng";
            var (verifyOutput, verifyExitCode) = SSHService.Execute(verifyCmd, null, true);
            if (verifyExitCode.HasValue && verifyExitCode.Value == 0)
            {
                if (verifyOutput.Contains("cap_net_bind_service"))
                {
                    Logger.GetLogger("configure_cache_port").Printf("Verified CAP_NET_BIND_SERVICE capability is set: {0}", verifyOutput.Trim());
                }
                else
                {
                    Logger.GetLogger("configure_cache_port").Printf("Capability verification failed - capability not found in output: {0}", verifyOutput);
                    return false;
                }
            }
            else
            {
                Logger.GetLogger("configure_cache_port").Printf("Could not verify capability (getcap failed or returned empty): {0}", verifyOutput);
            }
        }

        Logger.GetLogger("configure_cache_port").Printf("Restarting apt-cacher-ng service to apply configuration changes...");
        string restartCmd = CLI.SystemCtl.NewSystemCtl().Service("apt-cacher-ng").Restart();
        var (restartOutput, restartExitCode) = SSHService.Execute(restartCmd, null, true);
        if (!restartExitCode.HasValue || restartExitCode.Value != 0)
        {
            Logger.GetLogger("configure_cache_port").Printf("restart apt-cacher-ng service failed with exit code {0}", restartExitCode?.ToString() ?? "null");
            if (!string.IsNullOrEmpty(restartOutput))
            {
                var lines = restartOutput.Split('\n');
                if (lines.Length > 0)
                {
                    Logger.GetLogger("configure_cache_port").Printf("restart output: {0}", lines[lines.Length - 1]);
                }
            }
            return false;
        }

        System.Threading.Thread.Sleep(3000);

        string isActiveCmd = CLI.SystemCtl.NewSystemCtl().Service("apt-cacher-ng").IsActive();
        var (status, statusExitCode) = SSHService.Execute(isActiveCmd, null, true);
        if (!statusExitCode.HasValue || statusExitCode.Value != 0 || !CLI.SystemCtl.ParseIsActive(status))
        {
            Logger.GetLogger("configure_cache_port").Printf("apt-cacher-ng service is not active after restart");
            return false;
        }

        // Verify Port is still in config file after restart
        var (portCheckAfterRestart, _) = SSHService.Execute(portCheckCmd, null, true);
        if (portCheckAfterRestart.Contains("not_found"))
        {
            Logger.GetLogger("configure_cache_port").Printf("Port {0} not found in config file after restart", port);
            return false;
        }

        // Verify apt-cacher-ng is actually listening on the configured port
        string verifyPortCmd = $"lsof -i -P -n 2>/dev/null | grep apt-cacher-ng | grep ':{port} ' || echo 'not_listening'";
        var (portListenOutput, _) = SSHService.Execute(verifyPortCmd, null, true);
        if (portListenOutput.Contains("not_listening"))
        {
            Logger.GetLogger("configure_cache_port").Printf("apt-cacher-ng is not listening on port {0} after restart", port);
            return false;
        }

        return true;
    }
}

public static class ConfigureCachePortActionFactory
{
    public static IAction NewConfigureCachePortAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new ConfigureCachePortAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
