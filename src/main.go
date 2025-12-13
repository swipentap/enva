package main

import (
	"fmt"
	"os"
	"path/filepath"

	"enva/commands"
	"enva/libs"
	"enva/services"

	"github.com/spf13/cobra"
)

var (
	verbose    bool
	configFile string
)

func main() {
	// Initialize logging BEFORE parsing arguments (matching Python behavior)
	logLevel := libs.LogLevelInfo
	if len(os.Args) > 1 {
		// Check for verbose flag before cobra parses
		for _, arg := range os.Args[1:] {
			if arg == "-v" || arg == "--verbose" {
				logLevel = libs.LogLevelDebug
				break
			}
		}
	}
	_, err := libs.InitLogger(logLevel, "", true)
	if err != nil {
		fmt.Fprintf(os.Stderr, "Failed to initialize logger: %v\n", err)
		os.Exit(1)
	}
	defer libs.CloseLogFile()

	var rootCmd = &cobra.Command{
		Use:   "enva",
		Short: "EnvA CLI - Manage LXC containers and Docker Swarm",
		Long:  "EnvA CLI tool for deploying and managing Kubernetes (k3s) clusters with supporting services on LXC containers",
	}

	rootCmd.PersistentFlags().BoolVarP(&verbose, "verbose", "v", false, "Show stdout from SSH service")
	rootCmd.PersistentFlags().StringVarP(&configFile, "config", "c", "", "Path to YAML configuration file (default: enva.yaml)")

	// Deploy command
	var deployCmd = &cobra.Command{
		Use:          "deploy [environment]",
		Short:        "Deploy complete environment: apt-cache, templates, and Docker Swarm",
		Args:         cobra.ExactArgs(1),
		RunE:         runDeploy,
		SilenceUsage: true, // Don't print usage on error (match Python behavior)
	}
	deployCmd.Flags().Int("start-step", 1, "Start from this step (default: 1)")
	deployCmd.Flags().Int("end-step", 0, "End at this step (default: last step, 0 means last)")
	deployCmd.Flags().Bool("planonly", false, "Show deployment plan and exit without executing")
	rootCmd.AddCommand(deployCmd)

	// Cleanup command
	var cleanupCmd = &cobra.Command{
		Use:          "cleanup [environment]",
		Short:        "Remove all containers and templates",
		Args:         cobra.ExactArgs(1),
		RunE:         runCleanup,
		SilenceUsage: true, // Don't print usage on error (match Python behavior)
	}
	rootCmd.AddCommand(cleanupCmd)

	// Redeploy command
	var redeployCmd = &cobra.Command{
		Use:          "redeploy [environment]",
		Short:        "Cleanup and then deploy complete environment",
		Args:         cobra.ExactArgs(1),
		RunE:         runRedeploy,
		SilenceUsage: true, // Don't print usage on error (match Python behavior)
	}
	redeployCmd.Flags().Int("start-step", 1, "Start from this step (default: 1)")
	redeployCmd.Flags().Int("end-step", 0, "End at this step (default: last step, 0 means last)")
	rootCmd.AddCommand(redeployCmd)

	// Status command
	var statusCmd = &cobra.Command{
		Use:          "status [environment]",
		Short:        "Show current environment status",
		Args:         cobra.ExactArgs(1),
		RunE:         runStatus,
		SilenceUsage: true, // Don't print usage on error (match Python behavior)
	}
	rootCmd.AddCommand(statusCmd)

	// Backup command
	var backupCmd = &cobra.Command{
		Use:          "backup [environment]",
		Short:        "Backup cluster according to enva.yaml configuration",
		Args:         cobra.ExactArgs(1),
		RunE:         runBackup,
		SilenceUsage: true, // Don't print usage on error (match Python behavior)
	}
	rootCmd.AddCommand(backupCmd)

	// Restore command
	var restoreCmd = &cobra.Command{
		Use:          "restore [environment]",
		Short:        "Restore cluster from backup",
		Args:         cobra.ExactArgs(1),
		RunE:         runRestore,
		SilenceUsage: true, // Don't print usage on error (match Python behavior)
	}
	restoreCmd.Flags().String("backup-name", "", "Name of the backup to restore (e.g., backup-20251130_120000)")
	restoreCmd.MarkFlagRequired("backup-name")
	rootCmd.AddCommand(restoreCmd)

	if err := rootCmd.Execute(); err != nil {
		fmt.Fprintf(os.Stderr, "Error: %v\n", err)
		os.Exit(1)
	}
}

