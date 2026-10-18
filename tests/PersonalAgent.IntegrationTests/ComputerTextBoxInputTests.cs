using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace PersonalAgent.IntegrationTests;

public sealed class ComputerTextBoxInputTests
{
    [Fact]
    public Task WpfFiltersUnicodeTabEvenWhenAcceptsTabIsEnabled() => OnWpfThread(async () =>
    {
        // Actual WPF control, without showing a window or sending global/native input.
        var box = new TextBox { AcceptsReturn = true, AcceptsTab = true };
        box.Measure(new Size(400, 100)); box.Arrange(new Rect(0, 0, 400, 100)); box.UpdateLayout();
        var input = new TextCompositionEventArgs(Keyboard.PrimaryDevice,
            new TextComposition(InputManager.Current, box, "A\tB")) { RoutedEvent = TextCompositionManager.TextInputEvent };
        box.RaiseEvent(input);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        Assert.Equal("AB", box.Text); // Reproduces the missing tab from the reported 41/42 sample.
        // The property permits storing literal tabs; the loss occurs in the input pipeline.
        box.Text = "A\tB";
        Assert.Equal("A\tB", box.Text);
    });

    private static Task OnWpfThread(Func<Task> test)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(new Action(async () =>
            {
                try { await test(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            }));
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
}
