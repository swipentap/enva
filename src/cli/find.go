package cli

import (
	"fmt"
)

// Find wraps find commands
type Find struct {
	directory string
	maxdepth  *int
	fileType  string
	name      string
}

// NewFind creates a new Find command builder
func NewFind() *Find {
	return &Find{}
}

// Directory sets directory to search
func (f *Find) Directory(path string) *Find {
	f.directory = path
	return f
}

// Maxdepth sets maxdepth
func (f *Find) Maxdepth(depth int) *Find {
	f.maxdepth = &depth
	return f
}

// Type sets file type
func (f *Find) Type(fileType string) *Find {
	f.fileType = fileType
	return f
}

// Name sets name pattern
func (f *Find) Name(pattern string) *Find {
	f.name = pattern
	return f
}

// Delete generates find command with -delete action
func (f *Find) Delete() string {
	if f.directory == "" {
		panic("Directory must be set")
	}
	cmd := fmt.Sprintf("find %s", quote(f.directory))
	if f.maxdepth != nil {
		cmd += fmt.Sprintf(" -maxdepth %d", *f.maxdepth)
	}
	if f.fileType != "" {
		cmd += fmt.Sprintf(" -type %s", f.fileType)
	}
	if f.name != "" {
		patternEscaped := escapeSingleQuotes(f.name)
		cmd += fmt.Sprintf(" -name '%s'", patternEscaped)
	}
	cmd += " -delete || true"
	return cmd
}

// Count generates find command that counts matching files
func (f *Find) Count() string {
	if f.directory == "" {
		panic("Directory must be set")
	}
	cmd := fmt.Sprintf("find %s", quote(f.directory))
	if f.maxdepth != nil {
		cmd += fmt.Sprintf(" -maxdepth %d", *f.maxdepth)
	}
	if f.fileType != "" {
		cmd += fmt.Sprintf(" -type %s", f.fileType)
	}
	if f.name != "" {
		patternEscaped := escapeSingleQuotes(f.name)
		cmd += fmt.Sprintf(" -name '%s'", patternEscaped)
	}
	cmd += " -print | wc -l"
	return cmd
}

