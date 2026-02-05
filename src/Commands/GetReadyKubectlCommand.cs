using System;
using System.Diagnostics;
using System.IO;
using Enva.Libs;

namespace Enva.Commands;

/// <summary>
/// Configures local kubectl context for the given environment: fetches kubeconfig from the K3s control node via SSH,
/// sets server URL and context/cluster/user names to the environment name, merges into ~/.kube/config, verifies, and switches context.
/// </summary>
public class GetReadyKubectlCommand
{
    private readonly LabConfig? _cfg;
    private readonly string _environment;

    public GetReadyKubectlCommand(LabConfig? cfg, string environment)
    {
        _cfg = cfg;
        _environment = environment;
    }

    public int Run()
    {
        var logger = Logger.GetLogger("get-ready-kubectl");
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
        string kubeDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".kube");
        string defaultKubeConfig = Path.Combine(kubeDir, "config");
        string tempKubeConfig = Path.Combine(Path.GetTempPath(), $"{_environment}-kubeconfig.yaml");
        string mergedPath = Path.Combine(Path.GetTempPath(), "merged-kubeconfig.yaml");

        logger.Printf("Configuring kubectl for environment '{0}' (control node {1})...", _environment, ip);

        // 1. Optional: check if context already exists and is correct
        if (ContextExistsAndCorrect(logger, defaultKubeConfig, out int checkExit))
        {
            if (checkExit != 0)
            {
                logger.Printf("Context check failed (exit {0}), will create or update context.", checkExit);
            }
            else
            {
                SwitchContext(logger, defaultKubeConfig);
                logger.Printf("Context '{0}' already correct; switched to it.", _environment);
                return 0;
            }
        }

        // 2. Fetch kubeconfig via SSH (no pct)
        string rawKubeConfig = FetchKubeConfigViaSsh(logger, user, ip);
        if (string.IsNullOrEmpty(rawKubeConfig))
        {
            logger.Printf("Failed to fetch kubeconfig from control node.");
            return 1;
        }

        // 3. Transform: fix server URL to config IP, rename default -> environment (keep CA from control node for TLS)
        string prepared = rawKubeConfig
            .Replace("server: https://127.0.0.1:6443", "server: https://" + ip + ":6443")
            .Replace("server: https://0.0.0.0:6443", "server: https://" + ip + ":6443")
            .Replace("current-context: default", "current-context: " + _environment)
            .Replace("name: default", "name: " + _environment)
            .Replace("cluster: default", "cluster: " + _environment)
            .Replace("user: default", "user: " + _environment);

        try
        {
            File.WriteAllText(tempKubeConfig, prepared);
        }
        catch (Exception ex)
        {
            logger.Printf("Failed to write temp kubeconfig: {0}", ex.Message);
            return 1;
        }

        // 4. Merge into ~/.kube/config
        if (!Directory.Exists(kubeDir))
        {
            Directory.CreateDirectory(kubeDir);
        }

        if (!File.Exists(defaultKubeConfig))
        {
            File.Copy(tempKubeConfig, defaultKubeConfig);
            try
            {
                // Restrict permissions on Unix
                if (Environment.OSVersion.Platform == PlatformID.Unix)
                {
                    var process = new Process
                    {
                        StartInfo = new ProcessStartInfo
                        {
                            FileName = "chmod",
                            Arguments = "600 " + defaultKubeConfig,
                            UseShellExecute = false
                        }
                    };
                    process.Start();
                    process.WaitForExit();
                }
            }
            catch
            {
                // Ignore chmod errors
            }
        }
        else
        {
            // Remove existing context/cluster/user for this environment so merge doesn't combine old (insecure) with new (CA)
            RunProcessWithKubeConfigSilent(defaultKubeConfig, "kubectl", $"config delete-context {_environment}");
            RunProcessWithKubeConfigSilent(defaultKubeConfig, "kubectl", $"config delete-cluster {_environment}");
            RunProcessWithKubeConfigSilent(defaultKubeConfig, "kubectl", $"config unset users.{_environment}");
            // Put our temp config first so our cluster/context (with correct server and TLS) wins
            string kubeConfigMergeOurFirst = tempKubeConfig + ":" + defaultKubeConfig;
            string merged = RunProcessCaptureEnv("KUBECONFIG", kubeConfigMergeOurFirst, "kubectl", "config view --flatten", out int mergeExit);
            if (mergeExit != 0)
            {
                logger.Printf("kubectl config view --flatten failed (exit {0}): {1}", mergeExit, merged);
                try { File.Delete(tempKubeConfig); } catch { }
                return 1;
            }
            try
            {
                File.WriteAllText(mergedPath, merged);
                File.Copy(mergedPath, defaultKubeConfig, true);
                File.Delete(mergedPath);
            }
            catch (Exception ex)
            {
                logger.Printf("Failed to write merged kubeconfig: {0}", ex.Message);
                try { File.Delete(tempKubeConfig); } catch { }
                return 1;
            }
        }

