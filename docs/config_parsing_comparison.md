# Complete Line-by-Line Comparison: Go vs C# Config Parsing

## Overview

This document provides a comprehensive line-by-line comparison of the configuration parsing implementations in Go (`src/libs/config.go`) and C# (`csharp-enva/Libs/ConfigParser.cs`, `ConfigLoader.cs`, and `LabConfig.cs`).

## File Structure Differences

**Go Version:**
- Single file: `src/libs/config.go` (1224 lines)
- Contains: type definitions, `LoadConfig`, `FromDict`, `ComputeDerivedFields`, convenience methods

**C# Version:**
- Three files:
  - `ConfigLoader.cs` (20 lines) - `LoadConfig` only
  - `ConfigParser.cs` (1259 lines) - `FromDict` only
  - `LabConfig.cs` (544 lines) - All type definitions, `ComputeDerivedFields`, convenience methods

---

## 1. LoadConfig Function

### Go (lines 260-273)
```go
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
```

### C# (ConfigLoader.cs lines 9-18)
```csharp
public static Dictionary<string, object> LoadConfig(string configFile)
{
	var data = System.IO.File.ReadAllText(configFile);
	var deserializer = new DeserializerBuilder()
		.WithNamingConvention(UnderscoredNamingConvention.Instance)
		.Build();
	var config = deserializer.Deserialize<Dictionary<string, object>>(data);
	return config;
}
```

### Differences:
1. **Return type**: Go returns `(map[string]interface{}, error)`; C# returns `Dictionary<string, object>` (no error return)
2. **File reading**: Go uses `os.ReadFile`; C# uses `File.ReadAllText`
3. **YAML library**: Go uses `gopkg.in/yaml.v3`; C# uses `YamlDotNet`
4. **Naming convention**: C# uses `UnderscoredNamingConvention.Instance`; Go does not apply naming conventions
5. **Error handling**: Go wraps errors with context; C# does not handle exceptions (throws them)

---

## 2. FromDict Function - Environment Handling

### Go (lines 276-293)
```go
func FromDict(data map[string]interface{}, verbose bool, environment *string) (*LabConfig, error) {
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
```

### C# (lines 40-109)
```csharp
public static LabConfig FromDict(Dictionary<string, object> data, bool verbose, string? environment)
{
	Dictionary<string, object>? envData = null;
	if (data.TryGetValue("environments", out var envsObj) && environment != null)
	{
		if (envsObj is Dictionary<object, object> envsObjDict)
		{
			var envs = new Dictionary<string, object>();
			foreach (var kvp in envsObjDict)
			{
				envs[kvp.Key.ToString() ?? ""] = kvp.Value;
			}
			if (envs.TryGetValue(environment, out var envObj))
			{
				if (envObj is Dictionary<object, object> envObjDict)
				{
					envData = new Dictionary<string, object>();
					foreach (var kvp in envObjDict)
					{
						envData[kvp.Key.ToString() ?? ""] = kvp.Value;
					}
				}
				else if (envObj is Dictionary<string, object> envMap)
				{
					envData = envMap;
				}
				else
				{
					throw new Exception($"environment '{environment}' data is not a map");
				}
			}
			else
			{
				var keys = string.Join(", ", envs.Keys);
				throw new Exception($"environment '{environment}' not found in configuration. Available: [{keys}]");
			}
		}
		else if (envsObj is Dictionary<string, object> envs)
		{
			// Similar handling...
		}
	}
```

### Differences:
1. **Parameter type**: Go uses `*string`; C# uses `string?`
2. **Return type**: Go returns `(*LabConfig, error)`; C# returns `LabConfig` and throws exceptions
3. **Dictionary conversion**: C# handles `Dictionary<object, object>` conversion (YamlDotNet may deserialize to this); Go assumes `map[string]interface{}`
4. **Error messages**: Go uses `%v` format; C# uses `[{keys}]` format
5. **Type checking**: C# uses `TryGetValue`; Go uses type assertion with `ok` check

---

## 3. ID Base Parsing

### Go (lines 295-303)
```go
idBase := 3000
if envData != nil {
	if idBaseVal, ok := envData["id-base"].(int); ok {
		idBase = idBaseVal
	}
} else if idBaseVal, ok := data["id-base"].(int); ok {
	idBase = idBaseVal
}
```

