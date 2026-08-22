// MenuInputBlock.cs - keep the game's chat closed while the F3 panel is up.
//
// MaxPracticeUI takes the cursor when it opens (SuppressPlayerInput sets
// isMouseRequired, which stops skating input), but the game's chat is not driven by
// the cursor - it hangs off two InputActions that UIManager subscribes to in Awake:
//
//     OnAllChatActionPerformed(InputAction.CallbackContext)
//     OnTeamChatActionPerformed(InputAction.CallbackContext)
//
// Those fire from the Input System's own update regardless of what is on screen. The
// game's own views don't have this problem because UIManager knows about them - it
// walks its `views` list in GetTopmostBlockingInteractingView to decide what may take
// input. Our panel isn't a UIView and isn't in that list, so as far as UIManager is
// concerned nothing is open and chat is fair game: press the chat key with the panel
// up and you get a focused text field on top of it, with keystrokes going to both.
//
// Registering the panel as a real UIView would be the tidier fix, but UIView is a
// MonoBehaviour the game instantiates and wires to a UIDocument, and joining that list
// means our panel starts being shown and hidden by ShowPhaseViews/HideAllViews. Two
// prefixes are the smaller change and they fail closed: if either method is ever
// renamed we log it and chat behaves exactly as it does today.
//
// Client-only by construction - a dedicated server has no UIManager instance, and
// MaxPracticeUI is never added there, so MenuOwnsKeyboard is never true.

using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace MaxPractice
{
    internal static class MenuInputBlock
    {
        // The two UIManager callbacks that open chat. Private, hence the string names;
        // both take a single InputAction.CallbackContext and there are no overloads.
        private static readonly string[] ChatOpenMethods =
        {
            "OnAllChatActionPerformed",
            "OnTeamChatActionPerformed",
        };

        /// <summary>
        /// Suppress the chat-open action while the panel owns the keyboard. Returning
        /// false skips UIManager's original, so no StartInput and no focused text field.
        /// </summary>
        public static bool Prefix()
        {
            try { return !MaxPracticeUI.MenuOwnsKeyboard; }
            catch { return true; }
        }

        /// <summary>
        /// Applied from the plugin rather than by attribute, because a rename in a future
        /// build would make PatchAll throw and take every other patch in the assembly down
        /// with it. Same defensive shape as the GameManager tick patch.
        /// </summary>
        public static void Apply(Harmony harmony)
        {
            if (harmony == null) return;

            Type uiManager = AccessTools.TypeByName("UIManager");
            if (uiManager == null)
            {
                // Dedicated servers still load the assembly, so this really is unexpected.
                Debug.LogWarning("[MaxPractice] UIManager not found - chat will stay openable " +
                                 "while the F3 panel is up.");
                return;
            }

            var prefix = new HarmonyMethod(typeof(MenuInputBlock), nameof(Prefix));

            foreach (string name in ChatOpenMethods)
            {
                try
                {
                    MethodInfo target = AccessTools.DeclaredMethod(uiManager, name);
                    if (target == null)
                    {
                        Debug.LogWarning($"[MaxPractice] UIManager.{name} not found - that chat " +
                                         "key will still open chat over the F3 panel.");
                        continue;
                    }

                    harmony.Patch(target, prefix: prefix);
                    ConfigManager.Log($"Patched UIManager.{name} (chat blocked while the menu is open)");
                }
                catch (Exception e)
                {
                    Debug.LogError($"[MaxPractice] Failed to patch UIManager.{name}: " + e.Message);
                }
            }
        }
    }
}
