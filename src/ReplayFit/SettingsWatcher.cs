using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BeatSaber.GameSettings;
using UnityEngine;

namespace ReplayFit
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

        /// <summary>
        /// A capture asked for by an event, to be done on our own frame instead.
        /// </summary>
        /// <remarks>
        /// The anchor event fires from inside <c>VRController.UpdateAnchorOffsetPose</c>,
        /// which the controller calls while setting itself up. Reading the offsets there
        /// caught the provider half-built and threw, and the throw did not stay ours: it
        /// unwound through the game's own setup, which left the controller unusable and then
        /// threw again from Update every frame after. Forty-seven thousand exceptions in
        /// twenty-five seconds, and a player who could not use their hands.
        ///
        /// Nothing this mod does needs to happen inside that call. Noting that a capture is
        /// wanted and doing it on the next frame keeps the promptness the event was for,
        /// coalesces a storm of them into one, and puts our work back on our own stack where
        /// a mistake in it can only cost us.
        /// </remarks>
        private volatile bool _capturePending;

        private void Update()
        {
            if (Time.unscaledTime >= _nextLook)
            {
                _nextLook = Time.unscaledTime + LookForControllersEvery;
                Attach();
            }
            if (_capturePending)
            {
                _capturePending = false;
                Capture();
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
                    GameApi.SubscribeProfilesUI(_profiles, OnProfilesUI, true);
                    Plugin.Log.Info(GameApi.HasProfilesUIEvent
                        ? "watching the controller profiles screen"
                        : "no profiles-screen event on this game version; "
                          + "relying on the anchor event and the poll");
                    break;
                }
            }
        }

        // Deliberately does nothing but raise a flag; see _capturePending.
        private void OnAnchorUpdated(VRController controller, Pose pose) =>
            _capturePending = true;

        private void OnProfilesUI(bool opened)
        {
            // On close: whatever was edited is settled by then, and re-reading on open would
            // only record what is about to be changed.
            if (!opened)
            {
                _capturePending = true;
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
        /// Persist which profile is selected, which nothing else here will do for us.
        /// </summary>
        /// <remarks>
        /// A profile's numbers and the choice of which profile is live are saved in two
        /// different places by two different owners. <c>ControllerProfilesModel.SaveAsync</c>
        /// writes the profiles file; the selection lives in the main settings, and the only
        /// thing that writes those is the game's own settings screen being closed with OK.
        ///
        /// The mod settings screen is not that screen -- its OK restarts the menu rather than
        /// saving anything -- so a selection made from here was never written down. It held
        /// for as long as the model stayed alive and then quietly went back, which reads as
        /// the switch having silently failed.
        ///
        /// Both dependencies hang off the model already held: the settings themselves, and
        /// the file storage the profiles are saved through.
        /// </remarks>
        internal static bool TrySaveSelectedProfile()
        {
            try
            {
                var model = _shared;
                if (model == null)
                {
                    return false;
                }
                var manager = Field(model, "_settingsManager") as SettingsManager;
                var fileModel = Field(model, "_fileModel");
                var storage = fileModel == null ? null : Field(fileModel, "_fileStorage") as IFileStorage;
                if (manager == null || storage == null)
                {
                    Plugin.Log.Warn(
                        "could not reach the settings writer; the selected profile will hold "
                        + "for this session only");
                    return false;
                }
                SettingsIO.SaveAsync(storage, manager.settings);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.Warn($"could not save the selected profile: {e.Message}");
                return false;
            }
        }

        private static object Field(object target, string name) =>
            target.GetType()
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(target);

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
            try
            {
                if (OffsetState.TryRead(out var reading))
                {
                    OffsetJournal.RecordIfChanged(reading);
                }
            }
            catch (Exception e)
            {
                // Journalling is worth having and worth nothing at all compared with the
                // game continuing to work. Whatever went wrong here stops here.
                Plugin.Log.Warn($"could not read the controller settings: {e.Message}");
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
                GameApi.SubscribeProfilesUI(_profiles, OnProfilesUI, false);
                _profiles = null;
            }
        }
    }
}