### C# (lines 111-138)
```csharp
int idBase = 3000;
if (envData != null)
{
	if (envData.TryGetValue("id-base", out var idBaseVal))
	{
		if (idBaseVal is int idBaseInt)
		{
			idBase = idBaseInt;
		}
		else if (idBaseVal is long idBaseLong)
		{
			idBase = (int)idBaseLong;
		}
	}
}
else if (data.TryGetValue("id-base", out var topIdBaseVal))
{
	if (topIdBaseVal is int topIdBaseInt)
	{
		idBase = topIdBaseInt;
	}
	else if (topIdBaseVal is long topIdBaseLong)
	{
		idBase = (int)topIdBaseLong;
	}
}
```

### Differences:
1. **Long handling**: C# handles `long` type (YamlDotNet may deserialize integers as `Int64`); Go only handles `int`
2. **Type checking**: C# uses `TryGetValue` with type checking; Go uses type assertion

---

## 4. MakeResources Helper

### Go (lines 305-320)
```go
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
```

### C# (lines 140-178)
```csharp
ContainerResources? MakeResources(Dictionary<string, object>? resMap)
{
	if (resMap == null)
	{
		return null;
	}
	resMap.TryGetValue("memory", out var memObj);
	resMap.TryGetValue("swap", out var swapObj);
	resMap.TryGetValue("cores", out var coresObj);
	resMap.TryGetValue("rootfs_size", out var rootfsSizeObj);
	
	int mem = 0;
	if (memObj is int memInt) mem = memInt;
	else if (memObj is long memLong) mem = (int)memLong;
	
	int swap = 0;
	if (swapObj is int swapInt) swap = swapInt;
	else if (swapObj is long swapLong) swap = (int)swapLong;
	
	int cores = 0;
	if (coresObj is int coresInt) cores = coresInt;
	else if (coresObj is long coresLong) cores = (int)coresLong;
	
	int rootfsSize = 0;
	if (rootfsSizeObj is int rootfsSizeInt) rootfsSize = rootfsSizeInt;
	else if (rootfsSizeObj is long rootfsSizeLong) rootfsSize = (int)rootfsSizeLong;
	
	return new ContainerResources
	{
		Memory = mem,
		Swap = swap,
		Cores = cores,
		RootfsSize = rootfsSize
	};
}
```

### Differences:
1. **Function type**: Go uses anonymous function; C# uses named local function
2. **Long handling**: C# handles `long` type; Go only handles `int`
3. **Initialization**: C# explicitly initializes to 0; Go uses zero values from type assertion
4. **Type checking**: C# uses `TryGetValue`; Go uses type assertion with `_` discard

---

## 5. Container Parsing

### Go (lines 322-388)
```go
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
		
		// ... resources, params, actions parsing ...
		
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
```

### C# (lines 180-286)
```csharp
var containers = new List<ContainerConfig>();
if (data.TryGetValue("ct", out var ctListObj) && ctListObj is List<object> ctList)
{
	foreach (var ctInterface in ctList)
	{
		if (!(ctInterface is Dictionary<string, object> ct) && !(ctInterface is Dictionary<object, object>))
		{
			continue;
		}
		Dictionary<string, object> ctDict;
		if (ctInterface is Dictionary<string, object> ctStr)
		{
			ctDict = ctStr;
		}
		else if (ctInterface is Dictionary<object, object> ctObj)
		{
			ctDict = ConvertDict(ctObj);
		}
		else
		{
			continue;
		}
		
		string name = ctDict.TryGetValue("name", out var nameObj) && nameObj is string nameStr ? nameStr : "";
		int ctID = 0;
		if (ctDict.TryGetValue("id", out var idObj))
		{
			if (idObj is int idInt) ctID = idInt;
			else if (idObj is long idLong) ctID = (int)idLong;
		}
		// ... similar for other fields ...
		
		bool autostart = true;
		if (ct.TryGetValue("autostart", out var autostartObj) && autostartObj is bool autostartBool)
		{
			autostart = autostartBool;
		}
		
		containers.Add(new ContainerConfig
		{
			Name = name,
			ID = ctID + idBase,
			IP = ip,
			Hostname = hostname,
			Template = template,
			Resources = resources,
			Params = paramsDict,
			Actions = actions,
			Privileged = privileged,
			Nested = nested,
			Autostart = autostart
		});
	}
}
```

### Differences:
1. **Collection type**: Go uses `[]ContainerConfig{}`; C# uses `new List<ContainerConfig>()`
2. **Dictionary conversion**: C# handles `Dictionary<object, object>` via `ConvertDict` helper; Go only handles `map[string]interface{}`
3. **Long handling**: C# handles `long` for IDs; Go only handles `int`
4. **Autostart field**: Go sets `Autostart: &autostart` (pointer); C# sets `Autostart = autostart` (value)
5. **Type checking**: C# uses `TryGetValue`; Go uses type assertion

---

## 6. Template Parsing

