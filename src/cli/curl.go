package cli

import (
	"fmt"
	"strings"
)

// Curl wraps curl commands
type Curl struct {
	failSilently bool
	silent       bool
	showErrors   bool
	location     bool
	output       *string
	url          string
}

// NewCurl creates a new Curl command builder
func NewCurl() *Curl {
	return &Curl{
		failSilently: true,
		silent:       true,
		showErrors:   true,
	}
}

// FailSilently sets fail silently mode
func (c *Curl) FailSilently(value bool) *Curl {
	c.failSilently = value
	return c
}

// Silent sets silent mode
func (c *Curl) Silent(value bool) *Curl {
	c.silent = value
	return c
}

// ShowErrors shows errors even in silent mode
func (c *Curl) ShowErrors(value bool) *Curl {
	c.showErrors = value
	return c
}

// Location follows redirects
func (c *Curl) Location(value bool) *Curl {
	c.location = value
	return c
}

// Output sets output file path
func (c *Curl) Output(path string) *Curl {
	c.output = &path
	return c
}

// URL sets URL to fetch
func (c *Curl) URL(url string) *Curl {
	c.url = url
	return c
}

// Download generates curl download command
func (c *Curl) Download() string {
	if c.url == "" {
		panic("URL must be set for curl download")
	}
	flags := []string{}
	if c.failSilently {
		flags = append(flags, "-f")
	}
	if c.silent {
		flags = append(flags, "-s")
	}
	if c.showErrors {
		flags = append(flags, "-S")
	}
	if c.location {
		flags = append(flags, "-L")
	}
	if c.output != nil {
		flags = append(flags, fmt.Sprintf("-o %s", quote(*c.output)))
	}
	flagStr := ""
	if len(flags) > 0 {
		flagStr = strings.Join(flags, " ")
	}
	return fmt.Sprintf("curl %s %s 2>&1", flagStr, quote(c.url))
}

