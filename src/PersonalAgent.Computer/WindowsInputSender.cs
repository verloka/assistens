namespace PersonalAgent.Computer;

// Internal bounded atomic driver primitive; not a tool/DI registration or a substitute for DesktopLease.
internal sealed class WindowsInputSender
{
    private static readonly SemaphoreSlim InputLock = new(1, 1);
    private readonly Func<(bool Allowed, long Generation)> authorize;
    private readonly IWindowsInputNative native;
    private bool faulted;

    internal WindowsInputSender(Func<(bool Allowed, long Generation)>? authorize = null)
        : this(authorize, new WindowsInputNative()) { }
    internal WindowsInputSender(Func<(bool Allowed, long Generation)>? authorize, IWindowsInputNative native)
    { this.authorize = authorize ?? (() => (false, 0)); this.native = native; }

    internal Task SendAsync(NativeInput[] inputs, long generation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var batch = inputs.ToArray();
        Validate(batch);
        return SendAsync(() => batch, generation, ct);
    }

    // Prepare geometry-dependent input inside the same serialized, authorized desktop boundary.
    internal Task SendAsync(Func<NativeInput[]> prepare, long generation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        return SendSequenceAsync([prepare], generation, ct);
    }

    internal Task SendBatchesAsync(IReadOnlyList<NativeInput[]> batches, long generation, CancellationToken ct = default)
    {
        // Own each batch before waiting. The same global lock covers the entire logical text operation,
        // while SendCore rechecks permission/session/cancellation at every atomic batch boundary.
        var owned = batches.Select(batch =>
        {
            var copy = batch.ToArray();
            Validate(copy);
            return (Func<NativeInput[]>)(() => copy);
        }).ToArray();
        return SendSequenceAsync(owned, generation, ct);
    }

