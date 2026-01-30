package cli

import (
	"fmt"
	"strings"
)

// Sed wraps sed commands
type Sed struct {
	delimiter string
	flags     string
}

// NewSed creates a new Sed command builder
func NewSed() *Sed {
	return &Sed{
		delimiter: "/",
		flags:     "g",
	}
}

// Delimiter sets delimiter
func (s *Sed) Delimiter(value string) *Sed {
	s.delimiter = value
	return s
}

// Flags sets flags
func (s *Sed) Flags(value string) *Sed {
	s.flags = value
	return s
}

// Replace generates sed command to replace text in a file
func (s *Sed) Replace(path, search, replacement string) string {
	escapedSearch := escapeDelimiter(escapeSingleQuotes(search), s.delimiter)
	escapedReplacement := escapeDelimiter(escapeSingleQuotes(replacement), s.delimiter)
	expression := fmt.Sprintf("s%s%s%s%s%s", s.delimiter, escapedSearch, s.delimiter, escapedReplacement, s.delimiter+s.flags)
	return fmt.Sprintf("sed -i '%s' %s", expression, quote(path))
}

func escapeDelimiter(value, delimiter string) string {
	return strings.ReplaceAll(value, delimiter, "\\"+delimiter)
}

