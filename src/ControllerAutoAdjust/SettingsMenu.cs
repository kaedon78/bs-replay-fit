using System;
using System.Collections.Generic;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaber.GameSettings;
using BeatSaberMarkupLanguage.Settings;
using UnityEngine;

namespace ControllerAutoAdjust
{
    /// <summary>
    /// The one question only the player can answer, and what the replays say about it.
    /// </summary>
    /// <remarks>
    /// Replays do not record the controller offsets they were played on, so months of
    /// history either count or they do not, and nothing in the files decides it. Asking is
    /// the only way to know.
    ///
    /// The answer is not taken on its own, though. Alongside it sits what the replays
    /// themselves say -- the residual either holds steady across those sessions or it shifts
    /// on a particular date -- so a misremembered grip change from three months ago is
    /// contradicted rather than believed. Answer and evidence, shown together, because
    /// either alone can be wrong in a way that produces a confident-looking number.
    /// </remarks>
    internal class SettingsMenu
    {
        internal const string MenuName = "Controller Auto Adjust";
        internal const string Resource = "ControllerAutoAdjust.Views.settings.bsml";

        private const string CurrentSettings = "As they are now";

        private static SettingsMenu _registered;

        /// <summary>
        /// Keeps trying to register until BSML's container exists.
        /// </summary>
        /// <remarks>
        /// <c>BSMLSettings.Instance</c> throws "too early" when asked during OnStart: it
        /// resolves through Zenject, which is not installed until the menu scene is built.
        /// There is no event for that, so this retries rather than guessing a delay.
        /// </remarks>
        internal class Installer : MonoBehaviour
        {
            private const float RetryEvery = 2f;
            private float _next;

            private void Update()
            {
                if (_registered != null || Time.unscaledTime < _next)
                {
                    return;
                }
                _next = Time.unscaledTime + RetryEvery;
                Register();
            }
        }

        internal static void Register()
        {
            try
            {
                if (_registered != null)
                {
                    return;
                }
                _registered = new SettingsMenu();
                BSMLSettings.Instance.AddSettingsMenu(MenuName, Resource, _registered);
                Plugin.Log.Info("settings menu registered");
            }
            catch (Exception e)
            {
                // Not fatal, and not necessarily final: before the menu scene exists this is
                // just "too early" and the next attempt will succeed. Everything the menu
                // shows is in the log either way.
                _registered = null;
                Plugin.Log.Info($"settings menu not ready yet: {e.Message}");
            }
        }

