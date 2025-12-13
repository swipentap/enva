package libs

import (
	"fmt"
	"os"

	"gopkg.in/yaml.v3"
)

// ContainerResources represents container resource allocation
type ContainerResources struct {
	Memory     int `yaml:"memory"`
	Swap       int `yaml:"swap"`
	Cores      int `yaml:"cores"`
	RootfsSize int `yaml:"rootfs_size"`
}

// ContainerConfig represents container configuration
type ContainerConfig struct {
	Name       string                 `yaml:"name"`
	ID         int                    `yaml:"id"`
	IP         int                    `yaml:"ip"` // Last octet only
	Hostname   string                 `yaml:"hostname"`
	Template   *string                `yaml:"template,omitempty"`
	Resources  *ContainerResources    `yaml:"resources,omitempty"`
	Params     map[string]interface{} `yaml:"params,omitempty"`
	Actions    []string               `yaml:"actions,omitempty"`
	IPAddress  *string                // Full IP, computed later
	Privileged *bool                  `yaml:"privileged,omitempty"`
	Nested     *bool                  `yaml:"nested,omitempty"`
	Autostart  *bool                  `yaml:"autostart,omitempty"`
}

// TemplateConfig represents template configuration
type TemplateConfig struct {
	Name       string              `yaml:"name"`
	ID         int                 `yaml:"id"`
	IP         int                 `yaml:"ip"` // Last octet only
	Hostname   string              `yaml:"hostname"`
	Template   *string             `yaml:"template,omitempty"` // "base" or name of another template
	Resources  *ContainerResources `yaml:"resources,omitempty"`
	IPAddress  *string             // Full IP, computed later
	Actions    []string            `yaml:"actions,omitempty"`
	Privileged *bool               `yaml:"privileged,omitempty"`
	Nested     *bool               `yaml:"nested,omitempty"`
}

// KubernetesConfig represents Kubernetes (k3s) configuration
type KubernetesConfig struct {
	Control []int `yaml:"control"`
	Workers []int `yaml:"workers"`
}

// LXCConfig represents LXC configuration
type LXCConfig struct {
	Host         string `yaml:"host"`
	Storage      string `yaml:"storage"`
	Bridge       string `yaml:"bridge"`
	TemplateDir  string `yaml:"template_dir"`
	GatewayOctet int    `yaml:"gateway_octet"`
}

// ServiceConfig represents service configuration
type ServiceConfig struct {
	Port      *int    `yaml:"port,omitempty"`
	Image     *string `yaml:"image,omitempty"`
	HTTPPort  *int    `yaml:"http_port,omitempty"`
	HTTPSPort *int    `yaml:"https_port,omitempty"`
	StatsPort *int    `yaml:"stats_port,omitempty"`
	Password  *string `yaml:"password,omitempty"`
	Username  *string `yaml:"username,omitempty"`
	Database  *string `yaml:"database,omitempty"`
	Version   *string `yaml:"version,omitempty"`
	Nodes     *int    `yaml:"nodes,omitempty"`
	Storage   *string `yaml:"storage,omitempty"`
	SQLPort   *int    `yaml:"sql_port,omitempty"`
	HTTPPort2 *int    `yaml:"http_port,omitempty"` // For cockroachdb
	GRPCPort  *int    `yaml:"grpc_port,omitempty"`
}

// ServicesConfig represents all services configuration
type ServicesConfig struct {
	APTcache    ServiceConfig  `yaml:"apt_cache"`
	PostgreSQL  *ServiceConfig `yaml:"postgresql,omitempty"`
	HAProxy     *ServiceConfig `yaml:"haproxy,omitempty"`
	Rancher     *ServiceConfig `yaml:"rancher,omitempty"`
	Longhorn    *ServiceConfig `yaml:"longhorn,omitempty"`
	CockroachDB *ServiceConfig `yaml:"cockroachdb,omitempty"`
}

// UserConfig represents individual user configuration
type UserConfig struct {
	Name      string  `yaml:"name"`
	Password  *string `yaml:"password,omitempty"`
	SudoGroup string  `yaml:"sudo_group"`
}

