using System;
using System.Collections.Generic;
using Enva.Libs;
using Enva.Services;

namespace Enva.Actions;

public interface IAction
{
    bool Execute();
    string Description();
}

public class BaseAction
{
    public SSHService? SSHService { get; set; }
    public APTService? APTService { get; set; }
    public PCTService? PCTService { get; set; }
    public string? ContainerID { get; set; }
    public LabConfig? Cfg { get; set; }
    public ContainerConfig? ContainerCfg { get; set; }
    public string? ActionName { get; set; }

    public virtual string Description()
    {
        return "";
    }

    protected Dictionary<string, object>? GetActionProperties()
    {
        if (ContainerCfg == null || string.IsNullOrEmpty(ActionName))
        {
            return null;
        }
        return ContainerCfg.GetActionProperties(ActionName);
    }

    protected T? GetProperty<T>(string key, T? defaultValue = default)
    {
        var props = GetActionProperties();
        if (props == null || !props.TryGetValue(key, out object? value))
        {
            return defaultValue;
        }
        if (value is T typedValue)
        {
            return typedValue;
        }
        try
        {
            return (T)Convert.ChangeType(value, typeof(T));
        }
        catch
        {
            return defaultValue;
        }
    }
}