        internal static void Unregister()
        {
            try
            {
                if (_registered != null)
                {
                    BSMLSettings.Instance.RemoveSettingsMenu(_registered);
                    _registered = null;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.Warn($"could not remove the settings menu: {e.Message}");
            }
        }

        [UIValue("profile-choices")]
        public List<object> ProfileChoices
        {
            get
            {
                var choices = new List<object> { CurrentSettings };
                foreach (var profile in SettingsWatcher.Profiles)
                {
                    choices.Add(Describe(profile));
                }
                return choices;
            }
        }

        [UIValue("profile")]
        public string Profile
        {
            get
            {
                var index = Preferences.RangeProfile;
                foreach (var profile in SettingsWatcher.Profiles)
                {
                    if (profile.index == index)
                    {
                        return Describe(profile);
                    }
                }
                return CurrentSettings;
            }
            set
            {
                foreach (var profile in SettingsWatcher.Profiles)
                {
                    if (Describe(profile) == value)
                    {
                        Preferences.RangeProfile = profile.index;
                        return;
                    }
                }
                Preferences.RangeProfile = -1;
            }
        }

        private static string Describe(ControllerProfile profile) =>
            $"#{profile.index}: L {Short(profile.leftController.rotation)} " +
            $"R {Short(profile.rightController.rotation)}";

        private static string Short(Vector3 v) =>
            $"{Mathf.RoundToInt(v.x)},{Mathf.RoundToInt(v.y)},{Mathf.RoundToInt(v.z)}";

        [UIValue("session-max")]
        public int SessionMax => Math.Max(Advice.Sessions.Count - 1, 0);

        [UIValue("start-index")]
        public int StartIndex
        {
            get => IndexOf(Preferences.RangeStart, 0);
            set => Preferences.RangeStart = DayAt(value);
        }

        [UIValue("end-index")]
        public int EndIndex
        {
            get => IndexOf(Preferences.RangeEnd, SessionMax);
            set => Preferences.RangeEnd = DayAt(value);
        }

        /// <summary>Slider position to something a person can read.</summary>
        [UIAction("format-session")]
        public string FormatSession(float raw)
        {
            var sessions = Advice.Sessions;
            if (sessions.Count == 0)
            {
                return "no replays";
            }
            var i = Mathf.Clamp(Mathf.RoundToInt(raw), 0, sessions.Count - 1);
            var day = sessions[i];
            return $"{day.Key:yyyy-MM-dd} ({day.Value} runs)";
        }

        /// <summary>
        /// The nearest session to a saved date, so a stale preference still lands somewhere.
        /// </summary>
        /// <remarks>
        /// Sessions come and go as replays age out of the window, so a date saved last week
        /// may no longer be one. Snapping to the nearest keeps the slider meaningful instead
        /// of silently resetting a choice the player made.
        /// </remarks>
        private static int IndexOf(DateTime? when, int fallback)
        {
            var sessions = Advice.Sessions;
            if (!when.HasValue || sessions.Count == 0)
            {
                return fallback;
            }
            var best = fallback;
            var closest = double.MaxValue;
            for (var i = 0; i < sessions.Count; i++)
            {
                var gap = Math.Abs((sessions[i].Key - when.Value.Date).TotalDays);
                if (gap < closest)
                {
                    closest = gap;
                    best = i;
                }
            }
            return best;
        }

        private static DateTime? DayAt(int index)
        {
            var sessions = Advice.Sessions;
            if (sessions.Count == 0)
            {
                return null;
            }
            return sessions[Mathf.Clamp(index, 0, sessions.Count - 1)].Key;
        }

        [UIValue("summary")]
        public string Summary => Advice.Summary;

        /// <summary>Runs per session as a row of blocks, so the shape of the history shows.</summary>
        [UIValue("timeline")]
        public string Timeline
        {
            get
            {
                var sessions = Advice.Sessions;
                if (sessions.Count == 0)
                {
                    return "";
                }
                var most = 1;
                foreach (var day in sessions)
                {
                    most = Math.Max(most, day.Value);
                }
                const string Blocks = "▁▂▃▅▆▇█";
                var bar = new char[sessions.Count];
                for (var i = 0; i < sessions.Count; i++)
                {
                    var share = (float)sessions[i].Value / most;
                    bar[i] = Blocks[Mathf.Clamp(
                        Mathf.RoundToInt(share * (Blocks.Length - 1)), 0, Blocks.Length - 1)];
                }
                return $"{sessions[0].Key:MMM d}  {new string(bar)}  {sessions[sessions.Count - 1].Key:MMM d}";
            }
        }

        [UIValue("evidence")]
        public string Evidence => Advice.Evidence;

        [UIValue("left-advice")]
        public string LeftAdvice => Advice.Left;

        [UIValue("right-advice")]
        public string RightAdvice => Advice.Right;
    }

    /// <summary>What the last ingestion concluded, for anything that wants to show it.</summary>
    /// <remarks>
    /// Written from the ingestion thread and read on the main one, so the fields are plain
    /// strings assigned whole. Nothing here is worth a lock: a torn read shows a stale line
    /// for one frame.
    /// </remarks>
    internal static class Advice
    {
        internal static volatile string Summary = "No replays read yet.";
        internal static volatile string Evidence = "";
        internal static volatile string Left = "";
        internal static volatile string Right = "";

        /// <summary>Days that have replays, oldest first, with how many each holds.</summary>
        /// <remarks>
        /// What the range sliders move over. Days rather than a continuous date, so every
        /// slider position lands on a session that exists -- picking a Tuesday nobody played
        /// is a position that cannot mean anything.
        /// </remarks>
        internal static volatile List<KeyValuePair<DateTime, int>> Sessions =
            new List<KeyValuePair<DateTime, int>>();
    }
}
