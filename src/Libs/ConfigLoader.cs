using System;
using System.Collections.Generic;
using System.Linq;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Enva.Libs;

public static class ConfigLoader
{
    /// <summary>
    /// Loads and deserializes the YAML config file directly to strongly-typed config classes
    /// </summary>
    public static EnvaConfig LoadConfig(string configFile)
    {
        var data = System.IO.File.ReadAllText(configFile);
        // Don't use naming convention - we use explicit [YamlMember] aliases everywhere
        var deserializer = new DeserializerBuilder()
            .IgnoreFields()
            .IgnoreUnmatchedProperties()
            .Build();
        
        var config = deserializer.Deserialize<EnvaConfig>(data);
        if (config == null)
        {
            throw new Exception("Failed to deserialize config file");
        }
        return config;
    }

    /// <summary>
    /// Converts EnvaConfig to LabConfig, applying environment-specific overrides
    /// </summary>
    public static LabConfig ToLabConfig(EnvaConfig config, string? environment, bool verbose)
    {
        // Get environment-specific config if provided
        EnvironmentConfig? envConfig = null;
        if (!string.IsNullOrEmpty(environment) && config.Environments != null)
        {
            if (!config.Environments.TryGetValue(environment, out envConfig))
            {
                var available = string.Join(", ", config.Environments.Keys);
                throw new Exception($"Environment '{environment}' not found in configuration. Available: [{available}]");
            }
        }

        // Determine ID base (environment takes precedence)
        int idBase = envConfig?.IDBase ?? 3000;

        // Determine network (environment takes precedence, then top-level)
        string network = envConfig?.Network ?? "10.11.3.0/24";

        // Merge LXC config (environment takes precedence, fallback to top-level for backward compatibility)
        LXCConfig? lxc = null;
        if (envConfig?.LXC != null)
        {
            lxc = envConfig.LXC;
        }
        else if (config.LXC != null)
        {
            lxc = config.LXC;
        }
        
        if (lxc == null)
        {
            throw new Exception("LXC configuration not found in environment or top-level config");
        }

        // Process containers (apply ID base)
        var containers = new List<ContainerConfig>();
        if (config.Containers != null)
        {
            foreach (var ct in config.Containers)
            {
                containers.Add(new ContainerConfig
                {
                    Name = ct.Name ?? "",
                    ID = ct.ID + idBase,
                    IP = ct.IP,
                    Hostname = ct.Hostname ?? "",
                    Template = ct.Template,
                    Resources = ct.Resources,
                    Params = ct.Params,
                    Actions = ct.Actions,
                    Privileged = ct.Privileged,
                    Nested = ct.Nested,
                    Autostart = ct.Autostart ?? true
                });
            }
        }

        // Process templates (apply ID base)
        var templates = new List<TemplateConfig>();
        if (config.Templates != null)
        {
            foreach (var tmpl in config.Templates)
            {
                templates.Add(new TemplateConfig
                {
                    Name = tmpl.Name ?? "",
                    ID = tmpl.ID + idBase,
                    IP = tmpl.IP,
                    Hostname = tmpl.Hostname ?? "",
                    Template = tmpl.Template,
                    Resources = tmpl.Resources,
                    Actions = tmpl.Actions,
                    Privileged = tmpl.Privileged,
                    Nested = tmpl.Nested
                });
            }
        }

        // Process Kubernetes config
        KubernetesConfig? kubernetes = null;
        var kubernetesActions = new List<string>();
        if (config.Kubernetes != null)
        {
            var control = new List<int>();
            if (config.Kubernetes.Control != null)
            {
                foreach (var c in config.Kubernetes.Control)
                {
                    int cID = 0;
                    if (c is int cInt)
                    {
                        cID = cInt > 0 ? cInt + idBase : 0;
                    }
                    else if (c is long cLong)
                    {
                        cID = cLong > 0 ? (int)cLong + idBase : 0;
                    }
                    else if (c is Dictionary<object, object> cDict)
                    {
                        if (cDict.TryGetValue("id", out var idObj))
                        {
                            int baseID = ToInt(idObj);
                            if (baseID > 0)
                            {
                                cID = baseID + idBase;
                            }
                        }
                    }
                    else if (c is Dictionary<string, object> cStrDict)
                    {
                        if (cStrDict.TryGetValue("id", out var idObj))
                        {
                            int baseID = ToInt(idObj);
                            if (baseID > 0)
                            {
                                cID = baseID + idBase;
                            }
                        }
                    }
                    if (cID > 0)
                    {
                        control.Add(cID);
                    }
                }
            }

            var workers = new List<int>();
            if (config.Kubernetes.Workers != null)
            {
                foreach (var w in config.Kubernetes.Workers)
                {
                    int wID = 0;
                    if (w is int wInt)
                    {
                        wID = wInt > 0 ? wInt + idBase : 0;
                    }
                    else if (w is long wLong)
                    {
                        wID = wLong > 0 ? (int)wLong + idBase : 0;
                    }
                    else if (w is Dictionary<object, object> wDict)
                    {
                        if (wDict.TryGetValue("id", out var idObj))
                        {
                            int baseID = ToInt(idObj);
                            if (baseID > 0)
                            {
                                wID = baseID + idBase;
                            }
                        }
                    }
                    else if (w is Dictionary<string, object> wStrDict)
                    {
                        if (wStrDict.TryGetValue("id", out var idObj))
                        {
                            int baseID = ToInt(idObj);
                            if (baseID > 0)
                            {
                                wID = baseID + idBase;
                            }
                        }
                    }
                    if (wID > 0)
                    {
                        workers.Add(wID);
                    }
                }
            }

            kubernetes = new KubernetesConfig
            {
                Control = control,
                Workers = workers,
                Actions = config.Kubernetes.Actions
            };
        }

        // Merge services (environment overrides top-level)
        var services = new ServicesConfig();
        if (config.Services != null)
        {
            services.Services = new Dictionary<string, ServicePortsConfig>(config.Services);
            if (envConfig?.Services != null)
            {
                foreach (var kvp in envConfig.Services)
                {
                    services.Services[kvp.Key] = kvp.Value;
                }
            }
        }
        else if (envConfig?.Services != null)
        {
            services.Services = new Dictionary<string, ServicePortsConfig>(envConfig.Services);
        }
        else
        {
            throw new Exception("services section not found");
        }

        // apt_cache service is optional (not required if apt-cache container is not configured)

        // Process users
        var users = new UsersConfig();
        if (config.Users != null && config.Users.Count > 0)
        {
            users = new UsersConfig { Users = config.Users };
        }
        else
        {
            throw new Exception("users section not found");
        }

        // Process DNS (add environment-specific DNS server if provided)
        var dns = new DNSConfig();
        if (config.DNS != null)
        {
            dns = new DNSConfig
            {
                Servers = config.DNS.Servers?.ToList() ?? new List<string>()
            };
        }
        else
        {
            throw new Exception("dns section not found");
        }
        
        if (envConfig?.DNSServer != null)
        {
            dns.Servers.Add(envConfig.DNSServer);
        }

        // Process template config
        var templateConfig = new TemplatePatternsConfig();
        if (config.TemplateConfig != null)
        {
            templateConfig = config.TemplateConfig;
        }

        // Process SSH
        var ssh = new SSHConfig
        {
            ConnectTimeout = config.SSH?.ConnectTimeout ?? 10,
            BatchMode = config.SSH?.BatchMode ?? false,
            DefaultExecTimeout = 300,
            ReadBufferSize = 4096,
            PollInterval = 0.05,
            DefaultUsername = "root",
            LookForKeys = true,
            AllowAgent = true,
            Verbose = verbose
        };

        // Process waits
        var waits = new WaitsConfig();
        if (config.Waits != null)
        {
            waits = config.Waits;
        }
        else
        {
            throw new Exception("waits section not found");
        }

        // Process timeouts
        var timeouts = new TimeoutsConfig();
        if (config.Timeouts != null)
        {
            timeouts = config.Timeouts;
        }
        else
        {
            throw new Exception("timeouts section not found");
        }

        // Process GlusterFS
        GlusterFSConfig? glusterfs = null;
        if (config.GlusterFS != null)
        {
            var clusterNodes = new List<GlusterFSNodeConfig>();
            if (config.GlusterFS.ClusterNodes != null)
            {
                foreach (var node in config.GlusterFS.ClusterNodes)
                {
                    clusterNodes.Add(new GlusterFSNodeConfig
                    {
                        ID = node.ID + idBase
                    });
                }
            }

            glusterfs = new GlusterFSConfig
            {
                VolumeName = config.GlusterFS.VolumeName ?? "swarm-storage",
                BrickPath = config.GlusterFS.BrickPath ?? "/gluster/brick",
                MountPoint = config.GlusterFS.MountPoint ?? "/mnt/gluster",
                ReplicaCount = config.GlusterFS.ReplicaCount > 0 ? config.GlusterFS.ReplicaCount : 2,
                ClusterNodes = clusterNodes
            };
        }

        // Process Backup
        BackupConfig? backup = null;
        if (config.Backup != null)
        {
            var items = new List<BackupItemConfig>();
            if (config.Backup.Items != null)
            {
                foreach (var item in config.Backup.Items)
                {
                    items.Add(new BackupItemConfig
                    {
                        Name = item.Name ?? "",
                        SourceContainerID = item.SourceContainerID + idBase,
                        SourcePath = item.SourcePath ?? "",
                        ArchiveBase = item.ArchiveBase,
                        ArchivePath = item.ArchivePath
                    });
                }
            }

            backup = new BackupConfig
            {
                ContainerID = config.Backup.ContainerID + idBase,
                BackupDir = config.Backup.BackupDir ?? "/backup",
                NamePrefix = config.Backup.NamePrefix ?? "backup",
                Items = items
            };
        }

        var labConfig = new LabConfig
        {
            Network = network,
            LXC = lxc,
            Containers = containers,
            Templates = templates,
            Services = services,
            Users = users,
            DNS = dns,
            TemplateConfig = templateConfig,
            SSH = ssh,
            Waits = waits,
            Timeouts = timeouts,
            IDBase = idBase,
            GlusterFS = glusterfs,
            Kubernetes = kubernetes,
            Backup = backup,
            APTCacheCT = config.APTCacheCT ?? "apt-cache",
            Domain = envConfig?.Domain,
            ArgoAppsBranch = envConfig?.Branch,
            CertificatePath = envConfig?.CertificatePath,
            CertificateSourcePath = envConfig?.CertificateSourcePath
        };

        labConfig.ComputeDerivedFields();

        return labConfig;
    }

    private static int ToInt(object? obj, int defaultValue = 0)
    {
        if (obj == null) return defaultValue;
        if (obj is int i) return i;
        if (obj is long l) return (int)l;
        if (obj is string s && int.TryParse(s, out var parsed)) return parsed;
        return defaultValue;
    }


}
