package cli

import (
	"fmt"
	"strings"
)

// FileOps wraps file operations
type FileOps struct {
	recursive       bool
	force           bool
	allowGlob       bool
	suppressErrors  bool
	append          bool
}

// NewFileOps creates a new FileOps command builder
func NewFileOps() *FileOps {
	return &FileOps{
		force: true,
	}
}

// Recursive sets recursive mode
func (f *FileOps) Recursive() *FileOps {
	f.recursive = true
	return f
}

// Force sets force mode
func (f *FileOps) Force(value bool) *FileOps {
	f.force = value
	return f
}

// AllowGlob allows glob expansion
func (f *FileOps) AllowGlob() *FileOps {
	f.allowGlob = true
	return f
}

// SuppressErrors suppresses errors
func (f *FileOps) SuppressErrors() *FileOps {
	f.suppressErrors = true
	return f
}

// Append sets append mode
func (f *FileOps) Append() *FileOps {
	f.append = true
	return f
}

// Write generates command that writes content to a file
func (f *FileOps) Write(path, content string) string {
	sanitized := strings.ReplaceAll(content, "\\", "\\\\")
	sanitized = escapeSingleQuotes(sanitized)
	redir := ">>"
	if !f.append {
		redir = ">"
	}
	return fmt.Sprintf("printf '%s' %s %s", sanitized, redir, quotePath(path))
}

// Chmod generates chmod command
func (f *FileOps) Chmod(path, mode string) string {
	return fmt.Sprintf("chmod %s %s", mode, quotePath(path))
}

// Mkdir generates mkdir command
func (f *FileOps) Mkdir(path string, parents bool) string {
	flag := ""
	if parents {
		flag = "-p "
	}
	return fmt.Sprintf("mkdir %s%s", flag, quotePath(path))
}

// Chown generates chown command
func (f *FileOps) Chown(path, owner string, group *string) string {
	ownerSpec := owner
	if group != nil {
		ownerSpec = fmt.Sprintf("%s:%s", owner, *group)
	}
	return fmt.Sprintf("chown %s %s", ownerSpec, quotePath(path))
}

// Remove generates rm command
func (f *FileOps) Remove(path string) string {
	flags := ""
	if f.recursive {
		flags += "r"
	}
	if f.force {
		flags += "f"
	}
	flagPart := ""
	if flags != "" {
		flagPart = fmt.Sprintf("-%s ", flags)
	}
	quotedPath := path
	if !f.allowGlob {
		quotedPath = quotePath(path)
	}
	return fmt.Sprintf("rm %s%s", flagPart, quotedPath)
}

// Truncate generates truncate command
func (f *FileOps) Truncate(path string) string {
	cmd := fmt.Sprintf("truncate -s 0 %s", quotePath(path))
	return cmd
}

// Symlink generates symlink command
func (f *FileOps) Symlink(target, linkPath string) string {
	return fmt.Sprintf("ln -s %s %s", quotePath(target), quotePath(linkPath))
}

// FindDelete generates find delete command
func (f *FileOps) FindDelete(directory, pattern, fileType string) string {
	patternEscaped := escapeSingleQuotes(pattern)
	cmd := fmt.Sprintf("find %s -type %s -name '%s' -delete", quotePath(directory), fileType, patternEscaped)
	return cmd
}

// Exists generates command to check if file exists
func (f *FileOps) Exists(path string) string {
	cmd := fmt.Sprintf("test -f %s && echo exists || echo not_found", quotePath(path))
	return cmd
}

func quotePath(path string) string {
	// Simple quoting - in production use proper shell escaping
	if strings.Contains(path, " ") || strings.Contains(path, "$") {
		return fmt.Sprintf("'%s'", strings.ReplaceAll(path, "'", "'\"'\"'"))
	}
	return path
}

func escapeSingleQuotes(value string) string {
	return strings.ReplaceAll(value, "'", "'\"'\"'")
}

