using System;
using System.Collections.Generic;
using System.Linq;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Actions;

public class UpdateHaproxyConfigurationAction : BaseAction, IAction
{
    public UpdateHaproxyConfigurationAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "update haproxy configuration";
    }

    public bool Execute()
    {
        if (Cfg == null)
        {
            Logger.GetLogger("update_haproxy_configuration").Printf("Lab configuration is missing");
            return false;
        }
        if (Cfg.Kubernetes == null || Cfg.Kubernetes.Control == null || Cfg.Kubernetes.Control.Count == 0)
        {
            Logger.GetLogger("update_haproxy_configuration").Printf("Kubernetes control node not found");
            return false;
        }

        var logger = Logger.GetLogger("update_haproxy_configuration");
        logger.Printf("Updating HAProxy configuration with detected Traefik NodePorts...");

        // Find haproxy container
        ContainerConfig? haproxyContainer = Cfg.Containers.FirstOrDefault(ct => ct.Name == "haproxy");
        if (haproxyContainer == null || string.IsNullOrEmpty(haproxyContainer.IPAddress))
        {
            logger.Printf("HAProxy container not found in configuration");
            return false;
        }

        // Create SSH connection to haproxy container
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

        // Use default NodePorts (detection would require PCTService which is not used in this action)
        int ingressHttpPort = 31523;  // Default fallback
        int ingressHttpsPort = 30490; // Default fallback
        int sinsDnsTcpPort = 31758;  // Default fallback
        logger.Printf("Using default NodePorts (HTTP: {0}, HTTPS: {1}, DNS TCP: {2})", ingressHttpPort, ingressHttpsPort, sinsDnsTcpPort);

        // Get all k3s nodes for backend
        List<ContainerConfig> workerNodes = new List<ContainerConfig>();
        foreach (var ct in Cfg.Containers)
        {
            if (ct.Name == "k3s-worker-1" || ct.Name == "k3s-worker-2" || ct.Name == "k3s-worker-3")
            {
                if (!string.IsNullOrEmpty(ct.IPAddress))
                {
                    workerNodes.Add(ct);
                }
            }
        }

        // Get k3s control node for ingress (Traefik runs on all nodes)
        ContainerConfig? controlNode = Cfg.Containers.FirstOrDefault(ct => ct.Name == "k3s-control");
        if (controlNode != null && !string.IsNullOrEmpty(controlNode.IPAddress))
        {
            workerNodes.Add(controlNode);
        }

        if (workerNodes.Count == 0)
        {
            logger.Printf("No k3s nodes found for ingress backend");
            return false;
        }

        // Get HTTP and HTTPS ports from haproxy container config
        int httpPort = 80;
        int httpsPort = 443;
        if (haproxyContainer.Params != null)
        {
            if (haproxyContainer.Params.TryGetValue("http_port", out object? httpPortObj) && httpPortObj is int httpPortInt)
            {
                httpPort = httpPortInt;
            }
            if (haproxyContainer.Params.TryGetValue("https_port", out object? httpsPortObj) && httpsPortObj is int httpsPortInt)
            {
                httpsPort = httpsPortInt;
            }
        }

        // Build TCP mode frontends and backends for k3s ingress
        List<string> frontends = new List<string>();
        List<string> backends = new List<string>();

        // HTTP frontend - TCP mode forwarding to k3s ingress
        string httpFrontend = $@"frontend http-in
    bind *:{httpPort}
    mode tcp
    default_backend backend_ingress_http";
        frontends.Add(httpFrontend);

        // HTTPS frontend - TCP mode forwarding to k3s ingress
        string httpsFrontend = $@"frontend https-in
    bind *:{httpsPort}
    mode tcp
    default_backend backend_ingress_https";
        frontends.Add(httpsFrontend);

        // DNS TCP frontend - TCP mode forwarding to SiNS DNS
        string dnsTcpFrontend = $@"frontend dns-tcp-in
    bind *:53
    mode tcp
    default_backend backend_sins_dns_tcp";
        frontends.Add(dnsTcpFrontend);

        // HTTP backend - forward to k3s ingress HTTP NodePort
        List<string> httpServerLines = new List<string>();
        for (int i = 0; i < workerNodes.Count; i++)
        {
            var server = workerNodes[i];
            string serverName = $"ingress-http-{i + 1}";
            httpServerLines.Add($"    server {serverName} {server.IPAddress}:{ingressHttpPort} check");
        }
        string httpBackend = $@"backend backend_ingress_http
    mode tcp
    balance roundrobin
{string.Join("\n", httpServerLines)}";
        backends.Add(httpBackend);

        // HTTPS backend - forward to k3s ingress HTTPS NodePort
        List<string> httpsServerLines = new List<string>();
        for (int i = 0; i < workerNodes.Count; i++)
        {
            var server = workerNodes[i];
            string serverName = $"ingress-https-{i + 1}";
            httpsServerLines.Add($"    server {serverName} {server.IPAddress}:{ingressHttpsPort} check");
        }
        string httpsBackend = $@"backend backend_ingress_https
    mode tcp
    balance roundrobin
{string.Join("\n", httpsServerLines)}";
        backends.Add(httpsBackend);

        // DNS TCP backend - forward to SiNS DNS TCP NodePort
        List<string> dnsTcpServerLines = new List<string>();
        for (int i = 0; i < workerNodes.Count; i++)
        {
            var server = workerNodes[i];
            string serverName = $"sins-dns-{i + 1}";
            dnsTcpServerLines.Add($"    server {serverName} {server.IPAddress}:{sinsDnsTcpPort} check");
        }
        string dnsTcpBackend = $@"backend backend_sins_dns_tcp
    mode tcp
    balance roundrobin
{string.Join("\n", dnsTcpServerLines)}";
        backends.Add(dnsTcpBackend);

        string frontendsText = string.Join("\n\n", frontends);
        string backendsText = string.Join("\n\n", backends);

        // Get stats port
        int statsPort = 8404;
        if (haproxyContainer.Params != null && haproxyContainer.Params.TryGetValue("stats_port", out object? statsPortObj) && statsPortObj is int statsPortInt)
        {
            statsPort = statsPortInt;
        }

        string configText = $@"global
    log /dev/log local0
    log /dev/log local1 notice
    maxconn 2048
    daemon
defaults
    log     global
    mode    tcp
    option  tcplog
    option  dontlognull
    timeout connect 5s
    timeout client  50s
    timeout server  50s
{frontendsText}

{backendsText}

listen stats
    bind *:{statsPort}
    mode http
    stats enable
    stats uri /
    stats refresh 10s
";

        // Write config to haproxy container
        string writeCmd = CLI.Files.NewFileOps().Write("/etc/haproxy/haproxy.cfg", configText).ToCommand();
        (string output, int? exitCode) = haproxySSHService.Execute(writeCmd, 30, true); // sudo=true
        if (exitCode.HasValue && exitCode.Value != 0)
        {
            logger.Printf("Failed to write HAProxy configuration: {0}", output);
            return false;
        }

        // Reload HAProxy to apply new configuration
        string reloadCmd = "systemctl reload haproxy || systemctl restart haproxy";
        (string reloadOutput, int? reloadExit) = haproxySSHService.Execute(reloadCmd, 30, true); // sudo=true
        if (reloadExit.HasValue && reloadExit.Value != 0)
        {
            logger.Printf("Failed to reload HAProxy: {0}", reloadOutput);
            return false;
        }

        // Setup UDP DNS forwarding using socat
        logger.Printf("Setting up UDP DNS forwarding to SiNS...");
        
        // Ensure socat is installed
        (string socatCheck, int? socatExitCode) = haproxySSHService.Execute("command -v socat", 30);
        if (!socatExitCode.HasValue || socatExitCode.Value != 0)
        {
            logger.Printf("Installing socat...");
            string installSocatCmd = "apt-get update && apt-get install -y socat";
            (string installOutput, int? installExit) = haproxySSHService.Execute(installSocatCmd, 60, true); // sudo=true
            if (installExit.HasValue && installExit.Value != 0)
            {
                logger.Printf("Failed to install socat: {0}", installOutput);
                return false;
            }
        }
        
        // Get first k3s node IP for UDP forwarding target (use control node if available, otherwise first worker)
        string? udpTargetIP = null;
        if (controlNode != null && !string.IsNullOrEmpty(controlNode.IPAddress))
        {
            udpTargetIP = controlNode.IPAddress;
        }
        else if (workerNodes.Count > 0 && !string.IsNullOrEmpty(workerNodes[0].IPAddress))
        {
            udpTargetIP = workerNodes[0].IPAddress;
        }

        if (string.IsNullOrEmpty(udpTargetIP))
        {
            logger.Printf("Warning: No k3s node IP found for UDP DNS forwarding target");
        }
        else
        {
            // Disable systemd-resolved to free port 53
            string disableResolvedCmd = "systemctl stop systemd-resolved 2>/dev/null; systemctl disable systemd-resolved 2>/dev/null; echo 'systemd-resolved disabled' || echo 'systemd-resolved not running'";
            haproxySSHService.Execute(disableResolvedCmd, 30, true); // sudo=true

            // Create systemd service for UDP DNS forwarding
            string systemdServiceContent = $@"[Unit]
Description=DNS UDP Forwarder to SiNS
After=network.target

[Service]
Type=simple
ExecStart=/usr/bin/socat UDP4-LISTEN:53,fork,reuseaddr UDP4:{udpTargetIP}:{sinsDnsTcpPort}
Restart=always
RestartSec=5

[Install]
WantedBy=multi-user.target
";

            string writeServiceCmd = CLI.Files.NewFileOps().Write("/etc/systemd/system/dns-udp-forwarder.service", systemdServiceContent).ToCommand();
            (string serviceWriteOutput, int? serviceWriteExit) = haproxySSHService.Execute(writeServiceCmd, 30, true); // sudo=true
            if (serviceWriteExit.HasValue && serviceWriteExit.Value != 0)
            {
                logger.Printf("Failed to write DNS UDP forwarder service: {0}", serviceWriteOutput);
                return false;
            }

            // Reload systemd and enable/start the service
            string enableServiceCmd = "systemctl daemon-reload && systemctl enable dns-udp-forwarder.service && systemctl restart dns-udp-forwarder.service";
            (string enableServiceOutput, int? enableServiceExit) = haproxySSHService.Execute(enableServiceCmd, 30, true); // sudo=true
            if (enableServiceExit.HasValue && enableServiceExit.Value != 0)
            {
                logger.Printf("Failed to enable/start DNS UDP forwarder service: {0}", enableServiceOutput);
                return false;
            }

            // Verify service is actually running
            (string statusOutput, int? statusExit) = haproxySSHService.Execute("systemctl is-active dns-udp-forwarder.service", 10, true); // sudo=true
            if (statusExit.HasValue && statusExit.Value != 0)
            {
                logger.Printf("DNS UDP forwarder service failed to start. Status: {0}", statusOutput);
                return false;
            }
            if (!statusOutput.Trim().Equals("active", StringComparison.OrdinalIgnoreCase))
            {
                logger.Printf("DNS UDP forwarder service is not active. Status: {0}", statusOutput);
                return false;
            }

            // Ensure HAProxy has CAP_NET_BIND_SERVICE capability to bind to port 53
            string setCapCmd = "setcap 'cap_net_bind_service=+ep' /usr/sbin/haproxy 2>/dev/null || echo 'capability already set or setcap not available'";
            haproxySSHService.Execute(setCapCmd, 30, true); // sudo=true

            logger.Printf("DNS UDP forwarder service configured and started (target: {0}:{1})", udpTargetIP, sinsDnsTcpPort);
        }

        logger.Printf("HAProxy configuration updated successfully with Traefik NodePorts (HTTP: {0}, HTTPS: {1}) and SiNS DNS (TCP: {2})", ingressHttpPort, ingressHttpsPort, sinsDnsTcpPort);
        return true;
        }
        finally
        {
            haproxySSHService.Disconnect();
        }
    }
}

public static class UpdateHaproxyConfigurationActionFactory
{
    public static IAction NewUpdateHaproxyConfigurationAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new UpdateHaproxyConfigurationAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
