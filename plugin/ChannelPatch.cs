using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using Logic;

namespace URAH_Net
{
    /// <summary>
    /// Keeps every P2P channel this machine listens on at 255 or below (base-game bug). THQNO_Wrapper's
    /// native ReadP2PPacket cuts its channel to one byte (movzx ebx, r9b) while IsP2PPacketAvailable and
    /// SendP2PPacket take the full number: on channel 262 Steam announces the packet, the read looks at
    /// channel 6, gets nothing (sender 2_0_0, 0 bytes, "message not completely read!") and the connection
    /// dies after its 5 s heartbeat timeout, again with every reconnect. The game counts channels up for
    /// the whole session (THQNOAcceptor.lastChannelUsed, static) and jumps to whatever an INCORRECT_CHANNEL
    /// asks for, so long sessions, many reconnects or a player whose game remembers a high channel of ours
    /// push it past 255 (2026-10-27: Löwenzahn asked for 261, every lobby connection after that broke).
    ///
    /// New connections whose own channel is above 255 or already used by a live connection get the next
    /// free one in 1..255 instead (wrapping around), and the game's counter is set to it. The other side's
    /// acceptor wants every CONNECT channel above the last one it saw from us (else INCORRECT_CHANNEL), so
    /// AcceptWrapPatch lets ours accept a channel that wrapped around: far below the last one (by 128 or
    /// more) or at most 255 after one above 255. A player without this fix answers a wrapped channel with
    /// INCORRECT_CHANNEL and the last one it saw, which we then follow (no wrap while answering one); when
    /// that is above 255 the connection breaks as before, until that player restarts the game.
    /// </summary>
    internal static class ChannelGuard
    {
        internal const int MaxChannel = 255;
        private const long NewConnectionMs = 10000;

        private struct Entry
        {
            public THQNOConnection connection;
            public long created;
        }

        private static readonly object Lock = new object();
        private static readonly List<Entry> live = new List<Entry>();
        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static int cursor;

        /// <summary>Set while Multiplayer.OnIncorrectChannel runs: the other side named the lowest channel it accepts.</summary>
        [ThreadStatic] internal static bool answeringIncorrectChannel;
        [ThreadStatic] internal static string incorrectChannelFrom;

        internal static void OnCreated(THQNOConnection connection)
        {
            int channel = connection.ownChannel;
            if (channel <= 0)
            {
                return; // -1 = none, 0 = THQNOAcceptor.ServerChannel
            }
            lock (Lock)
            {
                long now = clock.ElapsedMilliseconds;
                live.RemoveAll(e => !InUse(e, now));
                int chosen = channel;
                if (channel > MaxChannel || Used(channel))
                {
                    chosen = answeringIncorrectChannel ? FreeFrom(channel, wrap: false) : FreeFrom(cursor + 1, wrap: true);
                    if (chosen > 0)
                    {
                        Plugin.Log.LogInfo($"Own P2P channel {chosen} instead of {channel} ({(channel > MaxChannel ? "above 255" : "in use")}) " +
                            $"for {Plugin.Who(connection.GetTarget())}");
                        connection.ownChannel = chosen;
                        THQNOAcceptor.lastChannelUsed = chosen;
                    }
                    else
                    {
                        chosen = channel;
                        if (answeringIncorrectChannel && channel > MaxChannel)
                        {
                            Plugin.Log.LogWarning($"{Plugin.Who(incorrectChannelFrom ?? connection.GetTarget())} only accepts our P2P channels above " +
                                $"{channel - 1}, which this machine can not read: the connection will fail until that player restarts " +
                                "the game or runs URAH_Net 0.5.0 or later");
                        }
                        else
                        {
                            Plugin.Log.LogWarning($"No free P2P channel up to 255, {Plugin.Who(connection.GetTarget())} stays on {channel}");
                        }
                    }
                }
                if (chosen <= MaxChannel)
                {
                    cursor = chosen;
                }
                live.Add(new Entry { connection = connection, created = now });
            }
        }

        /// <summary>The first channel from <paramref name="from"/> to 255 (then from 1 on when wrapping) no live connection listens on, 0 if none.</summary>
        private static int FreeFrom(int from, bool wrap)
        {
            if (from < 1 || from > MaxChannel)
            {
                if (!wrap)
                {
                    return 0;
                }
                from = 1;
            }
            int count = wrap ? MaxChannel : MaxChannel - from + 1;
            for (int i = 0; i < count; i++)
            {
                int c = (from - 1 + i) % MaxChannel + 1;
                if (!Used(c))
                {
                    return c;
                }
            }
            return 0;
        }

        private static bool Used(int channel)
        {
            foreach (Entry e in live)
            {
                if (e.connection.ownChannel == channel)
                {
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// A connection listens until it is disconnected or its multiplayer shuts down. One that has no
        /// multiplayer yet counts for its first seconds (it gets one right after being created).
        /// </summary>
        private static bool InUse(Entry e, long now)
        {
            THQNOConnection c = e.connection;
            if (!c.IsConnected())
            {
                return false;
            }
            Logic.Multiplayer mp = c.multiplayer;
            if (mp == null)
            {
                return now - e.created < NewConnectionMs;
            }
            Logic.Multiplayer.Status status = mp.GetMultiplayerStatus();
            return status != Logic.Multiplayer.Status.Disconnected && status != Logic.Multiplayer.Status.ShutDown;
        }

        /// <summary>
        /// Whether our acceptor takes a CONNECT on <paramref name="channel"/> although it is not above the
        /// last one that player used: a sender that keeps its channels at 255 or below wrapped around.
        /// </summary>
        internal static bool Wrapped(int channel, int last)
        {
            return channel >= 1 && channel <= MaxChannel && (last > MaxChannel || last - channel >= 128);
        }
    }

    [HarmonyPatch(typeof(THQNOConnection), MethodType.Constructor,
        new[] { typeof(Connection.ConnectionType), typeof(string), typeof(int), typeof(int) })]
    internal static class ChannelPatch
    {
        private static void Postfix(THQNOConnection __instance)
        {
            try
            {
                ChannelGuard.OnCreated(__instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"P2P channel guard failed: {e}");
            }
        }
    }

    [HarmonyPatch(typeof(Logic.Multiplayer), "OnIncorrectChannel")]
    internal static class IncorrectChannelPatch
    {
        private static void Prefix(Connection ___connection)
        {
            ChannelGuard.answeringIncorrectChannel = true;
            ChannelGuard.incorrectChannelFrom = ___connection?.GetTarget();
        }

        private static void Finalizer()
        {
            ChannelGuard.answeringIncorrectChannel = false;
            ChannelGuard.incorrectChannelFrom = null;
        }
    }

    [HarmonyPatch(typeof(THQNOAcceptor), "SendIncorrectChannel")]
    internal static class AcceptWrapPatch
    {
        private static bool Prefix(THQNOAcceptor __instance, string player_handle, int playerChannel, int lastUsedChannel)
        {
            if (!ChannelGuard.Wrapped(playerChannel, lastUsedChannel))
            {
                return true;
            }
            Plugin.Log.LogInfo($"Accepting P2P channel {playerChannel} from {player_handle} (wrapped around, last was {lastUsedChannel})");
            Traverse.Create(__instance).Method("SetupNewConnection", player_handle, playerChannel).GetValue();
            return false;
        }
    }
}
