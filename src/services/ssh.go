package services

import (
	"encoding/base64"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"golang.org/x/crypto/ssh"

	"enva/libs"
)

// SSHService manages SSH connections and command execution
type SSHService struct {
	host      string
	hostname  string
	username  string
	sshConfig *libs.SSHConfig
	client    *ssh.Client
	connected bool
}

// NewSSHService creates a new SSH service
func NewSSHService(host string, sshConfig *libs.SSHConfig) *SSHService {
	service := &SSHService{
		host:      host,
		sshConfig: sshConfig,
		connected: false,
	}

	// Parse host (format: user@host or just host)
	if idx := strings.Index(host, "@"); idx >= 0 {
		service.username = host[:idx]
		service.hostname = host[idx+1:]
	} else {
		service.username = sshConfig.DefaultUsername
		service.hostname = host
	}

	return service
}

// Connect establishes SSH connection
func (s *SSHService) Connect() bool {
	if s.connected && s.client != nil {
		// Check if connection is still alive
		_, _, err := s.client.SendRequest("keepalive", false, nil)
		if err == nil {
			return true
		}
		// Connection is dead, need to reconnect
		s.client = nil
		s.connected = false
	}

	// Find and load private key
	keyFile := s.findPrivateKey()
	if keyFile == "" {
		libs.GetLogger("ssh").Printf("No private key found")
		return false
	}

	// Load private key
	signer, err := s.loadPrivateKey(keyFile)
	if err != nil {
		libs.GetLogger("ssh").Printf("Failed to load private key %s: %v", keyFile, err)
		return false
	}

	// Create SSH config
	config := &ssh.ClientConfig{
		User:            s.username,
		Auth:            []ssh.AuthMethod{ssh.PublicKeys(signer)},
		HostKeyCallback: ssh.InsecureIgnoreHostKey(), // In production, use knownhosts
		Timeout:         time.Duration(s.sshConfig.ConnectTimeout) * time.Second,
	}

	// Connect
	addr := fmt.Sprintf("%s:22", s.hostname)
	client, err := ssh.Dial("tcp", addr, config)
	if err != nil {
		libs.GetLogger("ssh").Printf("Failed to establish SSH connection to %s: %v", s.host, err)
		return false
	}

	s.client = client
	s.connected = true
	libs.GetLogger("ssh").Printf("SSH connection established to %s@%s", s.username, s.hostname)
	return true
}

// Disconnect closes SSH connection
func (s *SSHService) Disconnect() {
	if s.client != nil {
		s.client.Close()
		s.client = nil
		s.connected = false
		libs.GetLogger("ssh").Printf("SSH connection closed to %s", s.host)
	}
}

// IsConnected checks if SSH connection is active
func (s *SSHService) IsConnected() bool {
	if !s.connected || s.client == nil {
		return false
	}
	_, _, err := s.client.SendRequest("keepalive", false, nil)
	return err == nil
}

// Execute executes command via SSH connection
func (s *SSHService) Execute(command string, timeout *int, sudo ...bool) (string, *int) {
	if !s.IsConnected() {
		if !s.Connect() {
			libs.GetLogger("ssh").Printf("Cannot execute command: SSH connection not available")
			return "", nil
		}
	}

	execTimeout := s.sshConfig.DefaultExecTimeout
	if timeout != nil {
		execTimeout = *timeout
	}

	useSudo := len(sudo) > 0 && sudo[0]
	if useSudo {
		// For multi-line scripts or commands with single quotes, use base64 encoding to avoid quoting issues (matching Python behavior)
		if strings.Contains(command, "\n") || strings.Contains(command, "'") {
			encoded := base64.StdEncoding.EncodeToString([]byte(command))
			command = fmt.Sprintf("sudo -n bash -c 'echo %s | base64 -d | bash'", encoded)
		} else {
			// For single-line commands without single quotes, wrap in bash -c with proper quoting (matching Python shlex.quote behavior)
			command = fmt.Sprintf("sudo -n bash -c %s", quoteCommand(command))
		}
	}

	libs.GetLogger("ssh").Debug("Running: %s", command)

	// Create session
	session, err := s.client.NewSession()
	if err != nil {
		libs.GetLogger("ssh").Printf("Failed to create SSH session: %v", err)
		return "", nil
	}
	defer session.Close()

	// Set up terminal
	modes := ssh.TerminalModes{
		ssh.ECHO:          0,
		ssh.TTY_OP_ISPEED: 14400,
		ssh.TTY_OP_OSPEED: 14400,
	}
	if err := session.RequestPty("xterm", 80, 40, modes); err != nil {
		libs.GetLogger("ssh").Printf("Failed to request PTY: %v", err)
	}

	// Set up stdout/stderr
	var stdout, stderr strings.Builder
	session.Stdout = &stdout
	session.Stderr = &stderr

	// Execute command with timeout
	done := make(chan error, 1)
	go func() {
		done <- session.Run(command)
	}()

	select {
	case err := <-done:
		exitCode := 0
		if err != nil {
			if exitErr, ok := err.(*ssh.ExitError); ok {
				exitCode = exitErr.ExitStatus()
			} else {
				libs.GetLogger("ssh").Printf("Command execution error: %v", err)
				return "", nil
			}
		}
		output := stdout.String()
		errorOutput := stderr.String()
		combined := output
		if errorOutput != "" {
			if output != "" {
				combined = output + "\n" + errorOutput
			} else {
				combined = errorOutput
			}
		}
		// Show output if verbose
		if s.sshConfig.Verbose {
			fmt.Print(combined)
		}
		return strings.TrimSpace(combined), &exitCode
	case <-time.After(time.Duration(execTimeout) * time.Second):
		session.Close()
		libs.GetLogger("ssh").Printf("SSH command timeout after %ds - COMMAND FAILED", execTimeout)
		return "", nil
	}
}

// findPrivateKey finds private key file
func (s *SSHService) findPrivateKey() string {
	homeDir, err := os.UserHomeDir()
	if err != nil {
		return ""
	}

	keyPaths := []string{
		filepath.Join(homeDir, ".ssh", "id_rsa"),
		filepath.Join(homeDir, ".ssh", "id_ed25519"),
	}

	for _, keyPath := range keyPaths {
		if _, err := os.Stat(keyPath); err == nil {
			return keyPath
		}
	}
	return ""
}

// loadPrivateKey loads private key from file
func (s *SSHService) loadPrivateKey(keyFile string) (ssh.Signer, error) {
	key, err := os.ReadFile(keyFile)
	if err != nil {
		return nil, err
	}

	// Try RSA key first
	signer, err := ssh.ParsePrivateKey(key)
	if err == nil {
		return signer, nil
	}

	// Try with passphrase (empty for now)
	signer, err = ssh.ParsePrivateKeyWithPassphrase(key, []byte(""))
	if err == nil {
		return signer, nil
	}

	return nil, fmt.Errorf("failed to parse private key: %v", err)
}

// quoteCommand quotes a command for use in bash -c (matching Python shlex.quote)
func quoteCommand(cmd string) string {
	// Simple quoting - wrap in single quotes and escape single quotes
	if strings.Contains(cmd, "'") || strings.Contains(cmd, " ") || strings.Contains(cmd, "$") {
		escaped := strings.ReplaceAll(cmd, "'", "'\"'\"'")
		return fmt.Sprintf("'%s'", escaped)
	}
	return fmt.Sprintf("'%s'", cmd)
}
