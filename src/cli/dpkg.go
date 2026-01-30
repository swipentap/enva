package cli

import (
	"strings"
)

// Dpkg wraps dpkg utility commands
type Dpkg struct {
	all            bool
	logFile        *string
	suppressErrors bool
}

// NewDpkg creates a new Dpkg command builder
func NewDpkg() *Dpkg {
	return &Dpkg{
		suppressErrors: true,
	}
}

// All configures all packages
func (d *Dpkg) All(value bool) *Dpkg {
	d.all = value
	return d
}

// LogFile sets log file path
func (d *Dpkg) LogFile(path string) *Dpkg {
	d.logFile = &path
	return d
}

// SuppressErrors suppresses errors
func (d *Dpkg) SuppressErrors(value bool) *Dpkg {
	d.suppressErrors = value
	return d
}

// Configure generates dpkg --configure command
func (d *Dpkg) Configure() string {
	parts := []string{"dpkg"}
	if d.all {
		parts = append(parts, "--configure", "-a")
	} else {
		parts = append(parts, "--configure")
	}
	if d.logFile != nil {
		parts = append(parts, ">"+quote(*d.logFile))
	}
	return strings.Join(parts, " ")
}

// Divert generates dpkg-divert command
func (d *Dpkg) Divert(path string, quiet, local, rename bool, action string) string {
	parts := []string{"dpkg-divert"}
	if quiet {
		parts = append(parts, "--quiet")
	}
	if local {
		parts = append(parts, "--local")
	}
	if rename {
		parts = append(parts, "--rename")
	}
	parts = append(parts, action, quote(path))
	return strings.Join(parts, " ") + ""
}

