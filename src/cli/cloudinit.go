package cli

import (
	"fmt"
	"strings"
)

// CloudInit wraps cloud-init commands
type CloudInit struct {
	logFile        *string
	suppressOutput bool
}

// NewCloudInit creates a new CloudInit command builder
func NewCloudInit() *CloudInit {
	return &CloudInit{}
}

// LogFile sets log file path
func (c *CloudInit) LogFile(path string) *CloudInit {
	c.logFile = &path
	return c
}

// SuppressOutput suppresses output
func (c *CloudInit) SuppressOutput(value bool) *CloudInit {
	c.suppressOutput = value
	return c
}

// Status generates command to get cloud-init status
func (c *CloudInit) Status(wait bool) string {
	cmd := "cloud-init status"
	if wait {
		cmd += " --wait"
	}
	if c.logFile != nil {
		cmd += fmt.Sprintf(" >%s 2>&1", *c.logFile)
	} else if c.suppressOutput {
		cmd += " >/dev/null 2>&1"
	} else {
		cmd += " 2>&1"
	}
	return cmd
}

// Clean generates command to clean cloud-init data
func (c *CloudInit) Clean(logs, seed, machineID bool) string {
	cmd := "cloud-init clean"
	flags := []string{}
	if logs {
		flags = append(flags, "--logs")
	}
	if seed {
		flags = append(flags, "--seed")
	}
	if machineID {
		flags = append(flags, "--machine-id")
	}
	if len(flags) > 0 {
		cmd += " " + strings.Join(flags, " ")
	}
	if c.suppressOutput {
		cmd += " >/dev/null 2>&1"
	} else {
		cmd += " 2>&1"
	}
	return cmd
}

// Wait generates command to wait for cloud-init to complete
func (c *CloudInit) Wait(logFile *string) string {
	cmd := "cloud-init status --wait"
	if logFile != nil {
		cmd += fmt.Sprintf(" >%s 2>&1", *logFile)
	} else if c.logFile != nil {
		cmd += fmt.Sprintf(" >%s 2>&1", *c.logFile)
	} else if c.suppressOutput {
		cmd += " >/dev/null 2>&1"
	} else {
		cmd += " 2>&1"
	}
	return cmd
}

// ParseStatus parses cloud-init status output
func ParseStatus(output string) *string {
	if output == "" {
		return nil
	}
	outputLower := strings.ToLower(strings.TrimSpace(output))
	if strings.Contains(outputLower, "status:") {
		parts := strings.SplitN(outputLower, "status:", 2)
		if len(parts) > 1 {
			status := strings.TrimSpace(parts[1])
			if status != "" {
				fields := strings.Fields(status)
				if len(fields) > 0 {
					return &fields[0]
				}
			}
		}
	}
	if outputLower != "" {
		return &outputLower
	}
	return nil
}

