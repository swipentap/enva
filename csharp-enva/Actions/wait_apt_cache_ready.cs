using System;
using System.Linq;
using System.Threading;
using Enva.Libs;
using Enva.Services;

namespace Enva.Actions;

public class WaitAptCacheReadyAction : BaseAction, IAction
{
    public WaitAptCacheReadyAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "wait apt cache ready";
    }

    public bool Execute()
    {
        if (Cfg == null || ContainerCfg == null)
        {
            Logger.GetLogger("wait_apt_cache_ready").Printf("Configuration not initialized");
            return false;
        }

        Logger.GetLogger("wait_apt_cache_ready").Printf("Verifying apt-cache service is ready...");
        int maxAttempts = 20;
        string lxcHost = Cfg.LXCHost();
        int aptCachePort = Cfg.APTCachePort();

        LXCService lxcService = new LXCService(lxcHost, Cfg.SSH);
        if (!lxcService.Connect())
        {
            Logger.GetLogger("wait_apt_cache_ready").Printf("Failed to connect to LXC host for apt-cache verification");
            return false;
        }
        try
        {
            PCTService pctService = new PCTService(lxcService);
            for (int attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var (serviceCheck, _) = pctService.Execute(ContainerCfg.ID, "systemctl is-active apt-cacher-ng || echo 'inactive'", 10);
                if (serviceCheck.Contains("active"))
                {
                    string portCheckCmd = $"nc -z localhost {aptCachePort} && echo 'port_open' || echo 'port_closed'";
                    var (portCheck, _) = pctService.Execute(ContainerCfg.ID, portCheckCmd, 10);
                    if (portCheck.Contains("port_open"))
                    {
                        string testCmd = $"timeout 10 wget -qO- 'http://127.0.0.1:{aptCachePort}/acng-report.html' | grep -q 'Apt-Cacher NG' && echo 'working' || echo 'not_working'";
                        var (functionalityTest, _) = pctService.Execute(ContainerCfg.ID, testCmd, 15);
                        if (functionalityTest.Contains("working"))
                        {
                            if (ContainerCfg.IPAddress != null)
                            {
                                Logger.GetLogger("wait_apt_cache_ready").Printf("apt-cache service is ready on {0}:{1}", ContainerCfg.IPAddress, aptCachePort);
                            }
                            return true;
                        }
                        else if (attempt < maxAttempts)
                        {
                            Logger.GetLogger("wait_apt_cache_ready").Printf("apt-cache service not fully ready yet (attempt {0}/{1}), waiting...", attempt, maxAttempts);
                            Thread.Sleep(2000);
                            continue;
                        }
                    }
                }
                else
                {
                    if (attempt == 1)
                    {
                        string startCmd = "systemctl start apt-cacher-ng";
                        var (startOutput, _) = pctService.Execute(ContainerCfg.ID, startCmd, 10);
                        if (!string.IsNullOrEmpty(startOutput))
                        {
                            Logger.GetLogger("wait_apt_cache_ready").Printf("Service start attempt output: {0}", startOutput);
                        }
                        string statusCmd = "systemctl status apt-cacher-ng --no-pager -l | head -15";
                        var (statusOutput, _) = pctService.Execute(ContainerCfg.ID, statusCmd, 10);
                        if (!string.IsNullOrEmpty(statusOutput))
                        {
                            Logger.GetLogger("wait_apt_cache_ready").Printf("Service status: {0}", statusOutput);
                        }
                    }
                }
                if (attempt < maxAttempts)
                {
                    Logger.GetLogger("wait_apt_cache_ready").Printf("Waiting for apt-cache service... ({0}/{1})", attempt, maxAttempts);
                    Thread.Sleep(3000);
                }
                else
                {
                    string statusCmd = "systemctl status apt-cacher-ng --no-pager -l";
                    var (statusOutput, _) = pctService.Execute(ContainerCfg.ID, statusCmd, 10);
                    string journalCmd = "journalctl -u apt-cacher-ng --no-pager -n 30";
                    var (journalOutput, _) = pctService.Execute(ContainerCfg.ID, journalCmd, 10);
                    string errorMsg = "apt-cache service did not become ready in time";
                    if (!string.IsNullOrEmpty(statusOutput))
                    {
                        errorMsg += $"\nService status: {statusOutput}";
                    }
                    if (!string.IsNullOrEmpty(journalOutput))
                    {
                        errorMsg += $"\nService logs: {journalOutput}";
                    }
                    Logger.GetLogger("wait_apt_cache_ready").Printf(errorMsg);
                    return false;
                }
            }
        }
        finally
        {
            lxcService.Disconnect();
        }
        return false;
    }
}

public static class WaitAptCacheReadyActionFactory
{
    public static IAction NewWaitAptCacheReadyAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new WaitAptCacheReadyAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
