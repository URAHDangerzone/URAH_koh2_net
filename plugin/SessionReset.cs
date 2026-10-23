using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using Logic;
using UnityEngine;

namespace URAH_Net
{
    using Time = UnityEngine.Time;

    /// <summary>
    /// Manual reset of every P2P session, outside running games (main menu or lobby), on [Session] ResetKey.
    /// The game only ever closes single channels (CloseP2PChannelWithUser); once Steam's session with a
    /// player is broken, every reconnect on it fails (P2PSessionConnectFail: Timeout) until a restart.
    /// Three steps, a second apart: shut down every connection in MPBoss.pool (as the pool's own CleanUp
    /// does, which also sends the players a DISCONNECT) and take them out of the pool; close the whole
    /// Steam session with each of those players (THQNO_Wrapper.CloseP2PSessionWithUser, which the game has
    /// but never calls); connect to the relevant players again on fresh sessions. A player's game answers
    /// the DISCONNECT with a new connection within ~0.2 s, still on the old session, which the close then
    /// cuts: that player's side only notices after its 5 s heartbeat timeout and connects again then, so a
    /// player who was fine is grey for ~6 s (shutting the cut connection down too does not shorten it).
    /// </summary>
    internal static class SessionReset
    {
        private static int step;
        private static float next;
        private static readonly List<string> players = new List<string>();
        private static readonly MethodInfo notifyOffline = AccessTools.Method(typeof(MPBossMultiplayerPool), "NotifyOffline");

        internal static ConfigEntry<KeyboardShortcut> Key;

        /// <summary>Reconnect waits while a reset runs, so it does not connect between its steps.</summary>
        internal static bool Running => step != 0;

        /// <summary>A player of the entered campaign went offline since we entered it or since the last reset (the lobby button's state).</summary>
        internal static bool DropSeen;

        /// <summary>From PresenceLogPatch; the reset's own offline marking does not count.</summary>
        internal static void NoteOffline(string playerId)
        {
            if (Running)
            {
                return;
            }
            Campaign campaign = MPBoss.Get()?.currently_entered_campaign;
            if (campaign != null && campaign.GetPlayerIndex(playerId) >= 0)
            {
                DropSeen = true;
            }
        }

        public static void Update()
        {
            if (step == 0)
            {
                if (Key.Value.IsDown())
                {
                    Start();
                }
                return;
            }
            if (Time.realtimeSinceStartup < next)
            {
                return;
            }
            next = Time.realtimeSinceStartup + 1f;
            MPBoss boss = MPBoss.Get();
            MPBossMultiplayerPool pool = boss?.pool;
            if (pool == null || boss.IsInGame())
            {
                Plugin.Log.LogWarning("Session reset stopped: no lobby system or a game started");
                step = 0;
                return;
            }
            if (step == 1)
            {
                lock (THQNORequest.LockP2P)
                {
                    foreach (string id in players)
                    {
                        bool closed = THQNO_Wrapper.CloseP2PSessionWithUser(id);
                        Plugin.Log.LogInfo($"Steam session with {Plugin.Who(id)} closed: {closed}");
                    }
                }
                step = 2;
                return;
            }
            Reconnect.Reset("session reset");
            foreach (string id in boss.GetRelevantPlayers().ToList())
            {
                if (!pool.ContainsVirtualConnection(id, Logic.Multiplayer.ConnectionReason.Meta))
                {
                    Plugin.Log.LogInfo($"Session reset: connecting to {Plugin.Who(id)}");
                    pool.ConnectToPlayer(id);
                }
            }
            Plugin.Log.LogInfo("Session reset done");
            step = 0;
        }

        internal static void Start()
        {
            if (Running)
            {
                return;
            }
            MPBoss boss = MPBoss.Get();
            MPBossMultiplayerPool pool = boss?.pool;
            if (pool == null || pool.virtualConnections == null)
            {
                Plugin.Log.LogWarning("Session reset: no lobby system yet");
                return;
            }
            if (boss.IsInGame())
            {
                Plugin.Log.LogWarning("Session reset: not during a running game");
                return;
            }
            players.Clear();
            players.AddRange(pool.virtualConnections.Where(v => v != null).Select(v => v.player_id));
            players.AddRange(boss.GetRelevantPlayers());
            players.RemoveAll(string.IsNullOrEmpty);
            List<string> distinct = players.Distinct().ToList();
            players.Clear();
            players.AddRange(distinct);
            DropSeen = false;
            Plugin.Log.LogInfo($"Session reset: players {string.Join(", ", players.Select(Plugin.Who))}");
            ShutDown(pool);
            step = 1;
            next = Time.realtimeSinceStartup + 1f;
        }

        /// <summary>
        /// Shuts down every connection in the pool, as its own CleanUp does, takes it out and marks the player
        /// offline as the pool does for a lost connection: only the change back to online makes MPBoss send
        /// the player our lobby data again (OnPlayerPresence -> SendRelevantDataTo); without it the player
        /// is connected again but sees us offline.
        /// </summary>
        private static void ShutDown(MPBossMultiplayerPool pool)
        {
            List<MPBossMultiplayerPool.VirtualConnection> connections = pool.virtualConnections.Where(v => v != null).ToList();
            if (connections.Count == 0)
            {
                return;
            }
            Plugin.Log.LogInfo($"Session reset: shutting down {connections.Count} connection(s)");
            foreach (MPBossMultiplayerPool.VirtualConnection v in connections)
            {
                v.client?.ShutDown();
                v.serverClient?.ShutDown();
                pool.Remove(v);
                notifyOffline.Invoke(pool, new object[] { v.player_id });
            }
        }
    }
}
