using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using Logic;

namespace URAH_Net
{
    /// <summary>
    /// Heals the multiplayer lobby's P2P meta connections (base-game bug):
    /// a player whose heartbeat timed out stays grey until restart because the dead connection stays in
    /// MPBoss.pool and blocks every new one (PoolCleanupPatch), and a failed connect is never retried
    /// (Reconnect). Also fixes the lobby's mod list: read in full (LobbyModIdPatch) and sent sorted, so
    /// the order mods were switched on in does not matter (ModOrderPatch). Always on, no ModLink: the game
    /// does not compare plugins in multiplayer, so players with and without it still play together;
    /// every fix only acts on the machine that runs it.
    ///
    /// Patching MPBoss, MPBossMultiplayerPool, THQNORequest, UILobbyBrowser and ModManager at load is
    /// safe: none has a static constructor that reads defs, settings or the ModManager (unlike
    /// UserSettings, see URAH_Zoom).
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "urah.koh2.net";
        public const string Name = "URAH_Net";
        public const string Version = "0.2.0";

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> RemoveDeadConnections;

        private ConfigEntry<int> heartbeatTimeout;
        private Reconnect reconnect;
        private bool reconnectFailed;

        private void Awake()
        {
            Log = Logger;
            heartbeatTimeout = Config.Bind("Heartbeat", "TimeoutMs", 0,
                new ConfigDescription("Milliseconds without a heartbeat before a connection counts as lost (0 = keep the game's 5000). " +
                    "Only lengthens this machine's patience, the other side still drops after its own timeout",
                    new AcceptableValueRange<int>(0, 60000)));
            RemoveDeadConnections = Config.Bind("Pool", "RemoveDeadConnections", true,
                "Throw dead lobby connections out of the pool so the player can be connected again");
            ConfigEntry<int> firstDelay = Config.Bind("Reconnect", "FirstDelaySeconds", 2,
                new ConfigDescription("Seconds before the first reconnect to a player who went offline in the lobby (0 = no reconnects)",
                    new AcceptableValueRange<int>(0, 300)));
            ConfigEntry<int> maxDelay = Config.Bind("Reconnect", "MaxDelaySeconds", 30,
                new ConfigDescription("The delay doubles after every attempt up to this many seconds",
                    new AcceptableValueRange<int>(1, 600)));
            ConfigEntry<bool> sortMods = Config.Bind("Mods", "SortModList", true,
                "Send the active mod list sorted, so players who switched the same mods on in a different order can start a game together");
            reconnect = new Reconnect(firstDelay, maxDelay);

            HeartbeatPatch.Apply(heartbeatTimeout.Value);
            var harmony = new Harmony(Guid);
            Patch(harmony, typeof(PoolCleanupPatch));
            Patch(harmony, typeof(EnterLobbyPatch));
            Patch(harmony, typeof(LeaveCampaignPatch));
            Patch(harmony, typeof(PresenceLogPatch));
            Patch(harmony, typeof(P2PFailLogPatch));
            Patch(harmony, typeof(LobbyModIdPatch));
            if (sortMods.Value)
            {
                Patch(harmony, typeof(ModOrderPatch));
            }
            Log.LogInfo($"{Name} {Version} loaded, reconnect {firstDelay.Value} s up to {maxDelay.Value} s, " +
                $"dead connection cleanup {(RemoveDeadConnections.Value ? "on" : "off")}, " +
                $"sorted mod list {(sortMods.Value ? "on" : "off")}");
        }

        /// <summary>Each patch on its own, so one that no longer fits the game does not take the others with it.</summary>
        private static void Patch(Harmony harmony, Type patch)
        {
            try
            {
                harmony.CreateClassProcessor(patch).Patch();
            }
            catch (Exception e)
            {
                Log.LogError($"Patch {patch.Name} failed: {e}");
            }
        }

        private void Update()
        {
            if (reconnectFailed)
            {
                return;
            }
            try
            {
                reconnect.Update();
            }
            catch (Exception e)
            {
                Log.LogError($"Reconnect failed: {e}");
                reconnectFailed = true;
            }
        }

        /// <summary>"name (id)" for the log.</summary>
        internal static string Who(string playerId)
        {
            string name = PlayerInfo.Get(playerId, create: false)?.name;
            return string.IsNullOrEmpty(name) ? playerId : $"{name} ({playerId})";
        }
    }
}
