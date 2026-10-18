using Microsoft.EntityFrameworkCore;
using PersonalAgent.Core.Application;
using PersonalAgent.Core.Computer;
using PersonalAgent.Core.Domain;
using PersonalAgent.Persistence;
using PersonalAgent.IntegrationTests.Fakes;

namespace PersonalAgent.IntegrationTests;

public sealed class PauseScopePersistenceTests
{
    [Fact]
    public async Task RestoredGuiPauseBlocksNewLeaseAndGuiStepUntilExplicitResume()
    {
        using var database = new TemporaryAgentDatabase();
        await using (var db = database.Open()) await db.Database.MigrateAsync();
        var repository = new SqliteRunRepository(database);
        TaskRun paused;
        using (var first = new TaskRunCoordinator(repository, new DesktopAccessCoordinator(_ => true)))
        {
            var run = await first.SubmitAsync(new(Guid.NewGuid(), "Scoped pause", new TaskOrigin("desktop")));
            paused = await first.PauseAsync(new(Guid.NewGuid(), run.Id, run.RunVersion, PauseScope.GuiOnly));
        }
        var desktop = new DesktopAccessCoordinator(_ => true);
        using var restored = new TaskRunCoordinator(repository, desktop);
        await restored.RestoreHistoryAsync([]);
        await Assert.ThrowsAsync<DesktopAccessException>(() => desktop.AcquireAsync(paused.Id));
        await restored.WaitAtBoundaryAsync(paused.Id, new RunStepAccess(false, true, true));
        var gui = restored.WaitAtBoundaryAsync(paused.Id, new RunStepAccess(true, true, false));
        Assert.False(gui.IsCompleted);
        await restored.ResumeAsync(new(Guid.NewGuid(), paused.Id, paused.RunVersion));
        await gui.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Null((await repository.GetAsync(paused.Id))!.Run.PauseScope);
        Assert.True(desktop.IsCurrent(await desktop.AcquireAsync(paused.Id)));
    }
}
