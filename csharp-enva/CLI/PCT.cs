using System;

namespace Enva.CLI;

public class PCT
{
    private string containerID = "";
    private bool forceFlag;

    public static PCT NewPCT()
    {
        return new PCT();
    }

    public PCT ContainerID(object id)
    {
        containerID = id.ToString() ?? "";
        return this;
    }

    public PCT Force()
    {
        forceFlag = true;
        return this;
    }

    public string Create(string templatePath, string hostname, int memory, int swap, int cores, string ipAddress, string gateway, string bridge, string storage, int rootfsSize, bool unprivileged, string ostype, string arch)
    {
        string unprivValue = unprivileged ? "1" : "0";
        return $"pct create {containerID} {templatePath} --hostname {hostname} --memory {memory} --swap {swap} --cores {cores} --net0 name=eth0,bridge={bridge},ip={ipAddress}/24,gw={gateway} --rootfs {storage}:{rootfsSize} --unprivileged {unprivValue} --ostype {ostype} --arch {arch}";
    }

    public string Start()
    {
        string force = forceFlag ? " --force" : "";
        return $"pct start {containerID}{force}";
    }

    public string Stop()
    {
        string force = forceFlag ? " --force" : "";
        return $"pct stop {containerID}{force}";
    }

    public string Destroy()
    {
        string force = forceFlag ? " --force" : "";
        return $"pct destroy {containerID}{force}";
    }

    public string List()
    {
        return "pct list";
    }

    public string Status()
    {
        return $"pct status {containerID}";
    }

    public string Config()
    {
        return $"pct config {containerID}";
    }

    public string SetOption(string option, string value)
    {
        return $"pct set {containerID} --{option} {value}";
    }

    public string SetFeatures(bool nesting, bool keyctl, bool fuse)
    {
        return $"pct set {containerID} --features nesting={BoolToInt(nesting)},keyctl={BoolToInt(keyctl)},fuse={BoolToInt(fuse)}";
    }

    private int BoolToInt(bool b)
    {
        return b ? 1 : 0;
    }
}
