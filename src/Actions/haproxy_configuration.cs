using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;
using Enva.Verification;

namespace Enva.Actions;

public class ConfigureHaproxyAction : BaseAction, IAction
{
    public ConfigureHaproxyAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "haproxy configuration";
    }

    public bool Execute()
    {
        if (SSHService == null || Cfg == null)
        {
            Logger.GetLogger("configure_haproxy").Printf("SSH service or config not initialized");
            return false;
        }
        int httpPort = 80;
        int httpsPort = 443;
        int statsPort = 8404;
        if (ContainerCfg != null && ContainerCfg.Params != null)
        {
            if (ContainerCfg.Params.TryGetValue("http_port", out object? httpPortObj) && httpPortObj is int httpPortInt)
            {
                httpPort = httpPortInt;
            }
            if (ContainerCfg.Params.TryGetValue("https_port", out object? httpsPortObj) && httpsPortObj is int httpsPortInt)
            {
                httpsPort = httpsPortInt;
            }
            if (ContainerCfg.Params.TryGetValue("stats_port", out object? statsPortObj) && statsPortObj is int statsPortInt)
            {
                statsPort = statsPortInt;
            }
        }

        // Determine SSL certificate path - use configured path if provided, otherwise default
        string certPath = "/etc/haproxy/haproxy.pem";
        bool useCustomCert = false;
        if (!string.IsNullOrEmpty(Cfg.CertificatePath))
        {
            certPath = Cfg.CertificatePath;
            useCustomCert = true;
            Logger.GetLogger("configure_haproxy").Printf("Using configured SSL certificate path: {0}", certPath);
            
            // If custom certificate path is configured, certificate_source_path must also be configured
            if (string.IsNullOrEmpty(Cfg.CertificateSourcePath))
            {
                Logger.GetLogger("configure_haproxy").Printf("certificate_path is configured but certificate_source_path is missing");
                return false;
            }
            
            // Read certificate file from local filesystem
            string sourcePath = Cfg.CertificateSourcePath;
            // If path is relative, make it relative to current working directory
            if (!Path.IsPathRooted(sourcePath))
            {
                string wd = Directory.GetCurrentDirectory();
                sourcePath = Path.Combine(wd, sourcePath);
            }
            
            // Check if certificate source file exists
            if (!File.Exists(sourcePath))
            {
                Logger.GetLogger("configure_haproxy").Printf("Certificate source file not found at {0}", sourcePath);
                return false;
            }
            
            // Read certificate file content
            byte[] certContent;
            try
            {
                certContent = File.ReadAllBytes(sourcePath);
            }
            catch (Exception ex)
            {
                Logger.GetLogger("configure_haproxy").Printf("Failed to read certificate file from {0}: {1}", sourcePath, ex.Message);
                return false;
            }
            if (certContent.Length == 0)
            {
                Logger.GetLogger("configure_haproxy").Printf("Certificate file is empty at {0}", sourcePath);
                return false;
            }
            
            // Write certificate to container using base64 encoding to avoid escaping issues
            string encodedCert = Convert.ToBase64String(certContent);
            string writeCertCmd = $"echo {encodedCert} | base64 -d > {certPath} && chmod 644 {certPath} && echo 'success' || echo 'failed'";
            (string writeOutput, int? writeExit) = SSHService.Execute(writeCertCmd, null, true); // sudo=True
            if (!writeExit.HasValue || writeExit.Value != 0 || !writeOutput.Contains("success"))
            {
                Logger.GetLogger("configure_haproxy").Printf("Failed to write certificate to container at {0}: {1}", certPath, writeOutput);
                return false;
            }
            Logger.GetLogger("configure_haproxy").Printf("Certificate transferred successfully from {0} to {1}", sourcePath, certPath);
        }
        else
        {
            Logger.GetLogger("configure_haproxy").Printf("Using default SSL certificate path: {0}", certPath);
        }
        
        // For default certificate, check if it exists and generate if needed
        if (!useCustomCert)
        {
            string checkCertCmd = $"test -f {certPath} && echo 'exists' || echo 'missing'";
            (string certCheck, _) = SSHService.Execute(checkCertCmd, null, true); // sudo=True
            
            if (!certCheck.Contains("exists"))
            {
                Logger.GetLogger("configure_haproxy").Printf("Generating self-signed SSL certificate...");
                // Generate certificate with wildcard for domain
                string domain = "*";
                if (!string.IsNullOrEmpty(Cfg.Domain))
                {
                    domain = $"*.{Cfg.Domain}";
                }
                string generateCertCmd = $"cd /tmp && openssl req -x509 -newkey rsa:2048 -keyout haproxy.key -out haproxy.crt -days 365 -nodes -subj \"/CN={domain}\"  && cat haproxy.key haproxy.crt > {certPath} && chmod 644 {certPath} && rm -f haproxy.key haproxy.crt";
                (string certOutput, int? certExitCode) = SSHService.Execute(generateCertCmd, null, true); // sudo=True
                if (certExitCode.HasValue && certExitCode.Value != 0)
                {
                    Logger.GetLogger("configure_haproxy").Printf("Failed to generate SSL certificate: {0}", certOutput);
                    // Continue without SSL - will use HTTP mode only
                    certPath = "";
                }
                else
                {
                    Logger.GetLogger("configure_haproxy").Printf("SSL certificate generated successfully at {0}", certPath);
                }
            }
            else
            {
                Logger.GetLogger("configure_haproxy").Printf("SSL certificate exists at {0}", certPath);
            }
        }
        // Get all worker nodes for k3s ingress backend
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
            Logger.GetLogger("configure_haproxy").Printf("No k3s nodes found for ingress backend");
            return false;
        }

        // k3s Traefik ingress controller NodePorts - try to detect dynamically
        int ingressHttpPort = 31523;  // Default k3s value, will be overridden if detected
        int ingressHttpsPort = 30490; // Default k3s value, will be overridden if detected
        
        // Try to detect actual Traefik NodePorts from Kubernetes
        if (PCTService != null && Cfg != null && Cfg.Kubernetes != null && Cfg.Kubernetes.Control != null && Cfg.Kubernetes.Control.Count > 0)
        {
            var controlID = Cfg.Kubernetes.Control[0];
            var logger = Logger.GetLogger("configure_haproxy");
            
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
        string writeCmd = CLI.Files.NewFileOps().Write("/etc/haproxy/haproxy.cfg", configText).ToCommand();
        (string output, int? exitCode) = SSHService.Execute(writeCmd, null, true); // sudo=True
        if (exitCode.HasValue && exitCode.Value != 0)
        {
            Logger.GetLogger("configure_haproxy").Printf("write haproxy configuration failed with exit code {0}", exitCode.Value);
            if (!string.IsNullOrEmpty(output))
            {
                string[] lines = output.Split('\n');
                if (lines.Length > 0)
                {
                    Logger.GetLogger("configure_haproxy").Printf("write haproxy configuration output: {0}", lines[lines.Length - 1]);
                }
            }
            return false;
        }

        // Reload HAProxy to apply new configuration
        string reloadCmd = "systemctl reload haproxy || systemctl restart haproxy";
        (string reloadOutput, int? reloadExit) = SSHService.Execute(reloadCmd, null, true); // sudo=True
        if (reloadExit.HasValue && reloadExit.Value != 0)
        {
            Logger.GetLogger("configure_haproxy").Printf("Failed to reload HAProxy: {0}", reloadOutput);
            // Don't fail, HAProxy might already be running with the config
        }

        // Verify HAProxy backends after configuration
        if (PCTService != null)
        {
            Thread.Sleep(2000);
            Verification.Verification.VerifyHAProxyBackends(Cfg, PCTService);
        }

        return true;
    }
}

public static class ConfigureHaproxyActionFactory
{
    public static IAction NewConfigureHaproxyAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new ConfigureHaproxyAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