### Go (lines 390-444)
```go
templates := []TemplateConfig{}
if tmplList, ok := data["templates"].([]interface{}); ok {
	for _, tmplInterface := range tmplList {
		tmpl, ok := tmplInterface.(map[string]interface{})
		if !ok {
			continue
		}
		// ... parsing similar to containers ...
	}
}
```

### C# (lines 288-355)
```csharp
var templates = new List<TemplateConfig>();
if (data.TryGetValue("templates", out var tmplListObj) && tmplListObj is List<object> tmplList)
{
	foreach (var tmplInterface in tmplList)
	{
		if (tmplInterface is not Dictionary<string, object> tmpl)
		{
			continue;
		}
		// ... parsing ...
	}
}
```

### Differences:
1. **Dictionary conversion**: C# does NOT handle `Dictionary<object, object>` for templates (unlike containers) - potential inconsistency
2. **Helper function**: C# uses `ToInt` helper; Go uses direct type assertion
3. **Type checking**: C# uses `TryGetValue`; Go uses type assertion

---

## 7. Kubernetes Parsing

### Go (lines 446-492)
```go
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
	// ... workers similar ...
}
```

### C# (lines 357-436)
```csharp
KubernetesConfig? kubernetes = null;
var kubernetesActions = new List<string>();
if (data.TryGetValue("kubernetes", out var k8sObj) && k8sObj is Dictionary<string, object> k8sData)
{
	var control = new List<int>();
	if (k8sData.TryGetValue("control", out var cListObj) && cListObj is List<object> cList)
	{
		foreach (var c in cList)
		{
			int cID = 0;
			if (c is Dictionary<string, object> cMap)
			{
				int baseID = ToInt(cMap.TryGetValue("id", out var idObj) ? idObj : null);
				if (baseID > 0)
				{
					cID = baseID + idBase;
				}
			}
			else
			{
				int baseID = ToInt(c);
				if (baseID > 0)
				{
					cID = baseID + idBase;
				}
			}
			if (cID > 0)
			{
				control.Add(cID);
			}
		}
	}
	// ... workers similar ...
}
```

### Differences:
1. **ID filtering**: C# only adds IDs if `baseID > 0`; Go adds all IDs (including 0)
2. **Helper function**: C# uses `ToInt` helper; Go uses direct type assertion
3. **Type checking**: C# uses `TryGetValue`; Go uses type assertion

---

## 8. LXC Parsing

### Go (lines 494-522)
```go
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
```

### C# (lines 438-496)
```csharp
Dictionary<string, object>? lxcData = null;
if (envData != null)
{
	if (envData.TryGetValue("lxc", out var envLxcObj))
	{
		if (envLxcObj is Dictionary<object, object> envLxcObjDict)
		{
			lxcData = new Dictionary<string, object>();
			foreach (var kvp in envLxcObjDict)
			{
				lxcData[kvp.Key.ToString() ?? ""] = kvp.Value;
			}
		}
		else if (envLxcObj is Dictionary<string, object> envLxc)
		{
			lxcData = envLxc;
		}
	}
}
if (lxcData == null)
{
	if (data.TryGetValue("lxc", out var topLxcObj))
	{
		// Similar conversion logic...
	}
}
if (lxcData == null)
{
	throw new Exception("LXC configuration not found in environment or top-level config");
}

string host = lxcData.TryGetValue("host", out var hostObj) && hostObj is string hostStr ? hostStr : "";
string storage = lxcData.TryGetValue("storage", out var storageObj) && storageObj is string storageStr ? storageStr : "";
string bridge = lxcData.TryGetValue("bridge", out var bridgeObj) && bridgeObj is string bridgeStr ? bridgeStr : "";
string templateDir = lxcData.TryGetValue("template_dir", out var templateDirObj) && templateDirObj is string templateDirStr ? templateDirStr : "";
int gatewayOctet = ToInt(lxcData.TryGetValue("gateway_octet", out var gatewayOctetObj) ? gatewayOctetObj : null, 1);
```

### Differences:
1. **Dictionary conversion**: C# handles `Dictionary<object, object>` conversion; Go only handles `map[string]interface{}`
2. **Default value**: C# `gatewayOctet` defaults to `1`; Go defaults to `0`
3. **Type checking**: C# uses `TryGetValue`; Go uses type assertion

---

## 9. Services Parsing - APT Cache

### Go (lines 555-564)
```go
aptCacheData, _ := servicesData["apt_cache"].(map[string]interface{})
aptCachePort, _ := aptCacheData["port"].(int)
var aptCacheName *string
if name, ok := aptCacheData["name"].(string); ok {
	aptCacheName = &name
}

services := ServicesConfig{
	APTcache: ServiceConfig{Port: &aptCachePort, Name: aptCacheName},
}
```

