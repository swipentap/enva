using System;
using System.Linq;
using System.Threading;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Actions;

public class EnableSinsServiceAction : BaseAction, IAction
{
    public EnableSinsServiceAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "sins dns service enablement";
    }

    public bool Execute()
    {
        if (SSHService == null)
        {
            Logger.GetLogger("enable_sins_service").Printf("SSH service not initialized");
            return false;
        }
        var logger = Logger.GetLogger("enable_sins_service");

        logger.Printf("Checking what's using port 53...");
        string portCheckCmd = "ss -tulnp | grep ':53' || echo 'port_free'";
        var (portOutput, _) = SSHService.Execute(portCheckCmd, null, true);
        if (!portOutput.Contains("port_free"))
        {
            logger.Printf("Port 53 is in use: {0}", portOutput);
            string resolvedCheckCmd = CLI.SystemCtl.NewSystemCtl().Service("systemd-resolved").IsActive();
            var (resolvedStatus, _) = SSHService.Execute(resolvedCheckCmd, null, true);
            if (CLI.SystemCtl.ParseIsActive(resolvedStatus))
            {
                logger.Printf("systemd-resolved is still active, stopping it...");
                string stopResolvedCmd = CLI.SystemCtl.NewSystemCtl().Service("systemd-resolved").Stop();
                SSHService.Execute(stopResolvedCmd, null, true);
                string disableResolvedCmd = CLI.SystemCtl.NewSystemCtl().Service("systemd-resolved").Disable();
                SSHService.Execute(disableResolvedCmd, null, true);
                Thread.Sleep(2000);
            }
            string killPort53Cmd = "fuser -k 53/udp 53/tcp || true";
            SSHService.Execute(killPort53Cmd, null, true);
            Thread.Sleep(1000);
        }

        logger.Printf("Enabling and starting SiNS service...");
        string enableCmd = CLI.SystemCtl.NewSystemCtl().Service("sins").Enable();
        string startCmd = CLI.SystemCtl.NewSystemCtl().Service("sins").Start();
        SSHService.Execute(enableCmd, null, true);
        var (output, exitCode) = SSHService.Execute(startCmd, null, true);
        Thread.Sleep(5000);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            string statusCmd2 = CLI.SystemCtl.NewSystemCtl().Service("sins").Status();
            var (statusOutput, _) = SSHService.Execute(statusCmd2, null, true);
            if (statusOutput.Contains("activating (auto-restart)") || statusOutput.Contains("auto-restart"))
            {
                string journalCmdInner = "journalctl -u sins.service -n 10 --no-pager | grep -i 'postgres\\|connection refused' || true";
                var (journalOutputInner, _) = SSHService.Execute(journalCmdInner, null, true);
                if (journalOutputInner.ToLower().Contains("postgres") || journalOutputInner.ToLower().Contains("connection refused"))
                {
                    logger.Printf("SiNS service is restarting due to PostgreSQL connection failure. This is expected if PostgreSQL is not yet available. Service will retry automatically.");
                    return true;
                }
                string journalCmd2 = "journalctl -u sins.service -n 10 --no-pager | grep -i 'address already in use\\|port.*53' || true";
                var (journalOutput2, _) = SSHService.Execute(journalCmd2, null, true);
                if (journalOutput2.ToLower().Contains("address already in use") || journalOutput2.ToLower().Contains("port"))
                {
                    logger.Printf("SiNS service cannot bind to port 53 - port is already in use. Ensure systemd-resolved is disabled.");
                    return false;
                }
            }
            logger.Printf("Failed to start SiNS service: {0}", output);
            logger.Printf("Service status:\n{0}", statusOutput);
            string journalCmd = "journalctl -u sins.service -n 50 --no-pager";
            var (journalOutput, _) = SSHService.Execute(journalCmd, null, true);
            logger.Printf("Service journal logs:\n{0}", journalOutput);
            return false;
        }
        string statusCmd = CLI.SystemCtl.NewSystemCtl().Service("sins").IsActive();
        var (status, exitCode2) = SSHService.Execute(statusCmd, null, true);
        if (exitCode2.HasValue && exitCode2.Value == 0 && CLI.SystemCtl.ParseIsActive(status))
        {
            string portCheckCmd2 = "ss -tulnp | grep -E ':53.*sins|:53.*dotnet' || echo 'not_listening'";
            var (portOutput2, _) = SSHService.Execute(portCheckCmd2, null, true);
            if (!portOutput2.Contains("not_listening") && portOutput2.Contains(":53"))
            {
                logger.Printf("SiNS DNS server is running and listening on port 53");
                return true;
            }
            logger.Printf("SiNS service is active but not listening on port 53");
            string statusCmd2 = CLI.SystemCtl.NewSystemCtl().Service("sins").Status();
            var (statusOutput2, _) = SSHService.Execute(statusCmd2, null, true);
            if (statusOutput2.Contains("activating (auto-restart)") || statusOutput2.Contains("auto-restart"))
            {
                string journalCmd = "journalctl -u sins.service -n 20 --no-pager | grep -i 'postgres\\|connection refused' || true";
                var (journalOutput, _) = SSHService.Execute(journalCmd, null, true);
                if (journalOutput.ToLower().Contains("postgres") || journalOutput.ToLower().Contains("connection refused"))
                {
                    logger.Printf("SiNS service is restarting due to PostgreSQL connection failure. This is expected if PostgreSQL is not yet available. Service will retry automatically.");
                    return true;
                }
                string journalCmd2 = "journalctl -u sins.service -n 20 --no-pager | grep -i 'address already in use\\|port.*53\\|bind.*53' || true";
                var (journalOutput2, _) = SSHService.Execute(journalCmd2, null, true);
                if (journalOutput2.ToLower().Contains("address already in use") || journalOutput2.ToLower().Contains("port") || journalOutput2.ToLower().Contains("bind"))
                {
                    logger.Printf("SiNS service cannot bind to port 53. Attempting to fix...");
                    string killPort53Cmd = "fuser -k 53/udp 53/tcp || true";
                    SSHService.Execute(killPort53Cmd, null, true);
                    Thread.Sleep(2000);
                    string restartCmd = CLI.SystemCtl.NewSystemCtl().Service("sins").Restart();
                    SSHService.Execute(restartCmd, null, true);
                    Thread.Sleep(5000);
                    string portCheckCmd3 = "ss -tulnp | grep -E ':53.*sins|:53.*dotnet' || echo 'not_listening'";
                    var (portOutput3, _) = SSHService.Execute(portCheckCmd3, null, true);
                    if (!portOutput3.Contains("not_listening") && portOutput3.Contains(":53"))
                    {
                        logger.Printf("SiNS DNS server is now listening on port 53 after restart");
                        return true;
                    }
                    logger.Printf("SiNS service cannot bind to port 53 - port is already in use. Ensure systemd-resolved is disabled.");
                    return false;
                }
            }
            logger.Printf("Diagnosing why SiNS is not listening on port 53...");
            string journalCmd3 = "journalctl -u sins.service -n 50 --no-pager";
            var (journalOutput3, _) = SSHService.Execute(journalCmd3, null, true);
            logger.Printf("Service journal logs:\n{0}", journalOutput3);

            string configCheckCmd = "test -f /etc/sins/appsettings.json && echo 'exists' || test -f /opt/sins/appsettings.json && echo 'exists' || test -f /opt/sins/app/appsettings.json && echo 'exists' || echo 'missing'";
            var (configOutput, _) = SSHService.Execute(configCheckCmd, null, true);
            if (configOutput.Contains("missing"))
            {
                logger.Printf("ERROR: SiNS appsettings.json is missing. Service cannot start properly.");
                return false;
            }

            logger.Printf("Attempting to restart SiNS service...");
            string restartCmd2 = CLI.SystemCtl.NewSystemCtl().Service("sins").Restart();
            SSHService.Execute(restartCmd2, null, true);
            Thread.Sleep(5000);

            string portCheckCmd4 = "ss -tulnp | grep -E ':53.*sins|:53.*dotnet' || echo 'not_listening'";
            var (portOutput4, _) = SSHService.Execute(portCheckCmd4, null, true);
            if (!portOutput4.Contains("not_listening") && portOutput4.Contains(":53"))
            {
                logger.Printf("SiNS DNS server is now listening on port 53 after restart");
                return true;
            }

            string portCheckCmd5 = "ss -tulnp | grep ':53' || echo 'port_free'";
            var (portOutput5, _) = SSHService.Execute(portCheckCmd5, null, true);
            logger.Printf("Port 53 status: {0}", portOutput5);

            return false;
        }
        string statusCmd3 = CLI.SystemCtl.NewSystemCtl().Service("sins").Status();
        var (statusOutput3, _) = SSHService.Execute(statusCmd3, null, true);
        if (statusOutput3.Contains("activating (auto-restart)") || statusOutput3.Contains("auto-restart"))
        {
            string journalCmd = "journalctl -u sins.service -n 10 --no-pager | grep -i 'postgres\\|connection refused' || true";
            var (journalOutput, _) = SSHService.Execute(journalCmd, null, true);
            if (journalOutput.ToLower().Contains("postgres") || journalOutput.ToLower().Contains("connection refused"))
            {
                logger.Printf("SiNS service is restarting due to PostgreSQL connection failure. This is expected if PostgreSQL is not yet available. Service will retry automatically.");
                return true;
            }
            string journalCmd2 = "journalctl -u sins.service -n 10 --no-pager | grep -i 'address already in use\\|port.*53' || true";
            var (journalOutput2, _) = SSHService.Execute(journalCmd2, null, true);
            if (journalOutput2.ToLower().Contains("address already in use") || journalOutput2.ToLower().Contains("port"))
            {
                logger.Printf("SiNS service cannot bind to port 53 - port is already in use. Ensure systemd-resolved is disabled.");
                return false;
            }
        }
        string processCheckCmd = "pgrep -f 'sins\\.dll|^sins '  && echo running || echo not_running";
        var (processOutput, _) = SSHService.Execute(processCheckCmd, null, true);
        if (processOutput.Contains("running"))
        {
            string portCheckCmd6 = "ss -tulnp | grep -E ':53.*sins|:53.*dotnet' || echo 'not_listening'";
            var (portOutput6, _) = SSHService.Execute(portCheckCmd6, null, true);
            if (!portOutput6.Contains("not_listening") && portOutput6.Contains(":53"))
            {
                logger.Printf("SiNS process is running and listening on port 53");
                return true;
            }
            logger.Printf("SiNS process is running but not listening on port 53");
            return false;
        }
        logger.Printf("SiNS DNS server is not running");
        return false;
    }
}

public static class EnableSinsServiceActionFactory
{
    public static IAction NewEnableSinsServiceAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new EnableSinsServiceAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
