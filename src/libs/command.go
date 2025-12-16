package libs

// Command is the base interface for command classes
type Command interface {
	Run(args interface{}) error
}

// BaseCommand provides base functionality for commands
type BaseCommand struct {
	Cfg *LabConfig
}

