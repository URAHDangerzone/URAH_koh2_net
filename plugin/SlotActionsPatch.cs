using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace URAH_Net
{
    /// <summary>
    /// After a long game, opening a lobby breaks (base-game bug, TODO.md): UILobbyPlayers+UIActionIcon.Create
    /// throws a NullReferenceException (IL 0x2f, right after GetOrAddComponent&lt;UIActionIcon&gt;, so the
    /// icon component came back null) for every action icon of a player slot. Seen 2026-10-18 on host and
    /// guest, prototype and container alive. Through UISlotActions.BuildActions the exception kills
    /// MPBoss.CreateNewCampaign (no new lobby) or UILobbyPlayers.UpdatePlayers in every LateUpdate (lobby
    /// without map, starting still works).
    ///
    /// The spawned icon then carries the top-level UIActionIcon (the action bar's icon class) instead of the
    /// lobby's nested class of the same name (seen 2026-10-20: GetComponent(UILobbyPlayers+UIActionIcon)
    /// returns a UIActionIcon). Unity apparently mixes the two scripts up by class name; why only after a
    /// long game is unknown. Without the icon the host can't kick, close or open a slot.
    ///
    /// IconPatch logs the spawned icon object (components, state) and tries to finish the icon once more
    /// (GetComponent / AddComponent, SetData). When that fails too, it disables the wrong component and
    /// makes the icon work with SlotActionButton, a component of this plugin. The icon is thrown away only
    /// when that fails as well. SlotActionsPatch stops whatever is left at BuildActions, so the lobby opens
    /// with missing slot action icons at worst.
    /// </summary>
    internal static class SlotActions
    {
        internal static readonly Type IconType = AccessTools.TypeByName("UILobbyPlayers+UIActionIcon");
        internal static readonly Type SlotActionsType = AccessTools.TypeByName("UILobbyPlayers+UISlotActions");

        private static readonly MethodInfo setData = AccessTools.Method(IconType, "SetData", new[] { typeof(string), typeof(int) });
        private static readonly FieldInfo freedField = AccessTools.Field(typeof(ObjectPool), "freed");

        /// <summary>Errors are logged in full only this often per session; later ones get one line.</summary>
        private const int FullLogs = 5;
        private static int iconErrors, slotErrors;

        internal static void IconFailed(Exception error, string action, int slot, GameObject prototype, RectTransform parent)
        {
            iconErrors++;
            GameObject obj = Spawned(prototype, parent);
            string state = $"prototype {Unity(prototype)}, parent {Unity(parent)}, spawned {Describe(obj)}";
            string retry = Retry(obj, action, slot);
            if (iconErrors <= FullLogs)
            {
                Diagnostics.Log($"Action icon '{action}' of slot {slot} failed ({iconErrors}): {state}, retry: {retry}\n{error}");
            }
            else
            {
                Diagnostics.Log($"Action icon '{action}' of slot {slot} failed ({iconErrors}): {error.GetType().Name}, retry: {retry}");
            }
        }

        internal static void SlotFailed(Exception error, object slotActions)
        {
            slotErrors++;
            if (slotErrors > FullLogs)
            {
                return;
            }
            var t = Traverse.Create(slotActions);
            var prototype = t.Field("m_ActionIconPrototype").GetValue<GameObject>();
            var container = t.Field("m_ActionsContainer").GetValue<RectTransform>();
            int slot = t.Field("slotIndex").GetValue<int>();
            List<string> actions = MPBoss.Get()?.GetValidSlotActions(slot);
            Diagnostics.Log($"Lobby slot actions failed ({slotErrors}), lobby goes on without them: slot {slot}, " +
                $"prototype {Unity(prototype)}, container {Unity(container)}, " +
                $"actions [{(actions == null ? "none" : string.Join(", ", actions))}], pool {Pool(prototype)}\n{error}");
        }

        /// <summary>Common.Spawn parents the new icon last under the container; a clone of the prototype found there is it.</summary>
        private static GameObject Spawned(GameObject prototype, RectTransform parent)
        {
            if (parent == null || prototype == null || parent.childCount == 0)
            {
                return null;
            }
            GameObject last = parent.GetChild(parent.childCount - 1).gameObject;
            return last != prototype && last.name.StartsWith(prototype.name) ? last : null;
        }

        /// <summary>Second try on the spawned object; throws it away when that fails too.</summary>
        private static string Retry(GameObject obj, string action, int slot)
        {
            if (obj == null || IconType == null || setData == null)
            {
                return "nothing to retry";
            }
            Component icon;
            string how;
            try
            {
                icon = obj.GetComponent(IconType);
                how = icon != null ? "GetComponent found it" : null;
                if (icon == null)
                {
                    icon = obj.AddComponent(IconType);
                    how = icon != null ? "AddComponent worked" : "AddComponent returned null";
                }
                if (icon != null)
                {
                    setData.Invoke(icon, new object[] { action, slot });
                    return $"{how}, icon shown";
                }
            }
            catch (Exception e)
            {
                how = $"failed with {(e is TargetInvocationException tie ? tie.InnerException : e)}";
            }
            try
            {
                SlotActionButton.Attach(obj, action, slot);
                return $"{how}, own button shown";
            }
            catch (Exception e)
            {
                how = $"{how}, own button failed with {e}";
            }
            ObjectPool.DestroyObj(obj);
            return $"{how}, icon removed";
        }

        /// <summary>The lobby's own icon class sits on the object and works: then it handles the click.</summary>
        internal static bool HasWorkingIcon(GameObject obj)
        {
            return IconType != null && obj.GetComponent(IconType) is Behaviour icon && IconType.IsInstanceOfType(icon) && icon.enabled;
        }

        private static string Describe(GameObject obj)
        {
            if ((object)obj == null)
            {
                return "not found";
            }
            if (obj == null)
            {
                return "destroyed";
            }
            string components = string.Join(", ", obj.GetComponents<Component>()
                .Select(c => (object)c == null ? "missing script" : c.GetType().Name));
            return $"'{obj.name}' activeSelf {obj.activeSelf}, inHierarchy {obj.activeInHierarchy}, components [{components}]";
        }

        /// <summary>Unity objects compare equal to null once destroyed; this tells both apart.</summary>
        internal static string Unity(UnityEngine.Object obj)
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

    [HarmonyPatch]
    internal static class IconPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(SlotActions.IconType, "Create");
        }

        private static Exception Finalizer(Exception __exception, string action_key, int slot_index, GameObject prototype, RectTransform parent)
        {
            if (__exception == null)
            {
                return null;
            }
            try
            {
                SlotActions.IconFailed(__exception, action_key, slot_index, prototype, parent);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Action icon failed ({__exception.Message}), handling it failed too: {e}");
            }
            return null; // __result stays null, BuildActions goes on with the next icon
        }
    }

    [HarmonyPatch]
    internal static class SlotActionsPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(SlotActions.SlotActionsType, "BuildActions");
        }

        private static Exception Finalizer(Exception __exception, object __instance)
        {
            if (__exception == null)
            {
                return null;
            }
            try
            {
                SlotActions.SlotFailed(__exception, __instance);
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Lobby slot actions failed ({__exception.Message}), logging it failed too: {e}");
            }
            return null;
        }
    }

    /// <summary>
    /// Stands in for UILobbyPlayers+UIActionIcon when the game can't put it on the icon: the same sprite,
    /// tooltip and click (MPBoss.PerformSlotAction). Pooled icons are reused, so it stays on the object and
    /// keeps quiet while the lobby's own icon class works there again.
    /// </summary>
    internal sealed class SlotActionButton : MonoBehaviour, IPointerClickHandler
    {
        private string action;
        private int slot;

        internal static void Attach(GameObject obj, string action, int slot)
        {
            // the wrong icon class would answer the click too
            foreach (global::UIActionIcon wrong in obj.GetComponents<global::UIActionIcon>())
            {
                wrong.enabled = false;
            }
            SlotActionButton button = obj.GetComponent<SlotActionButton>() ?? obj.AddComponent<SlotActionButton>();
            button.action = action;
            button.slot = slot;
            Logic.DT.Field def = Defs.GetDefField("LobbyPlayerWindow")?.FindChild(action);
            Image image = obj.GetComponentsInChildren<Image>(true).FirstOrDefault(i => i.name == "id_Icon");
            if (image != null)
            {
                image.overrideSprite = Defs.GetObj<Sprite>(def, "icon");
            }
            Tooltip.Get(obj).SetDef("PlayerSlotActionTooltip", new Logic.Vars(def));
        }

        public void OnPointerClick(PointerEventData e)
        {
            if (e.button != PointerEventData.InputButton.Left || SlotActions.HasWorkingIcon(gameObject))
            {
                return;
            }
            string result = MPBoss.Get()?.PerformSlotAction(slot, action);
            Diagnostics.Log($"Slot action '{action}' of slot {slot} through the replacement icon: {result ?? "no MPBoss"}");
        }
    }
}
