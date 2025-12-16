package cli

import (
	"fmt"
	"strings"
)

// Gluster wraps GlusterFS commands
type Gluster struct {
	glusterCmd string
	force      bool
}

// NewGluster creates a new Gluster command builder
func NewGluster() *Gluster {
	return &Gluster{
		glusterCmd: "gluster",
		force:      true,
	}
}

// GlusterCmd sets gluster command path
func (g *Gluster) GlusterCmd(cmd string) *Gluster {
	g.glusterCmd = cmd
	return g
}

// Force sets force flag
func (g *Gluster) Force(value bool) *Gluster {
	g.force = value
	return g
}

// FindGluster generates command to find gluster command path
func (g *Gluster) FindGluster() string {
	parts := []string{
		"dpkg -L glusterfs-client 2>/dev/null | grep -E '/bin/gluster$|/sbin/gluster$' | head -1",
		"command -v gluster 2>/dev/null",
		"which gluster 2>/dev/null",
		"find /usr /usr/sbin /usr/bin -name gluster -type f 2>/dev/null | head -1",
		"test -x /usr/sbin/gluster && echo /usr/sbin/gluster",
		"test -x /usr/bin/gluster && echo /usr/bin/gluster",
		"echo 'gluster'",
	}
	return strings.Join(parts, " || ")
}

// PeerProbe generates command to probe a peer node
func (g *Gluster) PeerProbe(hostname string) string {
	return fmt.Sprintf("%s peer probe %s 2>&1", g.glusterCmd, hostname)
}

// PeerStatus generates command to get peer status
func (g *Gluster) PeerStatus() string {
	return fmt.Sprintf("%s peer status 2>&1", g.glusterCmd)
}

// VolumeCreate generates command to create a GlusterFS volume
func (g *Gluster) VolumeCreate(volumeName string, replicaCount int, bricks []string) string {
	bricksStr := strings.Join(bricks, " ")
	forceFlag := ""
	if g.force {
		forceFlag = "force"
	}
	parts := []string{
		g.glusterCmd,
		"volume",
		"create",
		volumeName,
		"replica",
		fmt.Sprintf("%d", replicaCount),
		bricksStr,
		forceFlag,
		"2>&1",
	}
	return strings.TrimSpace(strings.Join(parts, " "))
}

// VolumeStart generates command to start a GlusterFS volume
func (g *Gluster) VolumeStart(volumeName string) string {
	return fmt.Sprintf("%s volume start %s 2>&1", g.glusterCmd, volumeName)
}

// VolumeStatus generates command to get volume status
func (g *Gluster) VolumeStatus(volumeName string) string {
	return fmt.Sprintf("%s volume status %s 2>&1", g.glusterCmd, volumeName)
}

// VolumeInfo generates command to get volume information
func (g *Gluster) VolumeInfo(volumeName string) string {
	return fmt.Sprintf("%s volume info %s 2>&1", g.glusterCmd, volumeName)
}

// VolumeExistsCheck generates command to check if volume exists
func (g *Gluster) VolumeExistsCheck(volumeName string) string {
	return fmt.Sprintf("%s volume info %s >/dev/null 2>&1 && echo yes || echo no", g.glusterCmd, volumeName)
}

// IsInstalledCheck generates command to check if GlusterFS is installed
func (g *Gluster) IsInstalledCheck() string {
	return fmt.Sprintf("command -v %s >/dev/null 2>&1 && echo installed || echo not_installed", g.glusterCmd)
}

// ParseGlusterIsInstalled parses output to check if GlusterFS is installed
func ParseGlusterIsInstalled(output string) bool {
	return strings.Contains(output, "installed")
}

// ParseVolumeExists parses output to check if volume exists
func ParseVolumeExists(output string) bool {
	return strings.Contains(output, "yes")
}

