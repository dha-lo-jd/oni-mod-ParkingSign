using System.Collections.Generic;
using UnityEngine;

namespace ParkingSign;

/// <summary>
/// Tracks every live sign and decides which sign each selected duplicant should be parked at.
/// Also answers the pathfinding/chore questions asked by the Harmony patches.
/// </summary>
internal static class ParkingSignRegistry
{
    private static readonly HashSet<ParkingSignController> ActiveSigns = new HashSet<ParkingSignController>();

    private static readonly Dictionary<int, ParkingSignController> WinningSignsByMinion =
        new Dictionary<int, ParkingSignController>();

    private static readonly Dictionary<int, ParkingSignController> WinningSignsByProxy =
        new Dictionary<int, ParkingSignController>();

    // Instance ids whose reachability is being probed; the path-blocking patches must not interfere.
    private static readonly HashSet<int> ReachabilityChecks = new HashSet<int>();
    private static long nextSpawnOrder;

    // ---- Lifetime -------------------------------------------------------------------------------

    internal static long Register(ParkingSignController sign)
    {
        ActiveSigns.Add(sign);
        return nextSpawnOrder++;
    }

    internal static void Unregister(ParkingSignController sign)
    {
        ActiveSigns.Remove(sign);
        RemoveWinningSign(sign);
    }

    // ---- Queries used by the patches ------------------------------------------------------------

    internal static bool ShouldBlockPathFromSign(MinionIdentity minion, int destinationCell)
    {
        KPrefabID minionPrefabId = minion != null ? minion.GetComponent<KPrefabID>() : null;
        if (minionPrefabId == null || ReachabilityChecks.Contains(minionPrefabId.InstanceID))
        {
            return false;
        }

        int currentCell = Grid.PosToCell(minion.gameObject);
        if (!Grid.IsValidCell(currentCell) || currentCell == destinationCell)
        {
            return false;
        }

        return IsStandingOnGuidanceCell(GetCachedWinningSign(minionPrefabId.InstanceID), currentCell);
    }

    internal static bool ShouldBlockPathFromSign(int proxyInstanceId, int fromCell, int destinationCell)
    {
        if (fromCell == destinationCell || ReachabilityChecks.Contains(proxyInstanceId))
        {
            return false;
        }

        return IsStandingOnGuidanceCell(GetCachedWinningSignForProxy(proxyInstanceId), fromCell);
    }

    internal static bool TryGetGuidanceCell(GameObject minionObject, out int guidanceCell)
    {
        guidanceCell = Grid.InvalidCell;
        KPrefabID prefabId = minionObject != null ? minionObject.GetComponent<KPrefabID>() : null;
        if (prefabId == null || minionObject.GetComponent<MinionIdentity>() == null)
        {
            return false;
        }

        ParkingSignController winningSign = GetCachedWinningSign(prefabId.InstanceID);
        if (winningSign == null || !Grid.IsValidCell(winningSign.TargetCell))
        {
            return false;
        }

        guidanceCell = winningSign.TargetCell;
        return true;
    }

    internal static bool IsHeadingToGuidanceCell(GameObject minionObject)
    {
        return TryGetGuidanceCell(minionObject, out int guidanceCell) &&
               Grid.PosToCell(minionObject) != guidanceCell;
    }

    // At the guidance cell, only work doable in place stays reachable; everything else is blocked.
    private static bool IsStandingOnGuidanceCell(ParkingSignController winningSign, int fromCell)
    {
        return winningSign != null && Grid.IsValidCell(winningSign.TargetCell) &&
               fromCell == winningSign.TargetCell;
    }

    // ---- Reconciliation -------------------------------------------------------------------------

    internal static void ReconcileAllSelectedMinions()
    {
        HashSet<int> selectedMinions = new HashSet<int>();
        foreach (ParkingSignController sign in ActiveSigns)
        {
            if (sign != null)
            {
                selectedMinions.UnionWith(sign.SelectedMinionInstanceIds);
            }
        }

        foreach (int instanceId in selectedMinions)
        {
            ReconcileMinionAssignment(instanceId);
        }
    }

    internal static void ReconcileMinionAssignment(int instanceId)
    {
        MinionIdentity minion = FindMinion(instanceId);
        ParkingSignController previousSign = GetCachedWinningSign(instanceId);
        ParkingSignController winningSign = FindWinningReachableSign(minion);
        ParkingSignLog.Trace($"Reconcile: minion={(minion != null ? minion.GetProperName() : "?")} " +
                             $"cell={(minion != null ? Grid.PosToCell(minion.gameObject) : -1)} " +
                             $"previous={DescribeSign(previousSign)} winner={DescribeSign(winningSign)} " +
                             $"chore={GetChoreName(minion)}");
        SetWinningSign(instanceId, winningSign);
        if (winningSign == null)
        {
            foreach (ParkingSignController sign in ActiveSigns)
            {
                sign?.ReleaseMinion(instanceId);
            }

            return;
        }

        foreach (ParkingSignController sign in ActiveSigns)
        {
            if (sign != null && sign != winningSign)
            {
                sign.ForgetCommand(instanceId);
            }
        }

        winningSign.MoveMinionToSign(instanceId);
    }

