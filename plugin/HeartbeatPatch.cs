namespace URAH_Net
{
    /// <summary>
    /// Fix A (off by default). Multiplayer.HeartbeatReceiveTimeLimitInMilliseconds is filled from
    /// multiplayer_settings.heartbeat_timeout only while it is 0, when the first Multiplayer is created,
    /// so a value set before that sticks. Not needed for the lobby (running games use the same 5 s and
    /// are stable, and heartbeats are received and sent off the main thread, so hitches do not count);
    /// kept as a switch for the case where players drop one after another when someone leaves a game.
    /// </summary>
    internal static class HeartbeatPatch
    {
        public static void Apply(int timeoutMs)
        {
            if (timeoutMs <= 0)
            {
                return;
            }
            // touches Multiplayer's static fields only when switched on; their initializers are trivial
            int old = Logic.Multiplayer.HeartbeatReceiveTimeLimitInMilliseconds;
            Logic.Multiplayer.HeartbeatReceiveTimeLimitInMilliseconds = timeoutMs;
            Plugin.Log.LogInfo($"Heartbeat timeout {(old == 0 ? "5000 (game default)" : old.ToString())} -> {timeoutMs} ms");
        }
    }
}
