namespace ParkingSign;

/// <summary>Works out which cell a duplicant should stand on for a given sign.</summary>
internal static class ParkingSignTargetFinder
{
    private const int MaxHangingDrop = 10;
    private const string MinionNavGridName = "MinionNavGrid";

    /// <summary>A floor sign is its own target; a hanging sign targets the first standable cell below it.</summary>
    internal static int FindTargetCell(int signCell, bool isHanging)
    {
        return isHanging ? FindHangingTargetCell(signCell) : signCell;
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
}
