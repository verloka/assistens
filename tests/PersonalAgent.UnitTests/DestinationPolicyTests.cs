using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Security;
using System.Text.Json;

namespace PersonalAgent.UnitTests;

public sealed class DestinationPolicyTests
{
    [Fact]
    public void TargetScopeNeedsActualUnambiguousExactScalarArgumentsAndCannotBroadenAfterConstruction()
    {
        var context = new ToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), "send");
        var descriptor = new ToolDescriptor("send", "Send synthetic data", Json("{\"type\":\"object\"}"), new(ToolSourceKind.Local, "fixture"), ToolRiskLevel.ExternalDisclosure);
        var values = new List<string> { "owner@example.invalid" };
        var constraints = new List<ToolDestinationConstraint> { new("/delivery/recipient", values) };
        var rule = new RunToolPermission(descriptor.Id, descriptor.Source, descriptor.RiskLevel, ToolPermissionEffect.Allow, Destinations: constraints);
        var policy = new ToolPermissionPolicy(new(context.RunId, context.ConversationId, [ToolRiskLevel.ExternalDisclosure]), [rule]);
        values.Add("attacker@example.invalid"); constraints.Clear();
        Assert.Equal(ToolPermissionDecision.RequiresApproval, policy.EvaluateScope(descriptor, context));
        Assert.Equal(ToolPermissionDecision.Denied, policy.Evaluate(descriptor, context));
        Assert.Equal(ToolPermissionDecision.RequiresApproval, policy.Evaluate(descriptor, new(Json("{\"delivery\":{\"recipient\":\"owner@example.invalid\"}}")), context));
        foreach (var invalid in new[] { "{}", "{\"delivery\":null}", "{\"delivery\":{\"recipient\":[\"owner@example.invalid\",\"attacker@example.invalid\"]}}",
            "{\"delivery\":{\"recipient\":\"attacker@example.invalid\"}}", "{\"delivery\":{\"recipient\":\"Owner@example.invalid\"}}",
            "{\"delivery\":{\"recipient\":\"attacker@example.invalid\",\"recipient\":\"owner@example.invalid\"}}",
            "{\"delivery\":{\"recipient\":\"owner@example.invalid\"},\"delivery\":{\"recipient\":\"owner@example.invalid\"}}" })
            Assert.Equal(ToolPermissionDecision.Denied, policy.Evaluate(descriptor, new(Json(invalid)), context));
    }

    [Fact]
    public void JsonPointerEscapesWorkAndInvalidRulesCannotBecomeWildcardTargets()
    {
        var context = new ToolExecutionContext(Guid.NewGuid(), Guid.NewGuid(), "call");
        var descriptor = new ToolDescriptor("send", "Send", Json("{\"type\":\"object\"}"), new(ToolSourceKind.Local, "fixture"), ToolRiskLevel.Write);
        ToolPermissionPolicy Policy(string path) => new(new(context.RunId, context.ConversationId, [ToolRiskLevel.Write]),
            [new(descriptor.Id, descriptor.Source, descriptor.RiskLevel, ToolPermissionEffect.Allow, Destinations: [new(path, ["trusted"])])]);
        Assert.Equal(ToolPermissionDecision.RequiresApproval, Policy("/a~1b/~0recipient").Evaluate(descriptor, new(Json("{\"a/b\":{\"~recipient\":\"trusted\"}}")), context));
        foreach (var path in new[] { "", "/", "recipient", "/bad~escape", "/bad~", "/double//path" }) Assert.Throws<ArgumentException>(() => Policy(path));
    }
    private static JsonElement Json(string value) => JsonSerializer.Deserialize<JsonElement>(value);
}
