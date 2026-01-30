package cli

import (
	"fmt"
	"strconv"
	"strings"
)

// Vzdump wraps vzdump commands
type Vzdump struct {
	compress string
	mode     string
}

// NewVzdump creates a new Vzdump command builder
func NewVzdump() *Vzdump {
	return &Vzdump{
		compress: "zstd",
		mode:     "stop",
	}
}

// Compress sets compression format
func (v *Vzdump) Compress(value string) *Vzdump {
	v.compress = value
	return v
}

// Mode sets dump mode
func (v *Vzdump) Mode(value string) *Vzdump {
	v.mode = value
	return v
}

// CreateTemplate generates command to create template from container using vzdump
func (v *Vzdump) CreateTemplate(containerID, dumpdir string) string {
	return fmt.Sprintf("vzdump %s --dumpdir %s --compress %s --mode %s", containerID, dumpdir, v.compress, v.mode)
}

// FindArchive generates command to find the most recent archive file for a container
func (v *Vzdump) FindArchive(dumpdir, containerID string) string {
	return fmt.Sprintf("ls -t %s/vzdump-lxc-%s-*.tar.zst | head -1", dumpdir, containerID)
}

// GetArchiveSize generates command to get archive file size in bytes
func (v *Vzdump) GetArchiveSize(archivePath string) string {
	return fmt.Sprintf("stat -c%%s '%s' || echo '0'", archivePath)
}

// ParseArchiveSize parses output to get archive file size
func ParseArchiveSize(output string) *int {
	if output == "" {
		return nil
	}
	size, err := strconv.Atoi(strings.TrimSpace(output))
	if err != nil {
		return nil
	}
	return &size
}