### C# (lines 534-551)
```csharp
if (!servicesData.TryGetValue("apt_cache", out var aptCacheObj))
{
	throw new Exception("apt_cache service not found");
}
Dictionary<string, object> aptCacheData = ConvertDict(aptCacheObj);

int aptCachePort = ToInt(aptCacheData.TryGetValue("port", out var aptCachePortObj) ? aptCachePortObj : null);
string? aptCacheName = null;
if (aptCacheData.TryGetValue("name", out var aptCacheNameObj) && aptCacheNameObj is string aptCacheNameStr)
{
	aptCacheName = aptCacheNameStr;
}

var services = new ServicesConfig
{
	APTcache = new ServiceConfig { Port = aptCachePort, Name = aptCacheName }
};
```

### Differences:
1. **Validation**: C# throws if `apt_cache` is missing; Go does not check (may panic)
2. **Port type**: Go uses `&aptCachePort` (pointer); C# uses `aptCachePort` (value)
3. **Dictionary conversion**: C# uses `ConvertDict`; Go uses type assertion
4. **Helper function**: C# uses `ToInt`; Go uses type assertion

---

## 10. PostgreSQL Parsing

### Go (lines 566-591)
```go
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
	var pgName *string
	if name, ok := pgData["name"].(string); ok {
		pgName = &name
	}
	services.PostgreSQL = &ServiceConfig{
		Port:     &port,
		Username:  &username,
		Password:  &password,
		Database:  &database,
		Name:      pgName,
	}
}
```

### C# (lines 553-589)
```csharp
if (servicesData.TryGetValue("postgresql", out var pgObj))
{
	Dictionary<string, object> pgData = ConvertDict(pgObj);
	if (pgData.Count > 0)
	{
		int port = ToInt(pgData.TryGetValue("port", out var portObj) ? portObj : null);
		string username = "postgres";
		if (pgData.TryGetValue("username", out var uObj) && uObj is string uStr)
		{
			username = uStr;
		}
		string password = "postgres";
		if (pgData.TryGetValue("password", out var pgPObj) && pgPObj is string pgPStr)
		{
			password = pgPStr;
		}
		string database = "postgres";
		if (pgData.TryGetValue("database", out var dObj) && dObj is string dStr)
		{
			database = dStr;
		}
		string? pgName = null;
		if (pgData.TryGetValue("name", out var nameObj) && nameObj is string nameStr)
		{
			pgName = nameStr;
		}
		services.PostgreSQL = new ServiceConfig
		{
			Port = port,
			Username = username,
			Password = password,
			Database = database,
			Name = pgName
		};
	}
}
```

### Differences:
1. **Validation**: C# checks `pgData.Count > 0`; Go does not
2. **Pointer types**: Go uses pointers (`&port`, `&username`, etc.); C# uses values
3. **Dictionary conversion**: C# uses `ConvertDict`; Go uses type assertion
4. **Helper function**: C# uses `ToInt`; Go uses type assertion

---

## 11. HAProxy Parsing

### Go (lines 593-614)
```go
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
	var haproxyName *string
	if name, ok := haproxyData["name"].(string); ok {
		haproxyName = &name
	}
	services.HAProxy = &ServiceConfig{
		HTTPPort:  httpPort,
		HTTPSPort: httpsPort,
		StatsPort: statsPort,
		Name:      haproxyName,
	}
}
```

### C# (lines 591-616)
```csharp
if (servicesData.TryGetValue("haproxy", out var haproxyObj))
{
	Dictionary<string, object> haproxyData = ConvertDict(haproxyObj);
	if (haproxyData.Count > 0)
	{
		int? httpPort = haproxyData.TryGetValue("http_port", out var hpObj) ? ToInt(hpObj) : (int?)null;
		if (httpPort == 0) httpPort = null;
		int? httpsPort = haproxyData.TryGetValue("https_port", out var hspObj) ? ToInt(hspObj) : (int?)null;
		if (httpsPort == 0) httpsPort = null;
		int? statsPort = haproxyData.TryGetValue("stats_port", out var spObj) ? ToInt(spObj) : null;
		if (statsPort == 0) statsPort = null;
		// ... name parsing ...
		services.HAProxy = new ServiceConfig
		{
			HTTPPort = httpPort,
			HTTPSPort = httpsPort,
			StatsPort = statsPort,
			Name = haproxyName
		};
	}
}
```

