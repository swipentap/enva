using System;
using System.Collections.Generic;
using System.Linq;
using Enva.Libs;
using Enva.Services;

namespace Enva.Actions;

public class InstallDnsdistAction : BaseAction, IAction
{
    public InstallDnsdistAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "install dnsdist";
    }

    public bool Execute()
    {
        if (Cfg == null)
        {
            Logger.GetLogger("install_dnsdist").Printf("Lab configuration is missing");
            return false;
        }

        var logger = Logger.GetLogger("install_dnsdist");
        logger.Printf("Installing and configuring dnsdist on HAProxy host for DNS forwarding to SiNS worker NodePorts...");

        ContainerConfig? haproxyContainer = Cfg.Containers.FirstOrDefault(ct => ct.Name == "haproxy");
        if (haproxyContainer == null || string.IsNullOrEmpty(haproxyContainer.IPAddress))
        {
            logger.Printf("HAProxy container not found in configuration");
            return false;
        }

        string defaultUser = Cfg.Users.DefaultUser();
        SSHConfig haproxySSHConfig = new SSHConfig
        {
            ConnectTimeout = Cfg.SSH.ConnectTimeout,
            BatchMode = Cfg.SSH.BatchMode,
            DefaultExecTimeout = Cfg.SSH.DefaultExecTimeout,
            ReadBufferSize = Cfg.SSH.ReadBufferSize,
            PollInterval = Cfg.SSH.PollInterval,
            DefaultUsername = defaultUser,
            LookForKeys = Cfg.SSH.LookForKeys,
            AllowAgent = Cfg.SSH.AllowAgent,
            Verbose = Cfg.SSH.Verbose
        };
        SSHService haproxySSHService = new SSHService($"{defaultUser}@{haproxyContainer.IPAddress}", haproxySSHConfig);
        if (!haproxySSHService.Connect())
        {
            logger.Printf("Failed to connect to haproxy container {0} via SSH", haproxyContainer.IPAddress);
            return false;
        }

        try
        {
            const int sinsDnsUdpPort = 31757;

            List<ContainerConfig> workerNodes = new List<ContainerConfig>();
            foreach (var ct in Cfg.Containers)
            {
                if (ct.Name == "k3s-worker-1" || ct.Name == "k3s-worker-2" || ct.Name == "k3s-worker-3")
                {
                    if (!string.IsNullOrEmpty(ct.IPAddress))
                        workerNodes.Add(ct);
                }
            }
            ContainerConfig? controlNode = Cfg.Containers.FirstOrDefault(ct => ct.Name == "k3s-control");
            if (controlNode != null && !string.IsNullOrEmpty(controlNode.IPAddress))
                workerNodes.Add(controlNode);

            if (workerNodes.Count == 0)
            {
                logger.Printf("Warning: No k3s nodes found for dnsdist backends");
                haproxySSHService.Disconnect();
                return true;
            }

            string disableResolvedCmd = "systemctl stop systemd-resolved 2>/dev/null; systemctl disable systemd-resolved 2>/dev/null; echo done";
            haproxySSHService.Execute(disableResolvedCmd, 30, true);

            haproxySSHService.Execute("systemctl stop dns-udp-forwarder.service 2>/dev/null; systemctl disable dns-udp-forwarder.service 2>/dev/null; rm -f /etc/systemd/system/dns-udp-forwarder.service; systemctl daemon-reload", 30, true);
            haproxySSHService.Execute("apt-get remove -y socat 2>/dev/null || true", 60, true);

            (string dnsdistCheck, int? dnsdistExitCode) = haproxySSHService.Execute("command -v dnsdist", 30);
            if (!dnsdistExitCode.HasValue || dnsdistExitCode.Value != 0)
            {
                logger.Printf("Installing dnsdist...");
                (string installOutput, int? installExit) = haproxySSHService.Execute("apt-get update && apt-get install -y dnsdist", 120, true);
                if (installExit.HasValue && installExit.Value != 0)
                {
                    logger.Printf("Failed to install dnsdist: {0}", installOutput);
                    return false;
                }
            }

            var dnsdistServerLines = new List<string>();
            for (int i = 0; i < workerNodes.Count; i++)
            {
                var server = workerNodes[i];
                dnsdistServerLines.Add($"newServer({{address=\"{server.IPAddress}:{sinsDnsUdpPort}\", name=\"worker-{i + 1}\"}})");
            }
            string dnsdistConfigContent = "-- dnsdist configuration - forward DNS to worker node NodePorts (SiNS)\n"
                + "setSecurityPollSuffix(\"\")\n"
                + "setLocal(\"0.0.0.0:53\")\n"
                + string.Join("\n", dnsdistServerLines) + "\n";

            byte[] configBytes = System.Text.Encoding.UTF8.GetBytes(dnsdistConfigContent);
            string configB64 = Convert.ToBase64String(configBytes);
            string writeCmd = $"echo '{configB64}' | base64 -d > /tmp/dnsdist.conf && mv /tmp/dnsdist.conf /etc/dnsdist/dnsdist.conf";
            (string dnsdistWriteOutput, int? dnsdistWriteExit) = haproxySSHService.Execute(writeCmd, 30, true);
            if (dnsdistWriteExit.HasValue && dnsdistWriteExit.Value != 0)
            {
                logger.Printf("Failed to write dnsdist configuration: {0}", dnsdistWriteOutput);
                return false;
            }

            (string dnsdistEnableOutput, int? dnsdistEnableExit) = haproxySSHService.Execute("systemctl enable dnsdist && systemctl restart dnsdist", 30, true);
            if (dnsdistEnableExit.HasValue && dnsdistEnableExit.Value != 0)
            {
                logger.Printf("Failed to enable/start dnsdist: {0}", dnsdistEnableOutput);
                return false;
            }

            (string dnsdistStatusOutput, int? dnsdistStatusExit) = haproxySSHService.Execute("systemctl is-active dnsdist", 10, true);
            if (dnsdistStatusExit.HasValue && dnsdistStatusExit.Value != 0 || !dnsdistStatusOutput.Trim().Equals("active", StringComparison.OrdinalIgnoreCase))
            {
                logger.Printf("dnsdist is not active. Status: {0}", dnsdistStatusOutput);
                return false;
            }

            logger.Printf("dnsdist configured and started (backends: {0} nodes on port {1})", workerNodes.Count, sinsDnsUdpPort);
            return true;
        }
        finally
        {
            haproxySSHService.Disconnect();
        }
    }
}

public static class InstallDnsdistActionFactory
{
    public static IAction NewInstallDnsdistAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallDnsdistAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
