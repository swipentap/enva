using System;

namespace Enva.CLI;

public class Vzdump
{
    private string compress = "zstd";
    private string mode = "stop";

    public static Vzdump NewVzdump()
    {
        return new Vzdump();
    }

    public Vzdump Compress(string value)
    {
        compress = value;
        return this;
    }

    public Vzdump Mode(string value)
    {
        mode = value;
        return this;
    }

    public string CreateTemplate(string containerID, string dumpdir)
    {
        return $"vzdump {containerID} --dumpdir {Quote(dumpdir)} --compress {compress} --mode {mode}";
    }

    public string FindArchive(string dumpdir, string containerID)
    {
        return $"ls -t {Quote(dumpdir)}/vzdump-lxc-{containerID}-*.tar.zst | head -1";
    }

    public string GetArchiveSize(string archivePath)
    {
        return $"stat --format=%s {Quote(archivePath)} || echo '0'";
    }

    public static int? ParseArchiveSize(string output)
    {
        if (string.IsNullOrEmpty(output))
        {
            return null;
        }
        if (int.TryParse(output.Trim(), out int size))
        {
            return size;
        }
        return null;
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