// UsersConfig represents users configuration
type UsersConfig struct {
	Users []UserConfig `yaml:"users"`
}

// DefaultUser returns the first user's name (for backward compatibility)
func (u *UsersConfig) DefaultUser() string {
	if len(u.Users) > 0 {
		return u.Users[0].Name
	}
	return "root"
}

// SudoGroup returns the first user's sudo group (for backward compatibility)
func (u *UsersConfig) SudoGroup() string {
	if len(u.Users) > 0 {
		return u.Users[0].SudoGroup
	}
	return "sudo"
}

// DNSConfig represents DNS configuration
type DNSConfig struct {
	Servers []string `yaml:"servers"`
}

// DockerConfig represents Docker configuration
type DockerConfig struct {
	Version       string `yaml:"version"`
	Repository    string `yaml:"repository"`
	Release       string `yaml:"release"`
	UbuntuRelease string `yaml:"ubuntu_release"`
}

// TemplatePatternsConfig represents template patterns configuration
type TemplatePatternsConfig struct {
	Base     []string          `yaml:"base"`
	Patterns map[string]string `yaml:"patterns"`
	Preserve []string          `yaml:"preserve"`
}

// SSHConfig represents SSH configuration
type SSHConfig struct {
	ConnectTimeout     int     `yaml:"connect_timeout"`
	BatchMode          bool    `yaml:"batch_mode"`
	DefaultExecTimeout int     `yaml:"default_exec_timeout"`
	ReadBufferSize     int     `yaml:"read_buffer_size"`
	PollInterval       float64 `yaml:"poll_interval"`
	DefaultUsername    string  `yaml:"default_username"`
	LookForKeys        bool    `yaml:"look_for_keys"`
	AllowAgent         bool    `yaml:"allow_agent"`
	Verbose            bool    `yaml:"verbose"`
}

// WaitsConfig represents wait/retry configuration
type WaitsConfig struct {
	ContainerStartup          int `yaml:"container_startup"`
	ContainerReadyMaxAttempts int `yaml:"container_ready_max_attempts"`
	ContainerReadySleep       int `yaml:"container_ready_sleep"`
	NetworkConfig             int `yaml:"network_config"`
	ServiceStart              int `yaml:"service_start"`
	GlusterFSSetup            int `yaml:"glusterfs_setup"`
}

// GlusterFSNodeConfig represents a GlusterFS cluster node
type GlusterFSNodeConfig struct {
	ID int `yaml:"id"`
}

// GlusterFSConfig represents GlusterFS configuration
type GlusterFSConfig struct {
	VolumeName   string                `yaml:"volume_name"`
	BrickPath    string                `yaml:"brick_path"`
	MountPoint   string                `yaml:"mount_point"`
	ReplicaCount int                   `yaml:"replica_count"`
	ClusterNodes []GlusterFSNodeConfig `yaml:"cluster_nodes,omitempty"`
}

// TimeoutsConfig represents timeout configuration
type TimeoutsConfig struct {
	APTCache       int `yaml:"apt_cache"`
	UbuntuTemplate int `yaml:"ubuntu_template"`
}

// BackupItemConfig represents single backup item configuration
type BackupItemConfig struct {
	Name              string  `yaml:"name"`
	SourceContainerID int     `yaml:"source_container_id"`
	SourcePath        string  `yaml:"source_path"`
	ArchiveBase       *string `yaml:"archive_base,omitempty"`
	ArchivePath       *string `yaml:"archive_path,omitempty"`
}

// BackupConfig represents backup configuration
type BackupConfig struct {
	ContainerID int                `yaml:"container_id"`
	BackupDir   string             `yaml:"backup_dir"`
	NamePrefix  string             `yaml:"name_prefix"`
	Items       []BackupItemConfig `yaml:"items"`
}

