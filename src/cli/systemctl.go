package cli

import (
	"fmt"
	"strings"
)

// SystemCtl wraps systemctl commands
type SystemCtl struct {
	serviceName string
}

// NewSystemCtl creates a new SystemCtl command builder
func NewSystemCtl() *SystemCtl {
	return &SystemCtl{}
}

// Service sets the service name
func (s *SystemCtl) Service(name string) *SystemCtl {
	s.serviceName = name
	return s
}

// Start generates systemctl start command
func (s *SystemCtl) Start() string {
	return fmt.Sprintf("systemctl start %s", s.serviceName)
}

// Stop generates systemctl stop command
func (s *SystemCtl) Stop() string {
	return fmt.Sprintf("systemctl stop %s", s.serviceName)
}

// Enable generates systemctl enable command
func (s *SystemCtl) Enable() string {
	return fmt.Sprintf("systemctl enable %s", s.serviceName)
}

// Disable generates systemctl disable command
func (s *SystemCtl) Disable() string {
	return fmt.Sprintf("systemctl disable %s", s.serviceName)
}

// Restart generates systemctl restart command
func (s *SystemCtl) Restart() string {
	return fmt.Sprintf("systemctl restart %s", s.serviceName)
}

// Status generates systemctl status command
func (s *SystemCtl) Status() string {
	return fmt.Sprintf("systemctl status %s", s.serviceName)
}

// IsActive generates systemctl is-active command
func (s *SystemCtl) IsActive() string {
	return fmt.Sprintf("systemctl is-active %s", s.serviceName)
}

// ParseIsActive parses output to check if service is active
func ParseIsActive(output string) bool {
	return strings.TrimSpace(output) == "active"
}

