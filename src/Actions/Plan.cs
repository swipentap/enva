using Enva.Libs;

namespace Enva.Actions;

public class DeployPlan
{
    public ContainerConfig? AptCacheContainer { get; set; }
    public List<TemplateConfig> Templates { get; set; } = new();
    public List<ContainerConfig> ContainersList { get; set; } = new();
    public int TotalSteps { get; set; }
    public int Step { get; set; }
    public int StartStep { get; set; }
    public int? EndStep { get; set; }
    public int CurrentActionStep { get; set; }
    public bool PlanOnly { get; set; }
}
