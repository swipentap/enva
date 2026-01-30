package actions

import (
	"strings"
	"enva/libs"
	"enva/services"
)

// AppArmorParserStubAction disables AppArmor parser to avoid maintainer script failures
type AppArmorParserStubAction struct {
	*BaseAction
}

func NewAppArmorParserStubAction(sshService *services.SSHService, aptService *services.APTService, pctService *services.PCTService, containerID *string, cfg *libs.LabConfig, containerCfg *libs.ContainerConfig) Action {
	return &AppArmorParserStubAction{
		BaseAction: &BaseAction{
			SSHService:   sshService,
			APTService:   aptService,
			PCTService:   pctService,
			ContainerID: containerID,
			Cfg:          cfg,
			ContainerCfg: containerCfg,
		},
	}
}

func (a *AppArmorParserStubAction) Description() string {
	return "AppArmor parser stub"
}

func (a *AppArmorParserStubAction) Execute() bool {
	script := `APPARMOR_BIN=/usr/sbin/apparmor_parser
if command -v dpkg-divert  && [ -f "$APPARMOR_BIN" ]; then
  dpkg-divert --quiet --local --rename --add "$APPARMOR_BIN"  || true
  if [ -f "$APPARMOR_BIN.distrib" ]; then
    cat <<'APPARMOR_STUB' > "$APPARMOR_BIN"
#!/bin/sh
if [ "$1" = "--version" ] || [ "$1" = "-V" ]; then
  exec /usr/sbin/apparmor_parser.distrib "$@"
fi
exit 0
APPARMOR_STUB
    chmod +x "$APPARMOR_BIN" || true
  fi
fi
echo apparmor_stub_done`
	timeout := 60
	output, exitCode := a.SSHService.Execute(script, &timeout)
	if exitCode != nil && *exitCode != 0 {
		libs.GetLogger("apparmor_parser_stub").Printf("AppArmor parser stub failed with exit code %d", *exitCode)
		if output != "" {
			lines := strings.Split(output, "\n")
			if len(lines) > 0 {
				libs.GetLogger("apparmor_parser_stub").Printf("AppArmor parser stub output: %s", lines[len(lines)-1])
			}
		}
		return false
	}
	return true
}

