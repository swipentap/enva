using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Enva.Libs;

public class ContainerResources
{
    [YamlMember(Alias = "memory")]
    public int Memory { get; set; }

    [YamlMember(Alias = "swap")]
    public int Swap { get; set; }

    [YamlMember(Alias = "cores")]
    public int Cores { get; set; }

    [YamlMember(Alias = "rootfs_size")]
    public int RootfsSize { get; set; }
}

public class ContainerConfig
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "id")]
    public int ID { get; set; }

    [YamlMember(Alias = "ip")]
    public int IP { get; set; }

    [YamlMember(Alias = "hostname")]
    public string Hostname { get; set; } = "";

    [YamlMember(Alias = "template")]
    public string? Template { get; set; }

    [YamlMember(Alias = "resources")]
    public ContainerResources? Resources { get; set; }

    [YamlMember(Alias = "params")]
    public Dictionary<string, object>? Params { get; set; }

    [YamlMember(Alias = "actions")]
    public List<object>? Actions { get; set; }

    public string? IPAddress { get; set; }

    public List<string> GetActionNames()
    {
        var result = new List<string>();
        if (Actions == null) return result;
        foreach (var action in Actions)
        {
            if (action is string strAction)
            {
                result.Add(strAction);
            }
            else if (action is Dictionary<object, object> dictAction)
            {
                if (dictAction.TryGetValue("name", out object? nameObj))
                {
                    result.Add(nameObj?.ToString() ?? "");
                }
            }
        }
        return result;
    }

    public Dictionary<string, object>? GetActionProperties(string actionName)
    {
        if (Actions == null) return null;
        foreach (var action in Actions)
        {
            if (action is Dictionary<object, object> dictAction)
            {
                if (dictAction.TryGetValue("name", out object? nameObj) && nameObj?.ToString() == actionName)
                {
                    if (dictAction.TryGetValue("properties", out object? propsObj) && propsObj is Dictionary<object, object> propsDict)
                    {
                        var result = new Dictionary<string, object>();
                        foreach (var kvp in propsDict)
                        {
                            result[kvp.Key.ToString() ?? ""] = kvp.Value;
                        }
                        return result;
                    }
                }
            }
        }
        return null;
    }

    [YamlMember(Alias = "privileged")]
    public bool? Privileged { get; set; }

    [YamlMember(Alias = "nested")]
    public bool? Nested { get; set; }

    [YamlMember(Alias = "autostart")]
    public bool? Autostart { get; set; }
}

public class TemplateConfig
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "id")]
    public int ID { get; set; }

    [YamlMember(Alias = "ip")]
    public int IP { get; set; }

    [YamlMember(Alias = "hostname")]
    public string Hostname { get; set; } = "";

    [YamlMember(Alias = "template")]
    public string? Template { get; set; }

    [YamlMember(Alias = "resources")]
    public ContainerResources? Resources { get; set; }

    public string? IPAddress { get; set; }

    [YamlMember(Alias = "actions")]
    public List<object>? Actions { get; set; }

    [YamlMember(Alias = "privileged")]
    public bool? Privileged { get; set; }

    [YamlMember(Alias = "nested")]
    public bool? Nested { get; set; }

    public List<string> GetActionNames()
    {
        var result = new List<string>();
        if (Actions == null) return result;
        foreach (var action in Actions)
        {
            if (action is string strAction)
            {
                result.Add(strAction);
            }
            else if (action is Dictionary<object, object> dictAction)
            {
                if (dictAction.TryGetValue("name", out object? nameObj))
                {
                    result.Add(nameObj?.ToString() ?? "");
                }
            }
        }
        return result;
    }

    public Dictionary<string, object>? GetActionProperties(string actionName)
    {
        if (Actions == null) return null;
        foreach (var action in Actions)
        {
            if (action is Dictionary<object, object> dictAction)
            {
                if (dictAction.TryGetValue("name", out object? nameObj) && nameObj?.ToString() == actionName)
                {
                    if (dictAction.TryGetValue("properties", out object? propsObj) && propsObj is Dictionary<object, object> propsDict)
                    {
                        var result = new Dictionary<string, object>();
                        foreach (var kvp in propsDict)
                        {
                            result[kvp.Key.ToString() ?? ""] = kvp.Value;
                        }
                        return result;
                    }
                }
            }
        }
        return null;
    }
}

public class KubernetesConfig
{
    [YamlMember(Alias = "control")]
    public List<int> Control { get; set; } = new();

    [YamlMember(Alias = "workers")]
    public List<int> Workers { get; set; } = new();

    [YamlMember(Alias = "actions")]
    public List<object>? Actions { get; set; }

    public List<string> GetActionNames()
    {
        var result = new List<string>();
        if (Actions == null) return result;
        foreach (var action in Actions)
        {
            if (action is string strAction)
            {
                result.Add(strAction);
            }
            else if (action is Dictionary<object, object> dictAction)
            {
                if (dictAction.TryGetValue("name", out object? nameObj))
                {
                    result.Add(nameObj?.ToString() ?? "");
                }
            }
        }
        return result;
    }

