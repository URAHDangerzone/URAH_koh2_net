using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Logic;
using UnityEngine;

namespace URAH_Net
{
    using Multiplayer = Logic.Multiplayer;
    using Time = UnityEngine.Time;
    using VirtualConnection = MPBossMultiplayerPool.VirtualConnection;

    /// <summary>
    /// Log-only tracking of a campaign join. MPBoss.JoinCampaignCoro first waits up to 60 s until the owner
    /// is online (abort button still active), then sends the join request (abort greys out,
    /// canCancelJoinCampaing = false). A join that hangs with the abort button active is stuck in that
    /// first wait: no working meta connection to the owner. This logs the owner's pool entries when the
    /// join starts, every 5 s while it runs, when the request goes out, and how it ended.
    /// </summary>
    internal static class JoinTracker
    {
        private static readonly FieldInfo coroField = AccessTools.Field(typeof(MPBoss), "join_campaing_coro");

        private static Campaign campaign;
        private static string campaignId, ownerId;
        private static float started, nextLog;
        private static bool requestSent, cancelled;

        public static void Begin(Campaign c, string id, string owner)
        {
            campaign = c;
            campaignId = id;
            ownerId = owner;
            started = Time.realtimeSinceStartup;
            nextLog = started + 5f;
            requestSent = false;
            cancelled = false;
            Diagnostics.Log($"Join {campaignId} started, owner {Owner()}");
        }

        public static void Cancelled()
        {
            if (campaign != null)
            {
                cancelled = true;
            }
        }

        /// <summary>From the plugin's Update.</summary>
        public static void Update()
        {
            if (campaign == null)
            {
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (!requestSent && !MPBoss.canCancelJoinCampaing)
            {
                requestSent = true;
                Diagnostics.Log($"Join {campaignId}: owner online after {now - started:0.0} s, join request sent");
            }
            if (coroField?.GetValue(null) == null)
            {
                MPBoss boss = MPBoss.Get();
                bool joined = boss != null && boss.GetCampaignIndex(campaignId) >= 0 && campaign.GetLocalPlayerIndex() >= 0;
                string how = joined ? "joined" : cancelled ? "cancelled by the player" : requestSent ? "failed after the request" : "failed waiting for the owner";
                Diagnostics.Log($"Join {campaignId} {how} after {now - started:0.0} s, owner {Owner()}");
                campaign = null;
                return;
            }
            if (now - started > 120f)
            {
                // the coroutine handle can outlive a join that ended right away
                Diagnostics.Log($"Join {campaignId} still not finished after 120 s, no longer tracked, owner {Owner()}");
                campaign = null;
                return;
            }
            if (now >= nextLog)
            {
                nextLog = now + 5f;
                Diagnostics.Log($"Join {campaignId} waiting {now - started:0} s, owner {Owner()}");
            }
        }

        /// <summary>Online state and every pool entry of the owner.</summary>
        private static string Owner()
        {
            MPBoss boss = MPBoss.Get();
            var sb = new StringBuilder(Plugin.Who(ownerId));
            sb.Append(boss != null && boss.IsPlayerOnline(ownerId) ? " online" : " offline");
            List<VirtualConnection> entries = boss?.pool?.virtualConnections;
            int n = 0;
            if (entries != null)
            {
                foreach (VirtualConnection vc in entries)
                {
                    if (vc == null || vc.player_id != ownerId)
                    {
                        continue;
                    }
                    n++;
                    sb.Append($", {vc.connection_reason} entry: client {State(vc.client)}, serverClient {State(vc.serverClient)}");
                }
            }
            if (n == 0)
            {
                sb.Append(", no pool entry");
            }
            return sb.ToString();
        }

        private static string State(Multiplayer mp)
        {
            if (mp == null)
            {
                return "none";
            }
            return $"{mp.GetMultiplayerStatus()}{(mp.IsValid() ? "" : " (destroyed)")}";
        }
    }

    [HarmonyPatch(typeof(MPBoss), nameof(MPBoss.JoinCampaign), new[] { typeof(Campaign), typeof(string), typeof(string),
        typeof(Logic.Multiplayer.JoinReason), typeof(string), typeof(Action<Campaign>) })]
    internal static class JoinCampaignPatch
    {
        private static void Postfix(Campaign campaign, string campaign_id, string owner_id, Logic.Coroutine __result)
        {
            if (__result != null)
            {
                JoinTracker.Begin(campaign, campaign_id, owner_id);
            }
        }
    }

    [HarmonyPatch(typeof(MPBoss), nameof(MPBoss.CancelJoinCampaignCoro))]
    internal static class CancelJoinPatch
    {
        private static void Prefix()
        {
            JoinTracker.Cancelled();
        }
    }
}
