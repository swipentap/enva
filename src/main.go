package main

import (
	"fmt"
	"os"
	"path/filepath"

	"github.com/spf13/cobra"
	"enva/commands"
	"enva/libs"
	"enva/services"
)

var (
	verbose     bool
	configFile  string
	environment string
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
		Short: "EnvA CLI - Manage Proxmox LXC containers and Docker Swarm",
		Long:  "EnvA CLI tool for deploying and managing Kubernetes (k3s) clusters with supporting services on Proxmox LXC containers",
	}

	rootCmd.PersistentFlags().BoolVarP(&verbose, "verbose", "v", false, "Show stdout from SSH service")
	rootCmd.PersistentFlags().StringVarP(&configFile, "config", "c", "", "Path to YAML configuration file (default: enva.yaml)")
	rootCmd.PersistentFlags().StringVarP(&environment, "environment", "e", "", "Environment to use (prod, test, dev). Defaults to 'test' if environments section exists")

	// Deploy command
	var deployCmd = &cobra.Command{
		Use:          "deploy",
		Short:        "Deploy complete environment: apt-cache, templates, and Docker Swarm",
		RunE:         runDeploy,
		SilenceUsage: true, // Don't print usage on error (match Python behavior)
	}
	deployCmd.Flags().Int("start-step", 1, "Start from this step (default: 1)")
	deployCmd.Flags().Int("end-step", 0, "End at this step (default: last step, 0 means last)")
	deployCmd.Flags().Bool("planonly", false, "Show deployment plan and exit without executing")
	rootCmd.AddCommand(deployCmd)

	// Cleanup command
	var cleanupCmd = &cobra.Command{
		Use:          "cleanup",
		Short:        "Remove all containers and templates",
		RunE:         runCleanup,
		SilenceUsage: true, // Don't print usage on error (match Python behavior)
	}
	rootCmd.AddCommand(cleanupCmd)

	// Redeploy command
	var redeployCmd = &cobra.Command{
		Use:          "redeploy",
		Short:        "Cleanup and then deploy complete environment",
		RunE:         runRedeploy,
		SilenceUsage: true, // Don't print usage on error (match Python behavior)
	}
	redeployCmd.Flags().Int("start-step", 1, "Start from this step (default: 1)")
	redeployCmd.Flags().Int("end-step", 0, "End at this step (default: last step, 0 means last)")
	rootCmd.AddCommand(redeployCmd)

	// Status command
	var statusCmd = &cobra.Command{
		Use:          "status",
		Short:        "Show current environment status",
		RunE:         runStatus,
		SilenceUsage: true, // Don't print usage on error (match Python behavior)
	}
	rootCmd.AddCommand(statusCmd)

	// Backup command
	var backupCmd = &cobra.Command{
		Use:          "backup",
		Short:        "Backup cluster according to enva.yaml configuration",
		RunE:         runBackup,
		SilenceUsage: true, // Don't print usage on error (match Python behavior)
	}
	rootCmd.AddCommand(backupCmd)

	// Restore command
	var restoreCmd = &cobra.Command{
		Use:          "restore",
		Short:        "Restore cluster from backup",
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

func getConfig() (*libs.LabConfig, error) {
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

	// Determine environment
	env := environment
	if env == "" {
		if envs, ok := configData["environments"].(map[string]interface{}); ok && len(envs) > 0 {
			env = "test"
		}
	}

	var envPtr *string
	if env != "" {
		envPtr = &env
	}

	// Create config (logger already initialized in main)
	cfg, err := libs.FromDict(configData, verbose, envPtr)
	if err != nil {
		return nil, fmt.Errorf("failed to parse config: %w", err)
	}

	return cfg, nil
}

func runDeploy(cmd *cobra.Command, args []string) error {
	cfg, err := getConfig()
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
	cfg, err := getConfig()
	if err != nil {
		return err
	}

	lxcService := services.NewLXCService(cfg.LXCHost(), &cfg.SSH)
	pctService := services.NewPCTService(lxcService)
	cleanupCmd := commands.NewCleanup(cfg, lxcService, pctService)
	return cleanupCmd.Run()
}

func runRedeploy(cmd *cobra.Command, args []string) error {
	cfg, err := getConfig()
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
	cfg, err := getConfig()
	if err != nil {
		return err
	}

	lxcService := services.NewLXCService(cfg.LXCHost(), &cfg.SSH)
	pctService := services.NewPCTService(lxcService)
	statusCmd := commands.NewStatus(cfg, lxcService, pctService)
	return statusCmd.Run()
}

func runBackup(cmd *cobra.Command, args []string) error {
	cfg, err := getConfig()
	if err != nil {
		return err
	}

	lxcService := services.NewLXCService(cfg.LXCHost(), &cfg.SSH)
	pctService := services.NewPCTService(lxcService)
	backupCmd := commands.NewBackup(cfg, lxcService, pctService)
	return backupCmd.Run()
}

func runRestore(cmd *cobra.Command, args []string) error {
	cfg, err := getConfig()
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

