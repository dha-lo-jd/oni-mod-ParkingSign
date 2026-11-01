using UnityEngine;

namespace ParkingSign;

/// <summary>Owns the translucent duplicant marker displayed at a hanging sign's standing cell.</summary>
internal sealed class ParkingSignGhost
{
    private GameObject ghost;

    internal bool IsVisible => ghost != null && ghost.activeSelf;

    internal void Show(int cell)
    {
        if (!Grid.IsValidCell(cell))
        {
            Hide();
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

        ghost.transform.SetPosition(Grid.CellToPosCBC(cell, Grid.SceneLayer.FXFront2));
        ghost.SetActive(true);
    }

    internal void Hide()
    {
        if (ghost != null)
        {
            ghost.SetActive(false);
        }
    }

    internal void Destroy()
    {
        if (ghost != null)
        {
            Object.Destroy(ghost);
            ghost = null;
        }
    }
}
