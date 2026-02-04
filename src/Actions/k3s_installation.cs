using System;
using System.Linq;
using System.Threading;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Actions;

public class InstallK3sAction : BaseAction, IAction
{
    public InstallK3sAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "k3s installation";
    }

    public bool Execute()
    {
        if (SSHService == null)
        {
            Logger.GetLogger("install_k3s").Printf("SSH service not initialized");
            return false;
        }
        Logger.GetLogger("install_k3s").Printf("Installing k3s...");

        // Check if k3s is already installed
        string k3sCheckCmd = CLI.Command.NewCommand().SetCommand("k3s").Exists();
        (string checkOutput, int? checkExit) = SSHService.Execute(k3sCheckCmd, null);
        if (checkExit.HasValue && checkExit.Value == 0 && !string.IsNullOrEmpty(checkOutput))
        {
            string versionCheckCmd = "k3s --version";
            (string versionCheckOutput, int? versionCheckExit) = SSHService.Execute(versionCheckCmd, null);
            if (versionCheckExit.HasValue && versionCheckExit.Value == 0 && !string.IsNullOrEmpty(versionCheckOutput) && versionCheckOutput.ToLower().Contains("k3s"))
            {
                Logger.GetLogger("install_k3s").Printf("k3s is already installed: {0}", versionCheckOutput.Trim());
                // Ensure /dev/kmsg exists
                bool isControl = false;
                if (ContainerCfg != null && Cfg != null && Cfg.Kubernetes != null)
                {
                    foreach (int id in Cfg.Kubernetes.Control)
                    {
                        if (id == ContainerCfg.ID)
                        {
                            isControl = true;
                            break;
                        }
                    }
                }
                if (isControl)
                {
                    Logger.GetLogger("install_k3s").Printf("Ensuring /dev/kmsg exists for k3s (LXC requirement)...");
                    string removeCmd = "rm -f /dev/kmsg || true";
                    SSHService.Execute(removeCmd, null, true); // sudo=True
                    string createKmsgCmd = "ln -sf /dev/console /dev/kmsg";
                    (string createOutput, int? createExit) = SSHService.Execute(createKmsgCmd, null, true); // sudo=True
                    if (createExit.HasValue && createExit.Value != 0)
                    {
                        int outputLen = createOutput.Length;
                        int start = outputLen > 200 ? outputLen - 200 : 0;
                        Logger.GetLogger("install_k3s").Printf("Failed to create /dev/kmsg symlink: {0}", createOutput.Substring(start));
                        return false;
                    }
                    Logger.GetLogger("install_k3s").Printf("/dev/kmsg symlink created successfully");
                    string verifyCmd = "test -e /dev/kmsg && ls -l /dev/kmsg && echo exists || echo missing";
                    (string verifyOutput, _) = SSHService.Execute(verifyCmd, null, true); // sudo=True
                    if (!string.IsNullOrEmpty(verifyOutput))
                    {
                        Logger.GetLogger("install_k3s").Printf("/dev/kmsg status: {0}", verifyOutput.Trim());
                    }
                    string restartCmd = CLI.SystemCtl.NewSystemCtl().Service("k3s").Restart();
                    (string restartOutput, int? restartExit) = SSHService.Execute(restartCmd, null, true); // sudo=True
                    if (restartExit.HasValue && restartExit.Value != 0)
                    {
                        int outputLen = restartOutput.Length;
                        int start = outputLen > 200 ? outputLen - 200 : 0;
                        Logger.GetLogger("install_k3s").Printf("k3s restart had issues: {0}", restartOutput.Substring(start));
                    }
                    else
                    {
                        Logger.GetLogger("install_k3s").Printf("k3s service restarted");
                        Thread.Sleep(5000);
                    }
                }
                return true;
            }
        }

        // Check if curl is available
        (string curlCheckOutput, int? curlCheckExit) = SSHService.Execute(CLI.Command.NewCommand().SetCommand("curl").Exists(), null);
        bool hasCurl = curlCheckExit.HasValue && curlCheckExit.Value == 0 && curlCheckOutput.Contains("curl");

        if (!hasCurl)
        {
            Logger.GetLogger("install_k3s").Printf("curl not found, installing...");
            if (APTService == null)
            {
                Logger.GetLogger("install_k3s").Printf("APT service not initialized");
                return false;
            }
            (string curlInstallOutput, int? curlInstallExit) = APTService.Install(new[] { "curl" });
            if (!curlInstallExit.HasValue || curlInstallExit.Value != 0)
            {
                Logger.GetLogger("install_k3s").Printf("Failed to install curl: {0}", curlInstallOutput);
                return false;
            }
            Logger.GetLogger("install_k3s").Printf("curl installed successfully");
        }

        // Determine if this is a control node
        bool isControl2 = false;
        if (ContainerCfg != null && Cfg != null && Cfg.Kubernetes != null)
        {
            foreach (int id in Cfg.Kubernetes.Control)
            {
                if (id == ContainerCfg.ID)
                {
                    isControl2 = true;
                    break;
                }
            }
        }

        // Create /dev/kmsg device inside container (required for k3s in LXC)
        if (isControl2)
        {
            Logger.GetLogger("install_k3s").Printf("Creating /dev/kmsg device for k3s...");
            string removeCmd = "rm -f /dev/kmsg || true";
            SSHService.Execute(removeCmd, null, true); // sudo=True
            string createKmsgCmd = "ln -sf /dev/console /dev/kmsg";
            (string createOutput, int? createExit) = SSHService.Execute(createKmsgCmd, null, true); // sudo=True
            if (createExit.HasValue && createExit.Value != 0)
            {
                int outputLen = createOutput.Length;
                int start = outputLen > 200 ? outputLen - 200 : 0;
                Logger.GetLogger("install_k3s").Printf("Failed to create /dev/kmsg symlink: {0}", createOutput.Substring(start));
                return false;
            }
            string verifyCmd = "test -L /dev/kmsg && ls -l /dev/kmsg";
            (string verifyOutput, int? verifyExit) = SSHService.Execute(verifyCmd, null, true); // sudo=True
            if (verifyExit.HasValue && verifyExit.Value == 0 && !string.IsNullOrEmpty(verifyOutput) && verifyOutput.Contains("/dev/kmsg"))
            {
                Logger.GetLogger("install_k3s").Printf("/dev/kmsg symlink verified: {0}", verifyOutput.Trim());
            }
            else
            {
                Logger.GetLogger("install_k3s").Printf("/dev/kmsg symlink creation failed: {0}", verifyOutput);
                return false;
            }
        }

        if (isControl2)
        {
            Logger.GetLogger("install_k3s").Printf("Installing k3s server (control node)...");
            string configDir = "/etc/rancher/k3s";
            string configFile = $"{configDir}/config.yaml";
            string controlIP = ContainerCfg?.IPAddress ?? "127.0.0.1";
            string hostname = ContainerCfg?.Hostname ?? "k3s-control";
            string configContent = $@"# k3s configuration file
# This file is automatically generated
tls-san:
  - {controlIP}
  - {hostname}
bind-address: 0.0.0.0
advertise-address: {controlIP}
disable:
  - servicelb
";
            string createConfigCmd = $"mkdir -p {configDir} && cat > {configFile} << 'EOFCONFIG'\n{configContent}EOFCONFIG";
            (string configOutput, int? configExit) = SSHService.Execute(createConfigCmd, null, true); // sudo=True
            if (configExit.HasValue && configExit.Value != 0)
            {
                int outputLen = configOutput.Length;
                int start = outputLen > 200 ? outputLen - 200 : 0;
                Logger.GetLogger("install_k3s").Printf("Failed to create k3s config file: {0}", configOutput.Substring(start));
                return false;
            }
            Logger.GetLogger("install_k3s").Printf("k3s config file created successfully");

            string installCmd = "curl -sfL https://get.k3s.io | sh -";
            int timeout = 300;
            (string installOutput, int? installExit) = SSHService.Execute(installCmd, timeout);
            if (installExit.HasValue && installExit.Value != 0)
            {
                Logger.GetLogger("install_k3s").Printf("k3s installation failed with exit code {0}", installExit.Value);
                int outputLen = installOutput.Length;
                int start = outputLen > 1000 ? outputLen - 1000 : 0;
                Logger.GetLogger("install_k3s").Printf("k3s installation output: {0}", installOutput.Substring(start));
                return false;
            }
        }
        else
        {
            Logger.GetLogger("install_k3s").Printf("Skipping k3s agent installation for worker node (will be installed during orchestration)...");
            return true;
        }

        // Verify k3s is installed
        string k3sCheckCmd2 = CLI.Command.NewCommand().SetCommand("k3s").Exists();
        (string checkOutput2, int? checkExitCode) = SSHService.Execute(k3sCheckCmd2, null);
        if (!checkExitCode.HasValue || checkExitCode.Value != 0 || string.IsNullOrEmpty(checkOutput2))
        {
            Logger.GetLogger("install_k3s").Printf("k3s installation failed - k3s command not found");
            return false;
        }

        string finalVersionCmd = "k3s --version";
        (string finalVersionOutput, int? finalVersionExit) = SSHService.Execute(finalVersionCmd, null);
        if (!finalVersionExit.HasValue || finalVersionExit.Value != 0 || string.IsNullOrEmpty(finalVersionOutput) || !finalVersionOutput.ToLower().Contains("k3s"))
        {
            Logger.GetLogger("install_k3s").Printf("k3s installation failed - verification shows k3s is not installed");
            return false;
        }
        Logger.GetLogger("install_k3s").Printf("k3s installed successfully: {0}", finalVersionOutput.Trim());

        // Fix systemd service files to ensure /dev/kmsg exists before k3s starts (persistent fix for LXC)
        Logger.GetLogger("install_k3s").Printf("Configuring systemd service to ensure /dev/kmsg exists on startup...");
        string serviceName = isControl2 ? "k3s" : "k3s-agent";
        string serviceFile = $"/etc/systemd/system/{serviceName}.service";

        // Read current service file
        string readServiceCmd = $"cat {serviceFile}";
        (string serviceContent, _) = SSHService.Execute(readServiceCmd, null, true); // sudo=True

        // Check if ExecStartPre for /dev/kmsg already exists
        if (!serviceContent.Contains("/dev/kmsg"))
        {
            // Add ExecStartPre to create /dev/kmsg before other ExecStartPre commands
            // Insert before the first ExecStartPre line (which is usually modprobe br_netfilter)
            // Use script file approach for reliable sed execution (avoids escaping issues)
            string fixServiceScript = $@"serviceFile=""{serviceFile}""
export serviceFile
cat > /tmp/fix_k3s_service.sh << 'EOFSED'
#!/bin/bash
sed -i ""/ExecStartPre=-\/sbin\/modprobe br_netfilter/i ExecStartPre=-/bin/bash -c \\\""rm -f /dev/kmsg && ln -sf /dev/console /dev/kmsg\\\"""" ""$serviceFile""
EOFSED
chmod +x /tmp/fix_k3s_service.sh
/tmp/fix_k3s_service.sh && echo ""success"" || echo ""failed""
rm -f /tmp/fix_k3s_service.sh";
            (string fixOutput, int? fixExit) = SSHService.Execute(fixServiceScript, null, true); // sudo=True
            if (fixExit.HasValue && fixExit.Value == 0 && fixOutput.Contains("success"))
            {
                Logger.GetLogger("install_k3s").Printf("✓ Added /dev/kmsg fix to {0}.service", serviceName);
                // Reload systemd to pick up changes
                string reloadCmd = "systemctl daemon-reload";
                (string reloadOutput, int? reloadExit) = SSHService.Execute(reloadCmd, null, true); // sudo=True
                if (reloadExit.HasValue && reloadExit.Value == 0)
                {
                    Logger.GetLogger("install_k3s").Printf("✓ Systemd daemon reloaded");
                }
                else
                {
                    Logger.GetLogger("install_k3s").Printf("✗ Failed to reload systemd: {0}", reloadOutput);
                    Logger.GetLogger("install_k3s").Printf("✗ Deployment failed: systemd daemon-reload failed after adding ExecStartPre fix");
                    return false;
                }
            }
            else
            {
                Logger.GetLogger("install_k3s").Printf("✗ Failed to modify {0}.service: {1}", serviceName, fixOutput);
                Logger.GetLogger("install_k3s").Printf("✗ Deployment failed: {0}.service must have ExecStartPre fix for /dev/kmsg (required for LXC containers)", serviceName);
                return false;
            }
        }
        else
        {
            Logger.GetLogger("install_k3s").Printf("✓ {0}.service already has /dev/kmsg fix", serviceName);
        }

        // Setup kubectl PATH and kubeconfig for root user
        if (isControl2)
        {
            Logger.GetLogger("install_k3s").Printf("Setting up kubectl PATH and kubeconfig...");
            string symlinkCmd = "ln -sf /usr/local/bin/kubectl /usr/bin/kubectl || true";
            SSHService.Execute(symlinkCmd, null, true); // sudo=True
            int maxWait = 60;
            int waitTime = 0;
            bool kubeconfigReady = false;
            while (waitTime < maxWait)
            {
                string checkCmd = "test -f /etc/rancher/k3s/k3s.yaml && echo exists || echo missing";
                (string checkOutput3, int? checkExit3) = SSHService.Execute(checkCmd, null, true); // sudo=True
                if (checkExit3.HasValue && checkExit3.Value == 0 && !string.IsNullOrEmpty(checkOutput3) && checkOutput3.Contains("exists"))
                {
                    kubeconfigReady = true;
                    break;
                }
                Thread.Sleep(2000);
                waitTime += 2;
            }
            if (!kubeconfigReady)
            {
                Logger.GetLogger("install_k3s").Printf("k3s kubeconfig not generated after {0} seconds", maxWait);
                return false;
            }
            Logger.GetLogger("install_k3s").Printf("k3s kubeconfig generated");
            string controlIP2 = ContainerCfg?.IPAddress ?? "127.0.0.1";
            string fixKubeconfigCmd = $"sed -i 's|server: https://127.0.0.1:6443|server: https://{controlIP2}:6443|g; s|server: https://0.0.0.0:6443|server: https://{controlIP2}:6443|g' /etc/rancher/k3s/k3s.yaml && mkdir -p /root/.kube && cp /etc/rancher/k3s/k3s.yaml /root/.kube/config && chown root:root /root/.kube/config && chmod 600 /root/.kube/config";
            (string fixOutput, int? fixExit) = SSHService.Execute(fixKubeconfigCmd, null, true); // sudo=True
            if (fixExit.HasValue && fixExit.Value != 0)
            {
                int outputLen = fixOutput.Length;
                int start = outputLen > 200 ? outputLen - 200 : 0;
                Logger.GetLogger("install_k3s").Printf("Failed to setup kubeconfig: {0}", fixOutput.Substring(start));
                return false;
            }
            string verifyCmd = "test -f /root/.kube/config && echo exists || echo missing";
            (string verifyOutput2, int? verifyExit2) = SSHService.Execute(verifyCmd, null, true); // sudo=True
            if (!verifyExit2.HasValue || verifyExit2.Value != 0 || string.IsNullOrEmpty(verifyOutput2) || !verifyOutput2.Contains("exists"))
            {
                Logger.GetLogger("install_k3s").Printf("kubeconfig was not copied to /root/.kube/config");
                return false;
            }
            Logger.GetLogger("install_k3s").Printf("kubeconfig setup completed");
            
            // Configure Traefik with fixed NodePorts via HelmChartConfig
            Logger.GetLogger("install_k3s").Printf("Configuring Traefik with fixed NodePorts...");
            string helmChartConfigYaml = @"apiVersion: helm.cattle.io/v1
kind: HelmChartConfig
metadata:
  name: traefik
  namespace: kube-system
spec:
  valuesContent: |-
    service:
      type: NodePort
    ports:
      web:
        nodePort: 31523
      websecure:
        nodePort: 30490
";
            string createHelmChartConfigCmd = $"cat > /tmp/traefik-helmchartconfig.yaml << 'EOFHELM'\n{helmChartConfigYaml}EOFHELM";
            (string helmConfigOutput, int? helmConfigExit) = SSHService.Execute(createHelmChartConfigCmd, null, true); // sudo=True
            if (helmConfigExit.HasValue && helmConfigExit.Value == 0)
            {
                string applyHelmConfigCmd = "export PATH=/usr/local/bin:$PATH && export KUBECONFIG=/etc/rancher/k3s/k3s.yaml && kubectl apply -f /tmp/traefik-helmchartconfig.yaml && rm -f /tmp/traefik-helmchartconfig.yaml";
                (string applyOutput, int? applyExit) = SSHService.Execute(applyHelmConfigCmd, 60, true); // sudo=True
                if (applyExit.HasValue && applyExit.Value == 0)
                {
                    Logger.GetLogger("install_k3s").Printf("Traefik HelmChartConfig created successfully");
                    // Restart k3s to apply the HelmChartConfig
                    string restartK3sCmd = "systemctl restart k3s";
                    SSHService.Execute(restartK3sCmd, 60, true); // sudo=True
                    Logger.GetLogger("install_k3s").Printf("k3s restarted to apply Traefik NodePort configuration");
                    Thread.Sleep(10000); // Wait for k3s to restart
                }
                else
                {
                    Logger.GetLogger("install_k3s").Printf("Warning: Failed to apply Traefik HelmChartConfig: {0}", applyOutput);
                }
            }
            else
            {
                Logger.GetLogger("install_k3s").Printf("Warning: Failed to create Traefik HelmChartConfig file: {0}", helmConfigOutput);
            }
        }

        // Verify /dev/kmsg exists (especially important for worker nodes)
        if (!isControl2)
        {
            Logger.GetLogger("install_k3s").Printf("Verifying /dev/kmsg exists on worker node...");
            string verifyKmsgCmd = "test -e /dev/kmsg && echo exists || echo missing";
            (string verifyKmsgOutput, int? verifyKmsgExit) = SSHService.Execute(verifyKmsgCmd, null, true); // sudo=True
            if (!verifyKmsgExit.HasValue || verifyKmsgExit.Value != 0 || !verifyKmsgOutput.Contains("exists"))
            {
                Logger.GetLogger("install_k3s").Printf("Creating /dev/kmsg symlink on worker node...");
                string createKmsgCmd = "rm -f /dev/kmsg && ln -sf /dev/console /dev/kmsg";
                (string createOutput, int? createExit) = SSHService.Execute(createKmsgCmd, null, true); // sudo=True
                if (createExit.HasValue && createExit.Value == 0)
                {
                    Logger.GetLogger("install_k3s").Printf("✓ /dev/kmsg created on worker node");
                }
                else
                {
                    Logger.GetLogger("install_k3s").Printf("⚠ Failed to create /dev/kmsg on worker node: {0}", createOutput);
                }
            }
            else
            {
                Logger.GetLogger("install_k3s").Printf("✓ /dev/kmsg exists on worker node");
            }
        }

        return true;
    }
}

public static class InstallK3sActionFactory
{
    public static IAction NewInstallK3sAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new InstallK3sAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}