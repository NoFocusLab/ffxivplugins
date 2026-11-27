using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.Game;
using Lumina.Excel.Sheets;

namespace CombatPilot.Engine;

/// <summary>A stat potion you have: item id (NQ), name, item level, and how many NQ/HQ.</summary>
public sealed record OwnedPotion(uint ItemId, string Name, int ItemLevel, int Nq, int Hq)
{
    /// <summary>The id to use: HQ (id + 1,000,000) when you have one.</summary>
    public uint UseId => Hq > 0 ? ItemId + 1_000_000 : ItemId;
    public string Label => $"{Name} (ilvl {ItemLevel}, {(Hq > 0 ? $"{Hq} HQ" : "")}{(Hq > 0 && Nq > 0 ? ", " : "")}{(Nq > 0 ? $"{Nq} NQ" : "")})";
}

/// <summary>
/// Finds stat potions in your inventory: Medicine items named "... of [main stat]" (tinctures,
/// gemdraughts). [Assumption] English item names; the Medicine category is ItemUICategory 44.
/// </summary>
public static unsafe class Potions
{
    private const uint MedicineCategory = 44;
    private static List<(uint Id, string Name, int Ilvl, string Stat)>? all;

    private static List<(uint, string, int, string)> All()
    {
        if (all != null) return all;
        all = new();
        foreach (var row in Plugin.DataManager.GetExcelSheet<Item>())
        {
            if (row.ItemUICategory.RowId != MedicineCategory) continue;
            var name = row.Name.ExtractText();
            var i = name.IndexOf(" of ", System.StringComparison.Ordinal);
            if (i < 0 || !(name.Contains("Tincture") || name.Contains("Gemdraught") || name.Contains("Draught"))) continue;
            all.Add((row.RowId, name, (int)row.LevelItem.RowId, name[(i + 4)..]));
        }
        return all;
    }

    public static List<OwnedPotion> Owned(string mainStat)
    {
        var im = InventoryManager.Instance();
        var list = new List<OwnedPotion>();
        if (im == null) return list;
        foreach (var (id, name, ilvl, stat) in All())
        {
            if (!stat.Equals(mainStat, System.StringComparison.OrdinalIgnoreCase)) continue;
            var nq = im->GetInventoryItemCount(id, false, false, false);
            var hq = im->GetInventoryItemCount(id, true, false, false);
            if (nq + hq > 0) list.Add(new OwnedPotion(id, name, ilvl, nq, hq));
        }
        return list.OrderByDescending(p => p.ItemLevel).ToList();
    }

    /// <summary>The potion to use under the current setting, or null.</summary>
    public static OwnedPotion? Pick(Configuration c, string mainStat)
    {
        var owned = Owned(mainStat);
        if (c.Potion == PotionChoice.Specific) return owned.FirstOrDefault(p => p.ItemId == c.PotionItemId);
        return owned.FirstOrDefault();
    }
}
