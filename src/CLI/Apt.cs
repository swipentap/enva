using System;
using System.Collections.Generic;
using System.Linq;

namespace Enva.CLI;

public class Apt
{
    private bool quiet;
    private bool useAptGet;
    private bool noInstallRecommends;
    private Dictionary<string, string> options = new Dictionary<string, string>();

    public static Apt NewApt()
    {
        return new Apt();
    }

    public Apt Quiet()
    {
        quiet = true;
        return this;
    }

    public Apt UseAptGet()
    {
        useAptGet = true;
        return this;
    }

    public Apt NoInstallRecommends()
    {
        noInstallRecommends = true;
        return this;
    }

    public Apt Options(Dictionary<string, string> opts)
    {
        foreach (var kvp in opts)
        {
            options[kvp.Key] = kvp.Value;
        }
        return this;
    }

    public string Update()
    {
        string cmd = useAptGet ? "apt-get" : "apt";
        var flags = new List<string>();
        if (quiet)
        {
            flags.Add("-qq");
        }
        string flagStr = flags.Any() ? " " + string.Join(" ", flags) : "";
        return $"{cmd}{flagStr} update";
    }

    public string Install(IEnumerable<string> packages)
    {
        string cmd = useAptGet ? "apt-get" : "apt";
        var flags = new List<string> { "-y" };
        if (quiet)
        {
            flags.Add("-qq");
        }
        if (noInstallRecommends)
        {
            flags.Add("--no-install-recommends");
        }
        
        var optParts = new List<string>();
        if (options != null && options.Any())
        {
            foreach (var kvp in options)
            {
                if (kvp.Key == "Dpkg::Options::" && kvp.Value.Contains(" "))
                {
                    var optionsList = kvp.Value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var opt in optionsList)
                    {
                        optParts.Add($"-o {kvp.Key}={opt}");
                    }
                }
                else
                {
                    optParts.Add($"-o {kvp.Key}={kvp.Value}");
                }
            }
        }
        
        var parts = new List<string> { cmd };
        parts.Add(string.Join(" ", flags));
        if (optParts.Any())
        {
            parts.Add(string.Join(" ", optParts));
        }
        parts.Add("install");
        parts.Add(string.Join(" ", packages));
        return string.Join(" ", parts);
    }

    public string Upgrade()
    {
        string cmd = useAptGet ? "apt-get" : "apt";
        var flags = new List<string> { "-y" };
        if (quiet)
        {
            flags.Add("-qq");
        }
        return $"{cmd} {string.Join(" ", flags)} upgrade";
    }

    public string DistUpgrade()
    {
        string cmd = "apt-get";
        var flags = new List<string> { "-y" };
        if (quiet)
        {
            flags.Add("-qq");
        }
        return $"{cmd} {string.Join(" ", flags)} dist-upgrade";
    }

    public static string IsInstalledCheckCmd(string packageName)
    {
        return $"dpkg -l | grep -q '^ii.*{packageName}' && echo installed || echo not_installed";
    }

    public static bool ParseIsInstalled(string output)
    {
        return output.Contains("installed");
    }
}