### Differences:
1. **Zero handling**: C# converts `0` to `null`; Go does not (0 is a valid port number)
2. **Validation**: C# checks `haproxyData.Count > 0`; Go does not
3. **Nullable types**: C# uses nullable `int?`; Go uses `*int`
4. **Dictionary conversion**: C# uses `ConvertDict`; Go uses type assertion

---

## 12. GitHub Runner Parsing

### Go (lines 646-683)
```go
if githubRunnerData, ok := servicesData["github_runner"].(map[string]interface{}); ok {
	var token, organization, repository, label, namePrefix, namespace, group *string
	var replicas *int
	if t, ok := githubRunnerData["token"].(string); ok {
		token = &t
	}
	// ... similar for all fields ...
	if r, ok := githubRunnerData["replicas"].(int); ok {
		replicas = &r
	}
	// ...
}
```

### C# (lines 660-719)
```csharp
if (servicesData.TryGetValue("github_runner", out var githubRunnerObj))
{
	Dictionary<string, object> githubRunnerData = ConvertDict(githubRunnerObj);
	if (githubRunnerData.Count > 0)
	{
		string? token = null;
		// ... similar parsing ...
		int? replicas = null;
		int? replicasVal = githubRunnerData.TryGetValue("replicas", out var rObj) ? ToInt(rObj) : (int?)null;
		if (replicasVal.HasValue && replicasVal.Value > 0)
		{
			replicas = replicasVal;
		}
		// ...
	}
}
```

### Differences:
1. **Replicas filtering**: C# only sets `replicas` if `> 0`; Go sets it if present (including 0)
2. **Validation**: C# checks `githubRunnerData.Count > 0`; Go does not
3. **Nullable types**: C# uses nullable types; Go uses pointers

---

## 13. CockroachDB Parsing

### Go (lines 685-726)
```go
if cockroachData, ok := servicesData["cockroachdb"].(map[string]interface{}); ok {
	var sqlPort, httpPort, grpcPort *int
	var version, storage, password *string
	var nodes *int
	
	if sp, ok := cockroachData["sql_port"].(int); ok {
		sqlPort = &sp
	}
	// ... similar for all fields ...
	
	services.CockroachDB = &ServiceConfig{
		Name:      cockroachName,
		SQLPort:   sqlPort,
		HTTPPort2: httpPort,
		GRPCPort:  grpcPort,
		Version:   version,
		Storage:   storage,
		Password:  password,
		Nodes:     nodes,
	}
}
```

### C# (lines 721-768)
```csharp
if (servicesData.TryGetValue("cockroachdb", out var cockroachObj))
{
	Dictionary<string, object> cockroachData = ConvertDict(cockroachObj);
	if (cockroachData.Count > 0)
	{
		int? sqlPort = cockroachData.TryGetValue("sql_port", out var spObj) ? ToInt(spObj) : (int?)null;
		if (sqlPort == 0) sqlPort = null;
		int? httpPort = cockroachData.TryGetValue("http_port", out var hpObj) ? ToInt(hpObj) : (int?)null;
		if (httpPort == 0) httpPort = null;
		int? grpcPort = cockroachData.TryGetValue("grpc_port", out var gpObj) ? ToInt(gpObj) : (int?)null;
		if (grpcPort == 0) grpcPort = null;
		// ... similar for other fields ...
		int? nodes = cockroachData.TryGetValue("nodes", out var nObj) ? ToInt(nObj) : (int?)null;
		if (nodes == 0) nodes = null;
		// ...
	}
}
```

### Differences:
1. **Zero handling**: C# converts `0` to `null` for ports and nodes; Go does not
2. **Validation**: C# checks `cockroachData.Count > 0`; Go does not
3. **Nullable types**: C# uses nullable types; Go uses pointers

---

## 14. CertA Parsing

### Go (lines 728-777)
```go
if certaData, ok := servicesData["certa"].(map[string]interface{}); ok {
	var image, namespace, name *string
	var port, replicas *int
	var databaseHost, databaseName, databaseUser, databasePassword *string
	var databasePort *int
	
	// ... parsing all fields ...
	
	services.CertA = &CertAConfig{
		Image:            image,
		Port:             port,
		Namespace:        namespace,
		Replicas:         replicas,
		Name:             name,
		DatabaseHost:     databaseHost,
		DatabasePort:     databasePort,
		DatabaseName:     databaseName,
		DatabaseUser:     databaseUser,
		DatabasePassword: databasePassword,
	}
}
```

