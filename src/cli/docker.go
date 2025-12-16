package cli

import (
	"fmt"
	"strings"
)

// Docker wraps Docker commands
type Docker struct {
	dockerCmd   string
	showAll     bool
	includeAll  bool
	force       bool
	tail        int
}

// NewDocker creates a new Docker command builder
func NewDocker() *Docker {
	return &Docker{
		dockerCmd: "docker",
		tail:      20,
	}
}

// DockerCmd sets docker command path
func (d *Docker) DockerCmd(cmd string) *Docker {
	d.dockerCmd = cmd
	return d
}

// ShowAll shows all containers
func (d *Docker) ShowAll(value bool) *Docker {
	d.showAll = value
	return d
}

// IncludeAll includes all in prune
func (d *Docker) IncludeAll(value bool) *Docker {
	d.includeAll = value
	return d
}

// Force sets force flag
func (d *Docker) Force(value bool) *Docker {
	d.force = value
	return d
}

// Tail sets tail lines
func (d *Docker) Tail(lines int) *Docker {
	d.tail = lines
	return d
}

// FindDocker generates command to find docker command path
func (d *Docker) FindDocker() string {
	return "dpkg -L docker.io 2>/dev/null | grep -E '/bin/docker$' | head -1 || dpkg -L docker-ce 2>/dev/null | grep -E '/bin/docker$' | head -1 || command -v docker 2>/dev/null || which docker 2>/dev/null || find /usr /usr/local -name docker -type f 2>/dev/null | head -1 || test -x /usr/bin/docker && echo /usr/bin/docker || test -x /usr/local/bin/docker && echo /usr/local/bin/docker || echo 'docker'"
}

// Version generates command to get Docker version
func (d *Docker) Version() string {
	return fmt.Sprintf("%s --version 2>&1", d.dockerCmd)
}

// PS generates command to list containers
func (d *Docker) PS() string {
	allFlag := ""
	if d.showAll {
		allFlag = "-a"
	}
	return fmt.Sprintf("%s ps %s 2>&1", d.dockerCmd, allFlag)
}

// SwarmInit generates command to initialize Docker Swarm
func (d *Docker) SwarmInit(advertiseAddr string) string {
	return fmt.Sprintf("%s swarm init --advertise-addr %s 2>&1", d.dockerCmd, advertiseAddr)
}

// SwarmJoinToken generates command to get Swarm join token
func (d *Docker) SwarmJoinToken(role string) string {
	return fmt.Sprintf("%s swarm join-token %s -q 2>&1", d.dockerCmd, role)
}

// SwarmJoin generates command to join Docker Swarm
func (d *Docker) SwarmJoin(token, managerAddr string) string {
	return fmt.Sprintf("%s swarm join --token %s %s 2>&1", d.dockerCmd, token, managerAddr)
}

// NodeLS generates command to list Swarm nodes
func (d *Docker) NodeLS() string {
	return fmt.Sprintf("%s node ls 2>&1", d.dockerCmd)
}

// NodeUpdate generates command to update node availability
func (d *Docker) NodeUpdate(nodeName, availability string) string {
	return fmt.Sprintf("%s node update --availability %s %s 2>&1", d.dockerCmd, availability, nodeName)
}

// VolumeCreate generates command to create Docker volume
func (d *Docker) VolumeCreate(volumeName string) string {
	return fmt.Sprintf("%s volume create %s 2>/dev/null || true", d.dockerCmd, volumeName)
}

// VolumeRM generates command to remove Docker volume
func (d *Docker) VolumeRM(volumeName string) string {
	forceFlag := ""
	if d.force {
		forceFlag = "-f "
	}
	return fmt.Sprintf("%s volume rm %s%s 2>/dev/null || true", d.dockerCmd, forceFlag, volumeName)
}

// Run generates command to run Docker container
func (d *Docker) Run(image, name string, args map[string]interface{}) string {
	cmd := fmt.Sprintf("%s run -d --name %s", d.dockerCmd, name)
	if restart, ok := args["restart"].(string); ok {
		cmd += fmt.Sprintf(" --restart=%s", restart)
	}
	if network, ok := args["network"].(string); ok {
		cmd += fmt.Sprintf(" --network %s", network)
	}
	if volumes, ok := args["volumes"].([]string); ok {
		for _, vol := range volumes {
			cmd += fmt.Sprintf(" -v %s", vol)
		}
	}
	if ports, ok := args["ports"].([]string); ok {
		for _, port := range ports {
			cmd += fmt.Sprintf(" -p %s", port)
		}
	}
	if securityOpts, ok := args["security_opts"].([]string); ok {
		for _, opt := range securityOpts {
			cmd += fmt.Sprintf(" --security-opt %s", opt)
		}
	}
	cmd += fmt.Sprintf(" %s", image)
	if commandArgs, ok := args["command_args"].([]string); ok {
		for _, arg := range commandArgs {
			cmd += fmt.Sprintf(" %s", quote(arg))
		}
	}
	cmd += " 2>&1"
	return cmd
}

// Stop generates command to stop Docker container
func (d *Docker) Stop(containerName string) string {
	return fmt.Sprintf("%s stop %s 2>/dev/null || true", d.dockerCmd, containerName)
}

// RM generates command to remove Docker container
func (d *Docker) RM(containerName string) string {
	return fmt.Sprintf("%s rm %s 2>/dev/null || true", d.dockerCmd, containerName)
}

// Logs generates command to get Docker container logs
func (d *Docker) Logs(containerName string) string {
	return fmt.Sprintf("%s logs %s 2>&1 | tail -%d", d.dockerCmd, containerName, d.tail)
}

// SystemPrune generates command to prune Docker system
func (d *Docker) SystemPrune() string {
	flags := ""
	if d.includeAll {
		flags += " -a"
	}
	if d.force {
		flags += " -f"
	}
	return fmt.Sprintf("%s system prune%s 2>/dev/null || true", d.dockerCmd, flags)
}

// IsInstalledCheck generates command to check if Docker is installed
func (d *Docker) IsInstalledCheck() string {
	return fmt.Sprintf("command -v %s >/dev/null 2>&1 && echo installed || echo not_installed", d.dockerCmd)
}

// ParseDockerIsInstalled parses output to check if Docker is installed
func ParseDockerIsInstalled(output string) bool {
	return strings.Contains(strings.ToLower(output), "installed")
}

