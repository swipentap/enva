package cli

import (
	"fmt"
)

// Process wraps process management commands
type Process struct {
	signal         int
	fullMatch      bool
	suppressErrors bool
}

// NewProcess creates a new Process command builder
func NewProcess() *Process {
	return &Process{
		signal:         9,
		suppressErrors: true,
	}
}

// Signal sets signal number
func (p *Process) Signal(value int) *Process {
	p.signal = value
	return p
}

// FullMatch uses full match pattern
func (p *Process) FullMatch(value bool) *Process {
	p.fullMatch = value
	return p
}

// SuppressErrors suppresses errors
func (p *Process) SuppressErrors(value bool) *Process {
	p.suppressErrors = value
	return p
}

// Pkill generates pkill command
func (p *Process) Pkill(pattern string) string {
	flags := fmt.Sprintf("-%d", p.signal)
	if p.fullMatch {
		flags += " -f"
	}
	cmd := fmt.Sprintf("pkill %s %s", flags, quote(pattern))
	if p.suppressErrors {
		cmd += " 2>/dev/null || true"
	} else {
		cmd += " 2>&1"
	}
	return cmd
}

// LsofFile generates lsof command to find process using a file
func (p *Process) LsofFile(filePath string) string {
	cmd := fmt.Sprintf("lsof -t %s", quote(filePath))
	if p.suppressErrors {
		cmd += " 2>/dev/null | head -1"
	} else {
		cmd += " 2>&1 | head -1"
	}
	return cmd
}

// FuserFile generates fuser command to find process using a file
func (p *Process) FuserFile(filePath string) string {
	cmd := fmt.Sprintf("fuser %s", quote(filePath))
	if p.suppressErrors {
		cmd += " 2>/dev/null | grep -oE '[0-9]+' | head -1"
	} else {
		cmd += " 2>&1 | grep -oE '[0-9]+' | head -1"
	}
	return cmd
}

// CheckPID generates command to check if process with PID exists
func (p *Process) CheckPID(pid int) string {
	cmd := fmt.Sprintf("kill -0 %d", pid)
	if p.suppressErrors {
		cmd += " 2>/dev/null && echo exists || echo not_found"
	} else {
		cmd += " 2>&1 && echo exists || echo not_found"
	}
	return cmd
}

// GetProcessName generates command to get process name by PID
func (p *Process) GetProcessName(pid int) string {
	cmd := fmt.Sprintf("ps -p %d -o comm=", pid)
	if p.suppressErrors {
		cmd += " 2>/dev/null || echo unknown"
	} else {
		cmd += " 2>&1 || echo unknown"
	}
	return cmd
}

// Kill generates kill command for a specific PID
func (p *Process) Kill(pid int) string {
	cmd := fmt.Sprintf("kill -%d %d", p.signal, pid)
	if p.suppressErrors {
		cmd += " 2>/dev/null || true"
	} else {
		cmd += " 2>&1"
	}
	return cmd
}

