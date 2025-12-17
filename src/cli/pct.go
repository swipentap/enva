package cli

import (
	"fmt"
)

// PCT wraps pct commands
type PCT struct {
	containerID string
	forceFlag   bool
}

// NewPCT creates a new PCT command builder
func NewPCT() *PCT {
	return &PCT{}
}

// ContainerID sets the container ID
func (p *PCT) ContainerID(id interface{}) *PCT {
	p.containerID = fmt.Sprintf("%v", id)
	return p
}

// Force sets force flag
func (p *PCT) Force() *PCT {
	p.forceFlag = true
	return p
}

// Create generates pct create command
func (p *PCT) Create(templatePath, hostname string, memory, swap, cores int, ipAddress, gateway, bridge, storage string, rootfsSize int, unprivileged bool, ostype, arch string) string {
	unprivValue := "0"
	if unprivileged {
		unprivValue = "1"
	}
	return fmt.Sprintf(
		"pct create %s %s --hostname %s --memory %d --swap %d --cores %d --net0 name=eth0,bridge=%s,ip=%s/24,gw=%s --rootfs %s:%d --unprivileged %s --ostype %s --arch %s",
		p.containerID, templatePath, hostname, memory, swap, cores, bridge, ipAddress, gateway, storage, rootfsSize, unprivValue, ostype, arch,
	)
}

// Start generates pct start command
func (p *PCT) Start() string {
	force := ""
	if p.forceFlag {
		force = " --force"
	}
	return fmt.Sprintf("pct start %s%s", p.containerID, force)
}

// Stop generates pct stop command
func (p *PCT) Stop() string {
	force := ""
	if p.forceFlag {
		force = " --force"
	}
	return fmt.Sprintf("pct stop %s%s", p.containerID, force)
}

// Destroy generates pct destroy command
func (p *PCT) Destroy() string {
	force := ""
	if p.forceFlag {
		force = " --force"
	}
	return fmt.Sprintf("pct destroy %s%s", p.containerID, force)
}

// List generates pct list command
func (p *PCT) List() string {
	return "pct list"
}

// Status generates pct status command
func (p *PCT) Status() string {
	return fmt.Sprintf("pct status %s", p.containerID)
}

// Config generates pct config command
func (p *PCT) Config() string {
	return fmt.Sprintf("pct config %s", p.containerID)
}

// SetOption generates pct set command for an option
func (p *PCT) SetOption(option, value string) string {
	return fmt.Sprintf("pct set %s --%s %s", p.containerID, option, value)
}

// SetFeatures generates pct set --features command
func (p *PCT) SetFeatures(nesting, keyctl, fuse bool) string {
	return fmt.Sprintf("pct set %s --features nesting=%d,keyctl=%d,fuse=%d", p.containerID, boolToInt(nesting), boolToInt(keyctl), boolToInt(fuse))
}

func boolToInt(b bool) int {
	if b {
		return 1
	}
	return 0
}
