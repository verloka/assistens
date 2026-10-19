using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Security;
using System.Text.Json;

namespace PersonalAgent.UnitTests;

public sealed class ToolAccessContextTests
{
    [Fact]
    public void CategoriesAreExplicitImmutableAndDoNotFormAPrivilegeHierarchy()
    {
        var run = Guid.NewGuid(); var conversation = Guid.NewGuid();
        var invocation = new ToolExecutionContext(run, conversation, "one");
        var categories = new List<ToolRiskLevel> { ToolRiskLevel.ReadOnly };
        var context = new ToolAccessContext(run, conversation, categories);
        categories.Add(ToolRiskLevel.Destructive);
        foreach (var risk in Enum.GetValues<ToolRiskLevel>())
            Assert.Equal(risk == ToolRiskLevel.ReadOnly, context.Allows(Descriptor(risk), invocation));
        var destructiveOnly = new ToolAccessContext(run, conversation, [ToolRiskLevel.Destructive]);
        Assert.True(destructiveOnly.Allows(Descriptor(ToolRiskLevel.Destructive), invocation));
        Assert.False(destructiveOnly.Allows(Descriptor(ToolRiskLevel.ExternalDisclosure), invocation));
        Assert.False(destructiveOnly.Allows(Descriptor(ToolRiskLevel.ReadOnly), invocation));
        Assert.False(new ToolAccessContext(run, conversation, []).Allows(Descriptor(ToolRiskLevel.ReadOnly), invocation));
    }

    [Fact]
    public void ContextCannotBeBorrowedAcrossRunConversationOrInvalidClassification()
    {
        var run = Guid.NewGuid(); var conversation = Guid.NewGuid();
        var context = new ToolAccessContext(run, conversation, [ToolRiskLevel.ReadOnly, ToolRiskLevel.ExternalDisclosure]);
        var invocation = new ToolExecutionContext(run, conversation, "one");
        Assert.False(context.Allows(Descriptor(ToolRiskLevel.ReadOnly), invocation with { RunId = Guid.NewGuid() }));
        Assert.False(context.Allows(Descriptor(ToolRiskLevel.ReadOnly), invocation with { ConversationId = Guid.NewGuid() }));
        Assert.False(context.Allows(Descriptor(ToolRiskLevel.ReadOnly), invocation with { ToolCallId = "" }));
        Assert.False(context.Allows(Descriptor(ToolRiskLevel.ReadOnly) with { IsReadOnly = false }, invocation));
        Assert.True(context.Allows(Descriptor(ToolRiskLevel.ExternalDisclosure) with { IsReadOnly = true }, invocation)); // Screenshots still disclose data.
        Assert.Throws<ArgumentException>(() => new ToolAccessContext(run, conversation, [ToolRiskLevel.Unknown]));
        Assert.Throws<ArgumentException>(() => new ToolAccessContext(run, conversation, [(ToolRiskLevel)99]));
        Assert.Throws<ArgumentException>(() => new ToolAccessContext(Guid.Empty, conversation, []));
    }

    [Fact]
    public void ExistingPersistedEnumNumbersRemainStable()
    {
        Assert.Equal(0, (int)ToolRiskLevel.Unknown); Assert.Equal(1, (int)ToolRiskLevel.ReadOnly);
        Assert.Equal(2, (int)ToolRiskLevel.Write); Assert.Equal(3, (int)ToolRiskLevel.Destructive);
        Assert.Equal(4, (int)ToolRiskLevel.ExternalDisclosure); Assert.Equal(5, (int)ToolRiskLevel.Navigation);
    }

    private static ToolDescriptor Descriptor(ToolRiskLevel risk) => new("fixture", "Trusted fixture",
        JsonSerializer.SerializeToElement(new { type = "object" }), new(ToolSourceKind.Local, "fixture"), risk, IsReadOnly: risk == ToolRiskLevel.ReadOnly);
}