    public Dictionary<string, object>? GetActionProperties(string actionName)
    {
        if (Actions == null) return null;
        foreach (var action in Actions)
        {
            if (action is Dictionary<object, object> dictAction)
            {
                if (dictAction.TryGetValue("name", out object? nameObj) && nameObj?.ToString() == actionName)
                {
                    if (dictAction.TryGetValue("properties", out object? propsObj) && propsObj is Dictionary<object, object> propsDict)
                    {
                        var result = new Dictionary<string, object>();
                        foreach (var kvp in propsDict)
                        {
                            result[kvp.Key.ToString() ?? ""] = kvp.Value;
                        }
                        return result;
                    }
                }
            }
        }
        return null;
    }
}

public class LXCConfig
{
    [YamlMember(Alias = "host")]
    public string Host { get; set; } = "";

    [YamlMember(Alias = "storage")]
    public string Storage { get; set; } = "";

    [YamlMember(Alias = "bridge")]
    public string Bridge { get; set; } = "";

    [YamlMember(Alias = "template_dir")]
    public string TemplateDir { get; set; } = "";

    [YamlMember(Alias = "gateway_octet")]
    public int GatewayOctet { get; set; }
}

public class PortConfig
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "port")]
    public int Port { get; set; }

    [YamlMember(Alias = "type")]
    public string Type { get; set; } = "http"; // tcp | http | https
}

public class ServicePortsConfig
{
    [YamlMember(Alias = "ports")]
    public List<PortConfig> Ports { get; set; } = new();
}

public class ServicesConfig
{
    // Services dictionary - deserialized directly from YAML services: block
    // The YAML structure services: { apt_cache: {...}, postgresql: {...} }
    // is deserialized by YAML.NET into this Dictionary
    public Dictionary<string, ServicePortsConfig> Services { get; set; } = new();
}

public class UserConfig
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "password")]
    public string? Password { get; set; }

    [YamlMember(Alias = "sudo_group")]
    public string SudoGroup { get; set; } = "sudo";
}

public class UsersConfig
{
    [YamlMember(Alias = "users")]
    public List<UserConfig> Users { get; set; } = new();

    public string DefaultUser()
    {
        if (Users.Count > 0)
        {
            return Users[0].Name;
        }
        return "root";
    }

    public string SudoGroup()
    {
        if (Users.Count > 0)
        {
            return Users[0].SudoGroup;
        }
        return "sudo";
    }
}

public class DNSConfig
{
    [YamlMember(Alias = "servers")]
    public List<string> Servers { get; set; } = new();
}

public class TemplatePatternsConfig
{
    [YamlMember(Alias = "base")]
    public List<string> Base { get; set; } = new();

    [YamlMember(Alias = "patterns")]
    public Dictionary<string, string> Patterns { get; set; } = new();

    [YamlMember(Alias = "preserve")]
    public List<string> Preserve { get; set; } = new();
}

public class SSHConfig
{
    [YamlMember(Alias = "connect_timeout")]
    public int ConnectTimeout { get; set; }

    [YamlMember(Alias = "batch_mode")]
    public bool BatchMode { get; set; }

    [YamlMember(Alias = "default_exec_timeout")]
    public int DefaultExecTimeout { get; set; } = 300;

    [YamlMember(Alias = "read_buffer_size")]
    public int ReadBufferSize { get; set; } = 4096;

    [YamlMember(Alias = "poll_interval")]
    public double PollInterval { get; set; } = 0.05;

    [YamlMember(Alias = "default_username")]
    public string DefaultUsername { get; set; } = "root";

    [YamlMember(Alias = "look_for_keys")]
    public bool LookForKeys { get; set; } = true;

    [YamlMember(Alias = "allow_agent")]
    public bool AllowAgent { get; set; } = true;

    public bool Verbose { get; set; }
}

public class WaitsConfig
{
    [YamlMember(Alias = "container_startup")]
    public int ContainerStartup { get; set; }

    [YamlMember(Alias = "container_ready_max_attempts")]
    public int ContainerReadyMaxAttempts { get; set; }

    [YamlMember(Alias = "container_ready_sleep")]
    public int ContainerReadySleep { get; set; }

    [YamlMember(Alias = "network_config")]
    public int NetworkConfig { get; set; }

    [YamlMember(Alias = "service_start")]
    public int ServiceStart { get; set; }

    [YamlMember(Alias = "glusterfs_setup")]
    public int GlusterFSSetup { get; set; }
}

public class GlusterFSNodeConfig
{
    [YamlMember(Alias = "id")]
    public int ID { get; set; }
}

public class GlusterFSConfig
{
    [YamlMember(Alias = "volume_name")]
    public string VolumeName { get; set; } = "";

