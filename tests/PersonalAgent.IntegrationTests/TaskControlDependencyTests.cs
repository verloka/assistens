using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PersonalAgent.IntegrationTests;

public sealed class TaskControlDependencyTests
{
    [Theory]
    [InlineData("", "")] [InlineData("PA-113", "")] [InlineData("PA-999", "Owner explicitly defers this check")]
    public async Task MissingOwnerDecisionOrUndeclaredDependencyCannotBypassBlockedPrerequisite(string dependency, string note)
    {
        using var fixture = new Fixture();
        var args = new List<string> { "-Action", "start", "-Id", "PA-114" };
        if (dependency.Length > 0) args.AddRange(["-DeferDependency", dependency]);
        if (note.Length > 0) args.AddRange(["-Note", note]);
        Assert.NotEqual(0, (await fixture.Run(args.ToArray())).ExitCode);
        Assert.Equal("pending", fixture.State()["task_states"]!["PA-114"]!["status"]!.GetValue<string>());
        Assert.Equal(0, (await fixture.Run("-Action", "doctor")).ExitCode);
    }

    [Fact]
    public async Task ExplicitDeferralPermitsOnlyTheNamedBlockedEdgeAndPreservesItsUnverifiedState()
    {
        using var fixture = new Fixture();
        Assert.Equal(0, (await fixture.Run("-Action", "start", "-Id", "PA-114", "-DeferDependency", "PA-113", "-Note", "Owner authorizes114;113native checks deferred to final review")).ExitCode);
        Assert.Equal(0, (await fixture.Run("-Action", "done", "-Id", "PA-114", "-Evidence", "Synthetic tracker test only", "-Test", "Fixture acceptance verified")).ExitCode);
        Assert.Equal(0, (await fixture.Run("-Action", "doctor")).ExitCode);
        var state = fixture.State();
        Assert.Equal("blocked", state["task_states"]!["PA-113"]!["status"]!.GetValue<string>());
        Assert.Equal("done", state["task_states"]!["PA-114"]!["status"]!.GetValue<string>());
        Assert.Equal("PA-113", state["task_states"]!["PA-114"]!["deferred_dependencies"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(0, (await fixture.Run("-Action", "start", "-Id", "PA-125")).ExitCode);
        Assert.NotEqual(0, (await fixture.Run("-Action", "done", "-Id", "PA-125", "-Evidence", "Synthetic release fixture", "-Test", "Not sufficient for release")).ExitCode);
        Assert.Equal("in_progress", fixture.State()["task_states"]!["PA-125"]!["status"]!.GetValue<string>());
        state["task_states"]!["PA-113"]!["status"] = "pending";
        fixture.Save(state); Assert.NotEqual(0, (await fixture.Run("-Action", "doctor")).ExitCode);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "PersonalAgent.TrackerTests", Guid.NewGuid().ToString("N"));
        public Fixture()
        {
            var repository = new DirectoryInfo(AppContext.BaseDirectory);
            while (!File.Exists(Path.Combine(repository.FullName, "scripts", "taskctl.ps1"))) repository = repository.Parent ?? throw new InvalidOperationException("Repository script not found.");
            Directory.CreateDirectory(Path.Combine(root, "scripts")); Directory.CreateDirectory(Path.Combine(root, "progress"));
            File.Copy(Path.Combine(repository.FullName, "scripts", "taskctl.ps1"), Path.Combine(root, "scripts", "taskctl.ps1"));
            File.WriteAllText(Path.Combine(root, "tasks.json"), JsonSerializer.Serialize(new { tasks = new[] {
                new { id = "PA-113", sequence = 113, depends_on = Array.Empty<string>() }, new { id = "PA-114", sequence = 114, depends_on = new[] { "PA-113" } },
                new { id = "PA-125", sequence = 125, depends_on = new[] { "PA-114" } }
            } }));
            Save(JsonNode.Parse("""{"task_states":{"PA-113":{"status":"blocked","note":"Native checks deferred"},"PA-114":{"status":"pending","attempts":0,"evidence":[],"started_at":null,"completed_at":null,"note":null},"PA-125":{"status":"pending","attempts":0,"evidence":[],"started_at":null,"completed_at":null,"note":null}},"last_task_id":null,"updated_at":"initial"}""")!);
        }
        public JsonNode State() => JsonNode.Parse(File.ReadAllText(Path.Combine(root, "progress", "state.json")))!;
        public void Save(JsonNode state) => File.WriteAllText(Path.Combine(root, "progress", "state.json"), state.ToJsonString());
        public async Task<(int ExitCode, string Output)> Run(params string[] arguments)
        {
            var info = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "scripts", "taskctl.ps1") }.Concat(arguments)) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            return (process.ExitCode, await output + await error);
        }
        public void Dispose()
        {
            var allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PersonalAgent.TrackerTests")) + Path.DirectorySeparatorChar;
            if (Path.GetFullPath(root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) Directory.Delete(root, true);
        }
    }
}
