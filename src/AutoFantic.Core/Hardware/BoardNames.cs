namespace AutoFantic.Core.Hardware;

/// <summary>
/// What a mainboard's fan headers are called on the board itself. The library names a fan chip's
/// outputs from one list per chip, written for the boards it knows (the NCT6686D's is MSI's: "CPU Fan",
/// "Pump Fan", "System Fan #1" …). On a board it doesn't know that list is wrong: ASRock's second
/// CPU fan header shows up as "Pump Fan". Nothing in the PC says which output goes to which header;
/// only the board's make and model can be read. So the boards whose headers are known are listed
/// here, each with the outputs that were checked on a real one. An output that isn't listed keeps
/// the library's name.
/// </summary>
public static class BoardNames
{
    /// <param name="Name">Make and model as <see cref="Describe"/> gives them.</param>
    /// <param name="Chip">The fan chip in the sensor ids, e.g. "nct6686d".</param>
    /// <param name="Headers">The chip's output number → the header's name.</param>
    private sealed record Board(string Name, string Chip, IReadOnlyDictionary<int, string> Headers);

    private static readonly Board[] Known =
    [
        // CPU_FAN1 and CPU_FAN2 with one fan of the CPU cooler each, AIO_PUMP empty: outputs 0 and 1 turned (01.10.2026)
        new("ASRock X870 Steel Legend WiFi", "nct6686d", new Dictionary<int, string> { [0] = "CPU Fan 1", [1] = "CPU Fan 2" }),
    ];

    /// <summary>Make and model as one name ("ASRock X870 Steel Legend WiFi") from what the BIOS says about the board; null if it says nothing.</summary>
    public static string? Describe(string? maker, string? model)
    {
        (maker, model) = (maker?.Trim() ?? "", model?.Trim() ?? "");
        if (model.Length == 0)
            return maker.Length == 0 ? null : maker;
        return maker.Length == 0 || model.StartsWith(maker, StringComparison.OrdinalIgnoreCase) ? model : $"{maker} {model}";
    }

    /// <summary>
    /// The name of a fan or fan control sensor of the mainboard's fan chip on this board
    /// ("/lpc/nct6686d/0/control/1" → "CPU Fan 2"); <paramref name="name"/> as it is for every
    /// other sensor and on a board that isn't known.
    /// </summary>
    public static string For(string? board, string sensorId, string name)
    {
        if (board is null)
            return name;

        // "/lpc/<chip>/<number>/<fan or control>/<output>"
        string[] parts = sensorId.Split('/');
        if (parts.Length != 6 || parts[1] != "lpc" || parts[4] is not ("fan" or "control") || !int.TryParse(parts[5], out int output))
            return name;

        var known = Known.FirstOrDefault(b => b.Chip == parts[2] && b.Name.Equals(board, StringComparison.OrdinalIgnoreCase));
        return known?.Headers.GetValueOrDefault(output) ?? name;
    }
}