    private async Task SendSequenceAsync(IReadOnlyList<Func<NativeInput[]>> batches, long generation, CancellationToken ct)
    {
        if (generation < 0) throw new ArgumentOutOfRangeException(nameof(generation));
        ct.ThrowIfCancellationRequested();
        await InputLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await Task.Run(() =>
            {
                foreach (var batch in batches) SendCore(batch, generation, ct);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally { InputLock.Release(); }
    }

    private void SendCore(Func<NativeInput[]> prepare, long generation, CancellationToken ct)
    {
        NativeInput[] batch = [];
        ct.ThrowIfCancellationRequested();
        if (faulted) throw new ComputerInputException("InputStateUnknown", batch.Length, null, 0, false);
        try
        {
            CheckAuthorization(generation);
            if (!native.IsDesktopAvailable()) throw new ComputerInputException("DesktopUnavailable", batch.Length, 0, 0, true);
            batch = prepare();
            Validate(batch);
            if (!native.IsDesktopAvailable()) throw new ComputerInputException("DesktopUnavailable", batch.Length, 0, 0, true);
            // Session checks can take time. Recheck policy/generation and cancellation immediately before input.
            CheckAuthorization(generation);
            ct.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) { throw; }
        catch (ComputerInputException) { throw; }
        catch (Exception error) { throw new ComputerInputException("InputPreflightFailed", batch.Length, 0, 0, true, error); }
        InputInsertion result;
        try { result = native.Send(batch); }
        catch (Exception error)
        {
            faulted = true;
            // Unknown prefix: release only keys/buttons this validated batch could have pressed.
            throw new ComputerInputException("InputOutcomeUnknown", batch.Length, null, 0, Cleanup(AllReleases(batch)), error);
        }
        if (result.Count == batch.Length) return; // Native completion is atomic with respect to cancellation.
        faulted = true; // Caller must inspect/recreate context; never silently repeat a write.
        var releases = result.Count <= batch.Length ? PendingReleases(batch.Take((int)result.Count)) : AllReleases(batch);
        var cleaned = Cleanup(releases);
        throw new ComputerInputException(result.Count == 0 ? "InputFailed" : "InputOutcomeUnknown",
            batch.Length, result.Count, result.NativeError, cleaned);
    }

    private void CheckAuthorization(long generation)
    {
        var permission = authorize();
        if (!permission.Allowed) throw new ComputerInputException("InputDenied", 0, 0, 0, true);
        if (permission.Generation != generation) throw new ComputerInputException("StaleDesktopContext", 0, 0, 0, true);
    }

    private bool Cleanup(NativeInput[] releases)
    {
        if (releases.Length == 0) return true;
        try
        {
            // Cleanup may outlive cancellation/revocation; never send to a locked/secure desktop.
            return native.IsDesktopAvailable() && native.Send(releases).Count == releases.Length;
        }
        catch { return false; }
    }

    private static void Validate(NativeInput[] batch)
    {
        if (batch.Length is < 1 or > 64) throw new ArgumentOutOfRangeException(nameof(batch));
        var held = new List<NativeInput>();
        foreach (var input in batch)
        {
            if (input.Type == 1)
            {
                var key = input.Data.Keyboard;
                if ((key.Flags & ~7u) != 0 || key.Time != 0 || key.Extra != 0 ||
                    ((key.Flags & 4) != 0 ? key.VirtualKey != 0 : key.VirtualKey is < 1 or > 254 || key.Scan != 0))
                    throw new ArgumentException("Unsupported keyboard input.");
            }
            else if (input.Type == 0)
            {
                var mouse = input.Data.Mouse;
                if (mouse.Time != 0 || mouse.Extra != 0 ||
                    mouse.Flags is not (1 or 2 or 4 or 8 or 16 or 32 or 64 or 0x800 or 0x1000 or 0xc001) ||
                    (mouse.Flags is not (0x800 or 0x1000) && mouse.Data != 0))
                    throw new ArgumentException("Unsupported mouse input.");
            }
            else throw new ArgumentException("Unsupported input type.");
            Track(input, held);
        }
        if (held.Count != 0) throw new ArgumentException("Atomic input batch must balance all key/button presses.");
    }

    private static NativeInput[] PendingReleases(IEnumerable<NativeInput> inputs)
    {
        var held = new List<NativeInput>();
        foreach (var input in inputs) Track(input, held);
        held.Reverse();
        return held.ToArray();
    }
    private static NativeInput[] AllReleases(IEnumerable<NativeInput> inputs) => inputs
        .Where(input => IsPress(input)).Select(Release).Reverse().Distinct().ToArray();
    private static bool IsPress(NativeInput input) => input.Type == 1 ? (input.Data.Keyboard.Flags & 2) == 0 : input.Data.Mouse.Flags is 2 or 8 or 32;
    private static NativeInput Release(NativeInput input)
    {
        if (input.Type == 1) input.Data.Keyboard.Flags |= 2;
        else input.Data.Mouse.Flags <<= 1;
        return input;
    }
    private static void Track(NativeInput input, List<NativeInput> held)
    {
        if (IsPress(input))
        {
            var release = Release(input);
            if (held.Contains(release)) throw new ArgumentException("Repeated held input is not supported.");
            held.Add(release);
        }
        else if (input.Type == 1 || input.Data.Mouse.Flags is 4 or 16 or 64)
        {
            if (!held.Remove(input)) throw new ArgumentException("Input release has no matching press.");
        }
    }
}

public sealed class ComputerInputException(string code, int requested, uint? inserted, int nativeError,
    bool cleanupSucceeded, Exception? innerException = null)
    : Exception($"Windows input stopped: {code}; inserted={inserted?.ToString() ?? "unknown"}/{requested}; cleanup={cleanupSucceeded}. No action retry.", innerException)
{
    public string Code { get; } = code;
    public int Requested { get; } = requested;
    public uint? Inserted { get; } = inserted;
    public int NativeError { get; } = nativeError;
    public bool CleanupSucceeded { get; } = cleanupSucceeded;
}
