using System.Collections.Generic;
using BepInEx.Configuration;
using HarmonyLib;
using Logic;
using UnityEngine;

namespace URAH_Net
{
    using Multiplayer = Logic.Multiplayer;
    using Time = UnityEngine.Time;

    /// <summary>
    /// Fix C. The game connects to the players of its campaigns only in UpdateRelevantPlayers, i.e. when
    /// lobby data changes; a connect that timed out (60 s) or a connection removed by PoolCleanupPatch is
    /// not retried otherwise. Once a second, outside running games (the game's own in-game reconnect
    /// should not be raced), every relevant player who is offline and has no meta connection (no attempt
    /// running) gets one after a delay of 2, 4, 8, 16, 30, 30, ... s, counted from when their last attempt
    /// ended. The state resets when they are online again and when we enter or leave a lobby.
    /// </summary>
    internal class Reconnect
    {
        private class Retry
        {
            public float delay, next;
            public int attempts;
        }

        private static readonly Dictionary<string, Retry> retries = new Dictionary<string, Retry>();
        private readonly ConfigEntry<int> firstDelay, maxDelay;
        private readonly List<string> players = new List<string>();
        private float nextCheck;

        public Reconnect(ConfigEntry<int> firstDelay, ConfigEntry<int> maxDelay)
        {
            this.firstDelay = firstDelay;
            this.maxDelay = maxDelay;
        }

        public static void Reset(string why)
        {
            if (retries.Count > 0)
            {
                Plugin.Log.LogInfo($"Reconnect state reset ({why})");
                retries.Clear();
            }
        }

        public void Update()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextCheck || firstDelay.Value <= 0)
            {
                return;
            }
            nextCheck = now + 1f;
            MPBoss boss = MPBoss.Get();
            MPBossMultiplayerPool pool = boss?.pool;
            if (pool == null || boss.game == null || boss.GetSystemState() != MPBoss.SystemState.Initialized || boss.IsInGame())
            {
                return;
            }
            // GetRelevantPlayers returns the game's own list, which ConnectToPlayer may refill
            players.Clear();
            players.AddRange(boss.GetRelevantPlayers());
            foreach (string id in players)
            {
                retries.TryGetValue(id, out Retry retry);
                if (boss.IsPlayerOnline(id))
                {
                    if (retry != null)
                    {
                        if (retry.attempts > 0)
                        {
                            Plugin.Log.LogInfo($"{Plugin.Who(id)} back after {retry.attempts} attempts");
                        }
                        retries.Remove(id);
                    }
                    continue;
                }
                if (retry == null)
                {
                    retries[id] = retry = new Retry { delay = firstDelay.Value };
                    retry.next = now + retry.delay;
                }
                if (pool.ContainsVirtualConnection(id, Multiplayer.ConnectionReason.Meta))
                {
                    // an attempt is running (it has its own 60 s timeout): count the delay from its end
                    retry.next = now + retry.delay;
                    continue;
                }
                if (now < retry.next)
                {
                    continue;
                }
                retry.attempts++;
                retry.delay = Mathf.Min(retry.delay * 2f, maxDelay.Value);
                retry.next = now + retry.delay;
                Plugin.Log.LogInfo($"Reconnect to {Plugin.Who(id)}, attempt {retry.attempts}, next in {retry.delay:0} s");
                pool.ConnectToPlayer(id);
            }
        }
    }

    [HarmonyPatch(typeof(MPBoss), nameof(MPBoss.EnterLobby))]
    internal static class EnterLobbyPatch
    {
        private static void Postfix()
        {
            Reconnect.Reset("entered a lobby");
        }
    }

    [HarmonyPatch(typeof(MPBoss), nameof(MPBoss.LeaveCampaign))]
    internal static class LeaveCampaignPatch
    {
        private static void Postfix()
        {
            Reconnect.Reset("left a campaign");
        }
    }
}
