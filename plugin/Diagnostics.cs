using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Logic;

namespace URAH_Net
{
    using Multiplayer = Logic.Multiplayer;

    /// <summary>
    /// Log-only watchdog for the multiplayer drops where everyone drops when one player leaves. The game
    /// resets one heartbeat timer per player on every received message (Multiplayer.last_heartbeat_received)
    /// and drops all connections to a player 5 s after the last one.
    /// Each Multiplayer sends from its SS thread under its Lock, receive threads take the same Lock, and
    /// every Steam read / send on the machine goes through THQNORequest.LockP2P. A thread of its own looks
    /// every 250 ms at: the time since the last message from each player, the time since each connection
    /// last sent, LockP2P and every Multiplayer's Lock (not free within 50 ms on 3 looks in a row), SS queue
    /// lengths, and main thread stalls. It only reads (TryEnter with immediate Exit on the locks).
    /// With everyone's log after a drop it shows whether a machine stopped sending, stopped reading, or
    /// whether Steam stopped delivering. All lines carry the wall clock time, to match multiplayer.log.
    /// </summary>
    internal static class Diagnostics
    {
        private const int TickMs = 250, GapMs = 1500, StallMs = 500, LockTryMs = 50, LockTicks = 3, QueueLength = 20;

        private static readonly FieldInfo lastHeartbeatField = AccessTools.Field(typeof(Multiplayer), "last_heartbeat_received");
        private static readonly FieldInfo heartbeatLockField = AccessTools.Field(typeof(Multiplayer), "HeartbeatLock");
        private static readonly FieldInfo lockField = AccessTools.Field(typeof(Multiplayer), "Lock");
        private static readonly FieldInfo queueField = AccessTools.Field(typeof(Multiplayer), "ss_queue");
        private static readonly FieldInfo connectionField = AccessTools.Field(typeof(Multiplayer), "connection");
        private static readonly FieldInfo multiplayersLockField = AccessTools.Field(typeof(THQNORequest), "MultiplayersLock");

        private static readonly Stopwatch clock = Stopwatch.StartNew();
        private static long mainTick = -1;
        private static Thread thread;
        private static readonly ManualResetEvent stop = new ManualResetEvent(false);

        // watchdog thread only
        private static readonly Dictionary<string, long> rxGapSince = new Dictionary<string, long>();
        private static readonly Dictionary<Multiplayer, long> txGapSince = new Dictionary<Multiplayer, long>();
        private static readonly Dictionary<object, LockWatch> locks = new Dictionary<object, LockWatch>();
        private static readonly Dictionary<object, long> queueLogged = new Dictionary<object, long>();
        private static readonly List<Multiplayer> multiplayers = new List<Multiplayer>();
        private static readonly List<KeyValuePair<string, long>> heartbeats = new List<KeyValuePair<string, long>>();
        private static readonly HashSet<object> seen = new HashSet<object>();
        private static readonly List<object> stale = new List<object>();
        private static long stallSince = -1;

        private class LockWatch
        {
            public string name;
            public int misses;
            public long since;
            public bool logged;
        }

        /// <summary>"HH:mm:ss.fff text", the time format of multiplayer.log.</summary>
        internal static void Log(string text)
        {
            Plugin.Log.LogInfo($"{DateTime.Now:HH:mm:ss.fff} {text}");
        }

        public static void Start()
        {
            if (thread != null)
            {
                return;
            }
            stop.Reset();
            thread = new Thread(Run) { Name = "URAH_Net watchdog", IsBackground = true };
            thread.Start();
        }

        /// <summary>Ends the watchdog thread and waits for it (a tick is short), then forgets its state.</summary>
        public static void Stop()
        {
            if (thread == null)
            {
                return;
            }
            stop.Set();
            thread.Join();
            thread = null;
            rxGapSince.Clear();
            txGapSince.Clear();
            locks.Clear();
            queueLogged.Clear();
            multiplayers.Clear();
            stallSince = -1;
            Interlocked.Exchange(ref mainTick, -1);
        }

        /// <summary>From the plugin's Update: main thread liveness.</summary>
        public static void MainTick()
        {
            Interlocked.Exchange(ref mainTick, clock.ElapsedMilliseconds);
        }

        private static void Run()
        {
            int wait;
            do
            {
                wait = TickMs;
                try
                {
                    Tick();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Watchdog: {e}");
                    wait = 5000 + TickMs;
                }
            }
            while (!stop.WaitOne(wait));
        }

        private static void Tick()
        {
            CopyMultiplayers();
            if (multiplayers.Count == 0)
            {
                return;
            }
            long now = clock.ElapsedMilliseconds;
            WatchMainThread(now);
            WatchReceive();
            WatchLock(THQNORequest.LockP2P, "LockP2P", now);
            seen.Clear();
            seen.Add(THQNORequest.LockP2P);
            foreach (Multiplayer mp in multiplayers)
            {
                if (mp == null)
                {
                    continue;
                }
                WatchSend(mp);
                object mpLock = lockField?.GetValue(mp);
                if (mpLock != null && seen.Add(mpLock))
                {
                    WatchLock(mpLock, $"Lock of {mp}", now);
                }
                WatchQueue(mp, now);
            }
            Forget();
        }

        private static void CopyMultiplayers()
        {
            multiplayers.Clear();
            List<Multiplayer> all = THQNORequest.GetAllMultiplayers();
            object sync = multiplayersLockField?.GetValue(null);
            if (all == null || sync == null)
            {
                return;
            }
            lock (sync)
            {
                multiplayers.AddRange(all);
            }
        }

