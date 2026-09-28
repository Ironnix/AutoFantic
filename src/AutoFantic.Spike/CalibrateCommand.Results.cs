using AutoFantic.Core.Calibration;
using AutoFantic.Core.Reports;

namespace AutoFantic.Spike;

/// <summary>
/// The second half of the calibration: from every stored measurement to the result. Needs no
/// hardware, so it can run again at any time, e.g. after another calibration or for another preset.
/// </summary>
internal static partial class CalibrateCommand
{
    /// <summary>"recalculate": the result again from the stored measurements (no admin rights needed).</summary>
    public static int RecalculateCommand(string[] args)
    {
        var options = new Options(args);
        var runs = new RunsFolder(options.Get("--runs") ?? RunsFolder.Default());
        var inventory = runs.Inventory();
        if (inventory is null)
        {
            Console.WriteLine("No fans.json yet: run \"Find my fans\" first.");
            return 2;
        }

        var groups = inventory.Groups().ToList();
        var store = CalibrationFiles.LoadStore(runs.Path, groups, Console.WriteLine);
        if (store.Calibrations.Count == 0)
        {
            Console.WriteLine("No measurements yet: run a calibration first.");
            return 2;
        }

        var previous = CalibrationResult.Load(runs.File(CalibrationFiles.Result));
        var preset = Preset.For(options.Get("--profile") ?? options.Get("--preset") ?? previous?.Profile);
        double ambient = options.GetDouble("--ambient", store.Calibrations[^1].Ambient);
        var done = CalibrationFiles.Recalculate(runs.Path, inventory, store, preset, ambient, FansOffResult.Load(runs.File(CalibrationFiles.FansOff)));
        if (done is not { } result)
        {
            Console.WriteLine("Not enough stored runs match your fans to work out curves.");
            return 1;
        }

        Console.Write(result.Report);
        Console.WriteLine($"Saved: the report, calibration.json and the page {runs.File(CalibrationFiles.Page)}");
        return 0;
    }
}