        try { File.Delete(tempKubeConfig); } catch { }

        // 5. Verify
        int verifyExit = RunProcessWithKubeConfig(logger, defaultKubeConfig, "kubectl", $"--context={_environment} get nodes -o wide", "Verify nodes");
        if (verifyExit != 0)
        {
            logger.Printf("Verification failed (exit {0}). Context may not work.", verifyExit);
            return 1;
        }

        // 6. Switch to context
        SwitchContext(logger, defaultKubeConfig);
        logger.Printf("kubectl is ready for environment '{0}'. Current context: {0}.", _environment);
        return 0;
    }

    private bool ContextExistsAndCorrect(Logger logger, string kubeConfigPath, out int exitCode)
    {
        exitCode = 0;
        if (!File.Exists(kubeConfigPath))
            return false;

        int getExit = RunProcessWithKubeConfig(logger, kubeConfigPath, "kubectl", $"config get-contexts", "List contexts");
        if (getExit != 0)
        {
            exitCode = getExit;
            return false;
        }

        int nodesExit = RunProcessWithKubeConfig(logger, kubeConfigPath, "kubectl", $"--context={_environment} get nodes -o wide", "Check nodes");
        if (nodesExit != 0)
            return false;

        // Context exists and kubectl get nodes succeeded; consider it correct
        return true;
    }

    private void SwitchContext(Logger logger, string kubeConfigPath)
    {
        RunProcessWithKubeConfig(logger, kubeConfigPath, "kubectl", $"config use-context {_environment}", "Switch context");
    }

    private static string FetchKubeConfigViaSsh(Logger logger, string user, string ip)
    {
        string cmd = $"ssh -o ConnectTimeout=10 -o BatchMode=yes {user}@{ip} 'cat /etc/rancher/k3s/k3s.yaml'";
        string output = RunProcessCapture("/bin/sh", $"-c \"{cmd}\"", out int exitCode);
        if (exitCode == 0 && !string.IsNullOrWhiteSpace(output))
            return output;

        // Try with sudo
        cmd = $"ssh -o ConnectTimeout=10 -o BatchMode=yes {user}@{ip} 'sudo cat /etc/rancher/k3s/k3s.yaml'";
        output = RunProcessCapture("/bin/sh", $"-c \"{cmd}\"", out exitCode);
        if (exitCode != 0)
        {
            logger.Printf("Failed to fetch kubeconfig via SSH (exit {0}): {1}", exitCode, output);
            return "";
        }
        return output ?? "";
    }

    private static int RunProcessWithKubeConfig(Logger logger, string kubeConfigPath, string fileName, string arguments, string description)
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
        process.StartInfo.Environment["KUBECONFIG"] = kubeConfigPath;
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

    private static void RunProcessWithKubeConfigSilent(string kubeConfigPath, string fileName, string arguments)
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
        process.StartInfo.Environment["KUBECONFIG"] = kubeConfigPath;
        process.Start();
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
    }

    private static string RunProcessCaptureEnv(string envName, string envValue, string fileName, string arguments, out int exitCode)
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
        process.StartInfo.Environment[envName] = envValue;
        process.Start();
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        exitCode = process.ExitCode;
        return stdout + (string.IsNullOrEmpty(stderr) ? "" : "\n" + stderr);
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
