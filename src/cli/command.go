package cli

import (
	"fmt"
	"strings"
)

// Command wraps command utility
type Command struct {
	commandName string
}

// NewCommand creates a new Command command builder
func NewCommand() *Command {
	return &Command{}
}

// Command sets command name to check
func (c *Command) SetCommand(name string) *Command {
	c.commandName = name
	return c
}

// Exists generates command to check if a command exists
func (c *Command) Exists() string {
	if c.commandName == "" {
		panic("Command name must be set")
	}
	return fmt.Sprintf("command -v %s", quote(c.commandName))
}

func quote(s string) string {
	if strings.Contains(s, " ") || strings.Contains(s, "$") {
		return fmt.Sprintf("'%s'", strings.ReplaceAll(s, "'", "'\"'\"'"))
	}
	return s
}