### C# (lines 770-832)
```csharp
if (servicesData.TryGetValue("certa", out var certaObj))
{
	Dictionary<string, object> certaData = ConvertDict(certaObj);
	if (certaData.Count > 0)
	{
		// ... parsing ...
		int? port = certaData.TryGetValue("port", out var certaPObj) ? ToInt(certaPObj) : (int?)null;
		if (port == 0) port = null;
		int? replicas = certaData.TryGetValue("replicas", out var rObj) ? ToInt(rObj) : (int?)null;
		if (replicas == 0) replicas = null;
		int? databasePort = certaData.TryGetValue("database_port", out var dbpObj) ? ToInt(dbpObj) : (int?)null;
		if (databasePort == 0) databasePort = null;
		// ...
	}
}
```

### Differences:
1. **Zero handling**: C# converts `0` to `null` for ports and replicas; Go does not
2. **Validation**: C# checks `certaData.Count > 0`; Go does not

---

## 15. Users Parsing

### Go (lines 779-825)
```go
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
```

### C# (lines 834-890)
```csharp
if (!data.TryGetValue("users", out var usersDataObj))
{
	throw new Exception("users section not found");
}

var userList = new List<UserConfig>();
if (usersDataObj is List<object> usersArray)
{
	// New format: list of users
	foreach (var userInterface in usersArray)
	{
		if (userInterface is not Dictionary<string, object> user)
		{
			continue;
		}
		string name = user.TryGetValue("name", out var nameObj) && nameObj is string nameStr ? nameStr : "";
		string sudoGroup = "sudo";
		if (user.TryGetValue("sudo_group", out var sgObj) && sgObj is string sgStr)
		{
			sudoGroup = sgStr;
		}
		string? password = null;
		if (user.TryGetValue("password", out var userPObj) && userPObj is string userPStr)
		{
			password = userPStr;
		}
		userList.Add(new UserConfig
		{
			Name = name,
			Password = password,
			SudoGroup = sudoGroup
		});
	}
}
else if (usersDataObj is Dictionary<string, object> userMap)
{
	// Backward compatibility: convert old format to new format
	string defaultUser = userMap.TryGetValue("default_user", out var duObj) && duObj is string duStr ? duStr : "";
	string sudoGroup = "sudo";
	if (userMap.TryGetValue("sudo_group", out var sgObj) && sgObj is string sgStr)
	{
		sudoGroup = sgStr;
	}
	string? password = null;
	if (userMap.TryGetValue("password", out var userMapPObj) && userMapPObj is string userMapPStr)
	{
		password = userMapPStr;
	}
	userList.Add(new UserConfig
	{
		Name = defaultUser,
		Password = password,
		SudoGroup = sudoGroup
	});
}
var users = new UsersConfig { Users = userList };
```

### Differences:
1. **Type checking**: C# uses `TryGetValue`; Go uses type assertion
2. **Nullable types**: C# uses nullable `string?`; Go uses `*string`

---

## 16. DNS Parsing

### Go (lines 827-846)
```go
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
```

### C# (lines 892-918)
```csharp
if (!data.TryGetValue("dns", out var dnsObj))
{
	throw new Exception("dns section not found");
}
Dictionary<string, object> dnsData = ConvertDict(dnsObj);

var dnsServers = new List<string>();
if (dnsData.TryGetValue("servers", out var serversObj) && serversObj is List<object> servers)
{
	foreach (var server in servers)
	{
		if (server is string serverStr)
		{
			dnsServers.Add(serverStr);
		}
	}
}
// If environment-specific DNS server is provided, add it to the list
if (envData != null)
{
	if (envData.TryGetValue("dns_server", out var dnsServerObj) && dnsServerObj is string dnsServer)
	{
		dnsServers.Add(dnsServer);
	}
}
var dns = new DNSConfig { Servers = dnsServers };
```

### Differences:
1. **Dictionary conversion**: C# uses `ConvertDict`; Go uses type assertion
2. **Type checking**: C# uses `TryGetValue`; Go uses type assertion

---

## 17. SSH Parsing

### Go (lines 903-920)
```go
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
```

### C# (lines 990-1011)
```csharp
if (!data.TryGetValue("ssh", out var sshObj))
{
	throw new Exception("ssh section not found");
}
Dictionary<string, object> sshData = ConvertDict(sshObj);

int connectTimeout = ToInt(sshData.TryGetValue("connect_timeout", out var ctObj) ? ctObj : null, 10);
bool batchMode = sshData.TryGetValue("batch_mode", out var bmObj) && bmObj is bool bmBool ? bmBool : false;

var ssh = new SSHConfig
{
	ConnectTimeout = connectTimeout,
	BatchMode = batchMode,
	DefaultExecTimeout = 300,
	ReadBufferSize = 4096,
	PollInterval = 0.05,
	DefaultUsername = "root",
	LookForKeys = true,
	AllowAgent = true,
	Verbose = verbose
};
```

