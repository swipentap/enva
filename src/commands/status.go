package commands

import (
	"fmt"
	"enva/cli"
	"enva/libs"
	"enva/services"
)

// Status handles status display
type Status struct {
	cfg        *libs.LabConfig
	lxcService *services.LXCService
	pctService *services.PCTService
}

// NewStatus creates a new Status command
func NewStatus(cfg *libs.LabConfig, lxcService *services.LXCService, pctService *services.PCTService) *Status {
	return &Status{
		cfg:        cfg,
		lxcService: lxcService,
		pctService: pctService,
	}
}

// Run executes the status command
func (s *Status) Run() error {
	logger := libs.GetLogger("status")
	if !s.lxcService.Connect() {
		logger.Error("Failed to connect to Proxmox host %s", s.cfg.LXCHost())
		return fmt.Errorf("failed to connect to Proxmox host")
	}
	defer s.lxcService.Disconnect()
	logger.Info("==================================================")
	logger.Info("Lab Status")
	logger.Info("==================================================")
	logger.Info("Containers:")
	listCmd := cli.NewPCT().Status()
	result, _ := s.lxcService.Execute(listCmd, nil)
	if result != "" {
		logger.Info(result)
	} else {
		logger.Info("  No containers found")
	}
	templateDir := s.cfg.ProxmoxTemplateDir()
	logger.Info("Templates:")
	templateCmd := fmt.Sprintf("ls -lh %s/*.tar.zst 2>/dev/null || echo 'No templates'", templateDir)
	result, _ = s.lxcService.Execute(templateCmd, nil)
	if result != "" {
		logger.Info(result)
	} else {
		logger.Info("  No templates found")
	}
	return nil
}
