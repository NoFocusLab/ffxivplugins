using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI;
using Lumina.Excel.Sheets;

namespace MaterialStash;

/// <summary>
/// Money ventures: when nothing on the stock list fits a retainer, send it for whatever it can bring
/// back that's worth the most on your home world, among items that sell at least N ventures' worth a
/// day (N adjustable; optionally counting what other retainers are already fetching and what you hold
/// to sell). What comes back goes on the to-sell list: it stays in your bags, a sound plays, chat lists
/// it, and the bag slots are boxed until it's gone (sold, or listed by auto-sell).
/// </summary>
public sealed class Selling : IDisposable
{
    public sealed record Pick(VentureTask Task, int Yield, long UnitPrice, long Value, double UnitsPerDay, double Demand);

    private const double MarketTax = 0.95;

    private readonly Configuration config;
    private readonly Tracker tracker;
    private readonly Market market;
    private readonly Director director;
    private DateTime nextPrefetch = DateTime.MinValue, nextBagCheck = DateTime.MinValue;
    private Dictionary<uint, int> lastInBags = new();
    private bool primed;

    public Selling(Configuration config, Tracker tracker, Market market, Director director)
    {
        this.config = config;
        this.tracker = tracker;
        this.market = market;
        this.director = director;
        Plugin.Framework.Update += OnUpdate;
    }

    public void Dispose() => Plugin.Framework.Update -= OnUpdate;

    public static string HomeWorld()
    {
        try { return Plugin.ObjectTable.LocalPlayer?.HomeWorld.Value.Name.ExtractText() ?? ""; }
        catch (Exception) { return ""; }
    }

    private static bool Marketable(uint itemId) =>
        Plugin.DataManager.GetExcelSheet<Item>().GetRowOrDefault(itemId % 1_000_000) is { } r && r.ItemSearchCategory.RowId != 0 && !r.IsUntradable;

    /// <summary>Every venture item at least one of your retainers can bring back (for prices).</summary>
    public HashSet<uint> CandidateItems()
    {
        var set = new HashSet<uint>();
        foreach (var r in Director.Retainers())
        {
            var cat = Ventures.CategoryFor(r.ClassJob);
            foreach (var t in Ventures.AllTasks())
                if (t.Category == cat && t.RetainerLevel <= r.Level && Marketable(t.ItemId)) set.Add(t.ItemId);
        }
        return set;
    }

    /// <summary>The best money venture for this retainer, or null (leave it to AutoRetainer).</summary>
    public Pick? Choose(Director.RetainerInfo r, ArRetainerStats stats, Dictionary<uint, int>? incoming = null)
    {
        var me = tracker.Me();
        if (me == null || !config.MoneyVentures) return null;
        incoming ??= director.Incoming();
        Pick? best = null;
        var cat = Ventures.CategoryFor(r.ClassJob);
        foreach (var t in Ventures.AllTasks())
        {
            if (t.Category != cat || Director.CantTake(t, r, stats) != null) continue;
            if (market.Get(t.ItemId) is not { } info || info.Price <= 0) continue;
            var y = Ventures.Yield(t, r.ClassJob, stats.Ilvl, stats.Perception);
            if (y <= 0) continue;
            var value = (long)(y * info.Price * MarketTax);
            if (value < config.MinVentureValue) continue;
            // Demand: the item must sell N ventures' worth a day (plus, combined, what's coming and what's held).
            double load = y;
            if (config.DemandCombined)
                load += incoming.GetValueOrDefault(t.ItemId) + Tracker.Stacks(Tracker.Bags, id => id % 1_000_000 == t.ItemId).Sum(s => s.Qty)
                        + WaitingOnRetainers(me, t.ItemId) + OurListed(me, t.ItemId, info);
            var demand = config.DemandFactor * load;
            if (info.UnitsPerDay < demand) continue;
            if (best == null || value > best.Value) best = new Pick(t, y, info.Price, value, info.UnitsPerDay, demand);
        }
        return best;
    }

    /// <summary>
    /// A money venture was chosen: its item goes on the to-sell list, and we remember which retainer
    /// will have it (venture results land in the retainer's inventory, not your bags).
    /// </summary>
    public void Queue(uint itemId, ulong retainerId = 0)
    {
        var me = tracker.Me();
        if (me == null) return;
        var id = itemId % 1_000_000;
        me.SellQueue.Add(id);
        if (retainerId != 0)
        {
            if (!me.SellAt.TryGetValue(id, out var set)) me.SellAt[id] = set = new();
            set.Add(retainerId);
        }
        tracker.Save(force: true);
    }

    /// <summary>To-sell items of this kind sitting on retainers, as last recorded.</summary>
    private static int WaitingOnRetainers(CharacterData me, uint itemId)
    {
        if (!me.SellAt.TryGetValue(itemId, out var at)) return 0;
        var n = 0;
        foreach (var r in at)
            if (me.Retainers.TryGetValue(r, out var snap)) n += snap.Items.GetValueOrDefault(itemId);
        return n;
    }

    /// <summary>
    /// How many we already have on the market: Universalis's listings under your retainers' names, or
    /// what we listed in the last day if Universalis hasn't caught up yet (the larger).
    /// </summary>
    private static int OurListed(CharacterData me, uint itemId, Market.Info info)
    {
        var recent = 0;
        if (me.Listed.TryGetValue(itemId, out var lots))
        {
            lots.RemoveAll(l => (DateTime.UtcNow - l.At).TotalDays > 3);
            recent = lots.Where(l => (DateTime.UtcNow - l.At).TotalHours < 24).Sum(l => l.Qty);
        }
        return Math.Max(info.OurListed, recent);
    }

