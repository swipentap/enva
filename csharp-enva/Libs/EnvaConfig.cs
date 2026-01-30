using System.Collections.Generic;
using YamlDotNet.Serialization;

namespace Enva.Libs;

/// <summary>
/// Root configuration class that matches the YAML file structure
/// </summary>
public class EnvaConfig
{
    [YamlMember(Alias = "environments")]
    public Dictionary<string, EnvironmentConfig>? Environments { get; set; }

    [YamlMember(Alias = "lxc")]
    public LXCConfig? LXC { get; set; }

    [YamlMember(Alias = "apt-cache-ct")]
    public string? APTCacheCT { get; set; }

    [YamlMember(Alias = "templates")]
    public List<TemplateConfig>? Templates { get; set; }

    [YamlMember(Alias = "ct")]
    public List<ContainerConfig>? Containers { get; set; }

    [YamlMember(Alias = "kubernetes")]
    public KubernetesConfigYaml? Kubernetes { get; set; }

    [YamlMember(Alias = "timeouts")]
    public TimeoutsConfig? Timeouts { get; set; }

    [YamlMember(Alias = "users")]
    public List<UserConfig>? Users { get; set; }

    [YamlMember(Alias = "services")]
    public Dictionary<string, ServicePortsConfig>? Services { get; set; }

    [YamlMember(Alias = "dns")]
    public DNSConfig? DNS { get; set; }

    [YamlMember(Alias = "docker")]
    public DockerConfig? Docker { get; set; }

    [YamlMember(Alias = "template_config")]
    public TemplatePatternsConfig? TemplateConfig { get; set; }

    [YamlMember(Alias = "ssh")]
    public SSHConfig? SSH { get; set; }

    [YamlMember(Alias = "waits")]
    public WaitsConfig? Waits { get; set; }

    [YamlMember(Alias = "glusterfs")]
    public GlusterFSConfig? GlusterFS { get; set; }

    [YamlMember(Alias = "backup")]
    public BackupConfig? Backup { get; set; }
}

/// <summary>
/// Environment-specific configuration that overrides top-level config
/// </summary>
public class EnvironmentConfig
{
    [YamlMember(Alias = "id-base")]
    public int? IDBase { get; set; }

    [YamlMember(Alias = "network")]
    public string? Network { get; set; }

    [YamlMember(Alias = "postgres_host")]
    public string? PostgresHost { get; set; }

    [YamlMember(Alias = "dns_server")]
    public string? DNSServer { get; set; }

    [YamlMember(Alias = "domain")]
    public string? Domain { get; set; }

    [YamlMember(Alias = "certificate_path")]
    public string? CertificatePath { get; set; }

    [YamlMember(Alias = "certificate_source_path")]
    public string? CertificateSourcePath { get; set; }

    [YamlMember(Alias = "lxc")]
    public LXCConfig? LXC { get; set; }

    [YamlMember(Alias = "services")]
    public Dictionary<string, ServicePortsConfig>? Services { get; set; }
}

/// <summary>
/// Kubernetes configuration as it appears in YAML (supports both int and object with id)
/// </summary>
public class KubernetesConfigYaml
{
    [YamlMember(Alias = "control")]
    public List<object>? Control { get; set; }

    [YamlMember(Alias = "workers")]
    public List<object>? Workers { get; set; }

    [YamlMember(Alias = "actions")]
    public List<object>? Actions { get; set; }
}
