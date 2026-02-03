using System;
using System.Linq;
using System.Threading;
using Enva.Libs;

namespace Enva.Services;

public class APTService
{
    private SSHService ssh;
    private const int AptLockWait = 600;
    private const int AptLongTimeout = 600;

    public APTService(SSHService sshService)
    {
        this.ssh = sshService;
    }

    public (string output, int? exitCode) Update()
    {
        // Wait for dpkg lock to be released before proceeding
        if (!WaitForLock())
        {
            Logger.GetLogger("apt").Printf("Timeout waiting for dpkg lock to be released");
            return ("", -1);
        }

        string cmd = "sudo -n DEBIAN_FRONTEND=noninteractive apt-get update";
        int timeout = AptLongTimeout;
        return ssh.Execute(cmd, timeout);
    }

    // Matches Python: apt_service.execute() always runs apt-get update first via _wait_for_package_manager()
    public (string output, int? exitCode) Install(string[] packages)
    {
        // Wait for dpkg lock to be released before proceeding
        if (!WaitForLock())
        {
            Logger.GetLogger("apt").Printf("Timeout waiting for dpkg lock to be released");
            return ("", -1);
        }

        // Always run apt-get update first (matching Python behavior)
        var (_, updateExitCode) = Update();
        if (updateExitCode.HasValue && updateExitCode.Value != 0)
        {
            return ("", updateExitCode);
        }

        // Wait for lock again after update (another process might have acquired it)
        if (!WaitForLock())
        {
            Logger.GetLogger("apt").Printf("Timeout waiting for dpkg lock to be released after update");
            return ("", -1);
        }

        string cmd = $"sudo -n DEBIAN_FRONTEND=noninteractive apt-get install -y {string.Join(" ", packages)}\n\n\n";
        int timeout = AptLongTimeout;
        return ssh.Execute(cmd, timeout);
    }

    // Matches Python: apt_service.execute() always runs apt-get update first via _wait_for_package_manager()
    public (string output, int? exitCode) Upgrade()
    {
        // Always run apt-get update first (matching Python behavior)
        var (_, updateExitCode) = Update();
        if (updateExitCode.HasValue && updateExitCode.Value != 0)
        {
            return ("", updateExitCode);
        }

        // Wait for lock again after update (another process might have acquired it)
        if (!WaitForLock())
        {
            Logger.GetLogger("apt").Printf("Timeout waiting for dpkg lock to be released after update");
            return ("", -1);
        }

        string cmd = "sudo -n DEBIAN_FRONTEND=noninteractive apt-get upgrade -y";
        int timeout = AptLongTimeout;
        return ssh.Execute(cmd, timeout);
    }

    // Matches Python: apt_service.execute() always runs apt-get update first via _wait_for_package_manager()
    public (string output, int? exitCode) DistUpgrade()
    {
        // Always run apt-get update first (matching Python behavior)
        var (_, updateExitCode) = Update();
        if (updateExitCode.HasValue && updateExitCode.Value != 0)
        {
            return ("", updateExitCode);
        }

        // Wait for lock again after update (another process might have acquired it)
        if (!WaitForLock())
        {
            Logger.GetLogger("apt").Printf("Timeout waiting for dpkg lock to be released after update");
            return ("", -1);
        }

        string cmd = "sudo -n DEBIAN_FRONTEND=noninteractive apt-get dist-upgrade -y";
        int timeout = AptLongTimeout;
        return ssh.Execute(cmd, timeout);
    }

    public (string output, int? exitCode) Clean()
    {
        string cmd = "sudo -n apt-get clean";
        return ssh.Execute(cmd, null);
    }

    public (string output, int? exitCode) Autoremove()
    {
        string cmd = "sudo -n DEBIAN_FRONTEND=noninteractive apt-get autoremove -y";
        return ssh.Execute(cmd, null);
    }

    public bool WaitForLock()
    {
        int maxAttempts = AptLockWait / 5;
        for (int i = 0; i < maxAttempts; i++)
        {
            string cmd = "lsof /var/lib/dpkg/lock-frontend /var/lib/dpkg/lock /var/cache/apt/archives/lock || echo 'no_lock'";
            var (output, _) = ssh.Execute(cmd, null);
            if (output.Contains("no_lock"))
            {
                return true;
            }
            Thread.Sleep(5000);
        }
        return false;
    }
}
