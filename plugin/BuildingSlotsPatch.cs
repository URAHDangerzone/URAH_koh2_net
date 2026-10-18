using System.Collections.Generic;
using HarmonyLib;
using Logic;

namespace URAH_Net
{
    /// <summary>
    /// Reordering buildings in multiplayer only shows after the next load (base-game bug). A client's
    /// Castle.SwapBuildingSlots / MoveToBuildingSlot send a SwapBuildingSlotsEvent / MoveBuildingToSlotEvent
    /// to the host, the host (authority) swaps its buildings list - and nobody sends the list back, also
    /// not for the host's own reorders. When the list changed on the authority, this sends the
    /// StructuresState the game itself sends after building (clients rebuild their list from it). Only the
    /// host needs it; SendState does nothing in single player.
    /// </summary>
    [HarmonyPatch]
    internal static class BuildingSlotsPatch
    {
        [HarmonyPatch(typeof(Castle), nameof(Castle.SwapBuildingSlots))]
        [HarmonyPrefix]
        private static void SwapPrefix(Castle __instance, out List<Building> __state)
        {
            __state = Snapshot(__instance);
        }

        [HarmonyPatch(typeof(Castle), nameof(Castle.SwapBuildingSlots))]
        [HarmonyPostfix]
        private static void SwapPostfix(Castle __instance, List<Building> __state)
        {
            SendIfChanged(__instance, __state);
        }

        [HarmonyPatch(typeof(Castle), nameof(Castle.MoveToBuildingSlot))]
        [HarmonyPrefix]
        private static void MovePrefix(Castle __instance, out List<Building> __state)
        {
            __state = Snapshot(__instance);
        }

        [HarmonyPatch(typeof(Castle), nameof(Castle.MoveToBuildingSlot))]
        [HarmonyPostfix]
        private static void MovePostfix(Castle __instance, List<Building> __state)
        {
            SendIfChanged(__instance, __state);
        }

        /// <summary>A copy of the list, only where a change has to be sent (authority in multiplayer).</summary>
        private static List<Building> Snapshot(Castle castle)
        {
            if (!Plugin.SyncBuildingSlots.Value || castle?.buildings == null || castle.game?.multiplayer == null
                || !castle.IsAuthority())
            {
                return null;
            }
            return new List<Building>(castle.buildings);
        }

        private static void SendIfChanged(Castle castle, List<Building> before)
        {
            if (before == null || !Changed(before, castle.buildings))
            {
                return;
            }
            castle.SendState<Castle.StructuresState>();
            Plugin.Log.LogInfo($"Building slots of {castle} changed, structures sent");
        }

        private static bool Changed(List<Building> before, List<Building> after)
        {
            if (after == null || before.Count != after.Count)
            {
                return true;
            }
            for (int i = 0; i < before.Count; i++)
            {
                if (before[i] != after[i])
                {
                    return true;
                }
            }
            return false;
        }
    }
}
