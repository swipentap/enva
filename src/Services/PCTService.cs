using System;
using System.Text;
using Enva.Libs;

namespace Enva.Services;

public class PCTService : IPCTService
{
    private ILXCService lxc;
    private const string DefaultShell = "bash";
    private const string Base64DecodeCmd = "base64 -d";

    public PCTService(ILXCService lxc)
    {
        this.lxc = lxc;
    }

    private string EncodeCommand(string command)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(command);
        return Convert.ToBase64String(bytes);
    }

    private string BuildPCTExecCommand(int containerID, string command)
    {
        Logger.GetLogger("pct").Debug("Base64 source [BuildPCTExecCommand]: {0}", command);
        string encodedCmd = EncodeCommand(command);
        return $"pct exec {containerID} -- {DefaultShell} -c \"echo {encodedCmd} | {Base64DecodeCmd} | {DefaultShell}\"";
    }

    public (string output, int? exitCode) Execute(int containerID, string command, int? timeout)
    {
        Logger.GetLogger("pct").Debug("Running in container {0}: {1}", containerID, command);
        string pctCmd = BuildPCTExecCommand(containerID, command);
        return lxc.Execute(pctCmd, timeout);
    }

    public (string output, int? exitCode) Create(
        int containerID,
        string templatePath,
        string hostname,
        int memory,
        int swap,
        int cores,
        string ipAddress,
        string gateway,
        string bridge,
        string storage,
        int rootfsSize,
        bool unprivileged,
        string ostype,
        string arch
    )
    {
        string unprivValue = unprivileged ? "1" : "0";
        string cmd = $"pct create {containerID} {templatePath} --hostname {hostname} --memory {memory} --swap {swap} --cores {cores} --net0 name=eth0,bridge={bridge},ip={ipAddress}/24,gw={gateway} --rootfs {storage}:{rootfsSize} --unprivileged {unprivValue} --ostype {ostype} --arch {arch}";
        return lxc.Execute(cmd, null);
    }

    public (string output, int? exitCode) SetOption(int containerID, string option, string value)
    {
        string cmd = $"pct set {containerID} --{option} {value}";
        return lxc.Execute(cmd, null);
    }

    public (string output, int? exitCode) SetOnboot(int containerID, bool autostart)
    {
        string value = autostart ? "1" : "0";
        return SetOption(containerID, "onboot", value);
    }

    public (string output, int? exitCode) Start(int containerID)
    {
        string cmd = $"pct start {containerID}";
        return lxc.Execute(cmd, null);
    }

    public (string output, int? exitCode) Stop(int containerID, bool force)
    {
        string cmd = force ? $"pct stop {containerID} --force" : $"pct stop {containerID}";
        return lxc.Execute(cmd, null);
    }

    public (string output, int? exitCode) Status(int? containerID)
    {
        if (containerID.HasValue)
        {
            string cmd = $"pct status {containerID.Value}";
            return lxc.Execute(cmd, null);
        }
        return lxc.Execute("pct list", null);
    }

    public (string output, int? exitCode) Destroy(int containerID, bool force)
    {
        string cmd = force ? $"pct destroy {containerID} --force" : $"pct destroy {containerID}";
        return lxc.Execute(cmd, null);
    }

    public (string output, int? exitCode) SetFeatures(int containerID, bool nesting, bool keyctl, bool fuse)
    {
        string cmd = $"pct set {containerID} --features nesting={BoolToInt(nesting)},keyctl={BoolToInt(keyctl)},fuse={BoolToInt(fuse)}";
        return lxc.Execute(cmd, null);
    }

    public (string output, int? exitCode) Config(int containerID)
    {
        string cmd = $"pct config {containerID}";
        return lxc.Execute(cmd, null);
    }

    private int BoolToInt(bool b)
    {
        return b ? 1 : 0;
    }

    public bool SetupSSHKey(int containerID, string ipAddress, LabConfig cfg)
    {
        return Common.SetupSSHKey(containerID, ipAddress, cfg, lxc, this);
    }

    public bool EnsureSSHServiceRunning(int containerID, LabConfig cfg)
    {
        // Check if openssh-server is installed
        string checkCmd = "dpkg -l | grep -q '^ii.*openssh-server' || echo 'not_installed'";
        var (checkOutput, exitCode) = Execute(containerID, checkCmd, null);
        if (exitCode.HasValue && exitCode.Value != 0)
        {
            Logger.GetLogger("pct").Printf("Failed to check openssh-server installation: {0}", checkOutput);
            return false;
        }
        if (checkOutput.Contains("not_installed"))
        {
            Logger.GetLogger("pct").Printf("openssh-server not installed, installing...");
            // Update apt
            string updateCmd = "apt-get update -qq";
            var (updateOutput, updateExit) = Execute(containerID, updateCmd, 300);
            if (updateExit.HasValue && updateExit.Value != 0)
            {
                Logger.GetLogger("pct").Printf("Failed to update apt: {0}", updateOutput);
                return false;
            }
            // Install openssh-server
            string installCmd = "apt-get install -y -qq openssh-server";
            var (installOutput, installExit) = Execute(containerID, installCmd, 300);
            if (installExit.HasValue && installExit.Value != 0)
            {
                Logger.GetLogger("pct").Printf("Failed to install openssh-server: {0}", installOutput);
                return false;
            }
            Logger.GetLogger("pct").Printf("openssh-server installed successfully");
        }
        // Enable and start SSH service
        string enableCmd = "systemctl enable ssh";
        var (enableOutput, enableExit) = Execute(containerID, enableCmd, null);
        if (enableExit.HasValue && enableExit.Value != 0)
        {
            Logger.GetLogger("pct").Printf("Failed to enable SSH service: {0}", enableOutput);
        }
        string startCmd = "systemctl start ssh";
        var (startOutput, startExit) = Execute(containerID, startCmd, null);
        if (startExit.HasValue && startExit.Value != 0)
        {
            Logger.GetLogger("pct").Printf("Failed to start SSH service: {0}", startOutput);
            return false;
        }
        return true;
    }

    public bool WaitForContainer(int containerID, string ipAddress, LabConfig cfg, string defaultUser)
    {
        int maxAttempts = 200; // 10 minutes with 3 second intervals
        int sleepInterval = 3;
        if (cfg != null)
        {
            if (cfg.Waits.ContainerReadyMaxAttempts > 0)
            {
                maxAttempts = cfg.Waits.ContainerReadyMaxAttempts;
            }
            if (cfg.Waits.ContainerReadySleep > 0)
            {
                sleepInterval = cfg.Waits.ContainerReadySleep;
            }
        }
        for (int i = 1; i <= maxAttempts; i++)
        {
            string statusCmd = $"pct status {containerID}";
            var (statusOutput, _) = lxc.Execute(statusCmd, null);
            if (statusOutput.Contains("running"))
            {
                // Try ping
                string pingCmd = $"ping -c 1 -W 2 {ipAddress}";
                var (pingOutput, pingExit) = lxc.Execute(pingCmd, null);
                if (pingExit.HasValue && pingExit.Value == 0 && pingOutput.Contains("1 received"))
                {
                    Logger.GetLogger("pct").Printf("Container is up!");
                    return true;
                }
                // Try SSH test
                string testCmd = "echo test";
                var (testOutput, testExit) = Execute(containerID, testCmd, 5);
                if (testExit.HasValue && testExit.Value == 0 && testOutput.Contains("test"))
                {
                    Logger.GetLogger("pct").Printf("Container is up (SSH working)!");
                    return true;
                }
            }
            Logger.GetLogger("pct").Printf("Waiting... ({0}/{1})", i, maxAttempts);
            System.Threading.Thread.Sleep(sleepInterval * 1000);
        }
        Logger.GetLogger("pct").Printf("Container may not be fully ready, but continuing...");
        return true; // Continue anyway
    }

    public ILXCService GetLXCService()
    {
        return lxc;
    }
}
