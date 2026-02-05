using System;
using System.Diagnostics;
using System.IO;
using Enva.Libs;

namespace Enva.Commands;

/// <summary>
/// Updates ~/.ssh/known_hosts for the K3s control node (e.g. after redeploy when host key changes).
/// Runs: ssh-keygen -R IP, ssh-keyscan -H IP >> known_hosts, then verifies with ssh user@IP.
/// </summary>
public class UpdateControlNodeSshKeyCommand
{
    private readonly LabConfig? _cfg;
    private readonly string _environment;

    public UpdateControlNodeSshKeyCommand(LabConfig? cfg, string environment)
    {
        _cfg = cfg;
        _environment = environment;
    }

    public int Run()
    {
        var logger = Logger.GetLogger("update-control-node-ssh-key");
        if (_cfg == null)
        {
            logger.Printf("Configuration not loaded.");
            return 1;
        }
        if (_cfg.Kubernetes == null || _cfg.KubernetesControl == null || _cfg.KubernetesControl.Count == 0)
        {
            logger.Printf("No Kubernetes control node configured.");
            return 1;
        }

        var controlNode = _cfg.KubernetesControl[0];
        string? ip = controlNode.IPAddress;
        if (string.IsNullOrEmpty(ip))
        {
            logger.Printf("Control node IP not found.");
            return 1;
        }

        string user = _cfg.Users.DefaultUser();
        string knownHostsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".ssh", "known_hosts");

        logger.Printf("Updating SSH known_hosts for control node {0} ({1})...", ip, _environment);

        // 1. Remove old host keys
        int exit = RunProcess(logger, "ssh-keygen", $"-R {ip}", "Remove old host key");
        if (exit != 0)
        {
            // ssh-keygen returns 1 if key was not found; that's ok
            logger.Printf("ssh-keygen -R completed (exit {0})", exit);
        }

        // 2. Add fresh host keys (append to known_hosts)
        string keyscanOutput = RunProcessCapture("ssh-keyscan", $"-H {ip}", out int keyscanExit);
        if (keyscanExit != 0)
        {
            logger.Printf("ssh-keyscan failed (exit {0}): {1}", keyscanExit, keyscanOutput);
            return 1;
        }
        try
        {
            string? dir = Path.GetDirectoryName(knownHostsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.AppendAllText(knownHostsPath, keyscanOutput);
            if (!string.IsNullOrEmpty(keyscanOutput))
            {
                logger.Printf("Added host keys to {0}", knownHostsPath);
            }
        }
        catch (Exception ex)
        {
            logger.Printf("Failed to write known_hosts: {0}", ex.Message);
            return 1;
        }

        // 3. Verify: SSH to control node
        int verifyExit = RunProcess(logger, "ssh", $"{user}@{ip} -o BatchMode=yes -o ConnectTimeout=5 true", "Verify SSH connection");
        if (verifyExit != 0)
        {
            logger.Printf("SSH verification failed (exit {0}). Host key may still be wrong or host unreachable.", verifyExit);
            return 1;
        }

        logger.Printf("Control node SSH key updated and verified for {0}.", ip);
        return 0;
    }

    private static int RunProcess(Logger logger, string fileName, string arguments, string description)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (!string.IsNullOrEmpty(stdout))
            logger.Printf(stdout);
        if (!string.IsNullOrEmpty(stderr))
            logger.Printf(stderr);
        return process.ExitCode;
    }

    private static string RunProcessCapture(string fileName, string arguments, out int exitCode)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        exitCode = process.ExitCode;
        return stdout + (string.IsNullOrEmpty(stderr) ? "" : "\n" + stderr);
    }
}