    /// <summary>A listing was made (counts against demand until Universalis shows it).</summary>
    public void Listed(uint itemId, int qty)
    {
        var me = tracker.Me();
        if (me == null) return;
        var id = itemId % 1_000_000;
        if (!me.Listed.TryGetValue(id, out var lots)) me.Listed[id] = lots = new();
        lots.Add(new ListedLot { Qty = qty, At = DateTime.UtcNow });
        tracker.Save(force: true);
    }

    /// <summary>Retainers holding to-sell items (as far as we know).</summary>
    public HashSet<ulong> Holders()
    {
        var me = tracker.Me();
        var set = new HashSet<ulong>();
        if (me == null) return set;
        foreach (var (item, rs) in me.SellAt)
            if (me.SellQueue.Contains(item)) set.UnionWith(rs);
        return set;
    }

    /// <summary>This retainer has no to-sell items left (it was opened and they've been taken out).</summary>
    public void ClearHolder(ulong retainerId)
    {
        var me = tracker.Me();
        if (me == null) return;
        var changed = false;
        foreach (var set in me.SellAt.Values) changed |= set.Remove(retainerId);
        if (me.SellAt.Values.Any(v => v.Count == 0)) { foreach (var k in me.SellAt.Where(kv => kv.Value.Count == 0).Select(kv => kv.Key).ToList()) me.SellAt.Remove(k); changed = true; }
        if (changed) tracker.Save(force: true);
    }

    /// <summary>Anything to sell, in your bags or waiting on a retainer.</summary>
    public bool AnythingToSell() => InBags().Count > 0 || Holders().Count > 0;

    /// <summary>Queued items in your bags now: item -> quantity.</summary>
    public Dictionary<uint, int> InBags()
    {
        var me = tracker.Me();
        var result = new Dictionary<uint, int>();
        if (me == null || me.SellQueue.Count == 0) return result;
        foreach (var s in Tracker.Stacks(Tracker.Bags, id => me.SellQueue.Contains(id % 1_000_000)))
            result[s.ItemId % 1_000_000] = result.GetValueOrDefault(s.ItemId % 1_000_000) + s.Qty;
        return result;
    }

    /// <summary>Price to list at: lowest home-world listing minus the undercut, else the median sale.</summary>
    public long ListPrice(uint itemId)
    {
        if (market.Get(itemId) is not { } i) return 0;
        if (i.Lowest > 0) return Math.Max(1, i.Lowest - config.Undercut);
        return i.Median;
    }

    private void OnUpdate(IFramework framework)
    {
        try
        {
            if (Tracker.ContentId() == 0) return;
            if (DateTime.UtcNow >= nextPrefetch)
            {
                nextPrefetch = DateTime.UtcNow.AddMinutes(5);
                if (config.MoneyVentures || config.AutoSell)
                {
                    var ids = CandidateItems();
                    if (tracker.Me() is { } me)
                    {
                        ids.UnionWith(me.SellQueue);
                        market.OurRetainers = me.Retainers.Values.Select(r => r.Name).Where(n => n.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    }
                    market.Request(ids, HomeWorld(), Math.Max(10, config.PriceRefreshMinutes));
                }
            }
            if (DateTime.UtcNow >= nextBagCheck)
            {
                nextBagCheck = DateTime.UtcNow.AddSeconds(2);
                CheckBags();
            }
        }
        catch (Exception ex) { Plugin.Log.Warning(ex, "Material Stash: selling update failed"); }
    }

    /// <summary>New to-sell items in the bags: sound and a chat list. Also drops items no longer coming or held.</summary>
    private void CheckBags()
    {
        var me = tracker.Me();
        if (me == null) return;
        var now = InBags();
        var arrived = now.Where(kv => kv.Value > lastInBags.GetValueOrDefault(kv.Key)).ToList();
        if (arrived.Count > 0 && primed)
        {
            if (config.SellSound) PlaySound(config.SellSoundId);
            var lines = now.Select(kv =>
            {
                var price = market.Get(kv.Key)?.Price ?? 0;
                return $"{Items.Info(kv.Key)?.Name} x{kv.Value}" + (price > 0 ? $" (~{kv.Value * price * MarketTax:N0} gil)" : "");
            });
            Plugin.ChatGui.Print($"[Stash] To sell (kept in your bags, boxed): {string.Join(", ", lines)}");
        }
        lastInBags = now;
        primed = true;

        // Off the list once it's nowhere: not in your bags, not on its way from a venture, and not
        // waiting on a retainer (this used to forget items as soon as the venture was collected,
        // since the results land on the retainer, not in your bags).
        var coming = director.Incoming().Keys.Select(k => k % 1_000_000).ToHashSet();
        if (me.SellQueue.RemoveWhere(id => !now.ContainsKey(id) && !coming.Contains(id) &&
                                           !(me.SellAt.TryGetValue(id, out var at) && at.Count > 0)) > 0) tracker.Save(force: true);
    }

    private static unsafe void PlaySound(int id)
    {
        try { UIGlobals.PlayChatSoundEffect((uint)Math.Clamp(id, 1, 16)); }
        catch (Exception ex) { Plugin.Log.Debug(ex, "sound failed"); }
    }
}
