using System.Collections.Generic;
using KSerialization;

namespace ParkingSign;

/// <summary>
/// Component of a single sign: tracks its signal, animation, status, selected duplicants and
/// standing cell. Cross-sign decisions (who parks where) live in <see cref="ParkingSignRegistry"/>.
/// </summary>
[SerializationConfig(MemberSerialization.OptIn)]
public sealed class ParkingSignController : KMonoBehaviour, ISaveLoadable, ISim1000ms
{
    [Serialize] private List<int> selectedMinionInstanceIds = new List<int>();
    [Serialize] private int priority = -1;

    private readonly HashSet<int> commandedMinionInstanceIds = new HashSet<int>();
    private readonly ParkingSignGhost ghost = new ParkingSignGhost();
    private LogicPorts logicPorts;
    private KBatchedAnimController animController;
    private KSelectable selectable;
    private Prioritizable prioritizable;
    private bool hasPrioritizableReference;
    private StatusItem currentStatus;
    private int lastSignalState = -1;
    private bool isHanging;

    internal long SpawnOrder { get; private set; }
    internal int TargetCell { get; private set; } = Grid.InvalidCell;
    internal bool IsSignalActive => lastSignalState == 1;
    internal IReadOnlyList<int> SelectedMinionInstanceIds => selectedMinionInstanceIds;

    internal PrioritySetting Priority => prioritizable != null
        ? prioritizable.GetMasterPriority()
        : new PrioritySetting(PriorityScreen.PriorityClass.basic, 5);

    // ---- Lifecycle ------------------------------------------------------------------------------

    protected override void OnSpawn()
    {
        base.OnSpawn();
        SpawnOrder = ParkingSignRegistry.Register(this);
        KPrefabID prefabId = GetComponent<KPrefabID>();
        isHanging = prefabId != null && prefabId.PrefabTag.Name == HangingParkingSignConfig.ID;
        RefreshTargetCell();
        if (isHanging)
        {
            SimAndRenderScheduler.instance.Add(this, false);
            Subscribe((int)GameHashes.SelectObject, OnSelect);
        }

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

    protected override void OnCleanUp()
    {
        if (isHanging)
        {
            SimAndRenderScheduler.instance.Remove(this);
        }

        ghost.Destroy();

        if (prioritizable != null)
        {
            prioritizable.onPriorityChanged -= OnPriorityChanged;
            if (hasPrioritizableReference)
            {
                prioritizable.RemoveRef();
                hasPrioritizableReference = false;
            }
        }

        ParkingSignRegistry.Unregister(this);
        ReconcileSelectedMinions();

        base.OnCleanUp();
    }

    public void Sim1000ms(float dt)
    {
        int previous = TargetCell;
        RefreshTargetCell();
        RefreshStatus();
        if (previous != TargetCell)
        {
            ReconcileSelectedMinions();
        }
        else if (IsSignalActive)
        {
            // Reachability can change without any event we hear (a door built, a tile dug): re-check once a second.
            foreach (int instanceId in new List<int>(selectedMinionInstanceIds))
            {
                ParkingSignRegistry.RecheckMinionWinner(instanceId);
            }
        }

#if DEBUG
        foreach (int instanceId in selectedMinionInstanceIds)
        {
            MinionIdentity member = ParkingSignRegistry.FindMinion(instanceId);
            if (member != null)
            {
                ParkingSignLog.Trace($"Tick: sign={name} minion={member.GetProperName()} " +
                                     $"cell={Grid.PosToCell(member.gameObject)} targetCell={TargetCell} " +
                                     $"chore={ParkingSignRegistry.GetChoreName(member)}");
            }
        }
#endif

        UpdateGhost(IsSelected());
    }

    private void Update()
    {
        if (!isHanging)
        {
            return;
        }

        bool selected = IsSelected();
        if (selected == ghost.IsVisible)
        {
            return;
        }

        if (selected)
        {
            RefreshTargetCell();
        }

        UpdateGhost(selected);
    }

    // ---- Duplicant selection --------------------------------------------------------------------

    public bool IsMinionSelected(int instanceId)
    {
        return selectedMinionInstanceIds.Contains(instanceId);
    }

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

        ParkingSignRegistry.ReconcileMinionAssignment(instanceId);
    }

    private void OnPriorityChanged(PrioritySetting _)
    {
        ParkingSignRegistry.ReconcileAllSelectedMinions();
    }

