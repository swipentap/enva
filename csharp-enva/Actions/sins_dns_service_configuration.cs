using System;
using System.Linq;
using System.Text;
using Enva.Libs;
using Enva.Services;
using Enva.CLI;

namespace Enva.Actions;

public class ConfigureSinsServiceAction : BaseAction, IAction
{
    public ConfigureSinsServiceAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
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
        return "sins dns service configuration";
    }

    public bool Execute()
    {
        if (SSHService == null)
        {
            Logger.GetLogger("configure_sins_service").Printf("SSH service not initialized");
            return false;
        }
        int webPort = 80;
        if (ContainerCfg?.Params != null && ContainerCfg.Params.ContainsKey("web_port") && ContainerCfg.Params["web_port"] is int p)
        {
            webPort = p;
        }
        string checkServiceCmd = "test -f /etc/systemd/system/sins.service && echo exists || echo missing";
        var (serviceExists, _) = SSHService.Execute(checkServiceCmd, null);
        if (serviceExists.Contains("exists"))
        {
            Logger.GetLogger("configure_sins_service").Printf("SiNS service file already exists (provided by Debian package), updating web port configuration and timeout...");
            string readServiceCmd = "cat /etc/systemd/system/sins.service";
            var (existingService, _) = SSHService.Execute(readServiceCmd, null);
            bool needsUpdate = false;
            if (!existingService.Contains($"ASPNETCORE_URLS=http://+:{webPort}") && !existingService.Contains($"ASPNETCORE_URLS=http://0.0.0.0:{webPort}"))
            {
                needsUpdate = true;
            }
            if (existingService.Contains("Type=notify"))
            {
                needsUpdate = true;
            }
            if (!existingService.Contains("TimeoutStartSec"))
            {
                needsUpdate = true;
            }
            if (!needsUpdate)
            {
                Logger.GetLogger("configure_sins_service").Printf("SiNS service file already configured with correct web port and timeout");
                string reloadCmd = "systemctl daemon-reload || true";
                SSHService.Execute(reloadCmd, null, true);
                return true;
            }
            string updateCmd = $"sed -i 's|^Type=notify|Type=simple|' /etc/systemd/system/sins.service && sed -i 's|Environment=ASPNETCORE_URLS=.*|Environment=ASPNETCORE_URLS=http://0.0.0.0:{webPort}|' /etc/systemd/system/sins.service && grep -q '^TimeoutStartSec=' /etc/systemd/system/sins.service || sed -i '/^\\[Service\\]/a TimeoutStartSec=300' /etc/systemd/system/sins.service && sed -i 's|^TimeoutStartSec=.*|TimeoutStartSec=300|' /etc/systemd/system/sins.service";
            var (_, updateExitCode) = SSHService.Execute(updateCmd, null, true);
            if (updateExitCode.HasValue && updateExitCode.Value == 0)
            {
                Logger.GetLogger("configure_sins_service").Printf("Updated SiNS service file: changed Type=notify to Type=simple, ASPNETCORE_URLS=http://0.0.0.0:{0}, TimeoutStartSec=300", webPort);
                string reloadCmd = "systemctl daemon-reload || true";
                SSHService.Execute(reloadCmd, null, true);
                return true;
            }
            Logger.GetLogger("configure_sins_service").Printf("Failed to update service file, will create new one");
        }
        Logger.GetLogger("configure_sins_service").Printf("Creating SiNS systemd service...");
        string findBinaryCmd = "test -f /opt/sins/sins && echo /opt/sins/sins || (which sins || find /usr /opt -name 'sins.dll' -o -name 'sins' -type f | head -1)";
        var (binaryPathOutput, _) = SSHService.Execute(findBinaryCmd, null);
        string binaryPath = binaryPathOutput.Trim();
        if (string.IsNullOrEmpty(binaryPath))
        {
            Logger.GetLogger("configure_sins_service").Printf("Could not find SiNS binary");
            return false;
        }
        binaryPath = binaryPath.Trim();
        string workingDir = "/opt/sins";
        string execStart = "/opt/sins/sins";
        if (binaryPath.EndsWith(".dll"))
        {
            var parts = binaryPath.Split('/');
            if (parts.Length > 1)
            {
                workingDir = string.Join("/", parts.Take(parts.Length - 1));
            }
            execStart = $"/usr/bin/dotnet {binaryPath}";
        }
        else if (binaryPath != "/opt/sins/sins")
        {
            var parts = binaryPath.Split('/');
            if (parts.Length > 1)
            {
                workingDir = string.Join("/", parts.Take(parts.Length - 1));
            }
            else
            {
                workingDir = "/usr/bin";
            }
            execStart = binaryPath;
        }
        var appsettingsLocations = new[] { "/etc/sins/appsettings.json", $"{workingDir}/appsettings.json", "/opt/sins/app/appsettings.json" };
        string appsettingsPath = "/etc/sins/appsettings.json";
        foreach (string loc in appsettingsLocations)
        {
            string checkCmd = $"test -f {loc} && echo exists || echo missing";
            var (checkOutput, _) = SSHService.Execute(checkCmd, null);
            if (checkOutput.Contains("exists"))
            {
                appsettingsPath = loc;
                break;
            }
        }
        string serviceContent = $@"[Unit]
Description=SiNS DNS Server
After=network.target

[Service]
Type=simple
User=root
WorkingDirectory={workingDir}
Environment=ASPNETCORE_URLS=http://0.0.0.0:{webPort}
Environment=ASPNETCORE_ENVIRONMENT=Production
ExecStart={execStart}
Restart=always
RestartSec=10
TimeoutStartSec=300

[Install]
WantedBy=multi-user.target
";
        byte[] serviceBytes = Encoding.UTF8.GetBytes(serviceContent);
        string serviceB64 = Convert.ToBase64String(serviceBytes);
        string serviceCmd = $"systemctl stop sins || true; echo {serviceB64} | base64 -d > /etc/systemd/system/sins.service && systemctl daemon-reload || true";
        var (output, exitCode) = SSHService.Execute(serviceCmd, null, true);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("configure_sins_service").Printf("Failed to create SiNS service file: {0}", output);
            return false;
        }
        return true;
    }
}

public static class ConfigureSinsServiceActionFactory
{
    public static IAction NewConfigureSinsServiceAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new ConfigureSinsServiceAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