func getConfig(environment string) (*libs.LabConfig, error) {
	// Determine config file path (matching Python: use script directory)
	cfgPath := configFile
	if cfgPath == "" {
		// Python uses: SCRIPT_DIR / "enva.yaml" where SCRIPT_DIR is Path(__file__).parent.absolute()
		// For Go, use executable directory or current directory
		exePath, err := os.Executable()
		if err == nil {
			exeDir := filepath.Dir(exePath)
			defaultPath := filepath.Join(exeDir, "enva.yaml")
			if _, err := os.Stat(defaultPath); err == nil {
				cfgPath = defaultPath
			}
		}
		// Fallback to current directory
		if cfgPath == "" {
			if _, err := os.Stat("enva.yaml"); err == nil {
				cfgPath = "enva.yaml"
			} else {
				cfgPath = "enva.yaml" // Default
			}
		}
	}

	// Load config
	configData, err := libs.LoadConfig(cfgPath)
	if err != nil {
		return nil, fmt.Errorf("failed to load config: %w", err)
	}

	// Environment is required
	envPtr := &environment

	// Create config (logger already initialized in main)
	cfg, err := libs.FromDict(configData, verbose, envPtr)
	if err != nil {
		return nil, fmt.Errorf("failed to parse config: %w", err)
	}

	return cfg, nil
}

func runDeploy(cmd *cobra.Command, args []string) error {
	cfg, err := getConfig(args[0])
	if err != nil {
		return err
	}

	startStep, _ := cmd.Flags().GetInt("start-step")
	endStep, _ := cmd.Flags().GetInt("end-step")
	planOnly, _ := cmd.Flags().GetBool("planonly")

	var endStepPtr *int
	if endStep > 0 {
		endStepPtr = &endStep
	}

	lxcService := services.NewLXCService(cfg.LXCHost(), &cfg.SSH)
	pctService := services.NewPCTService(lxcService)
	deployCmd := commands.NewDeploy(cfg, lxcService, pctService)
	return deployCmd.Run(startStep, endStepPtr, planOnly)
}

func runCleanup(cmd *cobra.Command, args []string) error {
	cfg, err := getConfig(args[0])
	if err != nil {
		return err
	}

	lxcService := services.NewLXCService(cfg.LXCHost(), &cfg.SSH)
	pctService := services.NewPCTService(lxcService)
	cleanupCmd := commands.NewCleanup(cfg, lxcService, pctService)
	return cleanupCmd.Run()
}

func runRedeploy(cmd *cobra.Command, args []string) error {
	cfg, err := getConfig(args[0])
	if err != nil {
		return err
	}

	startStep, _ := cmd.Flags().GetInt("start-step")
	endStep, _ := cmd.Flags().GetInt("end-step")

	var endStepPtr *int
	if endStep > 0 {
		endStepPtr = &endStep
	}

	logger := libs.GetLogger("main")
	logger.Info("==================================================")
	logger.Info("Redeploy: Cleanup and Deploy")
	logger.Info("==================================================")
	logger.Info("\n[1/2] Running cleanup...")

	lxcService := services.NewLXCService(cfg.LXCHost(), &cfg.SSH)
	pctService := services.NewPCTService(lxcService)
	cleanupCmd := commands.NewCleanup(cfg, lxcService, pctService)
	// Python doesn't catch errors between cleanup and deploy - if cleanup fails, deploy still runs
	cleanupCmd.Run() // Ignore error to match Python behavior

	logger.Info("\n[2/2] Running deploy...")
	deployCmd := commands.NewDeploy(cfg, lxcService, pctService)
	if err := deployCmd.Run(startStep, endStepPtr, false); err != nil {
		return err
	}

	logger.Info("==================================================")
	logger.Info("Redeploy completed!")
	logger.Info("==================================================")
	return nil
}

func runStatus(cmd *cobra.Command, args []string) error {
	cfg, err := getConfig(args[0])
	if err != nil {
		return err
	}

	lxcService := services.NewLXCService(cfg.LXCHost(), &cfg.SSH)
	pctService := services.NewPCTService(lxcService)
	statusCmd := commands.NewStatus(cfg, lxcService, pctService)
	return statusCmd.Run()
}

func runBackup(cmd *cobra.Command, args []string) error {
	cfg, err := getConfig(args[0])
	if err != nil {
		return err
	}

	lxcService := services.NewLXCService(cfg.LXCHost(), &cfg.SSH)
	pctService := services.NewPCTService(lxcService)
	backupCmd := commands.NewBackup(cfg, lxcService, pctService)
	return backupCmd.Run()
}

func runRestore(cmd *cobra.Command, args []string) error {
	cfg, err := getConfig(args[0])
	if err != nil {
		return err
	}

	backupName, _ := cmd.Flags().GetString("backup-name")
	if backupName == "" {
		return fmt.Errorf("--backup-name is required")
	}

	lxcService := services.NewLXCService(cfg.LXCHost(), &cfg.SSH)
	pctService := services.NewPCTService(lxcService)
	restoreCmd := commands.NewRestore(cfg, lxcService, pctService)
	return restoreCmd.Run(backupName)
}
