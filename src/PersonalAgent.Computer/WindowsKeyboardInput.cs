namespace PersonalAgent.Computer;

// Internal driver only; trusted caller owns foreground, permission, lease generation and sensitive-field policy.
internal sealed class WindowsKeyboardInput
{
    internal const int MaxTextCodeUnits = 4096;
    internal const int BatchCodeUnits = 16;
    private readonly WindowsInputSender sender;

    internal WindowsKeyboardInput(Func<(bool Allowed, long Generation)>? authorize = null)
        : this(authorize, new WindowsInputNative()) { }
    internal WindowsKeyboardInput(Func<(bool Allowed, long Generation)>? authorize, IWindowsInputNative native)
    { sender = new(authorize, native); }

    internal Task TypeAsync(string text, long generation, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > MaxTextCodeUnits) throw new ArgumentOutOfRangeException(nameof(text));
        if (generation < 0) throw new ArgumentOutOfRangeException(nameof(generation));
        ct.ThrowIfCancellationRequested();
        // Validate the entire payload before any effect. Never repair malformed UTF-16 silently.
        for (var i = 0; i < text.Length; i++)
        {
            var value = text[i];
            if (char.IsHighSurrogate(value))
            {
                if (++i >= text.Length || !char.IsLowSurrogate(text[i])) throw new ArgumentException("Text requires well-formed UTF-16.", nameof(text));
            }
            else if (char.IsLowSurrogate(value) || (char.IsControl(value) && value is not ('\r' or '\n' or '\t')))
                throw new ArgumentException("Text contains unsupported control data or malformed UTF-16.", nameof(text));
        }
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var batches = new List<NativeInput[]>();
        for (var offset = 0; offset < normalized.Length;)
        {
            var length = Math.Min(BatchCodeUnits, normalized.Length - offset);
            // Keep both UTF-16 halves in the same atomic send, including at the 16-unit boundary.
            if (char.IsHighSurrogate(normalized[offset + length - 1])) length--;
            var batch = new List<NativeInput>(length * 2);
            foreach (var value in normalized.AsSpan(offset, length))
            {
                // Enter produces multiline text in normal edit controls. Tab remains text (VK_PACKET),
                // never a focus-changing VK_TAB shortcut. No clipboard, focus changes or modifier keys.
                batch.Add(value == '\n' ? NativeInput.Key(0x0d, false) : NativeInput.Unicode(value, false));
                batch.Add(value == '\n' ? NativeInput.Key(0x0d, true) : NativeInput.Unicode(value, true));
            }
            batches.Add(batch.ToArray());
            offset += length;
        }
        return batches.Count == 0 ? Task.CompletedTask : sender.SendBatchesAsync(batches, generation, ct);
    }
}
