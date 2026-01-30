package cli

import (
	"fmt"
	"strings"
)

// User wraps user-related commands
type User struct {
	username   string
	shell      string
	groups     []string
	createHome bool
}

// NewUser creates a new User command builder
func NewUser() *User {
	return &User{
		shell:      "/bin/bash",
		createHome: true,
	}
}

// Username sets username
func (u *User) Username(name string) *User {
	u.username = name
	return u
}

// Shell sets shell path
func (u *User) Shell(path string) *User {
	u.shell = path
	return u
}

// Groups sets groups
func (u *User) Groups(groupList []string) *User {
	u.groups = groupList
	return u
}

// CreateHome sets create home directory
func (u *User) CreateHome(value bool) *User {
	u.createHome = value
	return u
}

// CheckExists generates command to verify if user exists
func (u *User) CheckExists() string {
	if u.username == "" {
		panic("Username must be set")
	}
	return fmt.Sprintf("id -u %s", quote(u.username))
}

// Add generates command to add a user
func (u *User) Add() string {
	if u.username == "" {
		panic("Username must be set")
	}
	parts := []string{"useradd"}
	if u.createHome {
		parts = append(parts, "-m")
	}
	parts = append(parts, "-s", quote(u.shell))
	if len(u.groups) > 0 {
		groupSpec := strings.Join(u.groups, ",")
		parts = append(parts, "-G", quote(groupSpec))
	}
	parts = append(parts, quote(u.username))
	return strings.Join(parts, " ")
}

