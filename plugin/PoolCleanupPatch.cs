using HarmonyLib;
using Logic;

namespace URAH_Net
{
    using Multiplayer = Logic.Multiplayer;
    using VirtualConnection = MPBossMultiplayerPool.VirtualConnection;

    /// <summary>
    /// Fix B. A heartbeat timeout disconnects a meta Multiplayer without destroying it: it ends as
    /// Status.Disconnected, still IsValid(). CleanUpConnection only drops destroyed ones, so the dead
    /// client stays in the pool, UpdateMetaConnections sees an entry for the player and never connects
    /// them again (grey until restart; JoinCampaign then waits for the owner forever). This shuts such
    /// Multiplayers down after the original ran (ShutDown on a Disconnected one is the quiet path: no
    /// second connection_dropped, UpdateMetaConnections does the same for players who left the list)
    /// and removes the entry once both are gone; the original already sent NotifyOffline.
    /// Never touches Connecting / Disconnecting ones (live attempt, queued disconnect) or InGame
    /// connections (JoinGame has its own cleanup).
    /// </summary>
    [HarmonyPatch(typeof(MPBossMultiplayerPool), "CleanUpConnection")]
    internal static class PoolCleanupPatch
    {
        private static void Postfix(MPBossMultiplayerPool __instance, VirtualConnection connection)
        {
            if (connection == null || connection.connection_reason != Multiplayer.ConnectionReason.Meta
                || !Plugin.RemoveDeadConnections.Value)
            {
                return;
            }
            string client = Drop(ref connection.client);
            string serverClient = Drop(ref connection.serverClient);
            if (client == null && serverClient == null)
            {
                return;
            }
            bool removed = false;
            if (connection.client == null && connection.serverClient == null)
            {
                removed = __instance.virtualConnections?.Remove(connection) ?? false;
            }
            Plugin.Log.LogInfo($"Dead meta connection to {Plugin.Who(connection.player_id)} " +
                $"{(removed ? "removed" : "cleaned")} (client {client ?? State(connection.client)}, " +
                $"serverClient {serverClient ?? State(connection.serverClient)})");
        }

        /// <summary>Shuts down and clears a dead Multiplayer; returns its status, or null when it was left alone.</summary>
        private static string Drop(ref Multiplayer mp)
        {
            if (mp == null || mp.IsConnected())
            {
                return null;
            }
            Multiplayer.Status status = mp.GetMultiplayerStatus();
            if (status != Multiplayer.Status.Disconnected && status != Multiplayer.Status.ShutDown)
            {
                return null;
            }
            if (mp.IsValid())
            {
                mp.ShutDown();
            }
            mp = null;
            return status.ToString();
        }

        private static string State(Multiplayer mp)
        {
            return mp == null ? "none" : mp.GetMultiplayerStatus().ToString();
        }
    }
}
