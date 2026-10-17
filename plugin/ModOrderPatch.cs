using System;
using HarmonyLib;
using Logic;

namespace URAH_Net
{
    /// <summary>
    /// The mod list every machine sends ("name/sha1;name/sha1", ModManager.GetActiveModsSaveString) lists
    /// the active mods in the order they were switched on in the Mods menu, and starting a multiplayer
    /// game compares the players' lists as plain strings (Campaign, "client_version_mismatch"). Two
    /// players with the same mods switched on in a different order could not start. This sends the list
    /// sorted. The order the mods load in stays as it is; it hardly matters, the game does not let mods
    /// that change the same defs, texts or files be active together (ModManager.ActivateMod).
    /// Lobby browser and save games compare mods by checksum, they do not care about the order either way.
    /// Only helps when everyone runs it: an unsorted list still differs from a sorted one.
    /// </summary>
    [HarmonyPatch(typeof(ModManager), nameof(ModManager.GetActiveModsSaveString))]
    internal static class ModOrderPatch
    {
        private static void Postfix(ref string __result)
        {
            if (string.IsNullOrEmpty(__result) || __result.IndexOf(';') < 0)
            {
                return;
            }
            string[] mods = __result.Split(';');
            Array.Sort(mods, StringComparer.Ordinal);
            __result = string.Join(";", mods);
        }
    }
}
