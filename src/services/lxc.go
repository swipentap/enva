package services

import (
	"enva/libs"
)

// LXCService maintains a persistent SSH connection to LXC host
type LXCService struct {
	lxcHost    string
	sshConfig  *libs.SSHConfig
	sshService *SSHService
}

// NewLXCService creates a new LXC service
func NewLXCService(lxcHost string, sshConfig *libs.SSHConfig) *LXCService {
	return &LXCService{
		lxcHost:    lxcHost,
		sshConfig:  sshConfig,
		sshService: NewSSHService(lxcHost, sshConfig),
	}
}

// Connect establishes SSH connection to LXC host
func (l *LXCService) Connect() bool {
	return l.sshService.Connect()
}

// Disconnect closes SSH connection
func (l *LXCService) Disconnect() {
	l.sshService.Disconnect()
}

// IsConnected checks if SSH connection is active
func (l *LXCService) IsConnected() bool {
	return l.sshService.IsConnected()
}

// Execute executes command via SSH connection
func (l *LXCService) Execute(command string, timeout *int) (string, *int) {
	return l.sshService.Execute(command, timeout)
}
