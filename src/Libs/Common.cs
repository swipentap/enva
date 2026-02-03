using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Enva.Libs;

public static class Common
{
    public static bool ContainerExists(string lxcHost, int containerID, LabConfig? cfg, ILXCService? lxcService)
    {
        var containerIDStr = containerID.ToString();
        var cmd = $"pct list | grep '^{containerIDStr} '";

        string result = "";
        if (lxcService != null)
        {
            (result, _) = lxcService.Execute(cmd, null);
        }
        else if (cfg != null)
        {
            // Fallback: create temporary service
            // Note: This requires importing services, which creates a cycle
            // Callers should pass lxcService to avoid this
            return false;
        }
        else
        {
            // Fallback to subprocess
            var sshCmd = $"ssh -o ConnectTimeout=10 -o BatchMode=yes {lxcHost} \"{cmd}\"";
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = $"-c \"{sshCmd}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            try
            {
                process.Start();
                result = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
            }
            catch
            {
                // Ignore
            }
        }

        return !string.IsNullOrEmpty(result) && result.Contains(containerIDStr);
    }

    public static void DestroyContainer(string lxcHost, int containerID, LabConfig? cfg, ILXCService? lxcService)
    {
        var containerIDStr = containerID.ToString();

        if (lxcService == null)
        {
            Logger.GetLogger("common").Printf("lxc_service must be provided");
            return;
        }

        // Check if container exists
        var checkCmd = $"pct list | grep '^{containerIDStr} ' || echo 'not_found'";
        var (checkOutput, _) = lxcService.Execute(checkCmd, null);
        if (string.IsNullOrEmpty(checkOutput) || !checkOutput.Contains(containerIDStr) || checkOutput.Contains("not_found"))
        {
            Logger.GetLogger("common").Printf("Container {0} does not exist, skipping", containerID);
            return;
        }

        // Stop and destroy
        Logger.GetLogger("common").Printf("Stopping and destroying container {0}...", containerID);
        var destroyCmd = $"pct stop {containerID} || true; sleep 2; pct destroy {containerID}";
        var (_, destroyExit) = lxcService.Execute(destroyCmd, null);
        if (destroyExit != null && destroyExit.Value != 0)
        {
            Logger.GetLogger("common").Printf("Destroy failed, trying force destroy...");
            var forceCmd = $"pct destroy {containerIDStr} --force || true";
            lxcService.Execute(forceCmd, null);
            Thread.Sleep(1000);
        }

        // Verify destruction
        var verifyCmd = $"pct list | grep '^{containerIDStr} ' || echo 'not_found'";
        var (verifyOutput, _) = lxcService.Execute(verifyCmd, null);
        if (string.IsNullOrEmpty(verifyOutput) || !verifyOutput.Contains(containerIDStr) || verifyOutput.Contains("not_found"))
        {
            Logger.GetLogger("common").Printf("Container {0} destroyed", containerIDStr);
        }
        else
        {
            Logger.GetLogger("common").Printf("Container {0} still exists after destruction attempt", containerIDStr);
        }
    }

    public static bool WaitForContainer(string lxcHost, int containerID, string ipAddress, int? maxAttempts, int? sleepInterval, LabConfig? cfg)
    {
        var maxAttemptsVal = maxAttempts ?? (cfg?.Waits.ContainerReadyMaxAttempts ?? 30);
        var sleepIntervalVal = sleepInterval ?? (cfg?.Waits.ContainerReadySleep ?? 3);

        // Note: WaitForContainer needs lxcService parameter to avoid import cycle
        // This is a simplified version that uses subprocess fallback
        for (int i = 1; i <= maxAttemptsVal; i++)
        {
            string status = "";
            var cmd = $"ssh -o ConnectTimeout=10 {lxcHost} \"pct status {containerID}\"";
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/sh",
                    Arguments = $"-c \"{cmd}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            try
            {
                process.Start();
                status = process.StandardOutput.ReadToEnd();
                process.WaitForExit();
            }
            catch
            {
                // Ignore
            }

            if (status.Contains("running"))
            {
                // Try ping
                var pingProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ping",
                        Arguments = $"-c 1 -W 2 {ipAddress}",
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                try
                {
                    pingProcess.Start();
                    pingProcess.WaitForExit();
                    if (pingProcess.ExitCode == 0)
                    {
                        Logger.GetLogger("common").Printf("Container is up!");
                        return true;
                    }
                }
                catch
                {
                    // Ignore
                }
            }
            Logger.GetLogger("common").Printf("Waiting... ({0}/{1})", i, maxAttemptsVal);
            Thread.Sleep(TimeSpan.FromSeconds(sleepIntervalVal));
        }
        Logger.GetLogger("common").Printf("Container may not be fully ready, but continuing...");
        return true; // Continue anyway
    }