    [YamlMember(Alias = "brick_path")]
    public string BrickPath { get; set; } = "";

    [YamlMember(Alias = "mount_point")]
    public string MountPoint { get; set; } = "";

    [YamlMember(Alias = "replica_count")]
    public int ReplicaCount { get; set; }

    [YamlMember(Alias = "cluster_nodes")]
    public List<GlusterFSNodeConfig>? ClusterNodes { get; set; }
}

public class TimeoutsConfig
{
    [YamlMember(Alias = "ubuntu_template")]
    public int UbuntuTemplate { get; set; }
}

public class BackupItemConfig
{
    [YamlMember(Alias = "name")]
    public string Name { get; set; } = "";

    [YamlMember(Alias = "source_container_id")]
    public int SourceContainerID { get; set; }

    [YamlMember(Alias = "source_path")]
    public string SourcePath { get; set; } = "";

    [YamlMember(Alias = "archive_base")]
    public string? ArchiveBase { get; set; }

    [YamlMember(Alias = "archive_path")]
    public string? ArchivePath { get; set; }
}

public class BackupConfig
{
    [YamlMember(Alias = "container_id")]
    public int ContainerID { get; set; }

    [YamlMember(Alias = "backup_dir")]
    public string BackupDir { get; set; } = "";

    [YamlMember(Alias = "name_prefix")]
    public string NamePrefix { get; set; } = "";

    [YamlMember(Alias = "items")]
    public List<BackupItemConfig> Items { get; set; } = new();
}

public class LabConfig
{
    public string Network { get; set; } = "";
    public LXCConfig LXC { get; set; } = new();
    public List<ContainerConfig> Containers { get; set; } = new();
    public List<TemplateConfig> Templates { get; set; } = new();
    public ServicesConfig Services { get; set; } = new();
    public UsersConfig Users { get; set; } = new();
    public DNSConfig DNS { get; set; } = new();
    public TemplatePatternsConfig TemplateConfig { get; set; } = new();
    public SSHConfig SSH { get; set; } = new();
    public WaitsConfig Waits { get; set; } = new();
    public TimeoutsConfig Timeouts { get; set; } = new();
    public int IDBase { get; set; }
    public GlusterFSConfig? GlusterFS { get; set; }
    public KubernetesConfig? Kubernetes { get; set; }
    public BackupConfig? Backup { get; set; }
    public string APTCacheCT { get; set; } = "";
    public string? Domain { get; set; }

    /// <summary>Git branch for the Argo app-of-apps repo (targetRevision) when installing ArgoCD apps.</summary>
    public string? ArgoAppsBranch { get; set; }

    public string? CertificatePath { get; set; }
    public string? CertificateSourcePath { get; set; }
    
    public string? NetworkBase { get; set; }
    public string? Gateway { get; set; }
    public List<ContainerConfig> KubernetesControl { get; set; } = new();
    public List<ContainerConfig> KubernetesWorkers { get; set; } = new();

    public string LXCHost() => LXC.Host;
    public string LXCStorage() => LXC.Storage;
    public string LXCBridge() => LXC.Bridge;
    public string LXCTemplateDir() => LXC.TemplateDir;
    
    public int APTCachePort()
    {
        if (Services.Services != null && Services.Services.TryGetValue("apt_cache", out var aptCacheService))
        {
            if (aptCacheService.Ports != null && aptCacheService.Ports.Count > 0)
            {
                return aptCacheService.Ports[0].Port;
            }
        }
        return 80; // default port
    }

    public string GetGateway()
    {
        if (Gateway != null)
        {
            return Gateway;
        }
        return "";
    }

    public void ComputeDerivedFields()
    {
        var networkStr = Network;
        var slashIdx = networkStr.LastIndexOf('/');
        if (slashIdx >= 0)
        {
            networkStr = networkStr.Substring(0, slashIdx);
        }
        
        var parts = networkStr.Split('.');
        if (parts.Length >= 3)
        {
            var networkBase = $"{parts[0]}.{parts[1]}.{parts[2]}";
            NetworkBase = networkBase;
            var gateway = $"{networkBase}.{LXC.GatewayOctet}";
            Gateway = gateway;
        }

        foreach (var container in Containers)
        {
            if (NetworkBase != null)
            {
                container.IPAddress = $"{NetworkBase}.{container.IP}";
            }
        }

        foreach (var template in Templates)
        {
            if (NetworkBase != null)
            {
                template.IPAddress = $"{NetworkBase}.{template.IP}";
            }
        }

        if (Kubernetes != null)
        {
            foreach (var container in Containers)
            {
                if (Kubernetes.Control.Contains(container.ID))
                {
                    KubernetesControl.Add(container);
                }
                if (Kubernetes.Workers.Contains(container.ID))
                {
                    KubernetesWorkers.Add(container);
                }
            }
        }
    }
}