    private void ReconcileSelectedMinions()
    {
        foreach (int instanceId in selectedMinionInstanceIds)
        {
            ParkingSignRegistry.ReconcileMinionAssignment(instanceId);
        }
    }

    // ---- Signal, animation and status -----------------------------------------------------------

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

    private void RefreshStatus()
    {
        if (selectable == null || ParkingSignStatusItems.NoStandPoint == null)
        {
            return;
        }

        StatusItem item = isHanging && !Grid.IsValidCell(TargetCell)
            ? ParkingSignStatusItems.NoStandPoint
            : IsSignalActive
                ? ParkingSignStatusItems.Active
                : ParkingSignStatusItems.Inactive;
        if (item == currentStatus)
        {
            return;
        }

        currentStatus = item;
        selectable.SetStatusItem(Db.Get().StatusItemCategories.Main, item, this);
    }

    // ---- Moving duplicants ----------------------------------------------------------------------

    internal void MoveMinionToSign(int instanceId)
    {
        MinionIdentity minion = ParkingSignRegistry.FindMinion(instanceId);
        if (minion == null)
        {
            return;
        }

        ParkingSignLog.Trace($"MoveMinionToSign: sign={name} isHanging={isHanging} " +
                             $"minion={minion.GetProperName()} minionCell={Grid.PosToCell(minion.gameObject)} " +
                             $"targetCell={TargetCell} signCell={Grid.PosToCell(gameObject)}");

        Navigator navigator = minion.GetComponent<Navigator>();
        if (navigator == null)
        {
            ParkingSignLog.Warn($"Cannot move {minion.GetProperName()}: no Navigator component.");
            return;
        }

        if (!Grid.IsValidCell(TargetCell))
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
        ChoreDriver choreDriver = minion.GetComponent<ChoreDriver>();
        Chore currentChore = choreDriver != null ? choreDriver.GetCurrentChore() : null;
        bool alreadyAtTarget = Grid.PosToCell(minion.gameObject) == TargetCell;
        bool alreadyHeadingThere = currentChore != null &&
                                   currentChore.choreType == Db.Get().ChoreTypes.MoveTo &&
                                   moveMonitor.targetCell == TargetCell;
        if (alreadyAtTarget || alreadyHeadingThere)
        {
            commandedMinionInstanceIds.Add(instanceId);
            return;
        }

        if (choreDriver != null && choreDriver.HasChore())
        {
            choreDriver.StopChore();
        }

        moveMonitor.MoveToLocation(TargetCell);
        commandedMinionInstanceIds.Add(instanceId);
    }

    internal void ReleaseMinion(int instanceId)
    {
        if (!commandedMinionInstanceIds.Remove(instanceId))
        {
            return;
        }

        MinionIdentity minion = ParkingSignRegistry.FindMinion(instanceId);
        if (minion == null)
        {
            return;
        }

        ParkingSignLog.Trace($"ReleaseMinion: sign={name} minion={minion.GetProperName()} " +
                             $"cell={Grid.PosToCell(minion.gameObject)} targetCell={TargetCell} " +
                             $"signCell={Grid.PosToCell(gameObject)}");

        Navigator navigator = minion.GetComponent<Navigator>();
        MoveToLocationMonitor.Instance moveMonitor =
            navigator != null ? navigator.GetSMI<MoveToLocationMonitor.Instance>() : null;
        if (moveMonitor != null && moveMonitor.targetCell == TargetCell)
        {
            moveMonitor.GoTo(moveMonitor.sm.satisfied);
        }

        if (navigator != null)
        {
            navigator.Stop();
        }
    }

    /// <summary>Drops the record of having commanded this duplicant without touching its navigation.</summary>
    internal void ForgetCommand(int instanceId)
    {
        commandedMinionInstanceIds.Remove(instanceId);
    }

    // ---- Standing cell and ghost ----------------------------------------------------------------

    private void RefreshTargetCell()
    {
        TargetCell = ParkingSignTargetFinder.FindTargetCell(Grid.PosToCell(gameObject), isHanging);
    }

    private bool IsSelected()
    {
        return SelectTool.Instance != null && selectable != null && SelectTool.Instance.selected == selectable;
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
        if (show && isHanging)
        {
            ghost.Show(TargetCell);
        }
        else
        {
            ghost.Hide();
        }
    }
}