// LabConfig represents main lab configuration class
type LabConfig struct {
	Network           string
	LXC               LXCConfig
	Containers        []ContainerConfig
	Templates         []TemplateConfig
	Services          ServicesConfig
	Users             UsersConfig
	DNS               DNSConfig
	Docker            DockerConfig
	TemplateConfig    TemplatePatternsConfig
	SSH               SSHConfig
	Waits             WaitsConfig
	Timeouts          TimeoutsConfig
	IDBase            int
	PostgresHost      *string
	GlusterFS         *GlusterFSConfig
	Kubernetes        *KubernetesConfig
	KubernetesActions []string
	Backup            *BackupConfig
	APTCacheCT        string
	// Computed fields
	NetworkBase       *string
	Gateway           *string
	KubernetesControl []ContainerConfig
	KubernetesWorkers []ContainerConfig
}

// LoadConfig loads configuration from YAML file
func LoadConfig(configFile string) (map[string]interface{}, error) {
	data, err := os.ReadFile(configFile)
	if err != nil {
		return nil, fmt.Errorf("failed to read config file: %w", err)
	}

	var config map[string]interface{}
	if err := yaml.Unmarshal(data, &config); err != nil {
		return nil, fmt.Errorf("failed to parse YAML: %w", err)
	}

	return config, nil
}

