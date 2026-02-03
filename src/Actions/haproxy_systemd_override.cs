using System;
using System.Linq;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Actions;

public class ConfigureHaproxySystemdAction : BaseAction, IAction
{
    public ConfigureHaproxySystemdAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "haproxy systemd override";
    }

    public bool Execute()
    {
        if (SSHService == null)
        {
            Logger.GetLogger("configure_haproxy_systemd").Printf("SSH service not initialized");
            return false;
        }
        string mkdirCmd = CLI.Files.NewFileOps().Mkdir("/etc/systemd/system/haproxy.service.d", true).ToCommand();
        var (output, exitCode) = SSHService.Execute(mkdirCmd, null, true);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("configure_haproxy_systemd").Printf("create haproxy systemd override directory failed with exit code {0}", exitCode?.ToString() ?? "null");
            if (!string.IsNullOrEmpty(output))
            {
                var lines = output.Split('\n');
                if (lines.Length > 0)
                {
                    Logger.GetLogger("configure_haproxy_systemd").Printf("create haproxy systemd override directory output: {0}", lines[lines.Length - 1]);
                }
            }
            return false;
        }
        string overrideContent = @"[Service]
Type=notify
PrivateNetwork=no
ProtectSystem=no
ProtectHome=no
ExecStart=
ExecStart=/usr/sbin/haproxy -Ws -f $CONFIG -p $PIDFILE $EXTRAOPTS
";
        string overrideCmd = CLI.Files.NewFileOps().Write("/etc/systemd/system/haproxy.service.d/override.conf", overrideContent).ToCommand();
        (output, exitCode) = SSHService.Execute(overrideCmd, null, true);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("configure_haproxy_systemd").Printf("write haproxy systemd override failed with exit code {0}", exitCode?.ToString() ?? "null");
            if (!string.IsNullOrEmpty(output))
            {
                var lines = output.Split('\n');
                if (lines.Length > 0)
                {
                    Logger.GetLogger("configure_haproxy_systemd").Printf("write haproxy systemd override output: {0}", lines[lines.Length - 1]);
                }
            }
            return false;
        }
        string reloadCmd = "systemctl daemon-reload";
        (output, exitCode) = SSHService.Execute(reloadCmd, null, true);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("configure_haproxy_systemd").Printf("reload systemd daemon failed with exit code {0}", exitCode?.ToString() ?? "null");
            if (!string.IsNullOrEmpty(output))
            {
                var lines = output.Split('\n');
                if (lines.Length > 0)
                {
                    Logger.GetLogger("configure_haproxy_systemd").Printf("reload systemd daemon output: {0}", lines[lines.Length - 1]);
                }
            }
            return false;
        }
        return true;
    }
}

public static class ConfigureHaproxySystemdActionFactory
{
    public static IAction NewConfigureHaproxySystemdAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new ConfigureHaproxySystemdAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
