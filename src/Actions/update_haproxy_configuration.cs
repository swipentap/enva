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
        // Get MetalLB IP pool range from configuration or default
        string ipPoolStart = "10.11.2.20";
        string ipPoolEnd = "10.11.2.30";
        
        // Extract IP pool from network configuration
        if (!string.IsNullOrEmpty(Cfg.Network))
        {
            // Parse network like "10.11.2.0/24" and use .20-.30 for MetalLB pool
            string[] parts = Cfg.Network.Split('.');
            if (parts.Length >= 3)
            {
                ipPoolStart = $"{parts[0]}.{parts[1]}.{parts[2]}.20";
                ipPoolEnd = $"{parts[0]}.{parts[1]}.{parts[2]}.30";
            }
        }
        
        logger.Printf("Using MetalLB IP pool ({0} to {1}) for Traefik LoadBalancer", ipPoolStart, ipPoolEnd);

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

        // HTTP backend - load balance across MetalLB IP pool
        List<string> httpServerLines = new List<string>();
        // Parse IP pool range (e.g., 10.11.2.20-10.11.2.30)
        string[] startParts = ipPoolStart.Split('.');
        string[] endParts = ipPoolEnd.Split('.');
        if (startParts.Length == 4 && endParts.Length == 4 && 
            int.TryParse(startParts[3], out int startOctet) && 
            int.TryParse(endParts[3], out int endOctet))
        {
            string baseIP = $"{startParts[0]}.{startParts[1]}.{startParts[2]}";
            for (int i = startOctet; i <= endOctet; i++)
            {
                string serverIP = $"{baseIP}.{i}";
                string serverName = $"traefik-http-{i}";
                httpServerLines.Add($"    server {serverName} {serverIP}:80 check");
            }
        }
        string httpBackend = $@"backend backend_ingress_http
    mode tcp
    balance roundrobin
{string.Join("\n", httpServerLines)}";
        backends.Add(httpBackend);

        // HTTPS backend - load balance across MetalLB IP pool
        List<string> httpsServerLines = new List<string>();
        if (startParts.Length == 4 && endParts.Length == 4 && 
            int.TryParse(startParts[3], out int startOctet2) && 
            int.TryParse(endParts[3], out int endOctet2))
        {
            string baseIP = $"{startParts[0]}.{startParts[1]}.{startParts[2]}";
            for (int i = startOctet2; i <= endOctet2; i++)
            {
                string serverIP = $"{baseIP}.{i}";
                string serverName = $"traefik-https-{i}";
                httpsServerLines.Add($"    server {serverName} {serverIP}:443 check");
            }
        }
        string httpsBackend = $@"backend backend_ingress_https
    mode tcp
    balance roundrobin
{string.Join("\n", httpsServerLines)}";
        backends.Add(httpsBackend);

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

        logger.Printf("HAProxy configuration updated successfully with MetalLB IP pool ({0} to {1}) for Traefik LoadBalancer", ipPoolStart, ipPoolEnd);
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
