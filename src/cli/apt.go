package cli

import (
	"fmt"
	"strings"
)

// Apt wraps APT/APT-GET commands with fluent API
type Apt struct {
	quiet              bool
	useAptGet          bool
	noInstallRecommends bool
	options            map[string]string
}

// NewApt creates a new Apt command builder
func NewApt() *Apt {
	return &Apt{}
}

// Quiet sets quiet mode
func (a *Apt) Quiet() *Apt {
	a.quiet = true
	return a
}

// UseAptGet uses apt-get instead of apt
func (a *Apt) UseAptGet() *Apt {
	a.useAptGet = true
	return a
}

// NoInstallRecommends sets --no-install-recommends flag
func (a *Apt) NoInstallRecommends() *Apt {
	a.noInstallRecommends = true
	return a
}

// Options sets apt options (e.g., Dpkg::Options::)
func (a *Apt) Options(opts map[string]string) *Apt {
	if a.options == nil {
		a.options = make(map[string]string)
	}
	for k, v := range opts {
		a.options[k] = v
	}
	return a
}

// Update generates apt update command
func (a *Apt) Update() string {
	cmd := "apt"
	if a.useAptGet {
		cmd = "apt-get"
	}
	flags := []string{}
	if a.quiet {
		flags = append(flags, "-qq")
	}
	flagStr := ""
	if len(flags) > 0 {
		flagStr = " " + strings.Join(flags, " ")
	}
	return fmt.Sprintf("%s%s update", cmd, flagStr)
}

// Install generates apt install command
func (a *Apt) Install(packages []string) string {
	cmd := "apt"
	if a.useAptGet {
		cmd = "apt-get"
	}
	flags := []string{"-y"}
	if a.quiet {
		flags = append(flags, "-qq")
	}
	if a.noInstallRecommends {
		flags = append(flags, "--no-install-recommends")
	}
	
	// Build options (-o key=value)
	optParts := []string{}
	if a.options != nil {
		for key, value := range a.options {
			// For Dpkg::Options::, split multiple options and add each separately
			if key == "Dpkg::Options::" && strings.Contains(value, " ") {
				optionsList := strings.Fields(value)
				for _, opt := range optionsList {
					optParts = append(optParts, fmt.Sprintf("-o %s=%s", key, opt))
				}
			} else {
				optParts = append(optParts, fmt.Sprintf("-o %s=%s", key, value))
			}
		}
	}
	
	parts := []string{cmd}
	parts = append(parts, strings.Join(flags, " "))
	if len(optParts) > 0 {
		parts = append(parts, strings.Join(optParts, " "))
	}
	parts = append(parts, "install")
	parts = append(parts, strings.Join(packages, " "))
	return strings.Join(parts, " ")
}

// Upgrade generates apt upgrade command
func (a *Apt) Upgrade() string {
	cmd := "apt"
	if a.useAptGet {
		cmd = "apt-get"
	}
	flags := []string{"-y"}
	if a.quiet {
		flags = append(flags, "-qq")
	}
	return fmt.Sprintf("%s %s upgrade", cmd, strings.Join(flags, " "))
}

// DistUpgrade generates apt dist-upgrade command
func (a *Apt) DistUpgrade() string {
	cmd := "apt-get"
	flags := []string{"-y"}
	if a.quiet {
		flags = append(flags, "-qq")
	}
	return fmt.Sprintf("%s %s dist-upgrade", cmd, strings.Join(flags, " "))
}

// IsInstalledCheckCmd generates command to check if package is installed
func IsInstalledCheckCmd(packageName string) string {
	return fmt.Sprintf("dpkg -l | grep -q '^ii.*%s' && echo installed || echo not_installed", packageName)
}

// ParseIsInstalled parses output to check if package is installed
func ParseIsInstalled(output string) bool {
	return strings.Contains(output, "installed")
}

