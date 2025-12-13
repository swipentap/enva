package cli

import (
	"fmt"
	"strings"
)

// Shell wraps shell script execution
type Shell struct {
	shell      string
	scriptPath string
	args       []string
}

// NewShell creates a new Shell command builder
func NewShell() *Shell {
	return &Shell{
		shell: "sh",
	}
}

// Shell sets shell path
func (s *Shell) SetShell(shellPath string) *Shell {
	s.shell = shellPath
	return s
}

// Script sets script path
func (s *Shell) Script(path string) *Shell {
	s.scriptPath = path
	return s
}

// Args sets script arguments
func (s *Shell) Args(arguments []string) *Shell {
	s.args = arguments
	return s
}

// Execute generates shell script execution command
func (s *Shell) Execute() string {
	if s.scriptPath == "" {
		panic("Script path must be set for shell execution")
	}
	scriptQuoted := quote(s.scriptPath)
	argsQuoted := ""
	if len(s.args) > 0 {
		quotedArgs := make([]string, len(s.args))
		for i, arg := range s.args {
			quotedArgs[i] = quote(arg)
		}
		argsQuoted = strings.Join(quotedArgs, " ")
	}
	cmd := fmt.Sprintf("%s %s", s.shell, scriptQuoted)
	if argsQuoted != "" {
		cmd += " " + argsQuoted
	}
	return cmd + " 2>&1"
}

