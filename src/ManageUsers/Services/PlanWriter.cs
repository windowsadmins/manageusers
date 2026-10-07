using ManageUsers.Models;

namespace ManageUsers.Services;

/// <summary>
/// Writes a simulation's plan to <see cref="AppConstants.SimulationPlanFile"/>, in the
/// locked data folder, through a new file moved into place so a link at the path is
/// replaced rather than written through.
/// </summary>
public static class PlanWriter
{
    public static void Write(LogService log, IEnumerable<PlanItem> items)
    {
        var path = AppConstants.SimulationPlanFile;
        var dir = Path.GetDirectoryName(path)!;
        var temp = Path.Combine(dir, $".simulation.{Guid.NewGuid():N}.tmp");
        try
        {
            foreach (var note in SafeLogFile.RemoveLinks(dir, path))
                log.Warning(note);
            Directory.CreateDirectory(dir);
            var plan = new SimulationPlan { Generated = DateTimeOffset.Now, Items = items.ToList() };
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream))
                writer.Write(plan.ToJson());
            File.Move(temp, path, overwrite: true);
            log.Info($"Simulation plan: {plan.Items.Count} item(s) written to {path}");
        }
        catch (Exception ex)
        {
            log.Warning($"Could not write the simulation plan: {ex.Message}");
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }
}
