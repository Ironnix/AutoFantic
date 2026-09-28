namespace AutoFanatic.Spike;

/// <summary>
/// Ctrl+C never kills the tool outright: it cancels the running command, which then hands its
/// fans back to the BIOS itself. The test menu starts each step with a fresh token, so Ctrl+C
/// ends that step and returns to the menu.
/// </summary>
internal static class CtrlC
{
    private static CancellationTokenSource _current = new();

    public static CancellationToken Token => _current.Token;

    public static void Install() =>
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            _current.Cancel();
        };

    /// <summary>A token for the next step; fresh if Ctrl+C ended the previous one.</summary>
    public static CancellationToken Reset()
    {
        if (_current.IsCancellationRequested)
            _current = new CancellationTokenSource();
        return _current.Token;
    }
}
