package commands

import (
	"enva/cli"
	"enva/libs"
	"enva/services"
	"fmt"
	"os"
	"strconv"
	"strings"
)

// CleanupError is raised when cleanup fails
type CleanupError struct {
	Message string
}

func (e *CleanupError) Error() string {
	return e.Message
}

// Cleanup handles cleanup operations
type Cleanup struct {
	cfg        *libs.LabConfig
	lxcService *services.LXCService
	pctService *services.PCTService
}

// NewCleanup creates a new Cleanup command
func NewCleanup(cfg *libs.LabConfig, lxcService *services.LXCService, pctService *services.PCTService) *Cleanup {
	return &Cleanup{
		cfg:        cfg,
		lxcService: lxcService,
		pctService: pctService,
	}
}

// Run executes the cleanup
func (c *Cleanup) Run() error {
	logger := libs.GetLogger("cleanup")
	defer func() {
		if r := recover(); r != nil {
			if err, ok := r.(error); ok {
				logger.Error("Error during cleanup: %s", err.Error())
				logger.LogTraceback(err)
			} else {
				logger.Error("Error during cleanup: %v", r)
			}
			os.Exit(1)
		}
	}()
	logger.Info("==================================================")
	logger.Info("Cleaning Up Lab Environment")
	logger.Info("==================================================")
	logger.Info("Destroying ALL containers and templates...")
	if !c.lxcService.Connect() {
		logger.Error("Failed to connect to LXC host %s", c.cfg.LXCHost())
		return &CleanupError{Message: "Failed to connect to LXC host"}
	}
	defer c.lxcService.Disconnect()
	if err := c.destroyContainers(); err != nil {
		return err
	}
	if err := c.removeTemplates(); err != nil {
		return err
	}
	return nil
}

func (c *Cleanup) destroyContainers() error {
	logger := libs.GetLogger("cleanup")
	logger.Info("Stopping and destroying containers...")
	containerIDs := c.listContainerIDs()
	total := len(containerIDs)
	if total == 0 {
		logger.Info("No containers found")
		return nil
	}
	logger.Info("Found %d containers to destroy: %s", total, strings.Join(containerIDs, ", "))
	for idx, cidStr := range containerIDs {
		logger.Info("[%d/%d] Processing container %s...", idx+1, total, cidStr)
		cid, err := strconv.Atoi(cidStr)
		if err != nil {
			logger.Info("Invalid container ID: %s", cidStr)
			continue
		}
		libs.DestroyContainer(c.cfg.LXCHost(), cid, c.cfg, c.lxcService)
	}
	return c.verifyContainersRemoved()
}

func (c *Cleanup) listContainerIDs() []string {
	listCmd := cli.NewPCT().Status()
	result, _ := c.lxcService.Execute(listCmd, nil)
	var containerIDs []string
	if result != "" {
		lines := strings.Split(strings.TrimSpace(result), "\n")
		for _, line := range lines[1:] {
			parts := strings.Fields(line)
			if len(parts) > 0 && isNumeric(parts[0]) {
				containerIDs = append(containerIDs, parts[0])
			}
		}
	}
	return containerIDs
}

func (c *Cleanup) verifyContainersRemoved() error {
	logger := libs.GetLogger("cleanup")
	logger.Info("Verifying all containers are destroyed...")
	remainingResult, _ := c.lxcService.Execute(cli.NewPCT().Status(), nil)
	var remainingIDs []string
	if remainingResult != "" {
		remainingLines := strings.Split(strings.TrimSpace(remainingResult), "\n")
		for _, line := range remainingLines[1:] {
			parts := strings.Fields(line)
			if len(parts) > 0 && isNumeric(parts[0]) {
				remainingIDs = append(remainingIDs, parts[0])
			}
		}
	}
	if len(remainingIDs) > 0 {
		return &CleanupError{Message: fmt.Sprintf("%d containers still exist: %s", len(remainingIDs), strings.Join(remainingIDs, ", "))}
	}
	logger.Info("All containers destroyed")
	return nil
}

func (c *Cleanup) removeTemplates() error {
	logger := libs.GetLogger("cleanup")
	logger.Info("Removing templates...")
	templateDir := c.cfg.LXCTemplateDir()
	logger.Info("Cleaning template directory %s...", templateDir)
	countCmd := cli.NewFind().Directory(templateDir).Maxdepth(1).Type("f").Name("*.tar.zst").Count()
	countResult, _ := c.lxcService.Execute(countCmd, nil)
	templateCount := "0"
	if countResult != "" {
		templateCount = strings.TrimSpace(countResult)
	}
	logger.Info("Removing %s template files...", templateCount)
	deleteCmd := cli.NewFind().Directory(templateDir).Maxdepth(1).Type("f").Name("*.tar.zst").Delete()
	c.lxcService.Execute(deleteCmd, nil)
	logger.Info("Templates removed")
	return nil
}

func isNumeric(s string) bool {
	for _, r := range s {
		if r < '0' || r > '9' {
			return false
		}
	}
	return len(s) > 0
}
