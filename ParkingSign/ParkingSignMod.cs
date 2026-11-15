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
        // Runs after PLib's own Localization.Initialize postfix, which applies the translation. The keys have to be
        // (re)created afterwards so the Strings database (building names, descriptions, ...) gets the translated text.
        [HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            Localization.RegisterForTranslation(typeof(STRINGS));
            LocString.CreateLocStringKeys(typeof(STRINGS), null);
        }
    }
}