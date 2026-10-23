using HarmonyLib;
using Logic;

namespace URAH_Net
{
    /// <summary>Logs presence changes, so the log shows when and how long a player was grey.</summary>
    [HarmonyPatch(typeof(MPBoss), "OnPlayerPresence")]
    internal static class PresenceLogPatch
    {
        private static void Postfix(string player_id, bool online)
        {
            Plugin.Log.LogInfo($"{Plugin.Who(player_id)} {(online ? "online" : "offline")}");
            if (!online)
            {
                SessionReset.NoteOffline(player_id);
            }
        }
    }

    /// <summary>
    /// Logs failed P2P sessions (the game only logs them at multiplayer log level 2). A player who fails
    /// with every attempt can not be reached through Steam at all (NAT / relay), which no retry fixes.
    /// </summary>
    [HarmonyPatch(typeof(THQNORequest), nameof(THQNORequest.OnP2PSessionConnectFailResponse))]
    internal static class P2PFailLogPatch
    {
        private static void Postfix(object res)
        {
            // P2PSessionConnectFailCD is internal
            var t = Traverse.Create(res);
            string remote = t.Field("remoteID").GetValue<string>();
            object error = t.Field("P2PSessionError").GetValue();
            Plugin.Log.LogWarning($"P2P connect to {Plugin.Who(remote)} failed: {error}");
        }
    }
}
