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
    /// the order mods were switched on in does not matter (ModOrderPatch), and sends reordered building
    /// slots to the other players (BuildingSlotsPatch). Keeps the lobby working when its slot action icons
    /// fail after a long game (SlotActionsPatch). Diagnostics (log only) watch heartbeats, locks and
    /// send queues for the leave cascade, and campaign joins. No ModLink: the game does not compare
    /// plugins in multiplayer, so players with and without it still play together; every fix only acts
    /// on the machine that runs it. [General] Enabled switches it all on and off at runtime (the
    /// URAH_PluginManager convention): patches off, heartbeat timeout back, no reconnects, watchdog stopped.
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
        public const string Version = URAH.BuildVersion.Value; // plugin/version.txt

        internal static ManualLogSource Log;
        internal static ConfigEntry<bool> RemoveDeadConnections;
        internal static ConfigEntry<bool> SyncBuildingSlots;

        private ConfigEntry<bool> enabledSetting, sortMods;
        private ConfigEntry<int> heartbeatTimeout;
        private Harmony harmony;
        private Reconnect reconnect;
        private bool active, reconnectFailed, diagnostics, diagnosticsFailed;

        private void Awake()
        {
            Log = Logger;
            enabledSetting = Config.Bind("General", "Enabled", true,
                "Switch the plugin on and off (takes effect right away)");
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
            sortMods = Config.Bind("Mods", "SortModList", true,
                "Send the active mod list sorted, so players who switched the same mods on in a different order can start a game together");
            SyncBuildingSlots = Config.Bind("Buildings", "SyncSlots", true,
                "As host, send reordered building slots to the other players right away (the game only shows them after the next load)");
            diagnostics = Config.Bind("Diagnostics", "Enabled", true,
                "Log heartbeat gaps, held locks, send queues, main thread stalls and campaign joins (log only, changes nothing)").Value;
            reconnect = new Reconnect(firstDelay, maxDelay);
            harmony = new Harmony(Guid);
            enabledSetting.SettingChanged += (sender, e) => Apply();
            Apply();
            Log.LogInfo($"{Name} {Version} loaded, {(enabledSetting.Value ? "enabled" : "disabled")}, " +
                $"reconnect {firstDelay.Value} s up to {maxDelay.Value} s, " +
                $"dead connection cleanup {(RemoveDeadConnections.Value ? "on" : "off")}, " +
                $"sorted mod list {(sortMods.Value ? "on" : "off")}, building slot sync {(SyncBuildingSlots.Value ? "on" : "off")}, " +
                $"diagnostics {(diagnostics ? "on" : "off")}");
        }

        /// <summary>Everything on while Enabled is on; off undoes the patches and the non-Harmony parts.</summary>
        private void Apply()
        {
            if (enabledSetting.Value == active)
            {
                return;
            }
            active = enabledSetting.Value;
            if (!active)
            {
                harmony.UnpatchSelf();
                HeartbeatPatch.Restore();
                Diagnostics.Stop();
                JoinTracker.Stop();
                Reconnect.Reset("switched off");
                Log.LogInfo("Switched off");
                return;
            }
            reconnectFailed = diagnosticsFailed = false;
            HeartbeatPatch.Apply(heartbeatTimeout.Value);
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
            Patch(harmony, typeof(BuildingSlotsPatch));
            Patch(harmony, typeof(IconPatch));
            Patch(harmony, typeof(SlotActionsPatch));
            if (diagnostics)
            {
                Patch(harmony, typeof(UngracefulLogPatch));
                Patch(harmony, typeof(JoinCampaignPatch));
                Patch(harmony, typeof(CancelJoinPatch));
                Diagnostics.Start();
            }
            Log.LogInfo("Switched on");
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
            if (!active)
            {
                return;
            }
            if (diagnostics && !diagnosticsFailed)
            {
                Diagnostics.MainTick();
                try
                {
                    JoinTracker.Update();
                }
                catch (Exception e)
                {
                    Log.LogError($"Join tracking failed: {e}");
                    diagnosticsFailed = true;
                }
            }
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
