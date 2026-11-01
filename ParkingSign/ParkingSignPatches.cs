using HarmonyLib;
using PeterHan.PLib.UI;
using System.Reflection;

namespace ParkingSign;

public static class ParkingSignStatusItems
{
    public static StatusItem Active;
    public static StatusItem Inactive;
    public static StatusItem NoStandPoint;
}

[HarmonyPatch(typeof(GeneratedBuildings), nameof(GeneratedBuildings.LoadGeneratedBuildings))]
public static class GeneratedBuildings_LoadGeneratedBuildings_Patch
{
    public static void Prefix()
    {
        ModUtil.AddBuildingToPlanScreen("Utilities", ParkingSignConfig.ID);
        ModUtil.AddBuildingToPlanScreen("Utilities", HangingParkingSignConfig.ID);
    }
}

[HarmonyPatch(typeof(DetailsScreen), "OnPrefabInit")]
public static class DetailsScreen_OnPrefabInit_ParkingSign_Patch
{
    public static void Postfix()
    {
        PUIUtils.AddSideScreenContent<ParkingSignSideScreen>();
    }
}

[HarmonyPatch(typeof(Db), nameof(Db.Initialize))]
public static class Db_Initialize_ParkingSignStatusItems_Patch
{
    public static void Postfix(Db __instance)
    {
        if (ParkingSignStatusItems.Active != null && ParkingSignStatusItems.Inactive != null &&
            ParkingSignStatusItems.NoStandPoint != null)
        {
            return;
        }

        ParkingSignStatusItems.Active = __instance.BuildingStatusItems.Add(new StatusItem(
            "ParkingSignActive",
            "BUILDING",
            "",
            StatusItem.IconType.Info,
            NotificationType.Neutral,
            false,
            OverlayModes.None.ID,
            false));
        ParkingSignStatusItems.Inactive = __instance.BuildingStatusItems.Add(new StatusItem(
            "ParkingSignInactive",
            "BUILDING",
            "",
            StatusItem.IconType.Info,
            NotificationType.Neutral,
            false,
            OverlayModes.None.ID,
            false));
        ParkingSignStatusItems.NoStandPoint = __instance.BuildingStatusItems.Add(new StatusItem(
            "ParkingSignNoStandPoint",
            "BUILDING",
            "status_item_missing_foundation",
            StatusItem.IconType.Custom,
            NotificationType.Bad,
            false,
            OverlayModes.None.ID,
            true,
            63486));
    }
}

[HarmonyPatch]
public static class Grid_HasPermission_ParkingSign_Patch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(Grid), nameof(Grid.HasPermission),
            new[] { typeof(int), typeof(int), typeof(int), typeof(int), typeof(NavType) });
    }

    public static bool Prefix(int cell, int minionInstanceID, int fromCell, ref bool __result)
    {
        if (!ParkingSignRegistry.ShouldBlockPathFromSign(minionInstanceID, fromCell, cell))
        {
            return true;
        }

        __result = false;
        return false;
    }
}

[HarmonyPatch]
public static class Navigator_CanReach_ParkingSign_Patch
{
    private static MethodBase TargetMethod()
    {
        return AccessTools.Method(typeof(Navigator), nameof(Navigator.CanReach), new[] { typeof(int) });
    }

    public static bool Prefix(Navigator __instance, int __0, ref bool __result)
    {
        MinionIdentity minion = __instance != null ? __instance.GetComponent<MinionIdentity>() : null;
        if (!ParkingSignRegistry.ShouldBlockPathFromSign(minion, __0))
        {
            return true;
        }

        __result = false;
        return false;
    }
}

[HarmonyPatch(typeof(MoveToLocationMonitor.Instance), nameof(MoveToLocationMonitor.Instance.MoveToLocation))]
public static class MoveToLocationMonitor_MoveToLocation_ParkingSign_Patch
{
    public static void Prefix(MoveToLocationMonitor.Instance __instance, ref int cell)
    {
        if (ParkingSignRegistry.TryGetGuidanceCell(__instance.gameObject, out int guidanceCell) &&
            cell != guidanceCell)
        {
            ParkingSignLog.Trace($"MoveToLocation overridden: requested={cell} guidance={guidanceCell}");
            cell = guidanceCell;
        }
    }
}

[HarmonyPatch(typeof(ChoreConsumer), nameof(ChoreConsumer.FindNextChore))]
public static class ChoreConsumer_FindNextChore_ParkingSign_Patch
{
    public static void Postfix(ChoreConsumer __instance, ref bool __result, ref Chore.Precondition.Context out_context)
    {
        if (!ParkingSignRegistry.TryGetGuidanceCell(__instance.gameObject, out int guidanceCell) ||
            Grid.PosToCell(__instance.gameObject) == guidanceCell)
        {
            return;
        }

        if (__result && out_context.chore != null)
        {
            if (out_context.chore.choreType == Db.Get().ChoreTypes.MoveTo)
            {
                return;
            }

            __result = false;
        }

        ChoreDriver choreDriver = __instance.gameObject.GetComponent<ChoreDriver>();
        if (choreDriver == null || choreDriver.GetCurrentChore() != null)
        {
            return;
        }

        Navigator navigator = __instance.gameObject.GetComponent<Navigator>();
        MoveToLocationMonitor.Instance moveMonitor =
            navigator != null ? navigator.GetSMI<MoveToLocationMonitor.Instance>() : null;
        if (moveMonitor != null)
        {
            moveMonitor.MoveToLocation(guidanceCell);
        }
    }
}

[HarmonyPatch(typeof(ChoreDriver), nameof(ChoreDriver.SetChore))]
public static class ChoreDriver_SetChore_ParkingSign_Patch
{
    public static bool Prefix(ChoreDriver __instance, Chore.Precondition.Context context)
    {
        if (context.chore == null || context.chore.choreType == Db.Get().ChoreTypes.MoveTo ||
            !ParkingSignRegistry.IsHeadingToGuidanceCell(__instance.gameObject))
        {
            return true;
        }

        return false;
    }
}
