using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BeatSaber.GameSettings;
using UnityEngine;

namespace ControllerAutoAdjust
{
    /// <summary>
    /// Notices when the controller offsets change, and journals the new ones.
    /// </summary>
    /// <remarks>
    /// Getting this promptly is what makes replays attributable. A change made in the menu
    /// and played on immediately would otherwise be filed under the previous settings until
    /// the next launch -- the misattribution the journal exists to prevent, and a silent one,
    /// since the cuts still look perfectly ordinary.
    ///
    /// Two events rather than a poll, because the game already says when this happens:
    ///
    /// * <c>VRController.anchorUpdateEvent</c> fires when the *applied* offset pose actually
    ///   changes, which is the thing that matters rather than any particular cause of it.
    ///   Switching profiles ends in <c>RefreshControllersReference</c>, which drives it.
    /// * <c>ControllerProfilesModel.onControllerProfilesUIEvent(false)</c> fires when the
    ///   profiles screen closes, covering edits made in place to the selected profile.
    ///
    /// A slow poll stays as a backstop. Not for tidiness: a missed change does not fail, it
    /// quietly pollutes the fit with cuts filed under the wrong grip, and nothing downstream
    /// can detect that. Cheap insurance against an event this mod does not own.
    /// </remarks>
    internal class SettingsWatcher : MonoBehaviour
    {
        /// <summary>Backstop only; the events are expected to do the work.</summary>
        private const float PollEvery = 30f;

        private const float LookForControllersEvery = 2f;

        private static readonly FieldInfo ProfilesField =
            typeof(VRControllersValueSettingsOffsets).GetField(
                "_controllersProfile", BindingFlags.Instance | BindingFlags.NonPublic);

        private readonly List<VRController> _subscribed = new List<VRController>();
        private ControllerProfilesModel _profiles;
        private float _nextPoll;
        private float _nextLook;

        private void Update()
        {
            if (Time.unscaledTime >= _nextLook)
            {
                _nextLook = Time.unscaledTime + LookForControllersEvery;
                Attach();
            }
            if (Time.unscaledTime >= _nextPoll)
            {
                _nextPoll = Time.unscaledTime + PollEvery;
                Capture();
            }
        }

        private void Attach()
        {
            var controllers = Resources.FindObjectsOfTypeAll<VRController>()
                .Where(c => c != null && c.gameObject.scene.isLoaded)
                .ToList();

            // Entering the settings screen builds new controllers and destroys the old ones.
            // A destroyed Unity object is never equal to a live one, so without this the list
            // grows a stale pair per visit and the subscription is logged again each time.
            _subscribed.RemoveAll(c => c == null);

            foreach (var controller in controllers)
            {
                if (_subscribed.Contains(controller))
                {
                    continue;
                }
                controller.anchorUpdateEvent += OnAnchorUpdated;
                _subscribed.Add(controller);
                Plugin.Log.Info($"watching {controller.node} for offset changes");
            }

            if (_profiles != null)
            {
                return;
            }
            // The profiles model is Zenject-injected and not a MonoBehaviour, so it cannot be
            // found in the scene. It hangs off the offset provider, which can.
            foreach (var controller in controllers)
            {
                var provider = typeof(VRController)
                    .GetField("_transformOffset", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.GetValue(controller) as VRControllersValueSettingsOffsets;
                if (provider == null)
                {
                    continue;
                }
                if (ProfilesField?.GetValue(provider) is ControllerProfilesModel model)
                {
                    _profiles = model;
                    _shared = model;
                    _profiles.onControllerProfilesUIEvent += OnProfilesUI;
                    Plugin.Log.Info("watching the controller profiles screen");
                    break;
                }
            }
        }

        private void OnAnchorUpdated(VRController controller, Pose pose) => Capture();

        private void OnProfilesUI(bool opened)
        {
            // On close: whatever was edited is settled by then, and re-reading on open would
            // only record what is about to be changed.
            if (!opened)
            {
                Capture();
            }
        }

        /// <summary>The saved profiles, for naming which one a stretch of history used.</summary>
        /// <remarks>
        /// Better than assuming the current settings, because the profile still holds real
        /// numbers rather than a guess. Not infallible: profiles are editable, so it gives
        /// the values the profile holds *now*, which are the historical ones only if nobody
        /// has changed it since. That is still evidence where the alternative was none, and
        /// the change detector gets to disagree with it.
        /// </remarks>
        internal static IReadOnlyList<ControllerProfile> Profiles =>
            _shared?.profiles ?? (IReadOnlyList<ControllerProfile>)Array.Empty<ControllerProfile>();

        private static ControllerProfilesModel _shared;

        /// <summary>The profiles model itself, for writing a fitted grip into a profile.</summary>
        internal static ControllerProfilesModel Model => _shared;

        /// <summary>
        /// Journal the live settings now, rather than waiting to be told.
        /// </summary>
        /// <remarks>
        /// The hooks cover a player editing settings in the game's own screens. A write from
        /// this mod goes through the same model and should raise the same event, but a
        /// journal entry is what ties every future replay to the grip it was played on, and
        /// an epoch that is merely likely to have been recorded is not good enough. Appending
        /// is conditional on an actual change, so asking twice costs nothing.
        /// </remarks>
        internal static void CaptureNow() => Capture();

        private static void Capture()
        {
            if (OffsetState.TryRead(out var reading))
            {
                OffsetJournal.RecordIfChanged(reading);
            }
        }

        private void OnDestroy()
        {
            foreach (var controller in _subscribed)
            {
                if (controller != null)
                {
                    controller.anchorUpdateEvent -= OnAnchorUpdated;
                }
            }
            _subscribed.Clear();
            if (_profiles != null)
            {
                _profiles.onControllerProfilesUIEvent -= OnProfilesUI;
                _profiles = null;
            }
        }
    }
}
