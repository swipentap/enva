namespace Enva.CLI;

public class SystemCtl
{
    private string? serviceName;

    public static SystemCtl NewSystemCtl()
    {
        return new SystemCtl();
    }

    public SystemCtl Service(string name)
    {
        serviceName = name;
        return this;
    }

    public string Restart()
    {
        if (string.IsNullOrEmpty(serviceName))
        {
            throw new Exception("Service name must be set");
        }
        return $"systemctl restart {serviceName}";
    }

    public string Enable()
    {
        if (string.IsNullOrEmpty(serviceName))
        {
            throw new Exception("Service name must be set");
        }
        return $"systemctl enable {serviceName}";
    }

    public string IsActive()
    {
        if (string.IsNullOrEmpty(serviceName))
        {
            throw new Exception("Service name must be set");
        }
        return $"systemctl is-active {serviceName}";
    }

    public string Start()
    {
        if (string.IsNullOrEmpty(serviceName))
        {
            throw new Exception("Service name must be set");
        }
        return $"systemctl start {serviceName}";
    }

    public string Status()
    {
        if (string.IsNullOrEmpty(serviceName))
        {
            throw new Exception("Service name must be set");
        }
        return $"systemctl status {serviceName}";
    }

    public string Stop()
    {
        if (string.IsNullOrEmpty(serviceName))
        {
            throw new Exception("Service name must be set");
        }
        return $"systemctl stop {serviceName}";
    }

    public string Disable()
    {
        if (string.IsNullOrEmpty(serviceName))
        {
            throw new Exception("Service name must be set");
        }
        return $"systemctl disable {serviceName}";
    }

    public static bool ParseIsActive(string output)
    {
        return output.Trim() == "active";
    }
}
