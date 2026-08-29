using System;
using System.Reflection;
using BeatSaber.GameSettings;
using UnityEngine;

namespace ControllerAutoAdjust
{
    /// <summary>
    /// The few places the game's surface differs across the versions this mod supports.
    /// </summary>
    /// <remarks>
    /// Built against the oldest supported version, so everything bound directly is present
    /// everywhere. Three members are not, and each is reached here instead: two exist only in
    /// the newer game, and one is internal in both and so unreachable from outside either way.
    ///
    /// Reflection rather than two builds, because all three sit in cold paths -- a button
    /// press, a subscription made once, a test harness -- and one file that runs on every
    /// supported version is a great deal easier to ship and to reason about than a matrix of
    /// them. Every lookup is resolved once and each reports what it found, so a version that
    /// breaks one of them says so in the log rather than silently doing nothing.
    /// </remarks>
    internal static class GameApi
    {
        private const BindingFlags Any =
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        /// <summary>
        /// Write one hand's offset into a profile.
        /// </summary>
        /// <remarks>
        /// The public <c>UpdateControllerOffset</c> that does both at once arrived after the
        /// oldest supported version. The pair underneath it is internal, and has been in both
        /// for as long as the profiles have, so that is what is called.
        /// </remarks>
        private static readonly MethodInfo SetPosition =
            typeof(ControllerProfile).GetMethod("UpdateControllerPosition", Any);

        private static readonly MethodInfo SetRotation =
            typeof(ControllerProfile).GetMethod("UpdateControllerRotation", Any);

        internal static bool TrySetOffset(
            ControllerProfile profile, bool left, Vector3 position, Vector3 rotation)
        {
            if (SetPosition == null || SetRotation == null)
            {
                Plugin.Log.Error(
                    "this game version does not expose a way to write a controller profile");
                return false;
            }
            SetPosition.Invoke(profile, new object[] { left, position });
            SetRotation.Invoke(profile, new object[] { left, rotation });
            return true;
        }

        /// <summary>
        /// The event raised when the game's controller-profiles screen opens and closes.
        /// </summary>
        /// <remarks>
        /// Absent from the older game. Losing it costs a prompt journal entry when a player
        /// edits their grip in that screen, not the entry itself: the anchor event and the
        /// backstop poll both still notice, just less immediately.
        /// </remarks>
        private static readonly EventInfo ProfilesUI =
            typeof(ControllerProfilesModel).GetEvent("onControllerProfilesUIEvent", Any);

        internal static bool HasProfilesUIEvent => ProfilesUI != null;

        internal static void SubscribeProfilesUI(
            ControllerProfilesModel model, Action<bool> handler, bool add)
        {
            if (ProfilesUI == null || model == null)
            {
                return;
            }
            try
            {
                var typed = Delegate.CreateDelegate(
                    ProfilesUI.EventHandlerType, handler.Target, handler.Method);
                if (add)
                {
                    ProfilesUI.AddEventHandler(model, typed);
                }
                else
                {
                    ProfilesUI.RemoveEventHandler(model, typed);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.Warn($"could not watch the profiles screen: {e.Message}");
            }
        }

        /// <summary>Hand the sabers to the autoplay driver, where the game has one.</summary>
        /// <remarks>
        /// Only the test harness wants this, and only the newer game offers it. Where it is
        /// missing the harness reports that it cannot run rather than pretending to.
        /// </remarks>
        private static readonly MethodInfo Autoplay =
            typeof(PlayerVRControllersManager).GetMethod(
                "SetupAutoplayForAllControllers", Any, null, Type.EmptyTypes, null);

        internal static bool TryStartAutoplay(PlayerVRControllersManager manager)
        {
            if (Autoplay == null)
            {
                Plugin.Log.Warn(
                    "this game version has no autoplay driver; the swing harness cannot run");
                return false;
            }
            Autoplay.Invoke(manager, null);
            return true;
        }
    }
}
