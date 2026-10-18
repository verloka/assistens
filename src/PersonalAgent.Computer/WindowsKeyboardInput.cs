using PersonalAgent.Core.Computer;

namespace PersonalAgent.Computer;

// Internal driver only; trusted caller owns foreground, permission, lease generation and sensitive-field policy.
internal sealed class WindowsKeyboardInput
{
    internal const int MaxTextCodeUnits = 4096;
    internal const int BatchCodeUnits = 16;
    private readonly WindowsInputSender sender;
    private readonly IWindowsInputNative native;
    private readonly Func<string, bool> allowChord;

    internal WindowsKeyboardInput(Func<(bool Allowed, long Generation)>? authorize = null)
        : this(authorize, new WindowsInputNative()) { }
    internal WindowsKeyboardInput(Func<(bool Allowed, long Generation)>? authorize, IWindowsInputNative native,
        Func<string, bool>? allowChord = null)
    { sender = new(authorize, native); this.native = native; this.allowChord = allowChord ?? (_ => false); }

    internal Task PressKeyAsync(KeyCombination keys, long generation, CancellationToken ct = default)
    {
        var (canonical, inputs) = ParseChord(keys);
        return sender.SendAsync(() =>
        {
            if (!allowChord(canonical)) throw new ComputerInputException("KeyChordDenied", 0, 0, 0, true);
            if (native is not IWindowsKeyboardNative keyboard) throw new ComputerInputException("KeyStateUnavailable", 0, 0, 0, true);
            // Never release a user's already-held key as part of our chord cleanup.
            ushort[] modifiers = [0x10, 0x11, 0x12, 0x5b, 0x5c];
            if (modifiers.Concat(inputs.Select(input => input.Data.Keyboard.VirtualKey)).Distinct().Any(keyboard.IsKeyDown))
                throw new ComputerInputException("UserInputActive", 0, 0, 0, true);
            return inputs;
        }, generation, ct);
    }

    private static (string Canonical, NativeInput[] Inputs) ParseChord(KeyCombination keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentException.ThrowIfNullOrWhiteSpace(keys.Chord);
        var parts = keys.Chord.ToUpperInvariant().Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length is < 1 or > 4 || parts.Any(string.IsNullOrEmpty)) throw new ArgumentException("Invalid key combination.");
        (string Name, string Display, ushort Key)[] modifiers = [("CTRL", "Ctrl", 0x11), ("ALT", "Alt", 0x12), ("SHIFT", "Shift", 0x10)];
        var requested = parts[..^1];
        if (requested.Distinct().Count() != requested.Length || requested.Any(part => !modifiers.Any(modifier => modifier.Name == part)))
            throw new ArgumentException("Use distinct Ctrl/Alt/Shift modifiers followed by one supported key.");
        var name = parts[^1];
        (ushort Key, bool Extended, string Name) main;
        if (name.Length == 1 && name[0] is >= 'A' and <= 'Z' or >= '0' and <= '9') main = (name[0], false, name);
        else if (name.StartsWith('F') && int.TryParse(name.AsSpan(1), out var function) && function is >= 1 and <= 12)
            main = ((ushort)(0x6f + function), false, $"F{function}");
        else main = name switch
        {
            "ESC" or "ESCAPE" => (0x1b, false, "Escape"), "ENTER" => (0x0d, false, "Enter"), "TAB" => (0x09, false, "Tab"),
            "SPACE" => (0x20, false, "Space"), "BACKSPACE" => (0x08, false, "Backspace"),
            "PAGEUP" => (0x21, true, "PageUp"), "PAGEDOWN" => (0x22, true, "PageDown"),
            "END" => (0x23, true, "End"), "HOME" => (0x24, true, "Home"),
            "LEFT" => (0x25, true, "Left"), "UP" => (0x26, true, "Up"), "RIGHT" => (0x27, true, "Right"), "DOWN" => (0x28, true, "Down"),
            "INSERT" => (0x2d, true, "Insert"), "DELETE" => (0x2e, true, "Delete"),
            _ => throw new ArgumentException("Unsupported key name.")
        };
        var ordered = modifiers.Where(modifier => requested.Contains(modifier.Name)).ToArray();
        var canonical = string.Join('+', ordered.Select(modifier => modifier.Display).Append(main.Name));
        var inputs = ordered.Select(modifier => NativeInput.Key(modifier.Key, false))
            .Append(NativeInput.Key(main.Key, false, main.Extended)).Append(NativeInput.Key(main.Key, true, main.Extended))
            .Concat(ordered.Reverse().Select(modifier => NativeInput.Key(modifier.Key, true))).ToArray();
        return (canonical, inputs);
    }

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
            if (normalized[offset] is '\n' or '\t')
            {
                // Edit controls filter Unicode control packets. Use real Enter/Tab pairs instead.
                // Keep each separate so trusted target/focus policy is checked before more text.
                var key = normalized[offset++] == '\n' ? (ushort)0x0d : (ushort)0x09;
                batches.Add([NativeInput.Key(key, false), NativeInput.Key(key, true)]);
                continue;
            }
            var length = Math.Min(BatchCodeUnits, normalized.Length - offset);
            var control = normalized.AsSpan(offset, length).IndexOfAny('\n', '\t');
            if (control >= 0) length = control;
            // Keep both UTF-16 halves in the same atomic send, including at the 16-unit boundary.
            if (char.IsHighSurrogate(normalized[offset + length - 1])) length--;
            var batch = new List<NativeInput>(length * 2);
            foreach (var value in normalized.AsSpan(offset, length))
            {
                batch.Add(NativeInput.Unicode(value, false));
                batch.Add(NativeInput.Unicode(value, true));
            }
            batches.Add(batch.ToArray());
            offset += length;
        }
        return batches.Count == 0 ? Task.CompletedTask : sender.SendBatchesAsync(batches, generation, ct);
    }
}
