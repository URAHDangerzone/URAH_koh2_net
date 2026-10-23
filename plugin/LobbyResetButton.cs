using HarmonyLib;
using Logic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace URAH_Net
{
    using Object = UnityEngine.Object;

    /// <summary>
    /// A square button right of the lobby's "Copy Code" (a copy of it, its text replaced by the game's
    /// circle-of-arrows glyph that URAH_PluginManager uses for Update) that runs SessionReset, the same as
    /// [Session] ResetKey. Clickable once a player of the entered campaign went offline while we were in
    /// it (SessionReset.DropSeen), until the next reset.
    /// </summary>
    [HarmonyPatch(typeof(UILobby), "Init")]
    internal static class LobbyResetButtonPatch
    {
        private const string Name = "URAH_SessionReset";
        private static readonly AccessTools.FieldRef<UILobby, BSGButton> copyCode = AccessTools.FieldRefAccess<UILobby, BSGButton>("m_CopyCode");

        private static void Postfix(UILobby __instance)
        {
            BSGButton copy = copyCode(__instance);
            if (copy == null || copy.transform.parent == null || copy.transform.parent.Find(Name) != null)
            {
                return;
            }
            BSGButton b = Object.Instantiate(copy, copy.transform.parent, worldPositionStays: false);
            b.name = Name;
            b.transform.SetSiblingIndex(copy.transform.GetSiblingIndex() + 1);
            b.onClick = btn => SessionReset.Start();
            Color tint = Color.white;
            foreach (TextMeshProUGUI label in b.GetComponentsInChildren<TextMeshProUGUI>(includeInactive: true))
            {
                tint = label.color;
                label.name = "URAH_HiddenLabel";
                label.gameObject.SetActive(false);
            }
            var icon = new GameObject("URAH_Icon", typeof(RectTransform), typeof(Image));
            var irt = (RectTransform)icon.transform;
            irt.SetParent(b.transform, worldPositionStays: false);
            irt.anchorMin = new Vector2(0.2f, 0.2f);
            irt.anchorMax = new Vector2(0.8f, 0.8f);
            irt.offsetMin = irt.offsetMax = Vector2.zero;
            Image image = icon.GetComponent<Image>();
            image.sprite = UpdateIcon();
            image.color = tint;
            image.raycastTarget = false;
            image.preserveAspect = true;
            Tooltip.Get(b.gameObject).SetText("#" + Texts.ResetTip, "#" + Texts.ResetTitle);
            b.gameObject.AddComponent<LobbyResetButton>().copy = (RectTransform)copy.transform;
            b.gameObject.SetActive(true);
        }

        /// <summary>Loaded like Defs loads "path:name#2"; null leaves the button without an icon.</summary>
        private static Sprite UpdateIcon()
        {
            try
            {
                if (Assets.GetObject("Assets/UI/Elements/Common/Icons/Icon_Action_Change.png", "Icon_Action_Change", 2) is Sprite s && s != null)
                {
                    return s;
                }
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"Reset button icon: {e.Message}");
            }
            Plugin.Log.LogWarning("Reset button icon Icon_Action_Change not found");
            return null;
        }
    }

    /// <summary>
    /// Keeps the button square at Copy Code's height, right of it (through the parent's layout group when
    /// there is one, else placed by hand), and enabled only while a reset makes sense.
    /// </summary>
    internal class LobbyResetButton : MonoBehaviour
    {
        private const float Gap = 6f;

        internal RectTransform copy;
        private BSGButton button;
        private LayoutElement element;
        private bool inLayout;
        private float laidOut = -1f;

        private void Awake()
        {
            button = GetComponent<BSGButton>();
            inLayout = transform.parent != null && transform.parent.GetComponent<LayoutGroup>() != null;
            element = GetComponent<LayoutElement>();
            if (element == null)
            {
                element = gameObject.AddComponent<LayoutElement>();
            }
            element.ignoreLayout = !inLayout;
        }

        private void LateUpdate()
        {
            button.Enable(SessionReset.DropSeen && !SessionReset.Running);
            if (copy == null)
            {
                return;
            }
            float h = copy.rect.height;
            if (h <= 0f || Mathf.Approximately(h, laidOut))
            {
                return;
            }
            laidOut = h;
            var rt = (RectTransform)transform;
            if (inLayout)
            {
                element.minWidth = element.preferredWidth = h;
                element.minHeight = element.preferredHeight = h;
                element.flexibleWidth = 0f;
                return;
            }
            rt.anchorMin = copy.anchorMin;
            rt.anchorMax = new Vector2(copy.anchorMin.x, copy.anchorMax.y);
            rt.pivot = copy.pivot;
            rt.sizeDelta = new Vector2(h, copy.anchorMin.y == copy.anchorMax.y ? h : copy.sizeDelta.y);
            float right = copy.anchoredPosition.x + copy.rect.width * (1f - copy.pivot.x);
            rt.anchoredPosition = new Vector2(right + Gap + h * copy.pivot.x, copy.anchoredPosition.y);
        }
    }

    /// <summary>The button's texts: English and German, English as fallback.</summary>
    internal static class Texts
    {
        private static bool German => (Defs.Language ?? "en").StartsWith("de");

        public static string ResetTitle => German ? "Verbindungen neu aufbauen" : "Rebuild connections";

        public static string ResetTip => German
            ? "Trennt alle Lobby-Verbindungen, schließt die Steam-Sitzungen und verbindet neu. Für Spieler, die grau bleiben. Alle sind dabei einige Sekunden grau.\n\nAktiv, sobald ein Spieler der Lobby die Verbindung verloren hat. Auch mit Strg+Umschalt+F9."
            : "Drops every lobby connection, closes the Steam sessions and connects again. For players who stay grey. Everyone is grey for a few seconds.\n\nActive once a player of the lobby lost the connection. Also on Ctrl+Shift+F9.";
    }
}
