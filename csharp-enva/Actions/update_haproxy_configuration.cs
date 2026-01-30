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
        if (PCTService == null)
        {
            Logger.GetLogger("update_haproxy_configuration").Printf("PCT service is missing");
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

        int haproxyID = haproxyContainer.ID;
        var controlID = Cfg.Kubernetes.Control[0];

        // Detect Traefik NodePorts
        int ingressHttpPort = 31523;  // Default fallback
        int ingressHttpsPort = 30490; // Default fallback

        try
        {
            // Check if kubectl is available
            string kubectlCheckCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && command -v kubectl && echo installed || echo not_installed";
            (string kubectlCheck, _) = PCTService.Execute(controlID, kubectlCheckCmd, 30);
            
            if (kubectlCheck.Contains("installed"))
            {
                // Get HTTP NodePort
                string getHTTPPortCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get svc -n kube-system traefik -o jsonpath='{.spec.ports[?(@.name==\"web\")].nodePort}' 2>/dev/null || echo ''";
                (string httpPortOutput, _) = PCTService.Execute(controlID, getHTTPPortCmd, 30);
                if (!string.IsNullOrEmpty(httpPortOutput) && int.TryParse(httpPortOutput.Trim(), out int detectedHttpPort) && detectedHttpPort > 0)
                {
                    ingressHttpPort = detectedHttpPort;
                    logger.Printf("Detected Traefik HTTP NodePort: {0}", ingressHttpPort);
                }
                
                // Get HTTPS NodePort
                string getHTTPSPortCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl get svc -n kube-system traefik -o jsonpath='{.spec.ports[?(@.name==\"websecure\")].nodePort}' 2>/dev/null || echo ''";
                (string httpsPortOutput, _) = PCTService.Execute(controlID, getHTTPSPortCmd, 30);
                if (!string.IsNullOrEmpty(httpsPortOutput) && int.TryParse(httpsPortOutput.Trim(), out int detectedHttpsPort) && detectedHttpsPort > 0)
                {
                    ingressHttpsPort = detectedHttpsPort;
                    logger.Printf("Detected Traefik HTTPS NodePort: {0}", ingressHttpsPort);
                }
            }
            else
            {
                logger.Printf("kubectl not available, using default Traefik NodePorts (HTTP: {0}, HTTPS: {1})", ingressHttpPort, ingressHttpsPort);
            }
        }
        catch (Exception ex)
        {
            logger.Printf("Failed to detect Traefik NodePorts, using defaults (HTTP: {0}, HTTPS: {1}): {2}", ingressHttpPort, ingressHttpsPort, ex.Message);
        }

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
        (string output, int? exitCode) = PCTService.Execute(haproxyID, writeCmd, 30);
        if (exitCode.HasValue && exitCode.Value != 0)
        {
            logger.Printf("Failed to write HAProxy configuration: {0}", output);
            return false;
        }

        // Reload HAProxy to apply new configuration
        string reloadCmd = "systemctl reload haproxy || systemctl restart haproxy";
        (string reloadOutput, int? reloadExit) = PCTService.Execute(haproxyID, reloadCmd, 30);
        if (reloadExit.HasValue && reloadExit.Value != 0)
        {
            logger.Printf("Failed to reload HAProxy: {0}", reloadOutput);
            return false;
        }

        logger.Printf("HAProxy configuration updated successfully with Traefik NodePorts (HTTP: {0}, HTTPS: {1})", ingressHttpPort, ingressHttpsPort);
        return true;
    }
}

public static class UpdateHaproxyConfigurationActionFactory
{
    public static IAction NewUpdateHaproxyConfigurationAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new UpdateHaproxyConfigurationAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
