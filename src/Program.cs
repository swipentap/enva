using System;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.IO;
using System.Linq;
using Enva.Actions;
using Enva.Commands;
using Enva.Libs;
using Enva.Services;

namespace Enva;

class Program
{
    private static bool verbose = false;
    private static string configFile = "";
    private static string githubToken = "";

    static int Main(string[] args)
    {
        // Initialize actions first
        ActionInit.Initialize();
        
        // Initialize logging BEFORE parsing arguments (matching Python behavior)
        LogLevel logLevel = LogLevel.Info;
        if (args.Length > 0)
        {
            // Check for verbose flag before command line parsing
            foreach (string arg in args)
            {
                if (arg == "-v" || arg == "--verbose")
                {
                    logLevel = LogLevel.Debug;
                    break;
                }
            }
        }
        try
        {
            Logger.InitLogger(logLevel, "", true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to initialize logger: {ex.Message}");
            return 1;
        }

        try
        {
            var rootCommand = new RootCommand("EnvA CLI - Manage LXC containers and Docker Swarm")
            {
                Description = "EnvA CLI tool for deploying and managing Kubernetes (k3s) clusters with supporting services on LXC containers"
            };

            var verboseOption = new Option<bool>(
                new[] { "--verbose", "-v" },
                "Show stdout from SSH service");
            rootCommand.AddGlobalOption(verboseOption);

            var configOption = new Option<string>(
                new[] { "--config", "-c" },
                () => "",
                "Path to YAML configuration file (default: enva.yaml)");
            rootCommand.AddGlobalOption(configOption);

            var githubTokenOption = new Option<string>(
                "--github-token",
                () => "",
                "GitHub token for creating GitHub runner secrets");
            rootCommand.AddGlobalOption(githubTokenOption);

            // Deploy command
            var deployCommand = new Command("deploy", "Deploy complete environment: apt-cache, templates, and Docker Swarm");
            var deployEnvironmentArgument = new Argument<string>("environment", "Environment name");
            deployCommand.AddArgument(deployEnvironmentArgument);
            var deployStartStepOption = new Option<int>(
                "--start-step",
                () => 1,
                "Start from this step (default: 1)");
            deployCommand.AddOption(deployStartStepOption);
            var deployEndStepOption = new Option<int>(
                "--end-step",
                () => 0,
                "End at this step (default: last step, 0 means last)");
            deployCommand.AddOption(deployEndStepOption);
            var deployPlanOnlyOption = new Option<bool>(
                "--planonly",
                "Show deployment plan and exit without executing");
            deployCommand.AddOption(deployPlanOnlyOption);
            var deployUpdateSshKeyOption = new Option<bool>(
                "--update-control-node-ssh-key",
                "After deploy, update ~/.ssh/known_hosts for the K3s control node");
            deployCommand.AddOption(deployUpdateSshKeyOption);
            var deployGetReadyKubectlOption = new Option<bool>(
                "--get-ready-kubectl",
                "After deploy, configure kubectl context for this environment");
            deployCommand.AddOption(deployGetReadyKubectlOption);
            deployCommand.SetHandler((InvocationContext context) =>
            {
                var pr = context.ParseResult;
                verbose = pr.GetValueForOption(verboseOption);
                configFile = pr.GetValueForOption(configOption) ?? "";
                githubToken = pr.GetValueForOption(githubTokenOption) ?? "";
                string environment = pr.GetValueForArgument(deployEnvironmentArgument) ?? "";
                int startStep = pr.GetValueForOption(deployStartStepOption);
                int endStep = pr.GetValueForOption(deployEndStepOption);
                bool planOnly = pr.GetValueForOption(deployPlanOnlyOption);
                bool updateSshKey = pr.GetValueForOption(deployUpdateSshKeyOption);
                bool getReadyKubectl = pr.GetValueForOption(deployGetReadyKubectlOption);
                try
                {
                    RunDeploy(environment, startStep, endStep, planOnly, updateSshKey, getReadyKubectl);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    Environment.Exit(1);
                }
            });
            rootCommand.AddCommand(deployCommand);

            // Cleanup command
            var cleanupCommand = new Command("cleanup", "Remove all containers and templates");
            var cleanupEnvironmentArgument = new Argument<string>("environment", "Environment name");
            cleanupCommand.AddArgument(cleanupEnvironmentArgument);
            cleanupCommand.SetHandler((string environment, bool verboseOpt, string configOpt) =>
            {
                verbose = verboseOpt;
                configFile = configOpt;
                try
                {
                    RunCleanup(environment);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    Environment.Exit(1);
                }
            }, cleanupEnvironmentArgument, verboseOption, configOption);
            rootCommand.AddCommand(cleanupCommand);

            // Redeploy command
            var redeployCommand = new Command("redeploy", "Cleanup and then deploy complete environment");
            var redeployEnvironmentArgument = new Argument<string>("environment", "Environment name");
            redeployCommand.AddArgument(redeployEnvironmentArgument);
            var redeployStartStepOption = new Option<int>(
                "--start-step",
                () => 1,
                "Start from this step (default: 1)");
            redeployCommand.AddOption(redeployStartStepOption);
            var redeployEndStepOption = new Option<int>(
                "--end-step",
                () => 0,
                "End at this step (default: last step, 0 means last)");
            redeployCommand.AddOption(redeployEndStepOption);
            var redeployUpdateSshKeyOption = new Option<bool>(
                "--update-control-node-ssh-key",
                "After redeploy, update ~/.ssh/known_hosts for the K3s control node");
            redeployCommand.AddOption(redeployUpdateSshKeyOption);
            var redeployGetReadyKubectlOption = new Option<bool>(
                "--get-ready-kubectl",
                "After redeploy, configure kubectl context for this environment");
            redeployCommand.AddOption(redeployGetReadyKubectlOption);
            var redeployPlanOnlyOption = new Option<bool>(
                "--planonly",
                "Show deployment plan and exit without executing");
            redeployCommand.AddOption(redeployPlanOnlyOption);
            redeployCommand.SetHandler((InvocationContext context) =>
            {
                var pr = context.ParseResult;
                verbose = pr.GetValueForOption(verboseOption);
                configFile = pr.GetValueForOption(configOption) ?? "";
                githubToken = pr.GetValueForOption(githubTokenOption) ?? "";
                string environment = pr.GetValueForArgument(redeployEnvironmentArgument) ?? "";
                int startStep = pr.GetValueForOption(redeployStartStepOption);
                int endStep = pr.GetValueForOption(redeployEndStepOption);
                bool updateSshKey = pr.GetValueForOption(redeployUpdateSshKeyOption);
                bool getReadyKubectl = pr.GetValueForOption(redeployGetReadyKubectlOption);
                bool planOnly = pr.GetValueForOption(redeployPlanOnlyOption);
                try
                {
                    RunRedeploy(environment, startStep, endStep, updateSshKey, getReadyKubectl, planOnly);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    Environment.Exit(1);
                }
            });
            rootCommand.AddCommand(redeployCommand);

            // Status command
            var statusCommand = new Command("status", "Show current environment status");
            var statusEnvironmentArgument = new Argument<string>("environment", "Environment name");
            statusCommand.AddArgument(statusEnvironmentArgument);
            statusCommand.SetHandler((string environment, bool verboseOpt, string configOpt) =>
            {
                verbose = verboseOpt;
                configFile = configOpt;
                try
                {
                    RunStatus(environment);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    Environment.Exit(1);
                }
            }, statusEnvironmentArgument, verboseOption, configOption);
            rootCommand.AddCommand(statusCommand);

            // Backup command
            var backupCommand = new Command("backup", "Backup cluster according to enva.yaml configuration");
            var backupEnvironmentArgument = new Argument<string>("environment", "Environment name");
            backupCommand.AddArgument(backupEnvironmentArgument);
            var backupTimeoutOption = new Option<int>(
                "--timeout",
                () => 0,
                "Timeout in seconds for backup operations (0 = use defaults, applies to final tarball and archive operations)");
            backupCommand.AddOption(backupTimeoutOption);
            var backupExcludeOption = new Option<string[]>(
                "--exclude",
                () => Array.Empty<string>(),
                "Paths to exclude from backup (relative to archive base, can be specified multiple times)");
            backupCommand.AddOption(backupExcludeOption);
            var backupShowSizesOption = new Option<bool>(
                "--show-sizes",
                "Show directory/file sizes before backing up");
            backupCommand.AddOption(backupShowSizesOption);
            var backupCheckSpaceOption = new Option<bool>(
                "--check-space",
                "Show what's taking up space in directories before backing up");
            backupCommand.AddOption(backupCheckSpaceOption);
            backupCommand.SetHandler((string environment, bool verboseOpt, string configOpt, int timeout, string[] exclude, bool showSizes, bool checkSpace) =>
            {
                verbose = verboseOpt;
                configFile = configOpt;
                try
                {
                    RunBackup(environment, timeout, exclude.ToList(), showSizes, checkSpace);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    Environment.Exit(1);
                }
            }, backupEnvironmentArgument, verboseOption, configOption, backupTimeoutOption, backupExcludeOption, backupShowSizesOption, backupCheckSpaceOption);
            rootCommand.AddCommand(backupCommand);

            // Restore command
            var restoreCommand = new Command("restore", "Restore cluster from backup");
            var restoreEnvironmentArgument = new Argument<string>("environment", "Environment name");
            restoreCommand.AddArgument(restoreEnvironmentArgument);
            var restoreBackupNameOption = new Option<string>(
                "--backup-name",
                "Name of the backup to restore (e.g., backup-20251130_120000)")
            {
                IsRequired = true
            };
            restoreCommand.AddOption(restoreBackupNameOption);
            restoreCommand.SetHandler((string environment, bool verboseOpt, string configOpt, string backupName) =>
            {
                verbose = verboseOpt;
                configFile = configOpt;
                try
                {
                    RunRestore(environment, backupName);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Error: {ex.Message}");
                    Environment.Exit(1);
                }
            }, restoreEnvironmentArgument, verboseOption, configOption, restoreBackupNameOption);
            rootCommand.AddCommand(restoreCommand);

            return rootCommand.Invoke(args);
        }
        finally
        {
            Logger.CloseLogFile();
        }
    }

    private static LabConfig GetConfig(string environment)
    {
        // Determine config file path (matching Python: use script directory)
        string cfgPath = configFile;
        if (string.IsNullOrEmpty(cfgPath))
        {
            // For C#, use executable directory or current directory
            try
            {
                string? exePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
                if (!string.IsNullOrEmpty(exePath))
                {
                    string exeDir = Path.GetDirectoryName(exePath) ?? "";
                    string defaultPath = Path.Combine(exeDir, "enva.yaml");
                    if (File.Exists(defaultPath))
                    {
                        cfgPath = defaultPath;
                    }
                }
            }
            catch
            {
                // Ignore errors
            }
            // Fallback to current directory
            if (string.IsNullOrEmpty(cfgPath))
            {
                if (File.Exists("enva.yaml"))
                {
                    cfgPath = "enva.yaml";
                }
                else
                {
                    cfgPath = "enva.yaml"; // Default
                }
            }
        }

        // Load and process config
        var envaConfig = ConfigLoader.LoadConfig(cfgPath);
        var cfg = ConfigLoader.ToLabConfig(envaConfig, environment, verbose);
        return cfg;
    }

    private static void RunDeploy(string environment, int startStep, int endStep, bool planOnly, bool updateSshKey, bool getReadyKubectl)
    {
        // Set GitHub token as environment variable if provided via CLI
        if (!string.IsNullOrEmpty(githubToken))
        {
            Environment.SetEnvironmentVariable("ENVA_GITHUB_TOKEN", githubToken);
        }

        var cfg = GetConfig(environment);

        int? endStepPtr = null;
        if (endStep > 0)
        {
            endStepPtr = endStep;
        }

        var lxcService = new LXCService(cfg.LXCHost(), cfg.SSH);
        var pctService = new PCTService(lxcService);
        var deployCmd = new DeployCommand(cfg, lxcService, pctService);
        deployCmd.Run(startStep, endStepPtr, planOnly);

        if (!planOnly)
        {
            // Order matters: update SSH key first so get-ready-kubectl can SSH to control node without host key errors
            if (updateSshKey)
            {
                int rc = RunUpdateControlNodeSshKey(environment);
                if (rc != 0)
                    throw new Exception($"update-control-node-ssh-key failed with exit code {rc}");
            }
            if (getReadyKubectl)
            {
                int rc = RunGetReadyKubectl(environment);
                if (rc != 0)
                    throw new Exception($"get-ready-kubectl failed with exit code {rc}");
            }
        }
    }

    private static void RunCleanup(string environment)
    {
        var cfg = GetConfig(environment);

        var lxcService = new LXCService(cfg.LXCHost(), cfg.SSH);
        var pctService = new PCTService(lxcService);
        var cleanupCmd = new CleanupCommand(cfg, lxcService, pctService);
        cleanupCmd.Run();
    }

    private static void RunRedeploy(string environment, int startStep, int endStep, bool updateSshKey, bool getReadyKubectl, bool planOnly)
    {
        // Set GitHub token as environment variable if provided via CLI
        if (!string.IsNullOrEmpty(githubToken))
        {
            Environment.SetEnvironmentVariable("ENVA_GITHUB_TOKEN", githubToken);
        }

        var cfg = GetConfig(environment);

        int? endStepPtr = null;
        if (endStep > 0)
        {
            endStepPtr = endStep;
        }

        var logger = Logger.GetLogger("main");
        logger.Printf("=== Redeploy: Cleanup and Deploy ===");

        var lxcService = new LXCService(cfg.LXCHost(), cfg.SSH);
        try
        {
            var pctService = new PCTService(lxcService);
            if (planOnly)
            {
                logger.Printf("\n[1/2] Cleanup (plan only) — containers that would be destroyed:");
                var cleanupCmdPlan = new CleanupCommand(cfg, lxcService, pctService);
                cleanupCmdPlan.RunPlanOnly();
            }
            else
            {
                logger.Printf("\n[1/2] Running cleanup...");
                var cleanupCmd = new CleanupCommand(cfg, lxcService, pctService);
                try
                {
                    cleanupCmd.Run();
                }
                catch
                {
                    // Ignore cleanup errors
                }
            }

            logger.Printf("\n[2/2] Running deploy{0}...", planOnly ? " (plan only)" : "");
            var deployCmd = new DeployCommand(cfg, lxcService, pctService);
            deployCmd.Run(startStep, endStepPtr, planOnly);

            if (!planOnly)
            {
                logger.Printf("=== Redeploy completed! ===");
            }

            // Run these also in planonly mode for now (to test them)
            // Order matters: update SSH key first so get-ready-kubectl can SSH to control node without host key errors
            if (updateSshKey)
            {
                int rc = RunUpdateControlNodeSshKey(environment);
                if (rc != 0)
                    throw new Exception($"update-control-node-ssh-key failed with exit code {rc}");
            }
            if (getReadyKubectl)
            {
                int rc = RunGetReadyKubectl(environment);
                if (rc != 0)
                    throw new Exception($"get-ready-kubectl failed with exit code {rc}");
            }
        }
        finally
        {
            lxcService.Disconnect();
        }
    }

    private static void RunStatus(string environment)
    {
        var cfg = GetConfig(environment);

        var lxcService = new LXCService(cfg.LXCHost(), cfg.SSH);
        var pctService = new PCTService(lxcService);
        var statusCmd = new StatusCommand(cfg, lxcService, pctService);
        statusCmd.Run();
    }

    private static void RunBackup(string environment, int timeout, System.Collections.Generic.List<string> excludePaths, bool showSizes, bool checkSpace)
    {
        var cfg = GetConfig(environment);

        int? timeoutPtr = null;
        if (timeout > 0)
        {
            timeoutPtr = timeout;
        }

        var lxcService = new LXCService(cfg.LXCHost(), cfg.SSH);
        var pctService = new PCTService(lxcService);
        var backupCmd = new BackupCommand(cfg, lxcService, pctService);
        backupCmd.Run(timeoutPtr, excludePaths, showSizes, checkSpace);
    }

    private static void RunRestore(string environment, string backupName)
    {
        var cfg = GetConfig(environment);

        if (string.IsNullOrEmpty(backupName))
        {
            throw new Exception("--backup-name is required");
        }

        var lxcService = new LXCService(cfg.LXCHost(), cfg.SSH);
        var pctService = new PCTService(lxcService);
        var restoreCmd = new RestoreCommand(cfg, lxcService, pctService);
        restoreCmd.Run(backupName);
    }

    private static int RunUpdateControlNodeSshKey(string environment)
    {
        var cfg = GetConfig(environment);
        var cmd = new UpdateControlNodeSshKeyCommand(cfg, environment);
        return cmd.Run();
    }

    private static int RunGetReadyKubectl(string environment)
    {
        var cfg = GetConfig(environment);
        var cmd = new GetReadyKubectlCommand(cfg, environment);
        return cmd.Run();
    }
}