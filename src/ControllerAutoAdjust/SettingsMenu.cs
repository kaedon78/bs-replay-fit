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
                         nameof(Status), nameof(EvidenceLine), nameof(Advisory), nameof(AssignmentList), nameof(StartPercent), nameof(EndPercent),
                         nameof(ReadButton), nameof(FitButton),
                         nameof(ProfileChoices), nameof(Profile), nameof(TargetChoices), nameof(Target),
                         nameof(TimelineRow), nameof(ActionNote),
                         nameof(FromLabel), nameof(UntilLabel), nameof(ProfileLabel),
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
            private bool _autoRead;

            private void Update()
            {
                // A read at startup, for testing the read itself without a hand on the
                // button. Behind a marker so it cannot happen to a player: doing this
                // uninvited is exactly what the button replaced.
                if (!_autoRead && Recommender.HasRead == false && !Recommender.Running)
                {
                    _autoRead = true;
                    if (System.IO.File.Exists(
                            System.IO.Path.Combine(Paths.DataDir, "read-on-start.on")))
                    {
                        Plugin.Log.Info("read-on-start marker present; reading");
                        Recommender.BeginRead();
                    }
                }

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

        /// <summary>
        /// The profiles a fitted grip can be written into.
        /// </summary>
        /// <remarks>
        /// Modifiable ones only. The built-in profile refuses edits, and offering it would
        /// produce a button that reports success and changes nothing. Unlike the range
        /// picker, an all-default profile is offered here: an empty slot is exactly where a
        /// player would want a fitted grip to land.
        /// </remarks>
        private static List<ControllerProfile> Writable()
        {
            var writable = new List<ControllerProfile>();
            foreach (var profile in SettingsWatcher.Profiles)
            {
                if (profile != null && profile.modifiable)
                {
                    writable.Add(profile);
                }
            }
            return writable;
        }

        private static string Name(ControllerProfile profile) =>
            $"#{profile.index + 1}  {Describe(profile)}";

        [UIValue("target-choices")]
        public List<object> TargetChoices
        {
            get
            {
                var choices = new List<object>();
                foreach (var profile in Writable())
                {
                    choices.Add(Name(profile));
                }
                if (choices.Count == 0)
                {
                    choices.Add("No editable profile");
                }
                return choices;
            }
        }

        private string _target = "";

        [UIValue("target")]
        public string Target
        {
            get
            {
                var writable = Writable();
                foreach (var profile in writable)
                {
                    if (Name(profile) == _target)
                    {
                        return _target;
                    }
                }
                // Default to the one being played on, when it is one that can be written to.
                var model = SettingsWatcher.Model;
                if (model != null && model.selectedProfile != null
                    && model.selectedProfile.modifiable)
                {
                    return Name(model.selectedProfile);
                }
                return writable.Count > 0 ? Name(writable[0]) : "No editable profile";
            }
            set => _target = value;
        }

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
                Advice.Note = "Read the replays first.";
                Advice.Publish();
                return;
            }
            var from = Preferences.RangeStart ?? sessions[0].Start;
            var to = Preferences.RangeEnd ?? sessions[sessions.Count - 1].End;

            // "As they are now" is a real answer, not a missing one. It stores no grip --
            // there is no profile to copy from -- so the live settings stand in for it.
            // Refusing it silently made the button look broken for the default choice.
            if (!Preferences.TryGetRangeGrip(out var left, out var right, out var alternative))
            {
                if (!OffsetState.TryRead(out var live))
                {
                    Advice.Note = "Could not read the current settings.";
                    Advice.Publish();
                    return;
                }
                left = live.Left.TypedRotation;
                right = live.Right.TypedRotation;
                alternative = live.AlternativeHandling;
            }
            Preferences.AddAssignment(new Preferences.Assignment
            {
                From = from,
                To = to,
                LeftRotation = left,
                RightRotation = right,
                AlternativeHandling = alternative,
            });
            Advice.Note = $"Assigned {from:d MMM HH:mm} to {to:d MMM HH:mm}.";
            Advice.Publish();
        }

        /// <summary>
        /// Drop the most recent assigned range.
        /// </summary>
        /// <remarks>
        /// The latest rather than any of them, because there is nowhere to pick from: the
        /// list is a block of text, and a control to choose a row would cost more of the
        /// panel than it is worth for the two or three ranges a history usually needs. Ranges
        /// are built newest last, so undoing in that order matches how they were made.
        /// </remarks>
        [UIAction("remove-last")]
        public void RemoveLastAssignment()
        {
            var list = Preferences.Assignments;
            if (list.Count == 0)
            {
                Advice.Note = "No ranges to remove.";
                Advice.Publish();
                return;
            }
            var last = list[list.Count - 1];
            Preferences.RemoveAssignment(last);
            Advice.Note = $"Removed {last.From:d MMM HH:mm} to {last.To:d MMM HH:mm}.";
            Advice.Publish();
        }

        /// <summary>
        /// Write the fitted grip into a profile, select it, and record that it happened.
        /// </summary>
        /// <remarks>
        /// Selecting it is part of the job rather than an extra liberty. Written into a
        /// profile the player is not on, the numbers change nothing they can feel, and a
        /// button that reports success while play is unchanged is worse than one that does
        /// nothing at all.
        ///
        /// Recording it is the half that makes the next fit possible. Every replay from here
        /// belongs to a different grip than the ones behind the advice, and a journal entry
        /// is the only thing that says where the boundary falls; without it, the next fit
        /// pools both and quietly fits a compromise.
        /// </remarks>
        [UIAction("apply")]
        public void ApplyRecommendation()
        {
            var advice = Advice.Recommended;
            if (advice == null)
            {
                Advice.Note = "Nothing fitted yet to apply.";
                Advice.Publish();
                return;
            }
            var model = SettingsWatcher.Model;
            ControllerProfile target = null;
            foreach (var profile in Writable())
            {
                if (Name(profile) == Target)
                {
                    target = profile;
                    break;
                }
            }
            if (model == null || target == null)
            {
                Advice.Note = "No editable profile to write to.";
                Advice.Publish();
                return;
            }

            var before = $"L {Short(target.leftController.rotation)} "
                         + $"R {Short(target.rightController.rotation)}";

            // Rotation only. The fit moves the blade by turning the controller; the position
            // is the player's own and nothing here measured it.
            target.UpdateControllerOffset(true, target.leftController.position, advice.Left);
            target.UpdateControllerOffset(false, target.rightController.position, advice.Right);

            // The search composed each candidate under this flag to decide where the blade
            // lands, so the profile has to agree with it or the numbers mean something else.
            var handlingMoved = target.alternativeHandling != advice.AlternativeHandling;
            if (handlingMoved)
            {
                target.SetRotateThanMove(advice.AlternativeHandling);
            }

            var wasSelected = model.selectedProfile == target;
            if (!wasSelected)
            {
                // Its position in the list, not its own index. Built-in and custom profiles
                // number themselves separately, so profile.index is not a place in the list
                // and selecting by it lands on the wrong profile.
                var at = -1;
                for (var i = 0; i < model.profiles.Count; i++)
                {
                    if (model.profiles[i] == target)
                    {
                        at = i;
                        break;
                    }
                }
                if (at >= 0)
                {
                    model.UpdateSelectedProfile(at);
                }
            }
            model.SaveAsync();

            Plugin.Log.Info(
                $"applied to profile #{target.index + 1}: {before} -> "
                + $"L {Short(advice.Left)} R {Short(advice.Right)}"
                + (handlingMoved ? $", handling set to {advice.AlternativeHandling}" : "")
                + (wasSelected ? "" : ", and selected it"));

            // A frame or two late: the write refreshes the controllers, and the poses the
            // journal reads are last frame's until it has.
            _journalIn = 4;

            Advice.Note = $"Applied to profile #{target.index + 1}"
                          + (wasSelected ? "." : " and switched to it.")
                          + (handlingMoved ? " Rotate-then-move set to match the fit." : "");
            Advice.Publish();
        }

        private int _journalIn = -1;

        [UIAction("clear-assignments")]
        public void ClearAssignments()
        {
            Preferences.ClearAssignments();
            Plugin.Log.Info("assignments cleared");
            Advice.Note = "Assignments cleared.";
            Advice.Publish();
        }

        /// <summary>
        /// Held between changes, because reading it is not free.
        /// </summary>
        /// <remarks>
        /// It opens the preferences file and walks every run to count what each range covers.
        /// That was fine while it was read once per change, and stopped being fine when the
        /// row began collapsing itself, which asks every frame whether there is anything in
        /// it. Keyed on the advice version, which is bumped by everything that could alter
        /// the answer.
        /// </remarks>
        private int _listedFor = -1;
        private string _listed = "";

        [UIValue("assignments")]
        public string AssignmentList
        {
            get
            {
                if (_listedFor != Advice.Version)
                {
                    _listedFor = Advice.Version;
                    _listed = BuildAssignmentList();
                }
                return _listed;
            }
        }

        private string BuildAssignmentList()
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
                    : $"No ranges assigned, so none of those {Advice.UnknownRuns} runs "
                      + "will be used.";
            }

            // Each line carries how many runs it actually covers. A range that reads
            // plausibly and holds nothing is the failure worth catching here: the dates
            // look right, and the fit quietly has less than it appears to.
            var lines = new List<string>();
            foreach (var a in list)
            {
                lines.Add($"{a.From:d MMM HH:mm} - {a.To:d MMM HH:mm}  "
                          + $"L {Short(a.LeftRotation)} R {Short(a.RightRotation)}  "
                          + $"[{Recommender.RunsCoveredBy(a)} runs]");
            }
            return string.Join("\n", lines);
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
        /// Rows that vanish when they have nothing to say.
        /// </summary>
        /// <remarks>
        /// Each holds one line that is empty most of the time -- no advice until a fit has
        /// run, no feedback until a button is pressed -- and a fixed height for an empty line
        /// is dead space in a panel that scrolls. It costs most at the end: the trailing gap
        /// is what the view is filled with at full scroll, pushing the assigned ranges off
        /// the top of it.
        ///
        /// A height bound from code would do the same, but BSML will not bind a numeric
        /// attribute, which is what defeated the slider bounds earlier. Deactivating the row
        /// takes it out of the layout entirely, and the layout closes up on its own.
        /// </remarks>
        [UIObject("status-row")]
        private GameObject _statusRow;

        [UIComponent("fit-progress-fill")]
        private Image _fitFill;

        [UIObject("fit-progress-row")]
        private GameObject _fitProgressRow;

        [UIObject("fit-status-row")]
        private GameObject _fitStatusRow;

        [UIObject("timeline-row")]
        private GameObject _timelineRow;

        [UIObject("assignments-row")]
        private GameObject _assignmentsRow;

        [UIObject("evidence-row")]
        private GameObject _evidenceRow;

        [UIObject("note-row")]
        private GameObject _noteRow;

        [UIObject("advisory-row")]
        private GameObject _advisoryRow;

        [UIComponent("apply-button")]
        private Button _applyButton;

        [UIComponent("target-list")]
        private ListSetting _targetList;

        [UIObject("apply-row")]
        private GameObject _applyRow;

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

        private ScrollView _scroll;
        private string _rowsShown = "";
        private int _scrollIn = -1;

        /// <summary>
        /// Follow the fit down the panel as it grows.
        /// </summary>
        /// <remarks>
        /// Pressing fit adds a status line, a bar and then two lines of advice, all below the
        /// button, and all of it off the bottom of a panel already scrolled to show the
        /// button. The player pressed it and nothing appeared to happen.
        ///
        /// Only on a change in what is on screen, never continuously: a scroll that reasserts
        /// itself every frame is one the player cannot scroll away from. And only while
        /// fitting, so reading -- whose progress is at the top -- is left alone.
        ///
        /// Two frames late because the content size is stale until the layout has rebuilt
        /// around the rows that just appeared, and scrolling to the end of a size that
        /// predates them lands short.
        /// </remarks>
        private void KeepTheFitInView(bool fitting)
        {
            var shown = string.Concat(
                fitting ? "f" : "-",
                Active(_fitStatusRow), Active(_fitProgressRow), Active(_advisoryRow),
                Active(_applyRow));
            if (shown != _rowsShown)
            {
                _rowsShown = shown;
                if (fitting)
                {
                    _scrollIn = 2;
                }
            }
            if (_scrollIn < 0)
            {
                return;
            }
            if (_scrollIn-- > 0)
            {
                return;
            }
            if (_scroll == null && _fitStatusRow != null)
            {
                _scroll = _fitStatusRow.GetComponentInParent<ScrollView>(true);
                // Said once, because not finding it is a silent no-op otherwise, and a
                // scroll that never happens looks exactly like one that was not wanted.
                Plugin.Log.Info(_scroll != null
                    ? "found the settings scroll view"
                    : "no scroll view above the panel; it will not follow the fit down");
            }
            if (_scroll != null)
            {
                _scroll.UpdateContentSize();
                _scroll.ScrollToEnd(true);
            }
        }

        private static string Active(GameObject row) =>
            row != null && row.activeSelf ? "1" : "0";

        private static void Show(GameObject row, string content) =>
            Show(row, content.Length > 0);

        private static void Show(GameObject row, bool wanted)
        {
            if (row != null && row.activeSelf != wanted)
            {
                row.SetActive(wanted);
            }
        }

        internal void DrawProgress()
        {
            // Each step's own pair. Which one is on is the step's, not a preference: the
            // line a step leaves behind stays where the step was, so the answer sits under
            // the button that produced it rather than jumping back to the top of the panel.
            var fitting = Recommender.Phase == Recommender.Step.Fitting;
            Show(_progressRow, Recommender.Running && !fitting);
            Show(_fitProgressRow, Recommender.Running && fitting);
            Show(_statusRow, Status.Length > 0 && !fitting);
            Show(_fitStatusRow, Status.Length > 0 && fitting);
            Show(_timelineRow, Advice.Sessions.Count > 0);
            Show(_assignmentsRow, AssignmentList);
            Show(_evidenceRow, EvidenceLine);
            Show(_noteRow, ActionNote);
            Show(_advisoryRow, Advisory);

            // Both hidden until there is something to apply. A picker and a button that can
            // only report having nothing to do are two more rows of a panel saying no.
            var ready = Advice.Recommended != null && !Recommender.Running;
            Show(_applyRow, ready);
            if (_targetList != null)
            {
                _targetList.gameObject.SetActive(ready);
            }
            if (_applyButton != null)
            {
                _applyButton.interactable = ready;
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
            Fill(_fill);
            Fill(_fitFill);
            KeepTheFitInView(fitting);
            if (_journalIn >= 0 && _journalIn-- == 0)
            {
                SettingsWatcher.CaptureNow();
            }
        }

        private static void Fill(Image bar)
        {
            if (bar == null)
            {
                return;
            }
            if (bar.type != Image.Type.Filled)
            {
                bar.type = Image.Type.Filled;
                bar.fillMethod = Image.FillMethod.Horizontal;
                bar.fillOrigin = (int)Image.OriginHorizontal.Left;
            }
            bar.fillAmount = Mathf.Clamp01(Advice.Progress);
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
        [UIValue("status")]
        public string Status => Advice.Summary;

        /// <summary>
        /// What the mod cannot work out for itself, placed where it is acted on.
        /// </summary>
        /// <remarks>
        /// It ends "assigned below", and sat two rows above a chart and a button. Directly
        /// over the controls it refers to, it reads as a caption for them rather than as one
        /// more line of status.
        /// </remarks>
        [UIValue("evidence")]
        public string EvidenceLine => Recommender.HasRead ? Advice.Evidence : "";

        /// <summary>
        /// The range being described right now, so the labels say which one they build.
        /// </summary>
        /// <remarks>
        /// Three controls and a button add one segment at a time, and nothing on screen said
        /// so: the fields read as the only range there was, rather than as the next one.
        /// </remarks>
        private static int NextRange => Preferences.Assignments.Count + 1;

        [UIValue("from-label")]
        public string FromLabel => $"Range {NextRange}: Replays starting";

        [UIValue("until-label")]
        public string UntilLabel => $"Range {NextRange}: Replays ending";

        [UIValue("profile-label")]
        public string ProfileLabel => $"Range {NextRange}: Controller settings used";

        /// <summary>The last thing a button did, kept next to the buttons.</summary>
        /// <remarks>
        /// Was folded into the assignment list, which now sits near the top of the panel
        /// where the assigned ranges are visible without scrolling. Feedback for a press
        /// belongs where the finger is, so the two were separated.
        /// </remarks>
        [UIValue("note")]
        public string ActionNote => Advice.Note;

        [UIValue("timeline")]
        public string TimelineRow => Timeline;

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
                // Bucketed to a fixed width rather than one glyph per session, because the
                // container sizes itself to its widest child and an unbounded chart stretched
                // the panel until the labels ran off the left edge. Sixteen columns fitted and
                // said little: months of history per bar hides every gap worth seeing. The row
                // is monospaced in the markup instead, at a cell narrow enough to afford this
                // many and still line the bars up under one another.
                const int Columns = 40;
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
                // Bars on their own line at a fixed cell width, dates beneath. Sharing a
                // line with the dates left about two thirds of the row for the chart,
                // which is what held the column count down.
                var runs = 0;
                foreach (var sitting in sessions)
                {
                    runs += sitting.Runs;
                }
                return $"<mspace=2>{new string(bar)}</mspace>\n"
                       + $"{runs} runs from {first:MMM d} to "
                       + $"{sessions[sessions.Count - 1].Start:MMM d}";
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

        /// <summary>The last thing a button did, so pressing one is visibly not a no-op.</summary>
        internal static volatile string Note = "";
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

        /// <summary>
        /// The numbers behind the advice, kept so a button can act on them.
        /// </summary>
        /// <remarks>
        /// The panel showed the fit as a sentence, which is enough to read and retype and
        /// nothing else. Held as one object assigned whole, rather than a field per hand,
        /// because the fit runs on a worker: a reader either has the whole recommendation or
        /// the previous one, never half of each.
        ///
        /// Null until a fit produces something worth acting on, which is what the apply
        /// button is enabled by. A group too thin to recommend leaves it null even though
        /// there are numbers to show.
        /// </remarks>
        internal class Recommendation
        {
            public Vector3 Left;
            public Vector3 Right;
            public Vector3 WasLeft;
            public Vector3 WasRight;

            /// <summary>
            /// The handling the fit assumed, which the applied profile has to match.
            /// </summary>
            /// <remarks>
            /// The search composes a candidate with the legacy offset and this flag to work
            /// out where the blade lands. Written into a profile set the other way, the same
            /// three numbers put the blade somewhere else, and the result would be a
            /// recommendation that measurably makes things worse.
            /// </remarks>
            public bool AlternativeHandling;
        }

        internal static volatile Recommendation Recommended;

        /// <summary>Bumped whenever any of the above changes, so the menu can notice.</summary>
        internal static volatile int Version;

        /// <summary>How far through the current piece of work, from 0 to 1.</summary>
        internal static volatile float Progress;

        internal static void Publish() => Version++;
    }
}
