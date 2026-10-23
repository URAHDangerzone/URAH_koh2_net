using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using HarmonyLib;
using Logic;

namespace URAH_Net
{
    /// <summary>
    /// Diagnostics for the game's "THQNO.ReadP2PPacket() message not completely read!": every P2P read
    /// (THQNOConnection and THQNOAcceptor) first asks IsP2PPacketAvailable for the size on its channel,
    /// then reads; when the two sizes differ the game drops out of its read loop without saying from whom
    /// or on which channel. Both calls run on the same thread under THQNORequest.LockP2P, so the announced
    /// size is kept per thread and compared in the read's postfix. Logs sender, channel, both sizes and the
    /// first bytes; "same packet again" means the read did not take it off Steam's queue and the channel
    /// is stuck. The first 10 per sender and channel are logged, then every 50th. Log only.
    /// </summary>
    [HarmonyPatch]
    internal static class ReadLogPatch
    {
        private class Stats
        {
            public int count, same;
            public string lastBytes;
        }

        [ThreadStatic] private static uint announcedChannel, announcedSize;
        [ThreadStatic] private static bool announced;
        private static readonly Dictionary<string, Stats> stats = new Dictionary<string, Stats>();

        [HarmonyPostfix]
        [HarmonyPatch(typeof(THQNORequest), nameof(THQNORequest.IsP2PPacketAvailable))]
        private static void Available(ref uint messageSize, uint channel, THQNORequest __result)
        {
            announced = __result != null && __result.result.Bool();
            announcedChannel = channel;
            announcedSize = messageSize;
        }

        [HarmonyPostfix]
        [HarmonyPatch(typeof(THQNORequest), nameof(THQNORequest.ReadP2PPacket))]
        private static void Read(IntPtr dest, uint destSize, ref uint messageSize, uint channel, string __result)
        {
            try
            {
                if (!announced || announcedChannel != channel)
                {
                    if (announced)
                    {
                        Diagnostics.Log($"P2P read on channel {channel} after size check on channel {announcedChannel}");
                    }
                    return;
                }
                announced = false;
                if (messageSize == announcedSize)
                {
                    return;
                }
                string bytes = FirstBytes(dest, Math.Min(Math.Min(messageSize, announcedSize), destSize));
                string key = $"{__result ?? "(no sender)"}:{channel}";
                Stats s;
                lock (stats)
                {
                    if (!stats.TryGetValue(key, out s))
                    {
                        stats[key] = s = new Stats();
                    }
                    s.count++;
                    if (bytes == s.lastBytes)
                    {
                        s.same++;
                    }
                    s.lastBytes = bytes;
                }
                if (s.count <= 10 || s.count % 50 == 0)
                {
                    string who = __result == null ? "(no sender)" : Plugin.Who(__result);
                    Diagnostics.Log($"P2P read mismatch #{s.count} from {who} on channel {channel}: " +
                        $"announced {announcedSize} bytes, read {messageSize} (buffer {destSize}), first bytes {bytes}" +
                        (s.same > 0 ? $", same packet again {s.same}x so far" : ""));
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"P2P read logging failed: {e}");
            }
        }

        private static string FirstBytes(IntPtr dest, uint length)
        {
            int n = (int)Math.Min(length, 16u);
            if (dest == IntPtr.Zero || n <= 0)
            {
                return "-";
            }
            byte[] buffer = new byte[n];
            Marshal.Copy(dest, buffer, 0, n);
            return BitConverter.ToString(buffer);
        }
    }
}
