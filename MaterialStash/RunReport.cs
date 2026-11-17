using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace MaterialStash;

/// <summary>
/// What one bell run did, per retainer: items put into / taken out of each retainer (measured from the
/// retainer's own inventory before and after, so it shows what really moved), and market listings with
/// their price. Shown in the results window when the run ends.
/// </summary>
public sealed class RunReport
{
    /// <summary>ItemId includes the HQ offset (1,000,000). Qty &gt; 0 put in, &lt; 0 taken out.</summary>
    public sealed record Move(string Retainer, uint ItemId, int Qty);

    public sealed record Listing(string Retainer, uint ItemId, int Qty, long UnitPrice);

    public string Title { get; init; } = "";
    public DateTime Started { get; init; } = DateTime.Now;
    public DateTime Ended { get; set; } = DateTime.Now;
    public bool DuringAutoRetainer { get; init; }
    public string Summary { get; set; } = "";
    public List<Move> Moves { get; } = new();
    public List<Listing> Listings { get; } = new();

    public bool Empty => Moves.Count == 0 && Listings.Count == 0;

    public static string ItemName(uint itemId) =>
        (Items.Info(itemId)?.Name ?? $"#{itemId % 1_000_000}") + (itemId >= 1_000_000 ? " (HQ)" : "");

    /// <summary>Record the difference between a retainer's contents before and after.</summary>
    public void AddDiff(string retainer, IReadOnlyDictionary<uint, int> before, IReadOnlyDictionary<uint, int> after)
    {
        foreach (var id in before.Keys.Union(after.Keys))
        {
            var d = after.GetValueOrDefault(id) - before.GetValueOrDefault(id);
            if (d == 0) continue;
            // Several visits to one retainer (tidy laps) add up to one line per item.
            var i = Moves.FindIndex(m => m.Retainer == retainer && m.ItemId == id);
            if (i < 0) Moves.Add(new Move(retainer, id, d));
            else if (Moves[i].Qty + d == 0) Moves.RemoveAt(i);
            else Moves[i] = Moves[i] with { Qty = Moves[i].Qty + d };
        }
    }

    /// <summary>Plain-text version, for copying.</summary>
    public string ToText()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{Title} - {Ended:g}");
        if (Summary.Length > 0) sb.AppendLine(Summary);
        foreach (var g in Moves.GroupBy(m => m.Retainer))
        {
            sb.AppendLine();
            sb.AppendLine(g.Key);
            foreach (var m in g.OrderByDescending(m => m.Qty > 0).ThenBy(m => ItemName(m.ItemId)))
                sb.AppendLine($"  {(m.Qty > 0 ? "in " : "out")} {Math.Abs(m.Qty),6:N0}  {ItemName(m.ItemId)}");
        }
        if (Listings.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("Listed on the market");
            foreach (var l in Listings)
                sb.AppendLine($"  {l.Retainer}: {l.Qty:N0} x {ItemName(l.ItemId)} at {l.UnitPrice:N0} = {l.Qty * l.UnitPrice:N0} gil");
            sb.AppendLine($"  Total {Listings.Sum(l => l.Qty * l.UnitPrice):N0} gil (before the market tax)");
        }
        return sb.ToString();
    }
}
