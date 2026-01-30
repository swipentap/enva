using System;
using System.Collections.Generic;
using System.Linq;
using Enva.Libs;
using Enva.Services;

namespace Enva.Actions;

public delegate IAction ActionFactory(SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg);

public static class ActionRegistry
{
    private static readonly Dictionary<string, ActionFactory> _actionRegistry = new();

    public static void RegisterAction(string name, ActionFactory factory)
    {
        string normalized = NormalizeActionName(name);
        _actionRegistry[normalized] = factory;
    }

    public static IAction? GetAction(string actionName, SSHService? sshService, APTService? aptService, PCTService? pctService, string? containerID, LabConfig? cfg, ContainerConfig? containerCfg)
    {
        string normalized = NormalizeActionName(actionName);
        if (_actionRegistry.TryGetValue(normalized, out var factory))
        {
            var action = factory(sshService, aptService, pctService, containerID, cfg, containerCfg);
            if (action is BaseAction baseAction)
            {
                baseAction.ActionName = actionName;
            }
            return action;
        }
        throw new Exception($"action '{actionName}' not found. Available actions: [{string.Join(", ", GetAvailableActions())}]");
    }

    private static string NormalizeActionName(string name)
    {
        string normalized = name.ToLower();
        normalized = normalized.Replace(" ", "-");
        normalized = normalized.Replace("_", "-");
        return normalized.Trim();
    }

    private static List<string> GetAvailableActions()
    {
        return _actionRegistry.Keys.ToList();
    }
}
