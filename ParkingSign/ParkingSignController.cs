using System.Collections.Generic;
using KSerialization;
using UnityEngine;

namespace ParkingSign;

[SerializationConfig(MemberSerialization.OptIn)]
public sealed class ParkingSignController : KMonoBehaviour, ISaveLoadable, ISim1000ms
{
    private static readonly HashSet<ParkingSignController> ActiveSigns = new HashSet<ParkingSignController>();

    private static readonly Dictionary<int, ParkingSignController> WinningSignsByMinion =
        new Dictionary<int, ParkingSignController>();

    private static readonly Dictionary<int, ParkingSignController> WinningSignsByProxy =
        new Dictionary<int, ParkingSignController>();

    private static readonly HashSet<int> ReachabilityChecks = new HashSet<int>();
    private static long nextSpawnOrder;

    [Serialize] private List<int> selectedMinionInstanceIds = new List<int>();
    [Serialize] private int priority = -1;

    private readonly HashSet<int> commandedMinionInstanceIds = new HashSet<int>();
    private readonly HashSet<int> selectedProxyInstanceIds = new HashSet<int>();
    private long spawnOrder;
    private LogicPorts logicPorts;
    private KBatchedAnimController animController;
    private KSelectable selectable;
    private Prioritizable prioritizable;
    private bool hasPrioritizableReference;
    private int lastSignalState = -1;
    private int targetCell = Grid.InvalidCell;
    private bool isHanging;
    private GameObject ghost;

    private const int MaxHangingDrop = 10;
    private const string MinionNavGridName = "MinionNavGrid";

    protected override void OnSpawn()
    {
        base.OnSpawn();
        spawnOrder = nextSpawnOrder++;
        ActiveSigns.Add(this);
        KPrefabID prefabId = GetComponent<KPrefabID>();
        isHanging = prefabId != null && prefabId.PrefabTag.Name == HangingParkingSignConfig.ID;
        RefreshTargetCell();
        if (isHanging)
        {
            SimAndRenderScheduler.instance.Add(this, false);
            Subscribe((int)GameHashes.SelectObject, OnSelect);
        }

        RefreshSelectedProxyInstanceIds();
        logicPorts = GetComponent<LogicPorts>();
        animController = GetComponent<KBatchedAnimController>();
        selectable = GetComponent<KSelectable>();
        prioritizable = GetComponent<Prioritizable>();

        if (logicPorts == null || animController == null || selectable == null || prioritizable == null)
        {
            ParkingSignLog.Error(
                $"Parking Sign '{name}' is missing a required logic port, animation controller, selectable, or prioritizable.");
            return;
        }

        prioritizable.onPriorityChanged += OnPriorityChanged;
        hasPrioritizableReference = true;
        prioritizable.AddRef();
        if (priority >= 1 && priority <= 9)
        {
            prioritizable.SetMasterPriority(
                new PrioritySetting(PriorityScreen.PriorityClass.basic, priority));
            priority = -1;
        }

        Subscribe((int)GameHashes.LogicEvent, OnLogicValueChanged);
        UpdateAnimation();
    }

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

