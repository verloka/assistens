using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Agents;
using PersonalAgent.Core.Channels;
using PersonalAgent.Core.Security;
using PersonalAgent.IntegrationTests.Fakes;
using PersonalAgent.Persistence;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PersonalAgent.IntegrationTests;

public sealed class ProtectedSecretStoreTests
{
    [Theory]
    [InlineData("model")]
    [InlineData("telegram")]
    [InlineData("mcp")]
    public async Task CurrentOwnerSecretSurvivesIndependentProcessWithoutPlaintextArgumentsOrOutput(string kind)
    {
        using var database = new TemporaryAgentDatabase();
        var directory = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "secrets");
        var secret = "synthetic-" + kind + "-" + Guid.NewGuid().ToString("N");
        var reference = await new WindowsDpapiSecretStore(directory).StoreAsync(secret);
        var encrypted = await File.ReadAllBytesAsync(Assert.Single(Directory.GetFiles(directory)));
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(encrypted));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        // The integration output prunes WindowsDesktop shared-framework assemblies.
        // Give this copied child the same framework set rather than a missing private DLL.
        start.ArgumentList.Add("exec"); start.ArgumentList.Add("--runtimeconfig");
        start.ArgumentList.Add(Path.ChangeExtension(typeof(ProtectedSecretStoreTests).Assembly.Location, ".runtimeconfig.json"));
        start.ArgumentList.Add(typeof(RecoveryTestProcess.Program).Assembly.Location);
        start.ArgumentList.Add("--secret-roundtrip"); start.ArgumentList.Add(directory); start.ArgumentList.Add(reference);
        start.ArgumentList.Add(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))));
        Assert.DoesNotContain(start.ArgumentList, value => value.Contains(secret, StringComparison.Ordinal));
        using var child = Process.Start(start)!;
        var output = child.StandardOutput.ReadToEndAsync(timeout.Token);
        var errors = child.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await child.WaitForExitAsync(timeout.Token);
            Assert.True(child.ExitCode == 0, (await output).Trim()); Assert.Equal("SECRET_ROUNDTRIP_OK", (await output).Trim());
            Assert.Empty(await errors);
        }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
        Assert.Equal(secret, await new WindowsDpapiSecretStore(directory).ReadAsync(reference));
    }

    [Fact]
    public async Task ModelChannelAndMcpConfigurationBackupContainsOnlyReferencesAndCannotResolveKeysWithoutSeparateStore()
    {
        using var database = new TemporaryAgentDatabase();
        await using (var db = database.Open()) await db.Database.MigrateAsync();
        var root = Path.GetDirectoryName(database.DatabasePath)!;
        var directory = Path.Combine(root, "secrets"); var secrets = new WindowsDpapiSecretStore(directory);
        var values = Enumerable.Range(0, 3).Select(index => "synthetic-protected-" + index + "-" + Guid.NewGuid().ToString("N")).ToArray();
        var references = new List<string>();
        foreach (var value in values) references.Add(await secrets.StoreAsync(value));
        var profile = new ModelProfile { Id = "protected-model", ProviderId = "openai", ModelId = "fixture", SecretRef = references[0] };
        await new SqliteModelProfileRepository(database).SaveAsync(profile);
        var owner = new ApprovalOwner("desktop", "secret-backup-owner"); var channelId = Guid.NewGuid();
        var channel = await new SqliteChannelConfigurationRepository(database).SaveAsync(new(channelId, owner, "Synthetic bot",
            new("telegram", channelId.ToString("N"), "24"), "42", false, false, false, references[1], [], DateTimeOffset.UtcNow), 0);
        var mcp = new McpConnectionProfile { Configuration = new() { Id = Guid.NewGuid(), Name = "Synthetic MCP", Transport = McpTransportKind.StreamableHttp,
            Http = new(new("https://mcp.example.test/endpoint"), references[2]) } };
        await new SqliteMcpConnectionRepository(database).SaveAsync(mcp);
        var serialized = JsonSerializer.Serialize(new { profile, channel, mcp });
        foreach (var value in values) Assert.DoesNotContain(value, serialized);

        // Copy a consistent database only; protected credential files are outside this backup boundary.
        var backupDirectory = Path.Combine(root, "configuration-backup"); Directory.CreateDirectory(backupDirectory);
        var backupPath = Path.Combine(backupDirectory, "settings.db");
        await using (var source = new SqliteConnection("Data Source=" + database.DatabasePath + ";Pooling=False"))
        await using (var destination = new SqliteConnection("Data Source=" + backupPath + ";Pooling=False"))
        {
            await source.OpenAsync(); await destination.OpenAsync(); source.BackupDatabase(destination);
        }
        var backup = new SqliteAgentDbContextFactory(backupPath);
        var restoredProfile = Assert.Single(await new SqliteModelProfileRepository(backup).ListAsync());
        var restoredChannel = Assert.Single(await new SqliteChannelConfigurationRepository(backup).ListAsync(owner));
        var restoredMcp = Assert.Single(await new SqliteMcpConnectionRepository(backup).ListAsync());
        Assert.Equal(references[0], restoredProfile.SecretRef);
        Assert.Equal(references[1], restoredChannel.SecretReference);
        Assert.Equal(references[2], restoredMcp.Configuration.Http!.BearerSecretRef);
        var copiedBytes = await File.ReadAllBytesAsync(backupPath);
        foreach (var value in values) Assert.DoesNotContain(value, Encoding.UTF8.GetString(copiedBytes));
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var protectedBytes = await File.ReadAllBytesAsync(file);
            Assert.False(copiedBytes.AsSpan().IndexOf(protectedBytes) >= 0);
        }
        Assert.Empty(Directory.GetFiles(backupDirectory, "*.secret", SearchOption.AllDirectories));
        var absentStore = new WindowsDpapiSecretStore(Path.Combine(backupDirectory, "secrets"));
        foreach (var reference in references) Assert.Null(await absentStore.ReadAsync(reference));
        for (var index = 0; index < references.Count; index++) Assert.Equal(values[index], await secrets.ReadAsync(references[index]));
    }

    [Fact]
    public async Task CancelledWritesAndInvalidReferencesCannotCreateOrReadCredentialFiles()
    {
        using var database = new TemporaryAgentDatabase();
        var directory = Path.Combine(Path.GetDirectoryName(database.DatabasePath)!, "secrets");
        var store = new WindowsDpapiSecretStore(directory); using var stop = new CancellationTokenSource(); stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await store.StoreAsync("synthetic-cancelled-key", stop.Token));
        Assert.False(Directory.Exists(directory));
        foreach (var reference in new[] { "raw-key", "dpapi:../outside", "dpapi:" + Guid.NewGuid().ToString("N") + "/extra" })
            await Assert.ThrowsAsync<ArgumentException>(async () => await store.ReadAsync(reference));
        Assert.False(Directory.Exists(directory));
    }
}
