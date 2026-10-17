using System.Text;
using HarmonyLib;
using Logic;

namespace URAH_Net
{
    /// <summary>
    /// The lobby browser reads every lobby string through a 128-char buffer
    /// (UILobbyBrowser.LobbyData.GetStringVar). The host's mod_id is "name/sha1;name/sha1" of all active
    /// mods, 134 chars with "URAH HRE map (experimental)" and "URAH Royal Wedding", so it arrived cut
    /// off: the joining player saw a lobby without mods, his own active mods counted as missing there and
    /// the Join button stayed off - whoever hosted. This reads mod_id with a buffer that fits.
    /// </summary>
    [HarmonyPatch(typeof(UILobbyBrowser.LobbyData), "GetStringVar")]
    internal static class LobbyModIdPatch
    {
        private const int BufferSize = 4096;  // Steam's limit for a lobby value is 8192 bytes

        // __0 = Vars var, __1 = string lobby_id, __2 = string key
        private static bool Prefix(Vars __0, string __1, string __2)
        {
            if (__2 != "mod_id")
            {
                return true;
            }
            StringBuilder sb = new StringBuilder(BufferSize);
            if (THQNORequest.GetLobbyStringData(__1, __2, sb, BufferSize).error != null)
            {
                __0.Set(__2, "");
                return false;
            }
            string text = sb.ToString();
            if (string.IsNullOrEmpty(text))
            {
                __0.Set<string>(__2, null);
            }
            else
            {
                __0.Set(__2, text);
            }
            return false;
        }
    }
}
