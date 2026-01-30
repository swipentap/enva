using System;
using System.Linq;
using Enva.Libs;
using Enva.Services;

namespace Enva.Actions;

public class AppArmorParserStubAction : BaseAction, IAction
{
    public AppArmorParserStubAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        SSHService = sshService;
        APTService = aptService;
        PCTService = pctService;
        ContainerID = containerID;
        Cfg = cfg;
        ContainerCfg = containerCfg;
    }

    public override string Description()
    {
        return "AppArmor parser stub";
    }

    public bool Execute()
    {
        if (SSHService == null)
        {
            Logger.GetLogger("apparmor_parser_stub").Printf("SSH service not initialized");
            return false;
        }

        string script = @"APPARMOR_BIN=/usr/sbin/apparmor_parser
if command -v dpkg-divert  && [ -f ""$APPARMOR_BIN"" ]; then
  dpkg-divert --quiet --local --rename --add ""$APPARMOR_BIN""  || true
  if [ -f ""$APPARMOR_BIN.distrib"" ]; then
    cat <<'APPARMOR_STUB' > ""$APPARMOR_BIN""
#!/bin/sh
if [ ""$1"" = ""--version"" ] || [ ""$1"" = ""-V"" ]; then
  exec /usr/sbin/apparmor_parser.distrib ""$@""
fi
exit 0
APPARMOR_STUB
    chmod +x ""$APPARMOR_BIN"" || true
  fi
fi
echo apparmor_stub_done";

        int? timeout = 60;
        var (output, exitCode) = SSHService.Execute(script, timeout);
        if (!exitCode.HasValue || exitCode.Value != 0)
        {
            Logger.GetLogger("apparmor_parser_stub").Printf("AppArmor parser stub failed with exit code {0}", exitCode?.ToString() ?? "null");
            if (!string.IsNullOrEmpty(output))
            {
                var lines = output.Split('\n');
                if (lines.Length > 0)
                {
                    Logger.GetLogger("apparmor_parser_stub").Printf("AppArmor parser stub output: {0}", lines[lines.Length - 1]);
                }
            }
            return false;
        }
        return true;
    }
}

public static class AppArmorParserStubActionFactory
{
    public static IAction NewAppArmorParserStubAction(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        return new AppArmorParserStubAction(sshService, aptService, pctService, containerID, cfg, containerCfg);
    }
}
