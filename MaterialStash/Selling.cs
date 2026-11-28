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

    /// <summary>Clean-up approved this item for the market: everything of it in your bags gets listed.</summary>
    public void Queue(uint itemId)
    {
        var me = tracker.Me();
        if (me == null) return;
        me.SellQueue.Add(itemId % 1_000_000);
        tracker.Save(force: true);
    }

    // ------------------------------------------------------------------ venture ledger
    // Normal cycle: ventures run, stock is kept, and only what a money venture brought back is sold.
    // Nothing else on a retainer is ever pulled to sell (that's the clean-up's job).

    private void Note(CharacterData me, string text)
    {
        me.SellLog.Insert(0, $"{DateTime.Now:MM-dd HH:mm} {text}");
        if (me.SellLog.Count > 200) me.SellLog.RemoveRange(200, me.SellLog.Count - 200);
    }

    private string RName(CharacterData me, ulong r) => me.Retainers.TryGetValue(r, out var snap) && snap.Name.Length > 0 ? snap.Name : tracker.NameOf(r);
    private static string IName(uint id) => Items.Info(id)?.Name ?? $"#{id}";

    /// <summary>How many of an item this retainer holds: live if it's open, otherwise its last snapshot.</summary>
    private static int OnRetainer(CharacterData me, ulong retainerId, uint itemId)
    {
        if (Tracker.ActiveRetainer() is { } a && a.Id == retainerId && Tracker.Loaded(Tracker.RetainerPages))
            return Tracker.Stacks(Tracker.RetainerPages, id => id % 1_000_000 == itemId).Sum(x => x.Qty);
        return me.Retainers.TryGetValue(retainerId, out var snap)
            ? snap.Items.GetValueOrDefault(itemId) + snap.Items.GetValueOrDefault(itemId + 1_000_000) : 0;
    }

    /// <summary>A money venture went out: remember what the retainer held, so only the results get sold.</summary>
    public void VentureSent(ulong retainerId, uint itemId, int expected)
    {
        var me = tracker.Me();
        if (me == null) return;
        var id = itemId % 1_000_000;
        me.SellLots.Add(new SellLot { RetainerId = retainerId, ItemId = id, Baseline = OnRetainer(me, retainerId, id), Expected = expected, SentAt = DateTime.UtcNow });
        Note(me, $"{RName(me, retainerId)}: money venture for {IName(id)} (~{expected})");
        tracker.Save(force: true);
    }

    /// <summary>
    /// The retainer's last venture was just collected (we're at its next venture): what came back is the
    /// growth since it went out. [Estimate] Capped at 1.5x the expected yield, so anything you stored
    /// there meanwhile isn't counted as venture results.
    /// </summary>
    public void Collected(ulong retainerId)
    {
        var me = tracker.Me();
        if (me == null) return;
        var changed = false;
        foreach (var lot in me.SellLots.Where(l => l.RetainerId == retainerId && l.Got < 0))
        {
            var now = OnRetainer(me, retainerId, lot.ItemId);
            lot.Got = Math.Clamp(now - lot.Baseline, 0, (int)Math.Ceiling(lot.Expected * 1.5));
            // Stock comes first: a stocked item is kept, not sold.
            if (Stock.Wanted(config).ContainsKey(lot.ItemId)) { Note(me, $"{RName(me, retainerId)}: {IName(lot.ItemId)} x{lot.Got} back; it's on the stock list, kept"); lot.Done = true; }
            else Note(me, $"{RName(me, retainerId)}: {IName(lot.ItemId)} x{lot.Got} back, to sell");
            if (lot.Got == 0) lot.Done = true;
            changed = true;
        }
        me.SellLots.RemoveAll(l => l.Done && (DateTime.UtcNow - l.SentAt).TotalDays > 7);
        if (changed) tracker.Save(force: true);
    }

    /// <summary>What this retainer holds from money ventures, still to sell: item -> quantity.</summary>
    public Dictionary<uint, int> OwedAt(ulong retainerId)
    {
        var me = tracker.Me();
        var result = new Dictionary<uint, int>();
        if (me == null) return result;
        foreach (var l in me.SellLots.Where(l => l.RetainerId == retainerId && l.Left > 0))
            result[l.ItemId] = result.GetValueOrDefault(l.ItemId) + l.Left;
        return result;
    }

    /// <summary>Venture results were taken out of a retainer into your bags.</summary>
    public void Took(ulong retainerId, uint itemId, int qty)
    {
        var me = tracker.Me();
        if (me == null) return;
        var id = itemId % 1_000_000;
        var rest = qty;
        foreach (var l in me.SellLots.Where(l => l.RetainerId == retainerId && l.ItemId == id && l.Left > 0).OrderBy(l => l.SentAt))
        {
            var n = Math.Min(rest, l.Left);
            l.Taken += n;
            rest -= n;
            if (l.Left == 0) l.Done = true;
            if (rest <= 0) break;
        }
        // A venture for the same item that's still out counted these in its starting amount.
        foreach (var l in me.SellLots.Where(l => l.RetainerId == retainerId && l.ItemId == id && l.Got < 0))
            l.Baseline = Math.Max(0, l.Baseline - qty);
        me.SellOwed[id] = me.SellOwed.GetValueOrDefault(id) + qty;
        owedSince[id] = DateTime.UtcNow;
        Note(me, $"{RName(me, retainerId)}: took {IName(id)} x{qty} out to sell");
        tracker.Save(force: true);
    }

    /// <summary>Stop trying to take these venture results out (gone, or merged into a bigger stack).</summary>
    public void GiveUp(ulong retainerId, uint itemId, string why)
    {
        var me = tracker.Me();
        if (me == null) return;
        foreach (var l in me.SellLots.Where(l => l.RetainerId == retainerId && l.ItemId == itemId && l.Left > 0)) l.Done = true;
        Note(me, $"{RName(me, retainerId)}: {IName(itemId)} left there ({why})");
        tracker.Save(force: true);
    }

    /// <summary>How much of this bag stack may be listed: clean-up items all of it, venture results what's owed.</summary>
    public int SellableQty(uint itemId, int stackQty)
    {
        var me = tracker.Me();
        if (me == null) return 0;
        var id = itemId % 1_000_000;
        if (me.SellQueue.Contains(id)) return stackQty;
        return Math.Min(stackQty, me.SellOwed.GetValueOrDefault(id));
    }

    private readonly Dictionary<uint, DateTime> owedSince = new();

    /// <summary>To-sell venture results on retainers (pending ventures count their expected yield).</summary>
    private static int WaitingOnRetainers(CharacterData me, uint itemId) =>
        me.SellLots.Where(l => l.ItemId == itemId && !l.Done).Sum(l => l.Got < 0 ? l.Expected : l.Left);

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
        if (!me.SellQueue.Contains(id) && me.SellOwed.TryGetValue(id, out var owed))
        {
            if (owed - qty > 0) me.SellOwed[id] = owed - qty; else me.SellOwed.Remove(id);
        }
        Note(me, $"listed {IName(id)} x{qty}");
        tracker.Save(force: true);
    }

    /// <summary>Retainers holding money-venture results still to sell.</summary>
    public HashSet<ulong> Holders()
    {
        var me = tracker.Me();
        return me == null ? new HashSet<ulong>() : me.SellLots.Where(l => l.Left > 0).Select(l => l.RetainerId).ToHashSet();
    }

    /// <summary>Anything to sell, in your bags or waiting on a retainer.</summary>
    public bool AnythingToSell() => InBags().Count > 0 || Holders().Count > 0;

    /// <summary>To-sell items in your bags now: item -> quantity (venture results only up to what's owed).</summary>
    public Dictionary<uint, int> InBags()
    {
        var me = tracker.Me();
        var result = new Dictionary<uint, int>();
        if (me == null || (me.SellQueue.Count == 0 && me.SellOwed.Count == 0)) return result;
        foreach (var s in Tracker.Stacks(Tracker.Bags, id => me.SellQueue.Contains(id % 1_000_000) || me.SellOwed.ContainsKey(id % 1_000_000)))
            result[s.ItemId % 1_000_000] = result.GetValueOrDefault(s.ItemId % 1_000_000) + s.Qty;
        foreach (var (id, owed) in me.SellOwed)
            if (!me.SellQueue.Contains(id) && result.TryGetValue(id, out var have)) result[id] = Math.Min(have, owed);
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
                        ids.UnionWith(me.SellOwed.Keys);
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
        var dirty = false;
        // Old format: money-venture items sat on the clean-up list and "every stack of it on that
        // retainer" was pulled to sell (stock and stored stacks too). Keep only what's in your bags.
        if (me.SellAt.Count > 0)
        {
            foreach (var id in me.SellAt.Keys)
            {
                if (now.TryGetValue(id, out var q)) me.SellOwed[id] = me.SellOwed.GetValueOrDefault(id) + q;
                me.SellQueue.Remove(id);
            }
            Note(me, $"switched to the venture ledger: {me.SellAt.Count} item(s) no longer pulled from retainers");
            me.SellAt.Clear();
            dirty = true;
        }
        // Clean-up items: off the list once none are in your bags.
        dirty |= me.SellQueue.RemoveWhere(id => !now.ContainsKey(id) && !me.SellOwed.ContainsKey(id)) > 0;
        // Venture results you moved or sold by hand: owed never exceeds what's in your bags (after a grace
        // period, so an item still on its way from a retainer isn't dropped).
        foreach (var (id, owed) in me.SellOwed.ToList())
        {
            if ((DateTime.UtcNow - owedSince.GetValueOrDefault(id, DateTime.MinValue)).TotalSeconds < 30) continue;
            var have = Tracker.Stacks(Tracker.Bags, x => x % 1_000_000 == id).Sum(x => x.Qty);
            if (have >= owed) continue;
            if (have > 0) me.SellOwed[id] = have; else me.SellOwed.Remove(id);
            dirty = true;
        }
        if (dirty) tracker.Save(force: true);
    }

    private static unsafe void PlaySound(int id)
    {
        try { UIGlobals.PlayChatSoundEffect((uint)Math.Clamp(id, 1, 16)); }
        catch (Exception ex) { Plugin.Log.Debug(ex, "sound failed"); }
    }
}