// FromDict creates LabConfig from dictionary (loaded from YAML)
func FromDict(data map[string]interface{}, verbose bool, environment *string) (*LabConfig, error) {
	// Get environment-specific values if environments section exists
	var envData map[string]interface{}
	if envs, ok := data["environments"].(map[string]interface{}); ok && environment != nil {
		if env, ok := envs[*environment]; ok {
			if envMap, ok := env.(map[string]interface{}); ok {
				envData = envMap
			} else {
				return nil, fmt.Errorf("environment '%s' data is not a map", *environment)
			}
		} else {
			keys := make([]string, 0, len(envs))
			for k := range envs {
				keys = append(keys, k)
			}
			return nil, fmt.Errorf("environment '%s' not found in configuration. Available: %v", *environment, keys)
		}
	}

	// Get ID base from environment or fallback to top-level or default
	idBase := 3000
	if envData != nil {
		if idBaseVal, ok := envData["id-base"].(int); ok {
			idBase = idBaseVal
		}
	} else if idBaseVal, ok := data["id-base"].(int); ok {
		idBase = idBaseVal
	}

	// Helper to create ContainerResources from map
	makeResources := func(resMap map[string]interface{}) *ContainerResources {
		if resMap == nil {
			return nil
		}
		mem, _ := resMap["memory"].(int)
		swap, _ := resMap["swap"].(int)
		cores, _ := resMap["cores"].(int)
		rootfsSize, _ := resMap["rootfs_size"].(int)
		return &ContainerResources{
			Memory:     mem,
			Swap:       swap,
			Cores:      cores,
			RootfsSize: rootfsSize,
		}
	}

	// Parse containers
	containers := []ContainerConfig{}
	if ctList, ok := data["ct"].([]interface{}); ok {
		for _, ctInterface := range ctList {
			ct, ok := ctInterface.(map[string]interface{})
			if !ok {
				continue
			}
			name, _ := ct["name"].(string)
			ctID, _ := ct["id"].(int)
			ip, _ := ct["ip"].(int)
			hostname, _ := ct["hostname"].(string)

			var template *string
			if t, ok := ct["template"].(string); ok {
				template = &t
			}

			var resources *ContainerResources
			if res, ok := ct["resources"].(map[string]interface{}); ok {
				resources = makeResources(res)
			}

			params := make(map[string]interface{})
			if p, ok := ct["params"].(map[string]interface{}); ok {
				params = p
			}

			actions := []string{}
			if a, ok := ct["actions"].([]interface{}); ok {
				for _, action := range a {
					if actionStr, ok := action.(string); ok {
						actions = append(actions, actionStr)
					}
				}
			}

			var privileged *bool
			if p, ok := ct["privileged"].(bool); ok {
				privileged = &p
			}

			var nested *bool
			if n, ok := ct["nested"].(bool); ok {
				nested = &n
			}

			autostart := true
			if a, ok := ct["autostart"].(bool); ok {
				autostart = a
			}

			containers = append(containers, ContainerConfig{
				Name:       name,
				ID:         ctID + idBase,
				IP:         ip,
				Hostname:   hostname,
				Template:   template,
				Resources:  resources,
				Params:     params,
				Actions:    actions,
				Privileged: privileged,
				Nested:     nested,
				Autostart:  &autostart,
			})
		}
	}

	// Parse templates
	templates := []TemplateConfig{}
	if tmplList, ok := data["templates"].([]interface{}); ok {
		for _, tmplInterface := range tmplList {
			tmpl, ok := tmplInterface.(map[string]interface{})
			if !ok {
				continue
			}
			name, _ := tmpl["name"].(string)
			tmplID, _ := tmpl["id"].(int)
			ip, _ := tmpl["ip"].(int)
			hostname, _ := tmpl["hostname"].(string)

			var template *string
			if t, ok := tmpl["template"].(string); ok {
				template = &t
			}

			var resources *ContainerResources
			if res, ok := tmpl["resources"].(map[string]interface{}); ok {
				resources = makeResources(res)
			}

			actions := []string{}
			if a, ok := tmpl["actions"].([]interface{}); ok {
				for _, action := range a {
					if actionStr, ok := action.(string); ok {
						actions = append(actions, actionStr)
					}
				}
			}

			var privileged *bool
			if p, ok := tmpl["privileged"].(bool); ok {
				privileged = &p
			}

			var nested *bool
			if n, ok := tmpl["nested"].(bool); ok {
				nested = &n
			}

			templates = append(templates, TemplateConfig{
				Name:       name,
				ID:         tmplID + idBase,
				IP:         ip,
				Hostname:   hostname,
				Template:   template,
				Resources:  resources,
				Actions:    actions,
				Privileged: privileged,
				Nested:     nested,
			})
		}
	}

	// Parse kubernetes (optional)
	var kubernetes *KubernetesConfig
	var kubernetesActions []string
	if k8sData, ok := data["kubernetes"].(map[string]interface{}); ok {
		control := []int{}
		if cList, ok := k8sData["control"].([]interface{}); ok {
			for _, c := range cList {
				var cID int
				if cMap, ok := c.(map[string]interface{}); ok {
					if id, ok := cMap["id"].(int); ok {
						cID = id + idBase
					}
				} else if id, ok := c.(int); ok {
					cID = id + idBase
				}
				control = append(control, cID)
			}
		}

		workers := []int{}
		if wList, ok := k8sData["workers"].([]interface{}); ok {
			for _, w := range wList {
				var wID int
				if wMap, ok := w.(map[string]interface{}); ok {
					if id, ok := wMap["id"].(int); ok {
						wID = id + idBase
					}
				} else if id, ok := w.(int); ok {
					wID = id + idBase
				}
				workers = append(workers, wID)
			}
		}

		kubernetes = &KubernetesConfig{
			Control: control,
			Workers: workers,
		}

		if actions, ok := k8sData["actions"].([]interface{}); ok {
			for _, action := range actions {
				if actionStr, ok := action.(string); ok {
					kubernetesActions = append(kubernetesActions, actionStr)
				}
			}
		}
	}

	// Parse lxc - use environment-specific if available, otherwise fallback to top-level
	var lxcData map[string]interface{}
	if envData != nil {
		if lxc, ok := envData["lxc"].(map[string]interface{}); ok {
			lxcData = lxc
		}
	}
	if lxcData == nil {
		if lxc, ok := data["lxc"].(map[string]interface{}); ok {
			lxcData = lxc
		}
	}
	if lxcData == nil {
		return nil, fmt.Errorf("LXC configuration not found in environment or top-level config")
	}

	host, _ := lxcData["host"].(string)
	storage, _ := lxcData["storage"].(string)
	bridge, _ := lxcData["bridge"].(string)
	templateDir, _ := lxcData["template_dir"].(string)
	gatewayOctet, _ := lxcData["gateway_octet"].(int)

	lxc := LXCConfig{
		Host:         host,
		Storage:      storage,
		Bridge:       bridge,
		TemplateDir:  templateDir,
		GatewayOctet: gatewayOctet,
	}

	// Parse services
	servicesData, ok := data["services"].(map[string]interface{})
	if !ok {
		return nil, fmt.Errorf("services section not found")
	}

	aptCacheData, _ := servicesData["apt_cache"].(map[string]interface{})
	aptCachePort, _ := aptCacheData["port"].(int)

	services := ServicesConfig{
		APTcache: ServiceConfig{Port: &aptCachePort},
	}

	if pgData, ok := servicesData["postgresql"].(map[string]interface{}); ok {
		port, _ := pgData["port"].(int)
		username := "postgres"
		if u, ok := pgData["username"].(string); ok {
			username = u
		}
		password := "postgres"
		if p, ok := pgData["password"].(string); ok {
			password = p
		}
		database := "postgres"
		if d, ok := pgData["database"].(string); ok {
			database = d
		}
		services.PostgreSQL = &ServiceConfig{
			Port:     &port,
			Username: &username,
			Password: &password,
			Database: &database,
		}
	}

	if haproxyData, ok := servicesData["haproxy"].(map[string]interface{}); ok {
		var httpPort, httpsPort, statsPort *int
		if hp, ok := haproxyData["http_port"].(int); ok {
			httpPort = &hp
		}
		if hsp, ok := haproxyData["https_port"].(int); ok {
			httpsPort = &hsp
		}
		if sp, ok := haproxyData["stats_port"].(int); ok {
			statsPort = &sp
		}
		services.HAProxy = &ServiceConfig{
			HTTPPort:  httpPort,
			HTTPSPort: httpsPort,
			StatsPort: statsPort,
		}
	}

	if rancherData, ok := servicesData["rancher"].(map[string]interface{}); ok {
		var port *int
		var image *string
		if p, ok := rancherData["port"].(int); ok {
			port = &p
		}
		if img, ok := rancherData["image"].(string); ok {
			image = &img
		}
		services.Rancher = &ServiceConfig{
			Port:  port,
			Image: image,
		}
	}

	if longhornData, ok := servicesData["longhorn"].(map[string]interface{}); ok {
		var port *int
		if p, ok := longhornData["port"].(int); ok {
			port = &p
		}
		services.Longhorn = &ServiceConfig{
			Port: port,
		}
	}

	if cockroachData, ok := servicesData["cockroachdb"].(map[string]interface{}); ok {
		var sqlPort, httpPort, grpcPort *int
		var version, storage, password *string
		var nodes *int

		if sp, ok := cockroachData["sql_port"].(int); ok {
			sqlPort = &sp
		}
		if hp, ok := cockroachData["http_port"].(int); ok {
			httpPort = &hp
		}
		if gp, ok := cockroachData["grpc_port"].(int); ok {
			grpcPort = &gp
		}
		if v, ok := cockroachData["version"].(string); ok {
			version = &v
		}
		if s, ok := cockroachData["storage"].(string); ok {
			storage = &s
		}
		if p, ok := cockroachData["password"].(string); ok {
			password = &p
		}
		if n, ok := cockroachData["nodes"].(int); ok {
			nodes = &n
		}

		services.CockroachDB = &ServiceConfig{
			SQLPort:   sqlPort,
			HTTPPort2: httpPort,
			GRPCPort:  grpcPort,
			Version:   version,
			Storage:   storage,
			Password:  password,
			Nodes:     nodes,
		}
	}

	// Parse users
	usersData, ok := data["users"]
	if !ok {
		return nil, fmt.Errorf("users section not found")
	}

	userList := []UserConfig{}
	if usersArray, ok := usersData.([]interface{}); ok {
		// New format: list of users
		for _, userInterface := range usersArray {
			user, ok := userInterface.(map[string]interface{})
			if !ok {
				continue
			}
			name, _ := user["name"].(string)
			sudoGroup := "sudo"
			if sg, ok := user["sudo_group"].(string); ok {
				sudoGroup = sg
			}
			var password *string
			if p, ok := user["password"].(string); ok {
				password = &p
			}
			userList = append(userList, UserConfig{
				Name:      name,
				Password:  password,
				SudoGroup: sudoGroup,
			})
		}
	} else if userMap, ok := usersData.(map[string]interface{}); ok {
		// Backward compatibility: convert old format to new format
		defaultUser, _ := userMap["default_user"].(string)
		sudoGroup := "sudo"
		if sg, ok := userMap["sudo_group"].(string); ok {
			sudoGroup = sg
		}
		var password *string
		if p, ok := userMap["password"].(string); ok {
			password = &p
		}
		userList = append(userList, UserConfig{
			Name:      defaultUser,
			Password:  password,
			SudoGroup: sudoGroup,
		})
	}
	users := UsersConfig{Users: userList}

	// Parse DNS - add environment-specific DNS server if provided
	dnsData, ok := data["dns"].(map[string]interface{})
	if !ok {
		return nil, fmt.Errorf("dns section not found")
	}
	dnsServers := []string{}
	if servers, ok := dnsData["servers"].([]interface{}); ok {
		for _, server := range servers {
			if serverStr, ok := server.(string); ok {
				dnsServers = append(dnsServers, serverStr)
			}
		}
	}
	// If environment-specific DNS server is provided, add it to the list
	if envData != nil {
		if dnsServer, ok := envData["dns_server"].(string); ok {
			dnsServers = append(dnsServers, dnsServer)
		}
	}
	dns := DNSConfig{Servers: dnsServers}

	// Parse Docker
	dockerData, ok := data["docker"].(map[string]interface{})
	if !ok {
		return nil, fmt.Errorf("docker section not found")
	}
	version, _ := dockerData["version"].(string)
	repository, _ := dockerData["repository"].(string)
	release, _ := dockerData["release"].(string)
	ubuntuRelease, _ := dockerData["ubuntu_release"].(string)
	docker := DockerConfig{
		Version:       version,
		Repository:    repository,
		Release:       release,
		UbuntuRelease: ubuntuRelease,
	}

	// Parse template_config
	templateConfigData := make(map[string]interface{})
	if tc, ok := data["template_config"].(map[string]interface{}); ok {
		templateConfigData = tc
	}

	base := []string{}
	if b, ok := templateConfigData["base"].([]interface{}); ok {
		for _, item := range b {
			if itemStr, ok := item.(string); ok {
				base = append(base, itemStr)
			}
		}
	}

	patterns := make(map[string]string)
	if p, ok := templateConfigData["patterns"].(map[string]interface{}); ok {
		for k, v := range p {
			if vStr, ok := v.(string); ok {
				patterns[k] = vStr
			}
		}
	}

	preserve := []string{}
	if pr, ok := templateConfigData["preserve"].([]interface{}); ok {
		for _, item := range pr {
			if itemStr, ok := item.(string); ok {
				preserve = append(preserve, itemStr)
			}
		}
	}

	templateConfig := TemplatePatternsConfig{
		Base:     base,
		Patterns: patterns,
		Preserve: preserve,
	}

	// Parse SSH
	sshData, ok := data["ssh"].(map[string]interface{})
	if !ok {
		return nil, fmt.Errorf("ssh section not found")
	}
	connectTimeout, _ := sshData["connect_timeout"].(int)
	batchMode, _ := sshData["batch_mode"].(bool)
	ssh := SSHConfig{
		ConnectTimeout:     connectTimeout,
		BatchMode:          batchMode,
		DefaultExecTimeout: 300,
		ReadBufferSize:     4096,
		PollInterval:       0.05,
		DefaultUsername:    "root",
		LookForKeys:        true,
		AllowAgent:         true,
		Verbose:            verbose,
	}

	// Parse waits
	waitsData, ok := data["waits"].(map[string]interface{})
	if !ok {
		return nil, fmt.Errorf("waits section not found")
	}
	containerStartup, _ := waitsData["container_startup"].(int)
	containerReadyMaxAttempts, _ := waitsData["container_ready_max_attempts"].(int)
	containerReadySleep, _ := waitsData["container_ready_sleep"].(int)
	networkConfig, _ := waitsData["network_config"].(int)
	serviceStart, _ := waitsData["service_start"].(int)
	glusterFSSetup, _ := waitsData["glusterfs_setup"].(int)
	waits := WaitsConfig{
		ContainerStartup:          containerStartup,
		ContainerReadyMaxAttempts: containerReadyMaxAttempts,
		ContainerReadySleep:       containerReadySleep,
		NetworkConfig:             networkConfig,
		ServiceStart:              serviceStart,
		GlusterFSSetup:            glusterFSSetup,
	}

	// Parse timeouts
	timeoutsData, ok := data["timeouts"].(map[string]interface{})
	if !ok {
		return nil, fmt.Errorf("timeouts section not found")
	}
	aptCacheTimeout, _ := timeoutsData["apt_cache"].(int)
	ubuntuTemplateTimeout, _ := timeoutsData["ubuntu_template"].(int)
	timeouts := TimeoutsConfig{
		APTCache:       aptCacheTimeout,
		UbuntuTemplate: ubuntuTemplateTimeout,
	}

	// Parse GlusterFS (optional)
	var glusterfs *GlusterFSConfig
	if glusterfsData, ok := data["glusterfs"].(map[string]interface{}); ok {
		volumeName := "swarm-storage"
		if vn, ok := glusterfsData["volume_name"].(string); ok {
			volumeName = vn
		}
		brickPath := "/gluster/brick"
		if bp, ok := glusterfsData["brick_path"].(string); ok {
			brickPath = bp
		}
		mountPoint := "/mnt/gluster"
		if mp, ok := glusterfsData["mount_point"].(string); ok {
			mountPoint = mp
		}
		replicaCount := 2
		if rc, ok := glusterfsData["replica_count"].(int); ok {
			replicaCount = rc
		}

		clusterNodes := []GlusterFSNodeConfig{}
		if cn, ok := glusterfsData["cluster_nodes"].([]interface{}); ok {
			for _, nodeInterface := range cn {
				var nodeID int
				if nodeMap, ok := nodeInterface.(map[string]interface{}); ok {
					if id, ok := nodeMap["id"].(int); ok {
						nodeID = id + idBase
					}
				} else if id, ok := nodeInterface.(int); ok {
					nodeID = id + idBase
				}
				clusterNodes = append(clusterNodes, GlusterFSNodeConfig{ID: nodeID})
			}
		}

		glusterfs = &GlusterFSConfig{
			VolumeName:   volumeName,
			BrickPath:    brickPath,
			MountPoint:   mountPoint,
			ReplicaCount: replicaCount,
			ClusterNodes: clusterNodes,
		}
	}

	// Parse Backup (optional)
	var backup *BackupConfig
	if backupData, ok := data["backup"].(map[string]interface{}); ok {
		containerID, _ := backupData["container_id"].(int)
		backupDir := "/backup"
		if bd, ok := backupData["backup_dir"].(string); ok {
			backupDir = bd
		}
		namePrefix := "backup"
		if np, ok := backupData["name_prefix"].(string); ok {
			namePrefix = np
		}

		items := []BackupItemConfig{}
		if itemsList, ok := backupData["items"].([]interface{}); ok {
			for _, itemInterface := range itemsList {
				item, ok := itemInterface.(map[string]interface{})
				if !ok {
					continue
				}
				name, _ := item["name"].(string)
				sourceContainerID, _ := item["source_container_id"].(int)
				sourcePath, _ := item["source_path"].(string)

				var archiveBase, archivePath *string
				if ab, ok := item["archive_base"].(string); ok {
					archiveBase = &ab
				}
				if ap, ok := item["archive_path"].(string); ok {
					archivePath = &ap
				}

				items = append(items, BackupItemConfig{
					Name:              name,
					SourceContainerID: sourceContainerID + idBase,
					SourcePath:        sourcePath,
					ArchiveBase:       archiveBase,
					ArchivePath:       archivePath,
				})
			}
		}

		backup = &BackupConfig{
			ContainerID: containerID + idBase,
			BackupDir:   backupDir,
			NamePrefix:  namePrefix,
			Items:       items,
		}
	}

	// Get network from environment or fallback to top-level
	network := "10.11.3.0/24"
	if envData != nil {
		if n, ok := envData["network"].(string); ok {
			network = n
		}
	} else if n, ok := data["network"].(string); ok {
		network = n
	}

	// Get postgres_host from environment or fallback to None
	var postgresHost *string
	if envData != nil {
		if ph, ok := envData["postgres_host"].(string); ok {
			postgresHost = &ph
		}
	}

	aptCacheCT := "apt-cache"
	if act, ok := data["apt-cache-ct"].(string); ok {
		aptCacheCT = act
	}

	config := &LabConfig{
		Network:           network,
		LXC:               lxc,
		Containers:        containers,
		Templates:         templates,
		Services:          services,
		Users:             users,
		DNS:               dns,
		Docker:            docker,
		TemplateConfig:    templateConfig,
		SSH:               ssh,
		Waits:             waits,
		Timeouts:          timeouts,
		IDBase:            idBase,
		PostgresHost:      postgresHost,
		GlusterFS:         glusterfs,
		Kubernetes:        kubernetes,
		KubernetesActions: kubernetesActions,
		Backup:            backup,
		APTCacheCT:        aptCacheCT,
	}

	config.ComputeDerivedFields()

	return config, nil
}