        ParkingSignController winningSign = GetCachedWinningSign(minionPrefabId.InstanceID);
        return IsBlockingGuidanceCell(winningSign, currentCell, destinationCell);
    }

    private static bool IsBlockingGuidanceCell(ParkingSignController winningSign, int fromCell, int destinationCell)
    {
        if (winningSign == null || !Grid.IsValidCell(winningSign.targetCell) ||
            fromCell != winningSign.targetCell)
        {
            return false;
        }

        // At the guidance cell, only work doable in place stays reachable; everything else is blocked.
        return true;
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
        if (winningSign == null || !Grid.IsValidCell(winningSign.targetCell))
        {
            return false;
        }

        guidanceCell = winningSign.targetCell;
        return true;
    }

    internal static bool IsHeadingToGuidanceCell(GameObject minionObject)
    {
        return TryGetGuidanceCell(minionObject, out int guidanceCell) &&
               Grid.PosToCell(minionObject) != guidanceCell;
    }

    private static string GetChoreName(MinionIdentity minion)
    {
        ChoreDriver choreDriver = minion != null ? minion.GetComponent<ChoreDriver>() : null;
        Chore chore = choreDriver != null ? choreDriver.GetCurrentChore() : null;
        return chore != null && chore.choreType != null ? chore.choreType.Id : "none";
    }

    internal static bool ShouldBlockPathFromSign(int proxyInstanceId, int fromCell, int destinationCell)
    {
        if (fromCell == destinationCell)
        {
            return false;
        }

        if (ReachabilityChecks.Contains(proxyInstanceId))
        {
            return false;
        }

        ParkingSignController winningSign = GetCachedWinningSignForProxy(proxyInstanceId);
        return IsBlockingGuidanceCell(winningSign, fromCell, destinationCell);
    }

    private void RefreshSelectedProxyInstanceIds()
    {
        selectedProxyInstanceIds.Clear();
        foreach (int minionInstanceId in selectedMinionInstanceIds)
        {
            MinionIdentity minion = FindMinion(minionInstanceId);
            MinionAssignablesProxy proxy = minion != null && minion.assignableProxy != null
                ? minion.assignableProxy.Get()
                : null;
            KPrefabID proxyPrefabId = proxy != null ? proxy.GetComponent<KPrefabID>() : null;
            if (proxyPrefabId != null)
            {
                selectedProxyInstanceIds.Add(proxyPrefabId.InstanceID);
            }
        }
    }

    public bool IsMinionSelected(int instanceId)
    {
        return selectedMinionInstanceIds.Contains(instanceId);
    }

    private PrioritySetting Priority => prioritizable != null
        ? prioritizable.GetMasterPriority()
        : new PrioritySetting(PriorityScreen.PriorityClass.basic, 5);

    public void SetMinionSelected(int instanceId, bool selected)
    {
        if (selected)
        {
            if (!selectedMinionInstanceIds.Contains(instanceId))
            {
                selectedMinionInstanceIds.Add(instanceId);
            }
        }
        else
        {
            selectedMinionInstanceIds.Remove(instanceId);
        }

        RefreshSelectedProxyInstanceIds();
        ReconcileMinionAssignment(instanceId);
    }

    private void OnPriorityChanged(PrioritySetting _)
    {
        ReconcileAllSelectedMinions();
    }

    private static void ReconcileAllSelectedMinions()
    {
        HashSet<int> selectedMinions = new HashSet<int>();
        foreach (ParkingSignController sign in ActiveSigns)
        {
            if (sign != null)
            {
                selectedMinions.UnionWith(sign.selectedMinionInstanceIds);
            }
        }

        foreach (int instanceId in selectedMinions)
        {
            ReconcileMinionAssignment(instanceId);
        }
    }

    private void OnLogicValueChanged(object data)
    {
        if (((LogicValueChanged)data).portID == new HashedString(ParkingSignConfig.LogicPortId))
        {
            UpdateAnimation();
        }
    }

    private void UpdateAnimation()
    {
        int portCell = logicPorts.GetPortCell(new HashedString(ParkingSignConfig.LogicPortId));
        LogicCircuitNetwork network = Game.Instance.logicCircuitManager.GetNetworkForCell(portCell);
        int signalState = network != null && LogicCircuitNetwork.IsBitActive(0, network.OutputValue) ? 1 : 0;
        if (signalState == lastSignalState)
        {
            return;
        }

        lastSignalState = signalState;
        HashedString animation = signalState == 1 ? new HashedString("on") : new HashedString("off");
        animController.Play(animation, KAnim.PlayMode.Loop, 1f, 0f);
        RefreshStatus();

        ReconcileSelectedMinions();
    }

    private StatusItem currentStatus;

    private void RefreshStatus()
    {
        if (selectable == null || ParkingSignStatusItems.NoStandPoint == null)
        {
            return;
        }

        StatusItem item = isHanging && !Grid.IsValidCell(targetCell)
            ? ParkingSignStatusItems.NoStandPoint
            : lastSignalState == 1
                ? ParkingSignStatusItems.Active
                : ParkingSignStatusItems.Inactive;
        if (item == currentStatus)
        {
            return;
        }

        currentStatus = item;
        selectable.SetStatusItem(Db.Get().StatusItemCategories.Main, item, this);
    }

    private void ReconcileSelectedMinions()
    {
        foreach (int instanceId in selectedMinionInstanceIds)
        {
            ReconcileMinionAssignment(instanceId);
        }
    }

    private static void ReconcileMinionAssignment(int instanceId)
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
                sign.commandedMinionInstanceIds.Remove(instanceId);
            }
        }

        winningSign.MoveMinionToSign(instanceId);
    }

    private static string DescribeSign(ParkingSignController sign)
    {
        return sign == null
            ? "none"
            : $"{sign.name}#{sign.spawnOrder}(target={sign.targetCell},prio={sign.Priority.priority_value})";
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
        MinionAssignablesProxy proxy = minion.assignableProxy != null
            ? minion.assignableProxy.Get()
            : null;
        KPrefabID proxyPrefabId = proxy != null ? proxy.GetComponent<KPrefabID>() : null;
        bool addedMinion = ReachabilityChecks.Add(instanceId);
        bool addedProxy = proxyPrefabId != null && ReachabilityChecks.Add(proxyPrefabId.InstanceID);
        ParkingSignController winner = null;
        ParkingSignController cachedWinner = GetCachedWinningSign(instanceId);
        int currentCell = Grid.PosToCell(minion.gameObject);
        // The cached path probe is stale-blocked while the minion stands on its winning sign's cell.
        bool probeBlocked = cachedWinner != null && cachedWinner.targetCell == currentCell;
        try
        {
            foreach (ParkingSignController sign in ActiveSigns)
            {
                if (sign == null || sign.lastSignalState != 1 || !sign.IsMinionSelected(instanceId))
                {
                    continue;
                }

                int signTargetCell = sign.targetCell;
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
                ReachabilityChecks.Remove(proxyPrefabId.InstanceID);
            }
        }

        return winner;
    }

    private static void SetWinningSign(int instanceId, ParkingSignController sign)
    {
        if (sign == null)
        {
            WinningSignsByMinion.Remove(instanceId);
        }
        else
        {
            WinningSignsByMinion[instanceId] = sign;
        }

        MinionIdentity minion = FindMinion(instanceId);
        MinionAssignablesProxy proxy = minion != null && minion.assignableProxy != null
            ? minion.assignableProxy.Get()
            : null;
        KPrefabID proxyPrefabId = proxy != null ? proxy.GetComponent<KPrefabID>() : null;
        if (proxyPrefabId == null)
        {
            return;
        }

        if (sign == null)
        {
            WinningSignsByProxy.Remove(proxyPrefabId.InstanceID);
        }
        else
        {
            WinningSignsByProxy[proxyPrefabId.InstanceID] = sign;
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
        List<int> minionIdsToRemove = new List<int>();
        foreach (KeyValuePair<int, ParkingSignController> entry in WinningSignsByMinion)
        {
            if (entry.Value == sign)
            {
                minionIdsToRemove.Add(entry.Key);
            }
        }

        foreach (int instanceId in minionIdsToRemove)
        {
            WinningSignsByMinion.Remove(instanceId);
        }

        List<int> proxyIdsToRemove = new List<int>();
        foreach (KeyValuePair<int, ParkingSignController> entry in WinningSignsByProxy)
        {
            if (entry.Value == sign)
            {
                proxyIdsToRemove.Add(entry.Key);
            }
        }

        foreach (int proxyInstanceId in proxyIdsToRemove)
        {
            WinningSignsByProxy.Remove(proxyInstanceId);
        }
    }

    private static bool HasHigherPrecedence(ParkingSignController candidate, ParkingSignController current)
    {
        return current == null || candidate.Priority > current.Priority ||
               candidate.Priority == current.Priority && candidate.spawnOrder < current.spawnOrder;
    }

    private void MoveMinionToSign(int instanceId)
    {
        MinionIdentity minion = FindMinion(instanceId);
        if (minion == null)
        {
            return;
        }

        ParkingSignLog.Trace($"MoveMinionToSign: sign={name} isHanging={isHanging} " +
                  $"minion={minion.GetProperName()} minionCell={Grid.PosToCell(minion.gameObject)} " +
                  $"targetCell={targetCell} signCell={Grid.PosToCell(gameObject)}");

        Navigator navigator = minion.GetComponent<Navigator>();
        if (navigator == null)
        {
            ParkingSignLog.Warn($"Cannot move {minion.GetProperName()}: no Navigator component.");
            return;
        }

        if (!Grid.IsValidCell(targetCell))
        {
            ParkingSignLog.Warn($"'{name}' has an invalid destination cell.");
            return;
        }

        MoveToLocationMonitor.Instance moveMonitor =
            navigator.GetSMI<MoveToLocationMonitor.Instance>();
        if (moveMonitor == null)
        {
            ParkingSignLog.Warn($"Cannot move {minion.GetProperName()}: move monitor is unavailable.");
            return;
        }

        // Re-issuing the move while already at or heading to the guidance cell cancels the
        // duplicant's current chore each reconcile, so only command it when needed.
        bool alreadyAtTarget = Grid.PosToCell(minion.gameObject) == targetCell;
        ChoreDriver currentDriver = minion.GetComponent<ChoreDriver>();
        Chore currentChore = currentDriver != null ? currentDriver.GetCurrentChore() : null;
        bool alreadyHeadingThere = currentChore != null &&
                                   currentChore.choreType == Db.Get().ChoreTypes.MoveTo &&
                                   moveMonitor.targetCell == targetCell;
        if (alreadyAtTarget || alreadyHeadingThere)
        {
            commandedMinionInstanceIds.Add(instanceId);
            return;
        }

        ChoreDriver choreDriver = minion.GetComponent<ChoreDriver>();
        if (choreDriver != null && choreDriver.HasChore())
        {
            choreDriver.StopChore();
        }

        moveMonitor.MoveToLocation(targetCell);
        commandedMinionInstanceIds.Add(instanceId);
    }

    private void ReleaseMinion(int instanceId)
    {
        if (!commandedMinionInstanceIds.Remove(instanceId))
        {
            return;
        }

        MinionIdentity minion = FindMinion(instanceId);
        if (minion == null)
        {
            return;
        }

        ParkingSignLog.Trace($"ReleaseMinion: sign={name} minion={minion.GetProperName()} " +
                         $"cell={Grid.PosToCell(minion.gameObject)} targetCell={targetCell} signCell={Grid.PosToCell(gameObject)}");

        Navigator navigator = minion.GetComponent<Navigator>();
        MoveToLocationMonitor.Instance moveMonitor =
            navigator != null ? navigator.GetSMI<MoveToLocationMonitor.Instance>() : null;
        if (moveMonitor != null && moveMonitor.targetCell == targetCell)
        {
            moveMonitor.GoTo(moveMonitor.sm.satisfied);
        }

        if (navigator != null)
        {
            navigator.Stop();
        }
    }

    private static MinionIdentity FindMinion(int instanceId)
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

    public void Sim1000ms(float dt)
    {
        int previous = targetCell;
        RefreshTargetCell();
        RefreshStatus();
        if (previous != targetCell)
        {
            ReconcileSelectedMinions();
        }

#if DEBUG
        foreach (int instanceId in selectedMinionInstanceIds)
        {
            MinionIdentity member = FindMinion(instanceId);
            if (member != null)
            {
                ParkingSignLog.Trace($"Tick: sign={name} minion={member.GetProperName()} " +
                                     $"cell={Grid.PosToCell(member.gameObject)} targetCell={targetCell} chore={GetChoreName(member)}");
            }
        }
#endif

        UpdateGhost(IsSelected());
    }

    private bool IsSelected()
    {
        return SelectTool.Instance != null && selectable != null && SelectTool.Instance.selected == selectable;
    }

    private void Update()
    {
        if (isHanging)
        {
            bool selected = IsSelected();
            if (selected != (ghost != null && ghost.activeSelf))
            {
                if (selected)
                {
                    RefreshTargetCell();
                }

                UpdateGhost(selected);
            }
        }
    }

    private void RefreshTargetCell()
    {
        int signCell = Grid.PosToCell(gameObject);
        targetCell = isHanging ? FindHangingTargetCell(signCell) : signCell;
    }

    private static int FindHangingTargetCell(int signCell)
    {
        NavGrid navGrid = Pathfinding.Instance != null ? Pathfinding.Instance.GetNavGrid(MinionNavGridName) : null;
        int cell = signCell;
        for (int i = 0; i < MaxHangingDrop; i++)
        {
            cell = Grid.CellBelow(cell);
            if (!Grid.IsValidCell(cell) || Grid.Solid[cell])
            {
                break;
            }

            if (IsStandable(navGrid, cell))
            {
                return cell;
            }
        }

        return Grid.InvalidCell;
    }

    private static bool IsStandable(NavGrid navGrid, int cell)
    {
        if (navGrid == null)
        {
            int below = Grid.CellBelow(cell);
            return Grid.IsValidCell(below) && Grid.Solid[below];
        }

        return navGrid.NavTable.IsValid(cell, NavType.Floor) ||
               navGrid.NavTable.IsValid(cell, NavType.Ladder) ||
               navGrid.NavTable.IsValid(cell, NavType.Pole);
    }

    private void OnSelect(object data)
    {
        bool selected = data is bool value && value;
        if (selected)
        {
            RefreshTargetCell();
        }

        UpdateGhost(selected);
    }

    private void UpdateGhost(bool show)
    {
        if (!show || !isHanging || !Grid.IsValidCell(targetCell))
        {
            if (ghost != null)
            {
                ghost.SetActive(false);
            }

            return;
        }

        if (ghost == null)
        {
            ghost = ParkingSignGhostFactory.Create();
            if (ghost == null)
            {
                return;
            }
        }

        ghost.transform.SetPosition(Grid.CellToPosCBC(targetCell, Grid.SceneLayer.FXFront2));
        ghost.SetActive(true);
    }

    protected override void OnCleanUp()
    {
        if (isHanging)
        {
            SimAndRenderScheduler.instance.Remove(this);
        }

        if (ghost != null)
        {
            Destroy(ghost);
            ghost = null;
        }

        if (prioritizable != null)
        {
            prioritizable.onPriorityChanged -= OnPriorityChanged;
            if (hasPrioritizableReference)
            {
                prioritizable.RemoveRef();
                hasPrioritizableReference = false;
            }
        }

        ActiveSigns.Remove(this);
        RemoveWinningSign(this);
        foreach (int instanceId in selectedMinionInstanceIds)
        {
            ReconcileMinionAssignment(instanceId);
        }

        base.OnCleanUp();
    }
}