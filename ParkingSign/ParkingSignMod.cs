using HarmonyLib;
using KMod;
using PeterHan.PLib.Core;
using PeterHan.PLib.Database;
using UnityEngine;

namespace ParkingSign;

public sealed class ParkingSignMod : UserMod2
{
    public override void OnLoad(Harmony harmony)
    {
        base.OnLoad(harmony);
        Debug.LogWarning("[ParkingSign] OnLoad: debug build with entry logs loaded.");
        PUtil.InitLibrary();
        new PLocalization().Register();
    }

    [HarmonyPatch(typeof(Localization), nameof(Localization.Initialize))]
    private static class Localization_Initialize_Patch
    {
        private static void Postfix()
        {
            Localization.RegisterForTranslation(typeof(STRINGS));
            LocString.CreateLocStringKeys(typeof(STRINGS), null);
        }
    }
}