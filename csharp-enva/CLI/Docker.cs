using System;
using System.Collections.Generic;
using System.Linq;

namespace Enva.CLI;

public class Docker
{
    private string dockerCmd = "docker";
    private bool showAll;
    private bool includeAll;
    private bool force;
    private int tail = 20;

    public static Docker NewDocker()
    {
        return new Docker();
    }

    public Docker DockerCmd(string cmd)
    {
        dockerCmd = cmd;
        return this;
    }

    public Docker ShowAll(bool value)
    {
        showAll = value;
        return this;
    }

    public Docker IncludeAll(bool value)
    {
        includeAll = value;
        return this;
    }

    public Docker Force(bool value)
    {
        force = value;
        return this;
    }

    public Docker Tail(int lines)
    {
        tail = lines;
        return this;
    }

    public string FindDocker()
    {
        return "dpkg -L docker.io | grep -E '/bin/docker$' | head -1 || dpkg -L docker-ce | grep -E '/bin/docker$' | head -1 || command -v docker || which docker || find /usr /usr/local -name docker -type f | head -1 || test -x /usr/bin/docker && echo /usr/bin/docker || test -x /usr/local/bin/docker && echo /usr/local/bin/docker || echo 'docker'";
    }

    public string Version()
    {
        return $"{dockerCmd} --version";
    }

    public string PS()
    {
        string allFlag = showAll ? "-a" : "";
        return $"{dockerCmd} ps {allFlag}".Trim();
    }

    public string SwarmInit(string advertiseAddr)
    {
        return $"{dockerCmd} swarm init --advertise-addr {advertiseAddr}";
    }

    public string SwarmJoinToken(string role)
    {
        return $"{dockerCmd} swarm join-token {role} -q";
    }

    public string SwarmJoin(string token, string managerAddr)
    {
        return $"{dockerCmd} swarm join --token {token} {managerAddr}";
    }

    public string NodeLS()
    {
        return $"{dockerCmd} node ls";
    }

    public string NodeUpdate(string nodeName, string availability)
    {
        return $"{dockerCmd} node update --availability {availability} {nodeName}";
    }

    public string VolumeCreate(string volumeName)
    {
        return $"{dockerCmd} volume create {volumeName} || true";
    }

    public string VolumeRM(string volumeName)
    {
        string forceFlag = force ? "-f " : "";
        return $"{dockerCmd} volume rm {forceFlag}{volumeName} || true";
    }

    public string Run(string image, string name, Dictionary<string, object> args)
    {
        string cmd = $"{dockerCmd} run -d --name {name}";
        if (args != null)
        {
            if (args.ContainsKey("restart") && args["restart"] is string restart)
            {
                cmd += $" --restart={restart}";
            }
            if (args.ContainsKey("network") && args["network"] is string network)
            {
                cmd += $" --network {network}";
            }
            if (args.ContainsKey("volumes") && args["volumes"] is List<string> volumes)
            {
                foreach (var vol in volumes)
                {
                    cmd += $" -v {vol}";
                }
            }
            if (args.ContainsKey("ports") && args["ports"] is List<string> ports)
            {
                foreach (var port in ports)
                {
                    cmd += $" -p {port}";
                }
            }
            if (args.ContainsKey("security_opts") && args["security_opts"] is List<string> securityOpts)
            {
                foreach (var opt in securityOpts)
                {
                    cmd += $" --security-opt {opt}";
                }
            }
        }
        cmd += $" {image}";
        if (args != null && args.ContainsKey("command_args") && args["command_args"] is List<string> commandArgs)
        {
            foreach (var arg in commandArgs)
            {
                cmd += $" {Quote(arg)}";
            }
        }
        return cmd;
    }

    public string Stop(string containerName)
    {
        return $"{dockerCmd} stop {containerName} || true";
    }

    public string RM(string containerName)
    {
        return $"{dockerCmd} rm {containerName} || true";
    }

    public string Logs(string containerName)
    {
        return $"{dockerCmd} logs {containerName} | tail -{tail}";
    }

    public string SystemPrune()
    {
        string flags = "";
        if (includeAll)
        {
            flags += " -a";
        }
        if (force)
        {
            flags += " -f";
        }
        return $"{dockerCmd} system prune{flags} || true";
    }

    public string IsInstalledCheck()
    {
        return $"command -v {dockerCmd} && echo installed || echo not_installed";
    }

    public static bool ParseDockerIsInstalled(string output)
    {
        return output.ToLower().Contains("installed");
    }

    private string Quote(string s)
    {
        if (s.Contains(" ") || s.Contains("$") || s.Contains("'"))
        {
            return $"'{s.Replace("'", "'\"'\"'")}'";
        }
        return s;
    }
}
