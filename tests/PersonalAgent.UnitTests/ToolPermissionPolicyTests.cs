using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Security;
using System.Text.Json;

namespace PersonalAgent.UnitTests;

public sealed class ToolPermissionPolicyTests
{
    private readonly Guid run = Guid.NewGuid(), conversation = Guid.NewGuid(), connection = Guid.NewGuid();
    private ToolExecutionContext Invocation => new(run, conversation, "one");
    private ToolAccessContext Access => new(run, conversation, Enum.GetValues<ToolRiskLevel>().Where(value => value != ToolRiskLevel.Unknown));
    private ToolDescriptor Descriptor(ToolRiskLevel risk = ToolRiskLevel.ReadOnly) => new("tool", "Reviewed tool",
        JsonSerializer.SerializeToElement(new { type = "object" }), new(ToolSourceKind.Mcp, connection.ToString("N")), risk,
        IsReadOnly: risk == ToolRiskLevel.ReadOnly) { Version = new string('a', 64) };
    private static RunToolPermission Allow(ToolDescriptor tool) => new(tool.Id, tool.Source, tool.RiskLevel, ToolPermissionEffect.Allow, tool.Version);
    private McpToolPermission Grant(ToolDescriptor tool) => new(connection, tool.Id, tool.Version!, tool.RiskLevel);

    [Fact]
    public void BothScopesMustMatchAndDenyWinsRegardlessOfOrderOrClassification()
    {
        var tool = Descriptor(); var allow = Allow(tool); var grant = Grant(tool);
        Assert.Equal(ToolPermissionDecision.Allowed, new ToolPermissionPolicy(Access, [allow], [grant]).Evaluate(tool, Invocation));
        foreach (var pair in new[] { (new[] { allow }, Array.Empty<McpToolPermission>()), (Array.Empty<RunToolPermission>(), new[] { grant }) })
            Assert.Equal(ToolPermissionDecision.Denied, new ToolPermissionPolicy(Access, pair.Item1, pair.Item2).Evaluate(tool, Invocation));
        var deny = allow with { Effect = ToolPermissionEffect.Deny, RiskLevel = ToolRiskLevel.Destructive, Version = "old" };
        Assert.Equal(ToolPermissionDecision.Denied, new ToolPermissionPolicy(Access, [allow, deny], [grant]).Evaluate(tool, Invocation));
        Assert.Equal(ToolPermissionDecision.Denied, new ToolPermissionPolicy(Access, [deny, allow], [grant]).Evaluate(tool, Invocation));
    }

    [Fact]
    public void ConnectionRunVersionRiskAndNewToolCannotInheritAnotherGrant()
    {
        var tool = Descriptor(); var policy = new ToolPermissionPolicy(Access, [Allow(tool)], [Grant(tool)]);
        foreach (var changed in new[] { tool with { Id = "new_tool" }, tool with { Source = new(ToolSourceKind.Mcp, Guid.NewGuid().ToString("N")) },
            tool with { Version = new string('b', 64) }, tool with { RiskLevel = ToolRiskLevel.Write, IsReadOnly = false },
            tool with { Source = new(ToolSourceKind.Computer, tool.Source.Id) } })
            Assert.Equal(ToolPermissionDecision.Denied, policy.Evaluate(changed, Invocation));
        Assert.Equal(ToolPermissionDecision.Denied, policy.Evaluate(tool, Invocation with { RunId = Guid.NewGuid() }));
        Assert.Equal(ToolPermissionDecision.Denied, new ToolPermissionPolicy(Access, [Allow(tool)], [Grant(tool) with { Version = "old" }]).Evaluate(tool, Invocation));
        Assert.Equal(ToolPermissionDecision.Denied, new ToolPermissionPolicy(Access, [Allow(tool)], [Grant(tool), Grant(tool)]).Evaluate(tool, Invocation));
    }

    [Theory]
    [InlineData(ToolRiskLevel.Navigation)] [InlineData(ToolRiskLevel.Write)]
    [InlineData(ToolRiskLevel.Destructive)] [InlineData(ToolRiskLevel.ExternalDisclosure)]
    public void SensitiveScopeNeverBecomesActionApproval(ToolRiskLevel risk)
    {
        var tool = Descriptor(risk);
        Assert.Equal(ToolPermissionDecision.RequiresApproval, new ToolPermissionPolicy(Access, [Allow(tool)], [Grant(tool)]).Evaluate(tool, Invocation));
        var readAccess = new ToolAccessContext(run, conversation, [ToolRiskLevel.ReadOnly]);
        Assert.Equal(ToolPermissionDecision.Denied, new ToolPermissionPolicy(readAccess, [Allow(tool)], [Grant(tool)]).Evaluate(tool, Invocation));
    }

    [Fact]
    public void InputCollectionsAreSnapshottedAndUnknownClassificationCannotGrantAuthority()
    {
        var tool = Descriptor(); var rules = new List<RunToolPermission>(); var grants = new List<McpToolPermission>();
        var policy = new ToolPermissionPolicy(Access, rules, grants); rules.Add(Allow(tool)); grants.Add(Grant(tool));
        Assert.Equal(ToolPermissionDecision.Denied, policy.Evaluate(tool, Invocation));
        var unknown = tool with { RiskLevel = ToolRiskLevel.Unknown };
        Assert.Equal(ToolPermissionDecision.Denied, new ToolPermissionPolicy(Access, [Allow(unknown)], [Grant(unknown)]).Evaluate(unknown, Invocation));
        Assert.Throws<ArgumentException>(() => new ToolPermissionPolicy(Access, [Allow(tool) with { Effect = (ToolPermissionEffect)99 }], []));
    }
}