    private static ParkingSignController FindWinningReachableSign(MinionIdentity minion)
    {
        if (minion == null)
        {
            return null;
        }

        KPrefabID prefabId = minion.GetComponent<KPrefabID>();
        Navigator navigator = minion.GetComponent<Navigator>();
        if (prefabId == null || navigator == null)
        {
            return null;
        }

        int instanceId = prefabId.InstanceID;
        int proxyInstanceId = GetProxyInstanceId(minion);
        bool addedMinion = ReachabilityChecks.Add(instanceId);
        bool addedProxy = proxyInstanceId != -1 && ReachabilityChecks.Add(proxyInstanceId);
        ParkingSignController winner = null;
        ParkingSignController cachedWinner = GetCachedWinningSign(instanceId);
        int currentCell = Grid.PosToCell(minion.gameObject);
        // The cached path probe is stale-blocked while the minion stands on its winning sign's cell.
        bool probeBlocked = cachedWinner != null && cachedWinner.TargetCell == currentCell;
        try
        {
            foreach (ParkingSignController sign in ActiveSigns)
            {
                if (sign == null || !sign.IsSignalActive || !sign.IsMinionSelected(instanceId))
                {
                    continue;
                }

                int signTargetCell = sign.TargetCell;
                bool canReach = Grid.IsValidCell(signTargetCell) && navigator.CanReach(signTargetCell);
                ParkingSignLog.Trace($"Candidate: minion={minion.GetProperName()} sign={DescribeSign(sign)} " +
                                     $"canReach={canReach} probeBlocked={probeBlocked} cached={DescribeSign(cachedWinner)}");
                if (Grid.IsValidCell(signTargetCell) &&
                    (currentCell == signTargetCell || canReach ||
                     probeBlocked && sign != cachedWinner) &&
                    HasHigherPrecedence(sign, winner))
                {
                    winner = sign;
                }
            }
        }
        finally
        {
            if (addedMinion)
            {
                ReachabilityChecks.Remove(instanceId);
            }

            if (addedProxy)
            {
                ReachabilityChecks.Remove(proxyInstanceId);
            }
        }

        return winner;
    }

    private static bool HasHigherPrecedence(ParkingSignController candidate, ParkingSignController current)
    {
        return current == null || candidate.Priority > current.Priority ||
               candidate.Priority == current.Priority && candidate.SpawnOrder < current.SpawnOrder;
    }

    // ---- Winner cache ---------------------------------------------------------------------------

    private static void SetWinningSign(int instanceId, ParkingSignController sign)
    {
        SetOrRemove(WinningSignsByMinion, instanceId, sign);

        int proxyInstanceId = GetProxyInstanceId(FindMinion(instanceId));
        if (proxyInstanceId != -1)
        {
            SetOrRemove(WinningSignsByProxy, proxyInstanceId, sign);
        }
    }

    private static void SetOrRemove(Dictionary<int, ParkingSignController> cache, int key,
        ParkingSignController sign)
    {
        if (sign == null)
        {
            cache.Remove(key);
        }
        else
        {
            cache[key] = sign;
        }
    }

    private static ParkingSignController GetCachedWinningSign(int instanceId)
    {
        return WinningSignsByMinion.TryGetValue(instanceId, out ParkingSignController sign) ? sign : null;
    }

    private static ParkingSignController GetCachedWinningSignForProxy(int proxyInstanceId)
    {
        return WinningSignsByProxy.TryGetValue(proxyInstanceId, out ParkingSignController sign) ? sign : null;
    }

    private static void RemoveWinningSign(ParkingSignController sign)
    {
        RemoveEntriesFor(WinningSignsByMinion, sign);
        RemoveEntriesFor(WinningSignsByProxy, sign);
    }

    private static void RemoveEntriesFor(Dictionary<int, ParkingSignController> cache, ParkingSignController sign)
    {
        List<int> keysToRemove = new List<int>();
        foreach (KeyValuePair<int, ParkingSignController> entry in cache)
        {
            if (entry.Value == sign)
            {
                keysToRemove.Add(entry.Key);
            }
        }

        foreach (int key in keysToRemove)
        {
            cache.Remove(key);
        }
    }

    // ---- Minion lookup --------------------------------------------------------------------------

    internal static MinionIdentity FindMinion(int instanceId)
    {
        if (Components.LiveMinionIdentities == null)
        {
            return null;
        }

        foreach (MinionIdentity minion in Components.LiveMinionIdentities.Items)
        {
            KPrefabID prefabId = minion != null ? minion.GetComponent<KPrefabID>() : null;
            if (prefabId != null && prefabId.InstanceID == instanceId)
            {
                return minion;
            }
        }

        return null;
    }

    /// <summary>Returns the instance id of the minion's assignables proxy, or -1 when it has none.</summary>
    private static int GetProxyInstanceId(MinionIdentity minion)
    {
        MinionAssignablesProxy proxy = minion != null && minion.assignableProxy != null
            ? minion.assignableProxy.Get()
            : null;
        KPrefabID proxyPrefabId = proxy != null ? proxy.GetComponent<KPrefabID>() : null;
        return proxyPrefabId != null ? proxyPrefabId.InstanceID : -1;
    }

    // ---- Diagnostics ----------------------------------------------------------------------------

    internal static string DescribeSign(ParkingSignController sign)
    {
        return sign == null
            ? "none"
            : $"{sign.name}#{sign.SpawnOrder}(target={sign.TargetCell},prio={sign.Priority.priority_value})";
    }

    internal static string GetChoreName(MinionIdentity minion)
    {
        ChoreDriver choreDriver = minion != null ? minion.GetComponent<ChoreDriver>() : null;
        Chore chore = choreDriver != null ? choreDriver.GetCurrentChore() : null;
        return chore != null && chore.choreType != null ? chore.choreType.Id : "none";
    }
}