### Differences:
1. **Default value**: C# defaults `connectTimeout` to `10`; Go defaults to `0`
2. **Dictionary conversion**: C# uses `ConvertDict`; Go uses type assertion
3. **Helper function**: C# uses `ToInt`; Go uses type assertion

---

## 18. GlusterFS Parsing

### Go (lines 954-996)
```go
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
	// ...
}
```

### C# (lines 1053-1111)
```csharp
GlusterFSConfig? glusterfs = null;
if (data.TryGetValue("glusterfs", out var glusterfsObj) && glusterfsObj is Dictionary<string, object> glusterfsData)
{
	string volumeName = "swarm-storage";
	if (glusterfsData.TryGetValue("volume_name", out var vnObj) && vnObj is string vnStr)
	{
		volumeName = vnStr;
	}
	string brickPath = "/gluster/brick";
	if (glusterfsData.TryGetValue("brick_path", out var bpObj) && bpObj is string bpStr)
	{
		brickPath = bpStr;
	}
	string mountPoint = "/mnt/gluster";
	if (glusterfsData.TryGetValue("mount_point", out var mpObj) && mpObj is string mpStr)
	{
		mountPoint = mpStr;
	}
	int replicaCount = ToInt(glusterfsData.TryGetValue("replica_count", out var rcObj) ? rcObj : null, 2);
	
	var clusterNodes = new List<GlusterFSNodeConfig>();
	if (glusterfsData.TryGetValue("cluster_nodes", out var cnObj) && cnObj is List<object> cn)
	{
		foreach (var nodeInterface in cn)
		{
			int nodeID = 0;
			if (nodeInterface is Dictionary<string, object> nodeMap)
			{
				int baseID = ToInt(nodeMap.TryGetValue("id", out var idObj) ? idObj : null);
				if (baseID > 0)
				{
					nodeID = baseID + idBase;
				}
			}
			else
			{
				int baseID = ToInt(nodeInterface);
				if (baseID > 0)
				{
					nodeID = baseID + idBase;
				}
			}
			if (nodeID > 0)
			{
				clusterNodes.Add(new GlusterFSNodeConfig { ID = nodeID });
			}
		}
	}
	// ...
}
```

### Differences:
1. **ID filtering**: C# only adds nodes if `baseID > 0`; Go adds all nodes (including 0)
2. **Helper function**: C# uses `ToInt`; Go uses type assertion

---

## 19. Backup Parsing

### Go (lines 998-1046)
```go
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
```

### C# (lines 1113-1171)
```csharp
BackupConfig? backup = null;
if (data.TryGetValue("backup", out var backupObj) && backupObj is Dictionary<string, object> backupData)
{
	int containerID = ToInt(backupData.TryGetValue("container_id", out var cidObj) ? cidObj : null);
	string backupDir = "/backup";
	if (backupData.TryGetValue("backup_dir", out var bdObj) && bdObj is string bdStr)
	{
		backupDir = bdStr;
	}
	string namePrefix = "backup";
	if (backupData.TryGetValue("name_prefix", out var npObj) && npObj is string npStr)
	{
		namePrefix = npStr;
	}
	
	var items = new List<BackupItemConfig>();
	if (backupData.TryGetValue("items", out var itemsObj) && itemsObj is List<object> itemsList)
	{
		foreach (var itemInterface in itemsList)
		{
			if (itemInterface is not Dictionary<string, object> item)
			{
				continue;
			}
			string name = item.TryGetValue("name", out var nameObj) && nameObj is string nameStr ? nameStr : "";
			int sourceContainerID = ToInt(item.TryGetValue("source_container_id", out var scidObj) ? scidObj : null);
			string sourcePath = item.TryGetValue("source_path", out var spObj) && spObj is string spStr ? spStr : "";
			
			string? archiveBase = null;
			if (item.TryGetValue("archive_base", out var abObj) && abObj is string abStr)
			{
				archiveBase = abStr;
			}
			string? archivePath = null;
			if (item.TryGetValue("archive_path", out var apObj) && apObj is string apStr)
			{
				archivePath = apStr;
			}
			
			items.Add(new BackupItemConfig
			{
				Name = name,
				SourceContainerID = sourceContainerID + idBase,
				SourcePath = sourcePath,
				ArchiveBase = archiveBase,
				ArchivePath = archivePath
			});
		}
	}
	
	backup = new BackupConfig
	{
		ContainerID = containerID + idBase,
		BackupDir = backupDir,
		NamePrefix = namePrefix,
		Items = items
	};
}
```

