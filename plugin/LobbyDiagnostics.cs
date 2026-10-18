using System;
using System.Collections;
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

    [HarmonyPatch(typeof(MPBoss), nameof(MPBoss.JoinCampaign))]
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

    /// <summary>
    /// Log-only, for the lobby that opens without the map after a long game (TODO.md):
    /// MPBoss.CreateNewCampaign died with a NullReferenceException in UILobbyPlayers+UIActionIcon.Create,
    /// called from UISlotActions.BuildActions. This logs the exception with the state of the prototype,
    /// the container and the object pool entry of the prototype, and lets the exception through unchanged.
    /// </summary>
    [HarmonyPatch]
    internal static class SlotActionsPatch
    {
        private static readonly FieldInfo freedField = AccessTools.Field(typeof(ObjectPool), "freed");

        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(AccessTools.TypeByName("UILobbyPlayers+UISlotActions"), "BuildActions");
        }

        private static Exception Finalizer(Exception __exception, object __instance)
        {
            if (__exception == null)
            {
                return null;
            }
            try
            {
                var t = Traverse.Create(__instance);
                var prototype = t.Field("m_ActionIconPrototype").GetValue<GameObject>();
                var container = t.Field("m_ActionsContainer").GetValue<RectTransform>();
                int slot = t.Field("slotIndex").GetValue<int>();
                List<string> actions = MPBoss.Get()?.GetValidSlotActions(slot);
                Diagnostics.Log($"Lobby slot actions failed: slot {slot}, prototype {Unity(prototype)}, container {Unity(container)}, " +
                    $"actions [{(actions == null ? "none" : string.Join(", ", actions))}], pool {Pool(prototype)}\n{__exception}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Lobby slot actions failed ({__exception.Message}), logging it failed too: {e}");
            }
            return __exception;
        }

        /// <summary>Unity objects compare equal to null once destroyed; this tells both apart.</summary>
        private static string Unity(UnityEngine.Object obj)
        {
            if ((object)obj == null)
            {
                return "null";
            }
            return obj == null ? "destroyed" : $"'{obj.name}'";
        }

        /// <summary>Freed objects ObjectPool keeps for the prototype, and how many of them are destroyed.</summary>
        private static string Pool(GameObject prototype)
        {
            if ((object)prototype == null || !(freedField?.GetValue(null) is IDictionary freed))
            {
                return "-";
            }
            if (!freed.Contains(prototype) || !(freed[prototype] is IList list))
            {
                return "no freed objects";
            }
            int destroyed = 0;
            foreach (object entry in list)
            {
                if (!(Traverse.Create(entry).Field("obj").GetValue() is GameObject go) || go == null)
                {
                    destroyed++;
                }
            }
            return $"{list.Count} freed, {destroyed} destroyed";
        }
    }
}
