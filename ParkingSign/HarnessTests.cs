#if DEBUG && HARNESS
// In-game tests run by Gravitas.TestHarness:
//   python Gravitas.TestProbe/probe.py run --project <this repo> --tests '{"tags":["parkingsign"]}'
using System.Collections;
using System.Reflection;
using Gravitas.TestHarness;
using UnityEngine;

namespace ParkingSign;

internal static class HarnessTests
{
    [HarnessTest(Name = "parkingsign.buildings_registered", Tags = "parkingsign")]
    public static void BuildingsRegistered()
    {
        Check.NotNull(Assets.GetBuildingDef(ParkingSignConfig.ID), "ParkingSign building def");
        Check.NotNull(Assets.GetBuildingDef(HangingParkingSignConfig.ID), "HangingParkingSign building def");
        Check.NoLogErrors();
    }

    // Two signs selected for one duplicant: the first-built wins a priority tie. When the winner is walled off, the
    // next reachable sign must take over without any signal / priority / selection change (re-checked from Sim1000ms).
    [HarnessTest(Name = "parkingsign.unreachable_sign_hands_over", Tags = "parkingsign")]
    public static IEnumerator UnreachableSignHandsOver()
    {
        yield return Wait.Ticks(5);
        var home = Arrange.HomeCell();
        // The test save may be an empty world: lay our own tile floor (OnFloor buildings need a constructed tile).
        var floorY = Grid.OffsetCell(home, 0, 0);
        var tiles = new System.Collections.Generic.List<GameObject>();
        for (int dx = -12; dx <= 12; dx++)
        {
            tiles.Add(Arrange.Build("Tile", Grid.OffsetCell(floorY, dx, -1)));
        }

        yield return Wait.Ticks(3);
        var signA = Arrange.Build(ParkingSignConfig.ID, Grid.OffsetCell(floorY, -6, 0));
        var signB = Arrange.Build(ParkingSignConfig.ID, Grid.OffsetCell(floorY, 6, 0));
        var dupe = Arrange.SpawnDuplicant(floorY);
        var walls = new System.Collections.Generic.List<(int cell, SimHashes element, float mass, float temp)>();
        try
        {
            yield return Wait.Ticks(3);
            var a = signA.GetComponent<ParkingSignController>();
            var b = signB.GetComponent<ParkingSignController>();
            Check.NotNull(a, "controller A");
            Check.NotNull(b, "controller B");
            var field = typeof(ParkingSignController).GetField("lastSignalState", BindingFlags.NonPublic | BindingFlags.Instance);
            field.SetValue(a, 1);
            field.SetValue(b, 1);
            var id = dupe.GetComponent<KPrefabID>().InstanceID;
            a.SetMinionSelected(id, true);
            b.SetMinionSelected(id, true);
            Check.True(ParkingSignRegistry.GetCachedWinningSign(id) == a, "first-built sign should win the tie");

            // Seal sign A's standing cell inside solid rock.
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -1; dy <= 1; dy++)
                {
                    int c = Grid.OffsetCell(a.TargetCell, dx, dy);
                    if (Grid.IsValidCell(c) && !Grid.Element[c].IsSolid && c != a.TargetCell)
                    {
                        walls.Add((c, Grid.Element[c].id, Grid.Mass[c], Grid.Temperature[c]));
                        Arrange.SetElement(c, SimHashes.Granite, 1000f, 293f);
                    }
                }
            }

            yield return Wait.Seconds(10);
            var nav = dupe.GetComponent<Navigator>();
            var w = ParkingSignRegistry.GetCachedWinningSign(id);
            string diag = " winner=" + (w == a ? "A" : w == b ? "B" : "none") + " aTarget=" + Arrange.XY(a.TargetCell) + " bTarget=" + Arrange.XY(b.TargetCell) +
                          " dupe=" + Arrange.XY(Grid.PosToCell(dupe)) + " aSolid=" + Grid.Element[Grid.OffsetCell(a.TargetCell, 1, 0)].id +
                          " signal=" + a.IsSignalActive + b.IsSignalActive + " canReachA=" + nav.CanReach(a.TargetCell) + " canReachB=" + nav.CanReach(b.TargetCell);
            Check.True(w == b, "the reachable sign should take over after A is sealed" + diag);
        }
        finally
        {
            foreach (var w in walls)
            {
                Arrange.SetElement(w.cell, w.element, w.mass, w.temp);
            }

            Arrange.Destroy(dupe);
            foreach (var t in tiles)
            {
                Arrange.Destroy(t);
            }

            Arrange.Destroy(signA);
            Arrange.Destroy(signB);
        }
    }
}
#endif