### Differences:
1. **Helper function**: C# uses `ToInt`; Go uses type assertion
2. **Nullable types**: C# uses nullable `string?`; Go uses `*string`

---

## 20. ComputeDerivedFields Method

### Go (lines 1121-1192)
```go
func (c *LabConfig) ComputeDerivedFields() {
	networkStr := c.Network
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
	
	for i := range c.Containers {
		if c.NetworkBase != nil {
			ipAddr := fmt.Sprintf("%s.%d", *c.NetworkBase, c.Containers[i].IP)
			c.Containers[i].IPAddress = &ipAddr
		}
	}
	
	for i := range c.Templates {
		if c.NetworkBase != nil {
			ipAddr := fmt.Sprintf("%s.%d", *c.NetworkBase, c.Templates[i].IP)
			c.Templates[i].IPAddress = &ipAddr
		}
	}
	
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
```

### C# (lines 494-542)
```csharp
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
```

### Differences:
1. **Network parsing**: Go manually parses network string character by character; C# uses `LastIndexOf` and `Split` methods
2. **Kubernetes matching**: Go uses nested loops with `break`; C# uses `Contains` method (more efficient)
3. **String formatting**: Go uses `fmt.Sprintf`; C# uses string interpolation (`$""`)
4. **Pointer dereferencing**: Go uses pointer dereferencing (`*c.NetworkBase`); C# uses direct access

---

## 21. Type Definitions

### Go
- Uses struct tags: `yaml:"field_name"`
- Uses pointers for optional fields: `*string`, `*int`, `*bool`
- Uses slices: `[]string`, `[]ContainerConfig`

### C#
- Uses `[YamlMember(Alias = "field_name")]` attributes
- Uses nullable types: `string?`, `int?`, `bool?`
- Uses `List<T>`: `List<string>`, `List<ContainerConfig>`

---

## Summary of Key Differences

### 1. Error Handling
- **Go**: Returns `(result, error)` tuples
- **C#**: Throws exceptions

### 2. Type Handling
- **Go**: Only handles `int` for numeric values
- **C#**: Handles both `int` and `long` (YamlDotNet may deserialize as `Int64`)

### 3. Dictionary Conversion
- **Go**: Assumes `map[string]interface{}`
- **C#**: Converts `Dictionary<object, object>` to `Dictionary<string, object>` via `ConvertDict` helper

### 4. Zero Value Handling
- **Go**: Keeps `0` values (0 is valid)
- **C#**: Converts `0` to `null` for some ports and numeric fields

### 5. ID Filtering
- **Go**: Includes all IDs (including 0)
- **C#**: Skips IDs `<= 0` in Kubernetes and GlusterFS parsing

### 6. Default Values
- **Go**: `gatewayOctet` defaults to `0`, `connectTimeout` defaults to `0`
- **C#**: `gatewayOctet` defaults to `1`, `connectTimeout` defaults to `10`

### 7. Service Validation
- **Go**: Does not check if service data is empty
- **C#**: Checks `Count > 0` for PostgreSQL, HAProxy, Rancher, Longhorn, GitHub Runner, CockroachDB, and CertA

### 8. Network Parsing
- **Go**: Manual character-by-character parsing
- **C#**: Uses `LastIndexOf` and `Split` methods

### 9. Kubernetes Matching
- **Go**: Nested loops with `break`
- **C#**: Uses `Contains` method (more efficient)

### 10. Pointer vs Nullable
- **Go**: Uses pointers (`*int`, `*string`)
- **C#**: Uses nullable types (`int?`, `string?`)

### 11. Type Checking
- **Go**: Uses type assertion with `ok` check: `val, ok := data["key"].(type)`
- **C#**: Uses `TryGetValue` and `is` operator: `data.TryGetValue("key", out var val) && val is Type`

### 12. Helper Functions
- **Go**: Uses anonymous functions and direct type assertions
- **C#**: Uses `ToInt` helper function and `ConvertDict` helper function

---

## Potential Issues

1. **Zero value handling inconsistency**: C# converts `0` to `null` for ports, which may not match Go behavior if `0` is a valid port number
2. **ID filtering**: C# skips IDs `<= 0` in some places, which may cause different behavior if IDs can be 0
3. **Default values**: Different defaults for `gatewayOctet` and `connectTimeout` may cause different behavior
4. **Template parsing**: C# does not handle `Dictionary<object, object>` for templates, unlike containers
5. **Error handling**: Different error handling approaches may cause different behavior in error scenarios

---

## Recommendations

1. Align zero value handling between both implementations
2. Standardize ID filtering logic
3. Unify default values
4. Ensure consistent dictionary conversion handling across all parsing sections
5. Document any intentional behavioral differences
