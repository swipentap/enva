package cli

// Generic wraps generic/arbitrary commands
type Generic struct{}

// NewGeneric creates a new Generic command builder
func NewGeneric() *Generic {
	return &Generic{}
}

// Passthrough returns the command unchanged
func Passthrough(command string) string {
	return command
}

