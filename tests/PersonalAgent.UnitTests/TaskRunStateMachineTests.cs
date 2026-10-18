using PersonalAgent.Core.Domain;

namespace PersonalAgent.UnitTests;

public sealed class TaskRunStateMachineTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-18T10:00:00Z");

    [Fact]
    public void TaskRunQueuesStartsAndCompletesWithVersionAndStableStartTime()
    {
        var created = Create();
        var queued = Move(created, RunStatus.Queued);
        Assert.Null(queued.StartedAtUtc);
        var running = Move(queued, RunStatus.Running);
        var completed = TaskRunStateMachine.Transition(running, RunStatus.Completed, 2, Now.AddMinutes(1).ToOffset(TimeSpan.FromHours(3)));
        Assert.Equal(RunStatus.Created, created.Status);
        Assert.Equal(3, completed.RunVersion);
        Assert.Equal(Now, completed.StartedAtUtc);
        Assert.Equal(Now.AddMinutes(1), completed.EndedAtUtc);
        Assert.Equal(TimeSpan.Zero, completed.EndedAtUtc!.Value.Offset);
    }

    [Theory]
    [InlineData(RunStatus.WaitingForDesktop)]
    [InlineData(RunStatus.WaitingForUser)]
    [InlineData(RunStatus.WaitingForApproval)]
    public void TaskRunWaitsAndContinuesWithoutResettingStart(RunStatus waiting)
    {
        var running = Move(Move(Create(), RunStatus.Queued), RunStatus.Running);
        var resumed = TaskRunStateMachine.Transition(Move(running, waiting), RunStatus.Running, 3, Now.AddMinutes(1));
        Assert.Equal(running.StartedAtUtc, resumed.StartedAtUtc);
        Assert.Null(resumed.EndedAtUtc);
    }

    [Theory]
    [InlineData(PauseScope.EntireRun, RunStatus.Paused)]
    [InlineData(PauseScope.GuiOnly, RunStatus.PausedForUser)]
    public void TaskRunPauseRequiresPausingBoundaryAndExplicitResume(PauseScope scope, RunStatus pausedStatus)
    {
        var running = Move(Move(Create(), RunStatus.Queued), RunStatus.Running);
        Assert.Throws<InvalidOperationException>(() => Move(running, pausedStatus));
        var pausing = TaskRunStateMachine.Transition(running, RunStatus.Pausing, 2, Now, pauseScope: scope);
        var paused = Move(pausing, pausedStatus);
        Assert.Equal(scope, paused.PauseScope);
        Assert.Null(paused.EndedAtUtc);
        var resumed = Move(paused, RunStatus.Running);
        Assert.Null(resumed.PauseScope);
        Assert.Null(resumed.Reason);
        Assert.Equal(running.StartedAtUtc, resumed.StartedAtUtc);
    }

    [Fact]
    public void TaskRunBlockedRequiresReasonAndExplicitRequeue()
    {
        var queued = Move(Create(), RunStatus.Queued);
        Assert.Throws<ArgumentException>(() => Move(queued, RunStatus.Blocked));
        var blocked = Move(queued, RunStatus.Blocked, "Model unavailable");
        Assert.Throws<InvalidOperationException>(() => Move(blocked, RunStatus.Running));
        var requeued = Move(blocked, RunStatus.Queued);
        Assert.Null(requeued.Reason);
        Assert.Null(requeued.EndedAtUtc);
    }

    [Theory]
    [InlineData(RunStatus.Completed)]
    [InlineData(RunStatus.Failed)]
    [InlineData(RunStatus.Cancelled)]
    [InlineData(RunStatus.Skipped)]
    [InlineData(RunStatus.Interrupted)]
    public void TaskRunTerminalStateCannotResumeRequeueOrChangeToAnotherOutcome(RunStatus terminal)
    {
        var run = Create() with { Status = terminal, EndedAtUtc = Now };
        Assert.True(TaskRunStateMachine.IsTerminal(terminal));
        foreach (var next in Enum.GetValues<RunStatus>())
            Assert.Throws<InvalidOperationException>(() => Move(run, next, "reason"));
    }

    [Theory]
    [InlineData(RunStatus.Created, RunStatus.Completed)]
    [InlineData(RunStatus.Queued, RunStatus.PausedForUser)]
    [InlineData(RunStatus.Paused, RunStatus.Completed)]
    [InlineData(RunStatus.Running, RunStatus.Queued)]
    [InlineData(RunStatus.Pausing, RunStatus.Running)]
    [InlineData(RunStatus.Running, (RunStatus)999)]
    [InlineData((RunStatus)999, RunStatus.Running)]
    public void TaskRunRejectsInvalidTransitionsWithoutMutatingSnapshot(RunStatus current, RunStatus next)
    {
        var run = Create() with { Status = current };
        Assert.Throws<InvalidOperationException>(() => Move(run, next));
        Assert.Equal(current, run.Status);
        Assert.Equal(0, run.RunVersion);
    }

    [Fact]
    public void TaskRunRejectsStaleVersionBeforeCancelOrResume()
    {
        var run = Create() with { Status = RunStatus.Paused, RunVersion = 4 };
        var error = Assert.Throws<RunVersionConflictException>(() => TaskRunStateMachine.Transition(run, RunStatus.Running, 3, Now));
        Assert.Equal(4, error.ActualVersion);
        var cancelled = TaskRunStateMachine.Transition(run, RunStatus.Cancelled, 4, Now);
        Assert.Throws<RunVersionConflictException>(() => TaskRunStateMachine.Transition(cancelled, RunStatus.Running, 4, Now));
        Assert.Equal(RunStatus.Cancelled, cancelled.Status);
    }

    [Fact]
    public void TaskRunRejectsBackdatedTransition()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TaskRunStateMachine.Transition(Create(), RunStatus.Queued, 0, Now.AddSeconds(-1)));
    }

    [Fact]
    public void TaskRunCannotRequeueAlreadyStartedPausedAttempt()
    {
        var running = Move(Move(Create(), RunStatus.Queued), RunStatus.Running);
        var paused = Move(Move(running, RunStatus.Pausing), RunStatus.Paused);
        Assert.Throws<InvalidOperationException>(() => Move(paused, RunStatus.Queued));
    }

    [Fact]
    public void TaskRunPreservesGuiOnlyGateMetadataWhenNonGuiExecutionStarts()
    {
        var queued = Move(Create(), RunStatus.Queued) with { PauseScope = PauseScope.GuiOnly };
        Assert.Equal(PauseScope.GuiOnly, Move(queued, RunStatus.Running).PauseScope);
    }

    private static TaskRun Move(TaskRun run, RunStatus next, string? reason = null) =>
        TaskRunStateMachine.Transition(run, next, run.RunVersion, Now, reason);

    private static TaskRun Create() => new()
    {
        Id = Guid.NewGuid(), ConversationId = Guid.NewGuid(), Origin = new TaskOrigin("desktop"),
        Instructions = "Test", ModelProfileId = "fake", CreatedAtUtc = Now
    };
}
