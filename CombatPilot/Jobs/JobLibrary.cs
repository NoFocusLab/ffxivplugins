using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;

namespace CombatPilot.Jobs;

public static class JobLibrary
{
    private static List<JobDef>? all;

    public static IReadOnlyList<JobDef> All => all ??= Load();

    public static JobDef? For(string abbreviation) =>
        All.FirstOrDefault(j => j.ClassJobs.Contains(abbreviation, StringComparer.OrdinalIgnoreCase));

    private static List<JobDef> Load()
    {
        var list = new List<JobDef>();
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("CombatPilot.Jobs.") && n.EndsWith(".json")))
        {
            try
            {
                using var stream = asm.GetManifestResourceStream(name)!;
                using var reader = new StreamReader(stream);
                if (JsonConvert.DeserializeObject<JobDef>(reader.ReadToEnd()) is { } def) list.Add(def);
            }
            catch (Exception ex)
            {
                Plugin.Log.Error(ex, $"Combat Pilot: couldn't read job file {name}");
            }
        }
        return list;
    }
}
