using System;
using System.Collections.Generic;
using Dalamud.Plugin.Ipc;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace TurnInManager.Core;

/// <summary>
/// Copies of an item held away from your bags: on retainers and in the saddlebag. The game only shares
/// those while they're open, so this asks plugins that keep records:
///   1. Material Stash ("MaterialStash.HeldElsewhere"): retainers + saddlebag, as last seen.
///   2. Allagan Tools ("AllaganTools.GetCharacterItems") per retainer, if Material Stash has nothing.
///      Retainers only (no saddlebag). [Assumption] entries are [itemId, flags, ownerId, quantity],
///      the same reading Material Stash uses; unverified in game.
/// Neither installed (or no records yet): 0, i.e. bags only, as before.
/// Answers are cached for a few seconds (the settings window asks every frame).
/// </summary>
public static unsafe class Stock
{
    public enum Source { None, MaterialStash, AllaganTools }

    private static ICallGateSubscriber<uint, int>? stash;
    private static ICallGateSubscriber<bool>? allaganReady;
    private static ICallGateSubscriber<ulong, HashSet<ulong[]>>? allaganItems;

    private static readonly Dictionary<uint, (int Qty, Source From, DateTime At)> cache = new();
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(3);

    /// <summary>Copies elsewhere (NQ + HQ) and where the number came from.</summary>
    public static (int Qty, Source From) Elsewhere(uint itemId)
    {
        if (cache.TryGetValue(itemId, out var c) && DateTime.UtcNow - c.At < CacheFor) return (c.Qty, c.From);
        var result = FromStash(itemId) ?? FromAllagan(itemId) ?? (Qty: 0, From: Source.None);
        cache[itemId] = (result.Qty, result.From, DateTime.UtcNow);
        return result;
    }

    /// <summary>Forget cached answers (e.g. at the start of a run).</summary>
    public static void Refresh() => cache.Clear();

    public static string Describe(Source s) => s switch
    {
        Source.MaterialStash => "Material Stash's records (retainers + saddlebag)",
        Source.AllaganTools => "Allagan Tools (retainers only)",
        _ => "no records (Material Stash / Allagan Tools not loaded, or no retainer seen yet)",
    };

    private static (int Qty, Source From)? FromStash(uint itemId)
    {
        try
        {
            stash ??= Plugin.PluginInterface.GetIpcSubscriber<uint, int>("MaterialStash.HeldElsewhere");
            var n = stash.InvokeFunc(itemId);
            return n < 0 ? null : (n, Source.MaterialStash);
        }
        catch (Exception) { return null; }
    }

    private static (int Qty, Source From)? FromAllagan(uint itemId)
    {
        try
        {
            allaganReady ??= Plugin.PluginInterface.GetIpcSubscriber<bool>("AllaganTools.IsInitialized");
            allaganItems ??= Plugin.PluginInterface.GetIpcSubscriber<ulong, HashSet<ulong[]>>("AllaganTools.GetCharacterItems");
            if (!allaganReady.InvokeFunc()) return null;

            var rm = RetainerManager.Instance();
            if (rm == null || !rm->IsReady) return null; // retainer list not known until a bell is used
            var nq = itemId % 1_000_000;
            var n = 0;
            var any = false;
            for (uint i = 0; i < rm->GetRetainerCount(); i++)
            {
                var r = rm->GetRetainerBySortedIndex(i);
                if (r == null || r->RetainerId == 0) continue;
                var set = allaganItems.InvokeFunc(r->RetainerId);
                if (set == null) continue;
                any = true;
                foreach (var e in set)
                    if (e != null && e.Length >= 4 && e[0] % 1_000_000 == nq) n += (int)e[3];
            }
            return any ? (n, Source.AllaganTools) : null;
        }
        catch (Exception ex)
        {
            Plugin.Log.Debug(ex, "Allagan Tools retainer count failed");
            return null;
        }
    }
}
