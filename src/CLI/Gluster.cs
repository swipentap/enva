using System;
using System.Collections.Generic;
using System.Linq;

namespace Enva.CLI;

public class Gluster
{
    private string glusterCmd = "gluster";
    private bool force = true;

    public static Gluster NewGluster()
    {
        return new Gluster();
    }

    public Gluster GlusterCmd(string cmd)
    {
        glusterCmd = cmd;
        return this;
    }

    public Gluster Force(bool value)
    {
        force = value;
        return this;
    }

    public string FindGluster()
    {
        var parts = new List<string>
        {
            "dpkg -L glusterfs-client | grep -E '/bin/gluster$|/sbin/gluster$' | head -1",
            "command -v gluster",
            "which gluster",
            "find /usr /usr/sbin /usr/bin -name gluster -type f | head -1",
            "test -x /usr/sbin/gluster && echo /usr/sbin/gluster",
            "test -x /usr/bin/gluster && echo /usr/bin/gluster",
            "echo 'gluster'"
        };
        return string.Join(" || ", parts);
    }

    public string PeerProbe(string hostname)
    {
        return $"{glusterCmd} peer probe {hostname}";
    }

    public string PeerStatus()
    {
        return $"{glusterCmd} peer status";
    }

    public string VolumeCreate(string volumeName, int replicaCount, IEnumerable<string> bricks)
    {
        string bricksStr = string.Join(" ", bricks);
        string forceFlag = force ? "force" : "";
        var parts = new List<string>
        {
            glusterCmd,
            "volume",
            "create",
            volumeName,
            "replica",
            replicaCount.ToString(),
            bricksStr
        };
        if (!string.IsNullOrEmpty(forceFlag))
        {
            parts.Add(forceFlag);
        }
        return string.Join(" ", parts).Trim();
    }

    public string VolumeStart(string volumeName)
    {
        return $"{glusterCmd} volume start {volumeName}";
    }

    public string VolumeStatus(string volumeName)
    {
        return $"{glusterCmd} volume status {volumeName}";
    }

    public string VolumeInfo(string volumeName)
    {
        return $"{glusterCmd} volume info {volumeName}";
    }

    public string VolumeExistsCheck(string volumeName)
    {
        return $"{glusterCmd} volume info {volumeName} && echo yes || echo no";
    }

    public string IsInstalledCheck()
    {
        return $"command -v {glusterCmd} && echo installed || echo not_installed";
    }

    public static bool ParseGlusterIsInstalled(string output)
    {
        return output.Contains("installed");
    }

    public static bool ParseVolumeExists(string output)
    {
        return output.Contains("yes");
    }
}
