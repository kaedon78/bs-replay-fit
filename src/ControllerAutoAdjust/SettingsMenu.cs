using System;
using System.ComponentModel;
using System.Collections.Generic;
using BeatSaberMarkupLanguage.Attributes;
using BeatSaber.GameSettings;
using BeatSaberMarkupLanguage.Components.Settings;
using HMUI;
using BeatSaberMarkupLanguage.Settings;
using UnityEngine;
using UnityEngine.UI;

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
    internal class SettingsMenu : INotifyPropertyChanged
    {
        /// <summary>
        /// BSML reads a bound value once, when the tab is first built.
        /// </summary>
        /// <remarks>
        /// Ingestion takes about a minute and the menu registers in seconds, so whatever the
        /// panel is showing was almost certainly read before there was anything to say. It
        /// showed the placeholder and looked, reasonably, like a mod that had done nothing.
        /// </remarks>
        public event PropertyChangedEventHandler PropertyChanged;

        private void Refresh()
        {
            var changed = PropertyChanged;
            if (changed == null)
            {
                return;
            }
            foreach (var name in new[]
                     {
                         nameof(Status), nameof(Scope), nameof(Advisory), nameof(AssignmentList), nameof(StartPercent), nameof(EndPercent),
                         nameof(ReadButton), nameof(FitButton),
                         nameof(ProfileChoices), nameof(Profile),
                     })
            {
                changed(this, new PropertyChangedEventArgs(name));
            }
        }

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

            private int _shown = -1;

            private void Update()
            {
                if (_registered == null)
                {
                    if (Time.unscaledTime < _next)
                    {
                        return;
                    }
                    _next = Time.unscaledTime + RetryEvery;
                    Register();
                    return;
                }

                // Raised here rather than where the advice is written: that happens on the
                // ingestion thread, and a UI notification from off the main thread is a crash
                // waiting for the right timing.
                if (_shown != Advice.Version)
                {
                    _shown = Advice.Version;
                    _registered.Refresh();
                    _registered.RedrawRange();
                }
                // Every frame, not only on a version bump: the fill should move smoothly
                // rather than in the steps the text updates on.
                _registered.DrawProgress();
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

        /// <summary>
        /// The profiles worth offering, labelled so they can be told apart.
        /// </summary>
        /// <remarks>
        /// Untouched presets are left out. An all-default profile describes no grip anybody
        /// holds, and picking one would quietly set the baseline to zero -- a wrong answer
        /// that looks like a deliberate one. Labels carry the rotations rather than the index
        /// because indices repeat: built-in and custom profiles both start at zero, which is
        /// why the list offered two "#0".
        /// </remarks>
        private static List<ControllerProfile> Usable()
        {
            var usable = new List<ControllerProfile>();
            foreach (var profile in SettingsWatcher.Profiles)
            {
                if (profile != null && !profile.HasDefaultValues())
                {
                    usable.Add(profile);
                }
            }
            return usable;
        }

        [UIValue("profile-choices")]
        public List<object> ProfileChoices
        {
            get
            {
                var choices = new List<object> { CurrentSettings };
                foreach (var profile in Usable())
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
                if (!Preferences.TryGetRangeGrip(out var left, out var right, out _))
                {
                    return CurrentSettings;
                }
                foreach (var profile in Usable())
                {
                    if (Near(profile.leftController.rotation, left)
                        && Near(profile.rightController.rotation, right))
                    {
                        return Describe(profile);
                    }
                }
                // The profile it was copied from has since been edited or removed. The numbers
                // are still the ones chosen, so they are shown rather than silently dropped.
                return $"L {Short(left)}  R {Short(right)}";
            }
            set
            {
                foreach (var profile in Usable())
                {
                    if (Describe(profile) == value)
                    {
                        Preferences.SetRangeGrip(
                            profile.leftController.rotation,
                            profile.rightController.rotation,
                            profile.alternativeHandling);
                        return;
                    }
                }
                Preferences.SetRangeGrip(null, null, true);
            }
        }

        private static bool Near(Vector3 a, Vector3 b) => (a - b).sqrMagnitude < 1e-4f;

        private static string Describe(ControllerProfile profile) =>
            $"L {Short(profile.leftController.rotation)}  " +
            $"R {Short(profile.rightController.rotation)}";

        private static string Short(Vector3 v) =>
            $"{Mathf.RoundToInt(v.x)},{Mathf.RoundToInt(v.y)},{Mathf.RoundToInt(v.z)}";

        /// <summary>
        /// Where in the history each slider sits, as a percentage rather than a session index.
        /// </summary>
        /// <remarks>
        /// BSML fixes a slider's min and max in the markup and does not bind them to a value,
        /// so a slider cannot be sized to however many sessions a player happens to have. An
        /// index slider therefore ran 0 to a guessed maximum, and clamped "the latest session"
        /// down to whatever that guess was -- the "until" field defaulted to the *earliest*
        /// date, which is the opposite of what it says. A percentage always spans exactly the
        /// history that exists, whatever its length.
        /// </remarks>
        [UIValue("start-percent")]
        public int StartPercent
        {
            get => PercentOf(Preferences.RangeStart, 0);
            set => Preferences.RangeStart = StartAt(SessionAt(value));
        }

        [UIValue("end-percent")]
        public int EndPercent
        {
            get => PercentOf(Preferences.RangeEnd, 100);
            set => Preferences.RangeEnd = EndAt(SessionAt(value));
        }

        private static int PercentOf(DateTime? when, int fallback)
        {
            var sessions = Advice.Sessions;
            if (!when.HasValue || sessions.Count < 2)
            {
                return fallback;
            }
            var i = IndexOf(when, 0);
            return Mathf.RoundToInt(100f * i / (sessions.Count - 1));
        }

        private static int SessionAt(float percent)
        {
            var sessions = Advice.Sessions;
            return sessions.Count == 0
                ? 0
                : Mathf.Clamp(
                    Mathf.RoundToInt(percent / 100f * (sessions.Count - 1)),
                    0, sessions.Count - 1);
        }

        /// <summary>Slider position to something a person can read.</summary>
        [UIAction("format-session")]
        public string FormatSession(float percent)
        {
            var sessions = Advice.Sessions;
            if (sessions.Count == 0)
            {
                return "no replays yet";
            }
            var span = sessions[SessionAt(percent)];
            return $"{span.Start:d MMM HH:mm} ({span.Runs})";
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
                var gap = Math.Abs((sessions[i].Start - when.Value).TotalMinutes);
                if (gap < closest)
                {
                    closest = gap;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>The start of a sitting, for the lower bound of a range.</summary>
        private static DateTime? StartAt(int index)
        {
            var sessions = Advice.Sessions;
            return sessions.Count == 0
                ? (DateTime?)null
                : sessions[Mathf.Clamp(index, 0, sessions.Count - 1)].Start;
        }

        /// <summary>
        /// The end of a sitting, for the upper bound.
        /// </summary>
        /// <remarks>
        /// The end rather than the start, so a range whose ends are the same sitting still
        /// contains it -- otherwise selecting one sitting selects nothing.
        /// </remarks>
        private static DateTime? EndAt(int index)
        {
            var sessions = Advice.Sessions;
            return sessions.Count == 0
                ? (DateTime?)null
                : sessions[Mathf.Clamp(index, 0, sessions.Count - 1)].End;
        }

        /// <summary>
        /// Reading and fitting are separate, and asked for rather than automatic.
        /// </summary>
        /// <remarks>
        /// Reading several hundred replays is a minute and a half; fitting both hands is
        /// twenty seconds; and between them sits a choice only the player can make. Joined
        /// together, nudging the date range by a week cost the whole read again for cuts that
        /// had not changed. Apart, the read is kept in memory and the fit re-runs against it.
        ///
        /// Neither happens on launch. Most launches are to play, and the panel is where the
        /// answer lives anyway.
        /// </remarks>
        [UIAction("read")]
        public void ReadReplays()
        {
            if (!Recommender.BeginRead())
            {
                Plugin.Log.Info("already working, or the controllers are not up yet");
            }
            Advice.Publish();
        }

        [UIAction("fit")]
        public void FitHands()
        {
            if (!Recommender.BeginFit())
            {
                Plugin.Log.Info("already working, or the controllers are not up yet");
            }
            Advice.Publish();
        }

        /// <summary>
        /// Records the current range and profile as one assignment, then leaves them free.
        /// </summary>
        /// <remarks>
        /// Built up a range at a time rather than edited as a table, because a table wants a
        /// dropdown per row and BSML does not do that comfortably -- and this way each entry
        /// is made with the same three controls the player has already used once.
        /// </remarks>
        [UIAction("assign")]
        public void AssignRange()
        {
            var sessions = Advice.Sessions;
            if (sessions.Count == 0)
            {
                return;
            }
            var from = Preferences.RangeStart ?? sessions[0].Start;
            var to = Preferences.RangeEnd ?? sessions[sessions.Count - 1].End;
            if (!Preferences.TryGetRangeGrip(out var left, out var right, out var alternative))
            {
                Plugin.Log.Info("pick which profile that range was played on before assigning");
                return;
            }
            Preferences.AddAssignment(new Preferences.Assignment
            {
                From = from,
                To = to,
                LeftRotation = left,
                RightRotation = right,
                AlternativeHandling = alternative,
            });
            Advice.Publish();
        }

        [UIAction("clear-assignments")]
        public void ClearAssignments()
        {
            Preferences.ClearAssignments();
            Plugin.Log.Info("assignments cleared");
            Advice.Publish();
        }

        [UIValue("assignments")]
        public string AssignmentList
        {
            get
            {
                var list = Preferences.Assignments;
                if (!Recommender.HasRead)
                {
                    return "";
                }
                if (list.Count == 0)
                {
                    return Advice.UnknownRuns == 0
                        ? ""
                        : $"No ranges assigned yet, so none of those {Advice.UnknownRuns} runs "
                          + "will be used.";
                }
                var lines = new List<string>();
                foreach (var a in list)
                {
                    lines.Add($"{a.From:d MMM HH:mm} - {a.To:d MMM HH:mm}: "
                              + $"L {Short(a.LeftRotation)}  R {Short(a.RightRotation)}");
                }
                return string.Join("\n", lines);
            }
        }

        [UIValue("read-button")]
        public string ReadButton => Recommender.Running
            ? "Working..."
            : Recommender.HasRead
                ? $"1. Re-read replays ({Recommender.RunsRead} in memory)"
                : "1. Read replays";

        [UIValue("fit-button")]
        public string FitButton => Recommender.Running
            ? "Working..."
            : Recommender.HasRead
                ? "3. Fit both hands"
                : "3. Fit both hands (read first)";

        /// <summary>
        /// A real bar, driven straight from the component rather than through markup.
        /// </summary>
        /// <remarks>
        /// BSML has no progress-bar tag -- only an indeterminate spinner -- and its numeric
        /// attributes do not take bound values, which is what defeated the slider bounds
        /// earlier. An image set to Filled has exactly the behaviour wanted, so the component
        /// is captured and its fill written each frame instead.
        /// </remarks>
        [UIComponent("progress-fill")]
        private Image _fill;

        [UIObject("progress-row")]
        private GameObject _progressRow;

        /// <summary>
        /// The buttons, so they can be greyed rather than merely labelled.
        /// </summary>
        /// <remarks>
        /// Saying "read first" on a button that still responds is a label pretending to be a
        /// rule. Driven from the component for the same reason as the fill: BSML fixes its
        /// attributes at parse time, and this state changes while the panel is open.
        /// </remarks>
        [UIComponent("read-button")]
        private Button _readButton;

        [UIComponent("fit-button")]
        private Button _fitButton;

        /// <summary>
        /// The range sliders, so their labels can be redrawn when the sessions arrive.
        /// </summary>
        /// <remarks>
        /// A slider runs its formatter when its *value* changes, not when the host says a
        /// property did. Both sliders sit at the same position before and after a read -- 0%
        /// and 100% -- so nothing re-formatted them, and they went on reporting the "no
        /// replays yet" they were built with while the panel above announced three hundred.
        /// </remarks>
        [UIComponent("from-slider")]
        private SliderSetting _fromSlider;

        [UIComponent("until-slider")]
        private SliderSetting _untilSlider;

        [UIComponent("profile-list")]
        private ListSetting _profileList;

        private int _sessionsShown = -1;

        internal void RedrawRange()
        {
            var sessions = Advice.Sessions.Count;
            if (sessions == _sessionsShown)
            {
                return;
            }
            _sessionsShown = sessions;
            // Only when the session list itself changes: doing this every frame would fight
            // the player for the handle they are dragging.
            //
            // ReceiveValue alone is not enough. It pulls the value back from the host, but the
            // label is drawn by the slider underneath and only when that slider's own value
            // moves -- and these sit at 0% and 100% before and after a read alike. So the
            // labels kept the "no replays yet" they were built with, while adjusting either
            // one by a notch showed the dates immediately, which is the shape of the bug.
            Redraw(_fromSlider);
            Redraw(_untilSlider);
        }

        private static void Redraw(SliderSetting setting)
        {
            if (setting == null)
            {
                return;
            }
            setting.ReceiveValue();
            var slider = setting.Slider as TextSlider;
            if (slider != null)
            {
                slider.Refresh();
            }
        }

        internal void DrawProgress()
        {
            if (_progressRow != null)
            {
                _progressRow.SetActive(Recommender.Running);
            }
            if (_readButton != null)
            {
                _readButton.interactable = !Recommender.Running;
            }
            if (_fitButton != null)
            {
                // Nothing to fit until something has been read, and nothing may start while
                // a step is already running.
                _fitButton.interactable = Recommender.HasRead && !Recommender.Running;
            }

            // Greyed rather than merely explained. These govern runs with no recorded
            // settings, so with none of those left they do nothing at all -- and a live
            // control that does nothing invites the player to wonder what they broke.
            var needed = Advice.UnknownRuns > 0 && !Recommender.Running;
            if (_fromSlider != null)
            {
                _fromSlider.Interactable = needed;
            }
            if (_untilSlider != null)
            {
                _untilSlider.Interactable = needed;
            }
            if (_profileList != null)
            {
                _profileList.Interactable = needed;
            }
            if (_fill == null)
            {
                return;
            }
            if (_fill.type != Image.Type.Filled)
            {
                _fill.type = Image.Type.Filled;
                _fill.fillMethod = Image.FillMethod.Horizontal;
                _fill.fillOrigin = (int)Image.OriginHorizontal.Left;
            }
            _fill.fillAmount = Mathf.Clamp01(Advice.Progress);
        }

        [UIValue("analyse-button")]
        public string AnalyseButton =>
            Recommender.Running ? "Working..." : "Analyse my replays";

        /// <summary>What the controls below actually govern, said plainly.</summary>
        /// <remarks>
        /// They apply only to runs from before the journal existed. Anything it covers is
        /// recorded fact and no answer here can improve on it -- but labelled "use replays
        /// from", they read as a filter over everything, which is how a control that governs
        /// a third of the data looked like one that governed all of it.
        /// </remarks>
        [UIValue("scope")]
        public string Scope => !Recommender.HasRead
            ? ""
            : Advice.UnknownRuns == 0
                ? "Every run has recorded settings."
                : $"The settings below apply to the {Advice.UnknownRuns} runs from before "
                  + "this mod was installed.";

        [UIValue("status")]
        public string Status => Advice.Summary
            + (Timeline.Length > 0 ? "\n" + Timeline : "")
            + (Advice.Evidence.Length > 0 ? "\n" + Advice.Evidence : "");

        [UIValue("advisory")]
        public string Advisory =>
            (Advice.Left + (Advice.Right.Length > 0 ? "\n" + Advice.Right : "")).Trim();

        /// <summary>Runs per session as a row of blocks, so the shape of the history shows.</summary>
        private string Timeline
        {
            get
            {
                var sessions = Advice.Sessions;
                if (sessions.Count == 0)
                {
                    return "";
                }
                // Bucketed to a fixed width rather than one glyph per session. A glyph each
                // made the line forty characters wide, and the container sizes itself to its
                // widest child -- so the chart quietly stretched the panel until the labels
                // ran off the left edge.
                const int Columns = 16;
                const string Blocks = "▁▂▃▅▆▇█";
                var totals = new int[Columns];
                var first = sessions[0].Start;
                var width = Math.Max((sessions[sessions.Count - 1].Start - first).TotalDays, 1);
                foreach (var sitting in sessions)
                {
                    var at = (int)((sitting.Start - first).TotalDays / width * (Columns - 1));
                    totals[Mathf.Clamp(at, 0, Columns - 1)] += sitting.Runs;
                }
                var most = 1;
                foreach (var total in totals)
                {
                    most = Math.Max(most, total);
                }
                var bar = new char[Columns];
                for (var i = 0; i < Columns; i++)
                {
                    bar[i] = totals[i] == 0
                        ? ' '
                        : Blocks[Mathf.Clamp(
                            Mathf.RoundToInt((float)totals[i] / most * (Blocks.Length - 1)),
                            0, Blocks.Length - 1)];
                }
                return $"{first:MMM d} {new string(bar)} {sessions[sessions.Count - 1].Start:MMM d}";
            }
        }

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

        /// <summary>A stretch of continuous play, and how many runs it holds.</summary>
        internal struct Span
        {
            public DateTime Start;
            public DateTime End;
            public int Runs;
        }

        /// <summary>Sittings, oldest first. What the range sliders move over.</summary>
        /// <remarks>
        /// Sittings rather than calendar days, because a settings change happens at a moment
        /// and not at midnight. Grouped by day, an evening's play before a change and the same
        /// evening's play after it are one indivisible point -- so a range could not be drawn
        /// between them, and a change made at 22:49 was unsplittable by construction.
        ///
        /// Also why each position lands on play that happened: picking a Tuesday nobody played
        /// is a position that cannot mean anything.
        /// </remarks>
        internal static volatile List<Span> Sessions = new List<Span>();

        /// <summary>A gap this long ends a sitting.</summary>
        internal static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(30);

        /// <summary>Runs with no recorded settings, which are the only ones the panel governs.</summary>
        internal static volatile int UnknownRuns;

        /// <summary>Bumped whenever any of the above changes, so the menu can notice.</summary>
        internal static volatile int Version;

        /// <summary>How far through the current piece of work, from 0 to 1.</summary>
        internal static volatile float Progress;

        internal static void Publish() => Version++;
    }
}