    public static string GetSSHKey()
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(homeDir))
        {
            return "";
        }

        var keyPaths = new[]
        {
            Path.Combine(homeDir, ".ssh", "id_rsa.pub"),
            Path.Combine(homeDir, ".ssh", "id_ed25519.pub")
        };

        foreach (var keyPath in keyPaths)
        {
            try
            {
                if (File.Exists(keyPath))
                {
                    var data = File.ReadAllText(keyPath);
                    return data.Trim();
                }
            }
            catch
            {
                // Continue to next path
            }
        }
        return "";
    }

    public static bool SetupSSHKey(int containerID, string ipAddress, LabConfig cfg, ILXCService? lxcService, IPCTService? pctService)
    {
        var sshKey = GetSSHKey();
        if (string.IsNullOrEmpty(sshKey))
        {
            Logger.GetLogger("common").Printf("SSH public key not found.");
            return false;
        }
        if (cfg == null)
        {
            Logger.GetLogger("common").Printf("Configuration required for SSH key setup");
            return false;
        }
        if (lxcService == null || pctService == null)
        {
            Logger.GetLogger("common").Printf("LXC or PCT service not provided for SetupSSHKey, skipping.");
            return false;
        }

        var defaultUser = cfg.Users.DefaultUser();

        // Remove old host key
        var removeKeyProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "/bin/sh",
                Arguments = $"-c \"ssh-keygen -R {ipAddress}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        try
        {
            removeKeyProcess.Start();
            removeKeyProcess.WaitForExit();
        }
        catch
        {
            // Ignore
        }

        // Base64 encode the key to avoid any shell escaping problems
        var keyBytes = Encoding.UTF8.GetBytes(sshKey);
        var keyB64 = Convert.ToBase64String(keyBytes);

        // Add to default user
        var userCmd = $"mkdir -p /home/{defaultUser}/.ssh && echo {keyB64} | base64 -d > /home/{defaultUser}/.ssh/authorized_keys && chmod 600 /home/{defaultUser}/.ssh/authorized_keys && chown {defaultUser}:{defaultUser} /home/{defaultUser}/.ssh/authorized_keys";
        var (_, userExit) = pctService.Execute(containerID, userCmd, null);
        if (userExit != null && userExit.Value != 0)
        {
            Logger.GetLogger("common").Printf("Failed to add SSH key for user {0}: {1}", defaultUser, userCmd);
            return false;
        }

        // Add to root user
        var rootCmd = $"mkdir -p /root/.ssh && echo {keyB64} | base64 -d > /root/.ssh/authorized_keys && chmod 600 /root/.ssh/authorized_keys";
        var (_, rootExit) = pctService.Execute(containerID, rootCmd, null);
        if (rootExit != null && rootExit.Value != 0)
        {
            Logger.GetLogger("common").Printf("Failed to add SSH key for root: {0}", rootCmd);
            return false;
        }

        // Verify the key file exists
        var verifyCmd = $"test -f /home/{defaultUser}/.ssh/authorized_keys && test -f /root/.ssh/authorized_keys && echo 'keys_exist' || echo 'keys_missing'";
        var (verifyOutput, _) = pctService.Execute(containerID, verifyCmd, null);

        if (verifyOutput.Contains("keys_exist"))
        {
            Logger.GetLogger("common").Printf("SSH key setup verified successfully");
            return true;
        }
        Logger.GetLogger("common").Printf("SSH key verification failed: {0}", verifyOutput);
        return false;
    }

    public static int? IntPtr(int i)
    {
        return i;
    }
}