        private static void WatchMainThread(long now)
        {
            long last = Interlocked.Read(ref mainTick);
            if (last < 0)
            {
                return;
            }
            if (now - last >= StallMs)
            {
                if (stallSince < 0)
                {
                    stallSince = last;
                }
            }
            else if (stallSince >= 0)
            {
                Log($"Main thread stalled for {last - stallSince} ms");
                stallSince = -1;
            }
        }

        /// <summary>Time since the last message from each player (the game's heartbeat timer).</summary>
        private static void WatchReceive()
        {
            var dict = lastHeartbeatField?.GetValue(null) as Dictionary<string, long>;
            object sync = heartbeatLockField?.GetValue(null);
            Stopwatch timer = Multiplayer.heartbeat_timer;
            if (dict == null || sync == null || timer == null)
            {
                return;
            }
            heartbeats.Clear();
            long now;
            lock (sync)
            {
                now = timer.ElapsedMilliseconds;
                heartbeats.AddRange(dict);
            }
            foreach (KeyValuePair<string, long> entry in heartbeats)
            {
                long gap = now - entry.Value;
                bool known = rxGapSince.TryGetValue(entry.Key, out long since);
                if (gap >= GapMs && !known)
                {
                    rxGapSince[entry.Key] = entry.Value;
                    Log($"Nothing received from {Plugin.Who(entry.Key)} for {gap} ms");
                }
                else if (gap < GapMs && known)
                {
                    rxGapSince.Remove(entry.Key);
                    Log($"{Plugin.Who(entry.Key)} heard again after {entry.Value - since} ms");
                }
            }
        }

        /// <summary>Time since each connected Multiplayer last sent (heartbeats go out every 1000 ms).</summary>
        private static void WatchSend(Multiplayer mp)
        {
            Stopwatch timer = Multiplayer.heartbeat_timer;
            if (mp.type == Multiplayer.Type.Server || timer == null || !(connectionField?.GetValue(mp) is Connection connection))
            {
                return;
            }
            long last = connection.last_send_time;
            if (last == long.MinValue || !mp.IsConnected())
            {
                txGapSince.Remove(mp);
                return;
            }
            long gap = timer.ElapsedMilliseconds - last;
            bool known = txGapSince.TryGetValue(mp, out long since);
            if (gap >= GapMs && !known)
            {
                txGapSince[mp] = last;
                Log($"{mp} sent nothing for {gap} ms, SS queue {QueueCount(mp)}");
            }
            else if (gap < GapMs && known)
            {
                txGapSince.Remove(mp);
                Log($"{mp} sends again after {last - since} ms");
            }
        }

        /// <summary>A lock that could not be taken within 50 ms on 3 looks in a row is logged with how long that lasted.</summary>
        private static void WatchLock(object sync, string name, long now)
        {
            if (!locks.TryGetValue(sync, out LockWatch watch))
            {
                locks[sync] = watch = new LockWatch();
            }
            watch.name = name;
            if (Monitor.TryEnter(sync, LockTryMs))
            {
                Monitor.Exit(sync);
                if (watch.logged)
                {
                    Log($"{watch.name} free again after {now - watch.since} ms");
                }
                watch.misses = 0;
                watch.logged = false;
                return;
            }
            if (watch.misses++ == 0)
            {
                watch.since = now;
            }
            if (watch.misses >= LockTicks && !watch.logged)
            {
                watch.logged = true;
                Log($"{watch.name} not free for {now - watch.since + LockTryMs} ms");
            }
        }

        private static void WatchQueue(Multiplayer mp, long now)
        {
            object queue = queueField?.GetValue(mp);
            if (queue == null || !seen.Add(queue))
            {
                return;
            }
            int count = QueueCount(mp);
            if (count < QueueLength)
            {
                queueLogged.Remove(queue);
                return;
            }
            if (!queueLogged.TryGetValue(queue, out long last) || now - last >= 2000)
            {
                queueLogged[queue] = now;
                Log($"SS queue of {mp}: {count} actions waiting");
            }
        }

        private static int QueueCount(Multiplayer mp)
        {
            return (queueField?.GetValue(mp) as ICollection)?.Count ?? 0;
        }

        /// <summary>Drops the state of locks, queues and connections that are gone.</summary>
        private static void Forget()
        {
            stale.Clear();
            foreach (object key in locks.Keys)
            {
                if (!seen.Contains(key))
                {
                    stale.Add(key);
                }
            }
            foreach (object key in queueLogged.Keys)
            {
                if (!seen.Contains(key))
                {
                    stale.Add(key);
                }
            }
            foreach (Multiplayer mp in txGapSince.Keys)
            {
                if (!multiplayers.Contains(mp))
                {
                    stale.Add(mp);
                }
            }
            foreach (object key in stale)
            {
                locks.Remove(key);
                queueLogged.Remove(key);
                if (key is Multiplayer mp)
                {
                    txGapSince.Remove(mp);
                }
            }
        }
    }

    /// <summary>The game logs heartbeat drops only to multiplayer.log; this puts them next to the watchdog lines.</summary>
    [HarmonyPatch(typeof(Multiplayer), nameof(Multiplayer.HandleUngracefulDisconnect))]
    internal static class UngracefulLogPatch
    {
        private static void Prefix(Multiplayer __instance)
        {
            Diagnostics.Log($"Ungraceful disconnect: {__instance}");
        }
    }
}