// ComputeDerivedFields computes derived fields like network_base, gateway, and IP addresses
func (c *LabConfig) ComputeDerivedFields() {
	// Compute network_base
	networkStr := c.Network
	// Remove /24 or similar suffix
	slashIdx := -1
	for i := len(networkStr) - 1; i >= 0; i-- {
		if networkStr[i] == '/' {
			slashIdx = i
			break
		}
	}
	if slashIdx >= 0 {
		networkStr = networkStr[:slashIdx]
	}
	// Split by dots
	parts := []string{}
	current := ""
	for i := 0; i < len(networkStr); i++ {
		if networkStr[i] == '.' {
			if current != "" {
				parts = append(parts, current)
				current = ""
			}
		} else {
			current += string(networkStr[i])
		}
	}
	if current != "" {
		parts = append(parts, current)
	}
	if len(parts) >= 3 {
		networkBase := parts[0] + "." + parts[1] + "." + parts[2]
		c.NetworkBase = &networkBase
		gateway := fmt.Sprintf("%s.%d", networkBase, c.LXC.GatewayOctet)
		c.Gateway = &gateway
	}

	// Compute IP addresses for containers
	for i := range c.Containers {
		if c.NetworkBase != nil {
			ipAddr := fmt.Sprintf("%s.%d", *c.NetworkBase, c.Containers[i].IP)
			c.Containers[i].IPAddress = &ipAddr
		}
	}

	// Compute IP addresses for templates
	for i := range c.Templates {
		if c.NetworkBase != nil {
			ipAddr := fmt.Sprintf("%s.%d", *c.NetworkBase, c.Templates[i].IP)
			c.Templates[i].IPAddress = &ipAddr
		}
	}

	// Build kubernetes control and workers lists
	if c.Kubernetes != nil {
		for i := range c.Containers {
			for _, controlID := range c.Kubernetes.Control {
				if c.Containers[i].ID == controlID {
					c.KubernetesControl = append(c.KubernetesControl, c.Containers[i])
					break
				}
			}
			for _, workerID := range c.Kubernetes.Workers {
				if c.Containers[i].ID == workerID {
					c.KubernetesWorkers = append(c.KubernetesWorkers, c.Containers[i])
					break
				}
			}
		}
	}
}

// Convenience methods
func (c *LabConfig) LXCHost() string {
	return c.LXC.Host
}

func (c *LabConfig) LXCStorage() string {
	return c.LXC.Storage
}

func (c *LabConfig) LXCBridge() string {
	return c.LXC.Bridge
}

func (c *LabConfig) LXCTemplateDir() string {
	return c.LXC.TemplateDir
}

func (c *LabConfig) APTCachePort() int {
	if c.Services.APTcache.Port != nil {
		return *c.Services.APTcache.Port
	}
	return 0
}

func (c *LabConfig) GetGateway() string {
	if c.Gateway != nil {
		return *c.Gateway
	}
	return ""
}
