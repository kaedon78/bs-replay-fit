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

namespace ReplayFit
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
                         nameof(Status), nameof(EvidenceLine), nameof(Advisory),
                         nameof(Headline), nameof(Hands), nameof(Gained), nameof(RangesNeeded),
                         nameof(Chart), nameof(Standing),
                         nameof(Range0), nameof(Range1), nameof(Range2), nameof(Range3),
                         nameof(Range4), nameof(Range5), nameof(Range6), nameof(Range7),
                         nameof(Range8), nameof(Range9), nameof(Range10), nameof(Range11),
                         nameof(Range0Button), nameof(Range1Button), nameof(Range2Button),
                         nameof(Range3Button), nameof(Range4Button), nameof(Range5Button),
                         nameof(Range6Button), nameof(Range7Button), nameof(Range8Button),
                         nameof(Range9Button), nameof(Range10Button), nameof(Range11Button),
                         nameof(Range0UseButton), nameof(Range1UseButton),
                         nameof(Range2UseButton), nameof(Range3UseButton),
                         nameof(Range4UseButton), nameof(Range5UseButton),
                         nameof(Range6UseButton), nameof(Range7UseButton),
                         nameof(Range8UseButton), nameof(Range9UseButton),
                         nameof(Range10UseButton), nameof(Range11UseButton), nameof(Managed0),
                         nameof(Managed1), nameof(Managed2), nameof(Managed3), nameof(Managed4),
                         nameof(Managed5), nameof(Managed6), nameof(Managed7), nameof(Managed8),
                         nameof(Managed9), nameof(Managed10), nameof(Managed11),
                         nameof(Managed0UseButton), nameof(Managed1UseButton),
                         nameof(Managed2UseButton), nameof(Managed3UseButton),
                         nameof(Managed4UseButton), nameof(Managed5UseButton),
                         nameof(Managed6UseButton), nameof(Managed7UseButton),
                         nameof(Managed8UseButton), nameof(Managed9UseButton),
                         nameof(Managed10UseButton), nameof(Managed11UseButton),
                         nameof(AssignmentList), nameof(ManagedList),
                         nameof(AssignedHeader), nameof(ManagedHeader),
                         nameof(StartPercent), nameof(EndPercent),
                         nameof(ReadButton), nameof(FitButton),
                         nameof(ProfileChoices), nameof(Profile), nameof(TargetChoices), nameof(Target),
                         nameof(TimelineRow), nameof(ActionNote),
                         nameof(FromLabel), nameof(UntilLabel), nameof(ProfileLabel),
                     })
            {
                changed(this, new PropertyChangedEventArgs(name));
            }
        }

        /// <summary>
        /// The label in the mod settings list, spaced where the identifier cannot be.
        /// </summary>
        /// <remarks>
        /// Only the label. The plugin id, the assembly and the folder under UserData stay
        /// ReplayFit, because those are identifiers and one of them names the folder the
        /// journal lives in.
        /// </remarks>
        internal const string MenuName = "Replay Fit";
        internal const string Resource = "ReplayFit.Views.settings.bsml";

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
                // Polled even once registered, because being registered does not stay true.
                if (Time.unscaledTime >= _next)
                {
                    _next = Time.unscaledTime + RetryEvery;
                    Register();
                }
                if (_registered == null)
                {
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
                    _registered.RedrawLists();
                }
                // Every frame, not only on a version bump: the fill should move smoothly
                // rather than in the steps the text updates on.
                _registered.DrawProgress();
            }
        }

        /// <summary>
        /// The BSMLSettings the menu was added to, which is not the same one forever.
        /// </summary>
        /// <remarks>
        /// It is a Zenject singleton, so it lives and dies with its container: reloading the
        /// menu scene -- which the game does after any settings change, including the one
        /// this panel's apply button makes -- builds a fresh instance holding an empty list.
        /// The entry added to the old one goes with it, and the mod simply vanishes from the
        /// settings list with nothing logged. Comparing instances is what notices.
        /// </remarks>
        private static BSMLSettings _addedTo;

        internal static void Register()
        {
            try
            {
                var settings = BSMLSettings.Instance;
                if (_registered != null && ReferenceEquals(settings, _addedTo))
                {
                    return;
                }
                var again = _registered != null;
                if (!again)
                {
                    _registered = new SettingsMenu();
                }
                // Keeping the same host across a re-add: BSML parses the markup again and
                // reassigns every bound field, and the old scene's objects are gone anyway.
                settings.AddSettingsMenu(MenuName, Resource, _registered);
                _addedTo = settings;
                Plugin.Log.Info(again
                    ? "settings menu re-registered after a scene reload"
                    : "settings menu registered");
            }
            catch (Exception e)
            {
                // Not fatal, and not necessarily final: before the menu scene exists this is
                // just "too early" and the next attempt will succeed. Everything the menu
                // shows is in the log either way.
                _registered = null;
                _addedTo = null;
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
                    _addedTo = null;
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
                // A stored range keeps the numbers it was given, not the profile it came
                // from, so editing that profile leaves the range describing a grip no
                // profile has any more. Without its own entry the list cannot show it and
                // silently falls back to the first, which reads as the answer having been
                // lost rather than as the profile having moved.
                var current = Profile;
                if (!choices.Contains(current))
                {
                    choices.Add(current);
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

        /// <summary>
        /// Where a new range ends: the newest unrecorded play nothing has claimed yet.
        /// </summary>
        /// <remarks>
        /// Not a choice any more. Ranges are built backwards now -- newest grip first, which
        /// is the one still remembered -- and each runs from wherever the player puts its
        /// start up to wherever the assigned ranges begin. That end is arithmetic, so asking
        /// for it was asking a question with one right answer, on a slider whose notch was
        /// worth two sittings and could therefore give the wrong one.
        ///
        /// The control stays, greyed, because the date it shows is the half of the range the
        /// player did not set and would otherwise have to infer.
        /// </remarks>
        [UIValue("end-percent")]
        public int EndPercent
        {
            get => PercentOf(NewestLoose(), 100);
            set { }
        }

        /// <summary>The end of the newest governable sitting no assignment covers.</summary>
        private static DateTime? NewestLoose()
        {
            var sittings = Advice.Governable;
            for (var i = sittings.Count - 1; i >= 0; i--)
            {
                var covered = false;
                foreach (var a in Preferences.Assignments)
                {
                    if (a.Covers(sittings[i].End))
                    {
                        covered = true;
                        break;
                    }
                }
                if (!covered)
                {
                    return sittings[i].End;
                }
            }
            return null;
        }

        private static int PercentOf(DateTime? when, int fallback)
        {
            var sessions = Advice.Governable;
            if (!when.HasValue || sessions.Count < 2)
            {
                return fallback;
            }
            var i = IndexOf(when, 0);
            return Mathf.RoundToInt(100f * i / (sessions.Count - 1));
        }

        private static int SessionAt(float percent)
        {
            var sessions = Advice.Governable;
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
            var sessions = Advice.Governable;
            if (sessions.Count == 0)
            {
                return "no replays yet";
            }
            var span = sessions[SessionAt(percent)];
            return $"{Shown.Moment(span.Start)} ({span.Runs})";
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
            var sessions = Advice.Governable;
            if (!when.HasValue || sessions.Count == 0)
            {
                return fallback;
            }
            var best = fallback;
            var closest = double.MaxValue;
            for (var i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                var gap = when.Value < s.Start ? (s.Start - when.Value).TotalMinutes
                        : when.Value > s.End ? (when.Value - s.End).TotalMinutes
                        : 0.0;
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
            var sessions = Advice.Governable;
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
            var sessions = Advice.Governable;
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
            var sessions = Advice.Governable;
            if (sessions.Count == 0)
            {
                Advice.Note = "Read the replays first.";
                Advice.Publish();
                return;
            }
            var from = Preferences.RangeStart ?? sessions[0].Start;
            var to = NewestLoose() ?? sessions[sessions.Count - 1].End;

            // Ranges may not overlap, and the sliders alone could not keep them apart. They
            // move over sittings, so the earliest start reachable after one range ends is
            // often the very sitting that range ends inside -- and a player wanting the next
            // range to begin there had no way to say so except by overlapping the last one.
            // A replay in the shared window then belonged to two grips at once and was
            // handed to whichever range the fit happened to test first.
            //
            // So the start is pushed past anything already spoken for rather than refused:
            // the press does what was asked wherever it still can, and says where it landed.
            var moved = false;
            foreach (var a in Preferences.Assignments)
            {
                if (a.Covers(from) && a.To >= from)
                {
                    from = NextSittingAfter(a.To) ?? a.To.AddTicks(1);
                    moved = true;
                }
            }
            if (to < from)
            {
                Advice.Note = "That range is already covered by an earlier one.";
                Advice.Publish();
                return;
            }

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
            // Back to the oldest, because the end is now fixed to whatever is still
            // unclaimed and that has just moved earlier. The next range is the stretch before
            // this one, so the widest useful start is the beginning of the history; a player
            // narrowing it drags forward from there. Left where it was, the start would sit
            // inside the range just made and the next press would have nothing to assign.
            Preferences.RangeStart = null;
            Advice.Note = moved
                ? $"Assigned {Shown.Moment(from)} to {Shown.Moment(to)}, "
                  + "starting after the range already covering it."
                : $"Assigned {Shown.Moment(from)} to {Shown.Moment(to)}.";
            Advice.Publish();
        }

        /// <summary>The start of the first sitting beginning after a moment, if there is one.</summary>
        private static DateTime? NextSittingAfter(DateTime when)
        {
            foreach (var sitting in Advice.Governable)
            {
                if (sitting.Start > when)
                {
                    return sitting.Start;
                }
            }
            return null;
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

            // Rotation only: the fit moves the blade by turning the controller, and nothing
            // here measured position. But the position has to come from the profile being
            // played on, not from the one being written to. Writing a fitted grip into an
            // empty slot took that slot's zeroes, which throws away a placement the player
            // arrived at by hand and is not a change anybody asked for.
            var playing = model.selectedProfile ?? target;
            var leftPosition = playing.leftController.position;
            var rightPosition = playing.rightController.position;
            if (!GameApi.TrySetOffset(target, true, leftPosition, advice.Left)
                || !GameApi.TrySetOffset(target, false, rightPosition, advice.Right))
            {
                Advice.Note = "This game version does not allow writing a profile.";
                Advice.Publish();
                return;
            }

            // The search composed each candidate under this flag to decide where the blade
            // lands, so the profile has to agree with it or the numbers mean something else.
            var handlingMoved = target.alternativeHandling != advice.AlternativeHandling;
            if (handlingMoved)
            {
                target.SetRotateThanMove(advice.AlternativeHandling);
            }

            var wasSelected = model.selectedProfile == target;
            var selected = wasSelected;
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
                    selected = true;
                }
            }
            model.SaveAsync();

            // The profiles file holds the numbers; which profile is live is a main setting,
            // and the only thing that writes those is the game's settings screen closing on
            // OK. This is not that screen, so the selection had to be written here or it
            // would hold until the model was next rebuilt and then quietly go back.
            var kept = !selected || SettingsWatcher.TrySaveSelectedProfile();

            Plugin.Log.Info(
                $"positions kept from the profile in use: L {OffsetState.Fmt(leftPosition)} "
                + $"R {OffsetState.Fmt(rightPosition)}");
            Plugin.Log.Info(
                $"applied to profile #{target.index + 1}: {before} -> "
                + $"L {Short(advice.Left)} R {Short(advice.Right)}"
                + (handlingMoved ? $", handling set to {advice.AlternativeHandling}" : "")
                + (wasSelected ? "" : selected ? ", and selected it" : ", but could NOT select it")
                + (kept ? "" : "; selection not saved"));

            // A frame or two late: the write refreshes the controllers, and the poses the
            // journal reads are last frame's until it has.
            _journalIn = 4;
            ScrollSoon();

            Advice.Note = $"Applied to profile #{target.index + 1}"
                          + (wasSelected ? "." : selected ? " and switched to it." : ".")
                          + (selected ? "" : " Could not switch to it; see the log.")
                          + (kept ? "" : " It will revert when the game restarts.")
                          + (handlingMoved ? " Rotate-then-move set to match the fit." : "");
            Advice.Publish();
        }

        private int _journalIn = -1;

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
        private int _assignedRuns;
        private int _looseRuns;

        [UIValue("assignments")]
        public string AssignmentList
        {
            get
            {
                Counted();
                return _listed;
            }
        }

        /// <summary>Refresh the list and the counts beside it, if anything has changed.</summary>
        /// <remarks>
        /// Three things read this memo and one of them used to force it by asking the list
        /// for its length and discarding the answer. Saying what is meant costs a method.
        /// </remarks>
        private void Counted()
        {
            if (_listedFor == Advice.Version)
            {
                return;
            }
            _listedFor = Advice.Version;
            _listed = BuildAssignmentList();
        }

        /// <summary>
        /// One row a range, plus whatever could not be given a row.
        /// </summary>
        /// <remarks>
        /// Slots rather than a list built at runtime: BSML binds by name at parse time, so a
        /// row that does not exist in the markup cannot be bound to later. Twelve is well past
        /// what this asks of anybody -- a range is a grip you remember having, and a player
        /// with more than a dozen of those has a journal instead -- and any beyond it are
        /// still listed, just without a button, with the two blanket buttons below still
        /// able to reach them.
        ///
        /// Sorted by start date, which is where the store already leaves them, so the row a
        /// button sits beside is the row it deletes.
        /// </remarks>
        private const int RangeSlots = 12;

        private readonly List<string> _rangeLines = new List<string>();
        private readonly List<Preferences.Assignment> _ranges =
            new List<Preferences.Assignment>();

        private string BuildAssignmentList()
        {
            var list = Preferences.Assignments;
            _ranges.Clear();
            _ranges.AddRange(list);
            _rangeLines.Clear();
            _assignedRuns = 0;
            _looseRuns = Recommender.RunsWithoutRange();
            var coverage = new List<Recommender.Coverage>(list.Count);
            foreach (var a in list)
            {
                var covers = Recommender.RunsCoveredBy(a);
                coverage.Add(covers);
                _assignedRuns += covers.Governed;
            }
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
            for (var i = 0; i < list.Count; i++)
            {
                var a = list[i];
                var covers = coverage[i];
                // Every replay the range spans, which is what the number reads as. It used
                // to say "already recorded" for a range holding only replays the journal
                // knows about, on the grounds that such a range is doing nothing. True, and
                // not worth a line: what a player can act on is how many replays still need
                // a range, and the line above the list says that.
                var held = covers.Covered > 0 ? $"{covers.Covered} replays" : "no replays";
                // No "Range 3" at the front any more. It numbered rows in a list already
                // ordered by date, next to a button that acts on the row it is beside, so
                // the number named nothing the player had to say out loud.
                _rangeLines.Add($"{Shown.Moment(a.From)} - {Shown.Moment(a.To)}  "
                                + $"L {Short(a.LeftRotation)} R {Short(a.RightRotation)}  {held}");
            }
            if (list.Count <= RangeSlots)
            {
                Flip();
                return "";
            }

            // The newest that fit, and the older ones named but not shown. Every range has to
            // stay reachable now that the only way to remove one is the button on its own row:
            // deleting from the newest end walks the older ones into view, whereas keeping the
            // oldest twelve would have stranded everything past them for good.
            var spare = list.Count - RangeSlots;
            _rangeLines.RemoveRange(0, spare);
            _ranges.RemoveRange(0, spare);
            Flip();
            return $"{spare} older range(s) are not shown. Remove one above to reveal them.";
        }

        /// <summary>
        /// Newest at the top, on both lists.
        /// </summary>
        /// <remarks>
        /// The store hands ranges back oldest first and the journal is written in the order it
        /// happened, which is the right order to build them in and the wrong one to read them
        /// in. What a player comes to this tab for is the recent end: the grip they are on,
        /// the one before it, the range they drew a minute ago and want to check or undo.
        /// Oldest first put all of that at the bottom of a list twelve rows long.
        ///
        /// The rows and the ranges behind them are flipped together, because a delete button
        /// resolves its row by position and a list that reads one way while the array behind
        /// it runs the other is a button that removes the wrong range.
        /// </remarks>
        private void Flip()
        {
            _rangeLines.Reverse();
            _ranges.Reverse();
        }

        /// <summary>
        /// Which row is one press from being deleted, and for how much longer.
        /// </summary>
        /// <remarks>
        /// A range can be several evenings of remembering, and the button is a few
        /// millimetres from the one beside it. So the first press arms and the second
        /// deletes, which is the only confirmation available here: BSML has a modal, it takes
        /// a screen of its own, and putting one in front of a settings panel to ask about a
        /// date range is heavier than the thing it guards.
        ///
        /// It disarms itself after a few seconds rather than staying armed, so a button left
        /// alone goes back to meaning what it says. Arming a second row disarms the first --
        /// only one can be primed, which is what keeps the second press unambiguous.
        /// </remarks>
        private const int ArmedFrames = 240;

        private string _armedKey = "";
        private int _armedLeft;

        /// <summary>True on the press that means it. Arms on any other.</summary>
        /// <remarks>
        /// One token for every button that needs confirming, rather than a flag each, so that
        /// arming one disarms the rest by construction. Two primed buttons on adjacent rows
        /// is the state where a second press stops being unambiguous, and there is no way to
        /// reach it if only one key can be held.
        /// </remarks>
        private bool Armed(string key, string ask)
        {
            if (_armedKey == key)
            {
                _armedKey = "";
                return true;
            }
            _armedKey = key;
            _armedLeft = ArmedFrames;
            Advice.Note = ask;
            Advice.Publish();
            return false;
        }

        private string Label(string key, string resting) =>
            _armedKey == key ? "Sure?" : resting;

        private string DeleteLabel(int slot) => Label("del" + slot, "X");

        private void DeleteRange(int slot)
        {
            Counted();
            if (slot >= _ranges.Count)
            {
                return;
            }
            if (!Armed("del" + slot, "Press again to remove that range."))
            {
                return;
            }
            var a = _ranges[slot];
            Preferences.RemoveAssignment(a);
            Advice.Note = $"Removed {Shown.Moment(a.From)} to {Shown.Moment(a.To)}.";
            Advice.Publish();
        }

        private string UseLabel(int slot) => Label("use" + slot, "Use");

        private string UseManagedLabel(int slot) => Label("useman" + slot, "Use");

        /// <summary>Put a range's stored grip back on, as the settings in use now.</summary>
        /// <remarks>
        /// The profile in use rather than one picked from a list. The button says "use this",
        /// and a picker on another tab deciding where it lands would make that a different
        /// sentence -- and there is no room on the row for one anyway.
        ///
        /// Rotation only, keeping the positions already set, for the reason the fit does the
        /// same: neither a range nor a journal entry records a placement anybody arrived at by
        /// hand, and overwriting one with zeroes is not a change that was asked for.
        ///
        /// It is written like any other settings change, so the journal notices and the
        /// stretch that starts here becomes a managed range of its own.
        /// </remarks>
        private void UseGrip(Vector3 left, Vector3 right, bool alternative, string what)
        {
            var model = SettingsWatcher.Model;
            var target = model == null ? null : model.selectedProfile;
            if (target == null)
            {
                Advice.Note = "No controller profile is in use.";
                Advice.Publish();
                return;
            }
            if (!target.modifiable)
            {
                Advice.Note = "The profile in use is built in and cannot be edited. "
                              + "Switch to a custom one first.";
                Advice.Publish();
                return;
            }
            var before = $"L {Short(target.leftController.rotation)} "
                         + $"R {Short(target.rightController.rotation)}";
            if (!GameApi.TrySetOffset(target, true, target.leftController.position, left)
                || !GameApi.TrySetOffset(
                    target, false, target.rightController.position, right))
            {
                Advice.Note = "This game version does not allow writing a profile.";
                Advice.Publish();
                return;
            }
            var handlingMoved = target.alternativeHandling != alternative;
            if (handlingMoved)
            {
                target.SetRotateThanMove(alternative);
            }
            model.SaveAsync();
            Plugin.Log.Info(
                $"profile #{target.index + 1} set from {what}: {before} -> "
                + $"L {Short(left)} R {Short(right)}"
                + (handlingMoved ? $", handling set to {alternative}" : ""));

            // A frame or two late: the write refreshes the controllers, and the poses the
            // journal reads are last frame's until it has.
            _journalIn = 4;
            Advice.Note = $"Profile #{target.index + 1} is now L {Short(left)} R {Short(right)}"
                          + (handlingMoved ? ", rotate-then-move set to match." : ".");
            Advice.Publish();
        }

        private void UseRange(int slot)
        {
            Counted();
            if (slot >= _ranges.Count
                || !Armed("use" + slot, "Press again to use that range's settings."))
            {
                return;
            }
            var a = _ranges[slot];
            UseGrip(a.LeftRotation, a.RightRotation, a.AlternativeHandling,
                    $"the range from {Shown.Day(a.From)}");
        }

        private void UseManaged(int slot)
        {
            var list = ManagedShown();
            if (slot >= list.Count
                || !Armed("useman" + slot, "Press again to use those recorded settings."))
            {
                return;
            }
            var m = list[slot];
            UseGrip(m.LeftRotation, m.RightRotation, m.AlternativeHandling,
                    $"what was recorded on {Shown.Day(m.From)}");
        }

        private string RangeText(int slot)
        {
            Counted();
            return slot < _rangeLines.Count ? _rangeLines[slot] : "";
        }

        /// <summary>
        /// Whether a fit would have any runs to fit.
        /// </summary>
        /// <remarks>
        /// A run counts if the journal recorded the settings behind it, or if the player has
        /// assigned a range that covers it. With neither, every run is dropped and the fit
        /// finishes instantly having done nothing, reporting no group large enough to advise
        /// -- which reads as "not enough history" rather than as "you have not said which
        /// settings this history was played on".
        ///
        /// Reads the memo, so it costs nothing per frame; touching AssignmentList first is
        /// what keeps it current, and DrawProgress does that before it reaches the button.
        /// </remarks>
        private bool Fittable =>
            Recommender.RunsRead - Advice.UnknownRuns > 0 || _assignedRuns > 0;

        // Generated from one template so the twelve cannot drift apart. See RangeSlots.
        [UIValue("range-0")]
        public string Range0 => RangeText(0);

        [UIValue("range-0-button")]
        public string Range0Button => DeleteLabel(0);

        [UIAction("range-0-delete")]
        public void DeleteRange0() => DeleteRange(0);

        [UIObject("range-0-row")]
        private GameObject _range0Row;

        [UIValue("range-1")]
        public string Range1 => RangeText(1);

        [UIValue("range-1-button")]
        public string Range1Button => DeleteLabel(1);

        [UIAction("range-1-delete")]
        public void DeleteRange1() => DeleteRange(1);

        [UIObject("range-1-row")]
        private GameObject _range1Row;

        [UIValue("range-2")]
        public string Range2 => RangeText(2);

        [UIValue("range-2-button")]
        public string Range2Button => DeleteLabel(2);

        [UIAction("range-2-delete")]
        public void DeleteRange2() => DeleteRange(2);

        [UIObject("range-2-row")]
        private GameObject _range2Row;

        [UIValue("range-3")]
        public string Range3 => RangeText(3);

        [UIValue("range-3-button")]
        public string Range3Button => DeleteLabel(3);

        [UIAction("range-3-delete")]
        public void DeleteRange3() => DeleteRange(3);

        [UIObject("range-3-row")]
        private GameObject _range3Row;

        [UIValue("range-4")]
        public string Range4 => RangeText(4);

        [UIValue("range-4-button")]
        public string Range4Button => DeleteLabel(4);

        [UIAction("range-4-delete")]
        public void DeleteRange4() => DeleteRange(4);

        [UIObject("range-4-row")]
        private GameObject _range4Row;

        [UIValue("range-5")]
        public string Range5 => RangeText(5);

        [UIValue("range-5-button")]
        public string Range5Button => DeleteLabel(5);

        [UIAction("range-5-delete")]
        public void DeleteRange5() => DeleteRange(5);

        [UIObject("range-5-row")]
        private GameObject _range5Row;

        [UIValue("range-6")]
        public string Range6 => RangeText(6);

        [UIValue("range-6-button")]
        public string Range6Button => DeleteLabel(6);

        [UIAction("range-6-delete")]
        public void DeleteRange6() => DeleteRange(6);

        [UIObject("range-6-row")]
        private GameObject _range6Row;

        [UIValue("range-7")]
        public string Range7 => RangeText(7);

        [UIValue("range-7-button")]
        public string Range7Button => DeleteLabel(7);

        [UIAction("range-7-delete")]
        public void DeleteRange7() => DeleteRange(7);

        [UIObject("range-7-row")]
        private GameObject _range7Row;

        [UIValue("range-8")]
        public string Range8 => RangeText(8);

        [UIValue("range-8-button")]
        public string Range8Button => DeleteLabel(8);

        [UIAction("range-8-delete")]
        public void DeleteRange8() => DeleteRange(8);

        [UIObject("range-8-row")]
        private GameObject _range8Row;

        [UIValue("range-9")]
        public string Range9 => RangeText(9);

        [UIValue("range-9-button")]
        public string Range9Button => DeleteLabel(9);

        [UIAction("range-9-delete")]
        public void DeleteRange9() => DeleteRange(9);

        [UIObject("range-9-row")]
        private GameObject _range9Row;

        [UIValue("range-10")]
        public string Range10 => RangeText(10);

        [UIValue("range-10-button")]
        public string Range10Button => DeleteLabel(10);

        [UIAction("range-10-delete")]
        public void DeleteRange10() => DeleteRange(10);

        [UIObject("range-10-row")]
        private GameObject _range10Row;

        [UIValue("range-11")]
        public string Range11 => RangeText(11);

        [UIValue("range-11-button")]
        public string Range11Button => DeleteLabel(11);

        [UIAction("range-11-delete")]
        public void DeleteRange11() => DeleteRange(11);

        [UIObject("range-11-row")]
        private GameObject _range11Row;

        // Generated from the same template as the delete rows. See RangeSlots.
        [UIValue("range-0-use-button")]
        public string Range0UseButton => UseLabel(0);

        [UIAction("range-0-use")]
        public void UseRange0() => UseRange(0);

        [UIValue("managed-0")]
        public string Managed0 => ManagedText(0);

        [UIValue("managed-0-use-button")]
        public string Managed0UseButton => UseManagedLabel(0);

        [UIAction("managed-0-use")]
        public void UseManaged0() => UseManaged(0);

        [UIObject("managed-0-row")]
        private GameObject _managed0Row;

        [UIValue("range-1-use-button")]
        public string Range1UseButton => UseLabel(1);

        [UIAction("range-1-use")]
        public void UseRange1() => UseRange(1);

        [UIValue("managed-1")]
        public string Managed1 => ManagedText(1);

        [UIValue("managed-1-use-button")]
        public string Managed1UseButton => UseManagedLabel(1);

        [UIAction("managed-1-use")]
        public void UseManaged1() => UseManaged(1);

        [UIObject("managed-1-row")]
        private GameObject _managed1Row;

        [UIValue("range-2-use-button")]
        public string Range2UseButton => UseLabel(2);

        [UIAction("range-2-use")]
        public void UseRange2() => UseRange(2);

        [UIValue("managed-2")]
        public string Managed2 => ManagedText(2);

        [UIValue("managed-2-use-button")]
        public string Managed2UseButton => UseManagedLabel(2);

        [UIAction("managed-2-use")]
        public void UseManaged2() => UseManaged(2);

        [UIObject("managed-2-row")]
        private GameObject _managed2Row;

        [UIValue("range-3-use-button")]
        public string Range3UseButton => UseLabel(3);

        [UIAction("range-3-use")]
        public void UseRange3() => UseRange(3);

        [UIValue("managed-3")]
        public string Managed3 => ManagedText(3);

        [UIValue("managed-3-use-button")]
        public string Managed3UseButton => UseManagedLabel(3);

        [UIAction("managed-3-use")]
        public void UseManaged3() => UseManaged(3);

        [UIObject("managed-3-row")]
        private GameObject _managed3Row;

        [UIValue("range-4-use-button")]
        public string Range4UseButton => UseLabel(4);

        [UIAction("range-4-use")]
        public void UseRange4() => UseRange(4);

        [UIValue("managed-4")]
        public string Managed4 => ManagedText(4);

        [UIValue("managed-4-use-button")]
        public string Managed4UseButton => UseManagedLabel(4);

        [UIAction("managed-4-use")]
        public void UseManaged4() => UseManaged(4);

        [UIObject("managed-4-row")]
        private GameObject _managed4Row;

        [UIValue("range-5-use-button")]
        public string Range5UseButton => UseLabel(5);

        [UIAction("range-5-use")]
        public void UseRange5() => UseRange(5);

        [UIValue("managed-5")]
        public string Managed5 => ManagedText(5);

        [UIValue("managed-5-use-button")]
        public string Managed5UseButton => UseManagedLabel(5);

        [UIAction("managed-5-use")]
        public void UseManaged5() => UseManaged(5);

        [UIObject("managed-5-row")]
        private GameObject _managed5Row;

        [UIValue("range-6-use-button")]
        public string Range6UseButton => UseLabel(6);

        [UIAction("range-6-use")]
        public void UseRange6() => UseRange(6);

        [UIValue("managed-6")]
        public string Managed6 => ManagedText(6);

        [UIValue("managed-6-use-button")]
        public string Managed6UseButton => UseManagedLabel(6);

        [UIAction("managed-6-use")]
        public void UseManaged6() => UseManaged(6);

        [UIObject("managed-6-row")]
        private GameObject _managed6Row;

        [UIValue("range-7-use-button")]
        public string Range7UseButton => UseLabel(7);

        [UIAction("range-7-use")]
        public void UseRange7() => UseRange(7);

        [UIValue("managed-7")]
        public string Managed7 => ManagedText(7);

        [UIValue("managed-7-use-button")]
        public string Managed7UseButton => UseManagedLabel(7);

        [UIAction("managed-7-use")]
        public void UseManaged7() => UseManaged(7);

        [UIObject("managed-7-row")]
        private GameObject _managed7Row;

        [UIValue("range-8-use-button")]
        public string Range8UseButton => UseLabel(8);

        [UIAction("range-8-use")]
        public void UseRange8() => UseRange(8);

        [UIValue("managed-8")]
        public string Managed8 => ManagedText(8);

        [UIValue("managed-8-use-button")]
        public string Managed8UseButton => UseManagedLabel(8);

        [UIAction("managed-8-use")]
        public void UseManaged8() => UseManaged(8);

        [UIObject("managed-8-row")]
        private GameObject _managed8Row;

        [UIValue("range-9-use-button")]
        public string Range9UseButton => UseLabel(9);

        [UIAction("range-9-use")]
        public void UseRange9() => UseRange(9);

        [UIValue("managed-9")]
        public string Managed9 => ManagedText(9);

        [UIValue("managed-9-use-button")]
        public string Managed9UseButton => UseManagedLabel(9);

        [UIAction("managed-9-use")]
        public void UseManaged9() => UseManaged(9);

        [UIObject("managed-9-row")]
        private GameObject _managed9Row;

        [UIValue("range-10-use-button")]
        public string Range10UseButton => UseLabel(10);

        [UIAction("range-10-use")]
        public void UseRange10() => UseRange(10);

        [UIValue("managed-10")]
        public string Managed10 => ManagedText(10);

        [UIValue("managed-10-use-button")]
        public string Managed10UseButton => UseManagedLabel(10);

        [UIAction("managed-10-use")]
        public void UseManaged10() => UseManaged(10);

        [UIObject("managed-10-row")]
        private GameObject _managed10Row;

        [UIValue("range-11-use-button")]
        public string Range11UseButton => UseLabel(11);

        [UIAction("range-11-use")]
        public void UseRange11() => UseRange(11);

        [UIValue("managed-11")]
        public string Managed11 => ManagedText(11);

        [UIValue("managed-11-use-button")]
        public string Managed11UseButton => UseManagedLabel(11);

        [UIAction("managed-11-use")]
        public void UseManaged11() => UseManaged(11);

        [UIObject("managed-11-row")]
        private GameObject _managed11Row;

        [UIValue("read-button")]
        public string ReadButton => Recommender.Running
            ? "Working..."
            : Recommender.HasRead
                ? $"Re-read replays ({Recommender.RunsRead} in memory)"
                : "Read replays";

        [UIValue("fit-button")]
        public string FitButton => Recommender.Running
            ? "Working..."
            : !Recommender.HasRead
                ? "Fit Both Hands (read first)"
                : Fittable
                    ? "Fit Both Hands"
                    : "Fit Both Hands (assign a range first)";

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

        [UIObject("assigned-header-row")]
        private GameObject _assignedHeaderRow;

        [UIObject("managed-header-row")]
        private GameObject _managedHeaderRow;

        [UIObject("managed-row")]
        private GameObject _managedRow;

        [UIObject("assignments-row")]
        private GameObject _assignmentsRow;

        [UIObject("evidence-row")]
        private GameObject _evidenceRow;

        [UIObject("note-row")]
        private GameObject _noteRow;

        [UIObject("advisory-row")]
        private GameObject _advisoryRow;

        [UIObject("assign-row")]
        private GameObject _assignRow;

        [UIObject("needed-row")]
        private GameObject _neededRow;

        [UIObject("chart-row")]
        private GameObject _chartRow;

        [UIObject("hands-row")]
        private GameObject _handsRow;

        [UIObject("gained-row")]
        private GameObject _gainedRow;


        [UIObject("standing-row")]
        private GameObject _standingRow;

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
        private DateTime? _endShown;

        internal void RedrawRange()
        {
            var sessions = Advice.Governable.Count;
            // And on the derived end moving, which assigning a range does without changing
            // how many sittings there are. Without that the greyed slider kept showing the
            // date from before the press, which is the one date on the tab nobody set and so
            // the one nobody would think to doubt.
            //
            // Not on every bump: dragging the other slider bumps the version a notch at a
            // time, and redrawing under a handle fights the player holding it. Neither of
            // these two values moves while a start is being dragged.
            var end = NewestLoose();
            if (sessions == _sessionsShown && end == _endShown)
            {
                return;
            }
            _sessionsShown = sessions;
            _endShown = end;
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
                    ScrollSoon();
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

        /// <summary>Ask for a scroll to the end once the layout has caught up.</summary>
        private void ScrollSoon() => _scrollIn = 2;

        /// <summary>
        /// Put the current choices back into the dropdowns.
        /// </summary>
        /// <remarks>
        /// BSML reads a list's choices once, when the panel is built. Both of these describe
        /// profiles by their values, so writing a fit into a profile changes every label in
        /// them, and the dropdown carried on offering the numbers that profile used to hold.
        ///
        /// Only when the labels have actually changed, because handing a list new values
        /// makes it re-read its selection, and doing that every frame would fight a player
        /// trying to change it.
        /// </remarks>
        internal void RedrawLists()
        {
            Relist(_profileList, ProfileChoices);
            Relist(_targetList, TargetChoices);
        }

        private static void Relist(ListSetting list, List<object> choices)
        {
            if (list == null)
            {
                return;
            }
            var held = list.Values;
            if (held != null && held.Count == choices.Count)
            {
                var same = true;
                for (var i = 0; i < choices.Count; i++)
                {
                    if (!Equals(held[i], choices[i]))
                    {
                        same = false;
                        break;
                    }
                }
                if (same)
                {
                    return;
                }
            }
            list.Values = choices;
            // Re-reads the bound value from the host, so the field shows what the getter
            // now says rather than the label it was holding.
            list.ReceiveValue();
        }

        private static string Active(GameObject row) =>
            row != null && row.activeSelf ? "1" : "0";

        /// <summary>
        /// Grey a slider without going through the setting's own property.
        /// </summary>
        /// <remarks>
        /// BSML's setter assigns <c>slider.interactable</c>, and on the version shipping with
        /// the older game that resolves to HMUI's own <c>new</c> property, which activates an
        /// increment and a decrement button that BSML never wires up. The result is a null
        /// reference on every write, thrown out of a method that runs each frame, so the
        /// progress bar, the collapsing rows, the scrolling and the remaining controls after
        /// it all stopped with it. Later BSML fixed this by casting to the base first, which
        /// is what this does: identical behaviour where the property works, and working
        /// behaviour where it does not.
        ///
        /// Sliders declared with buttons would keep theirs live on the old version. Ours are
        /// not, and a stray arrow is worth less than the frame this used to take down.
        /// </remarks>
        private static void Grey(SliderSetting setting, bool wanted)
        {
            var slider = setting == null ? null : setting.Slider as Selectable;
            if (slider != null && slider.interactable != wanted)
            {
                slider.interactable = wanted;
            }
        }

        private static void Show(GameObject row, string content) =>
            Show(row, content.Length > 0);

        /// <summary>A whole settings row, which BSML hands back as the component on it.</summary>
        private static void Show(UnityEngine.Component setting, bool wanted)
        {
            if (setting != null && setting.gameObject.activeSelf != wanted)
            {
                setting.gameObject.SetActive(wanted);
            }
        }

        /// <summary>
        /// Give a row the height its text needs, when that is not known in advance.
        /// </summary>
        /// <remarks>
        /// Most rows hold a fixed number of lines and can be sized in the markup. The
        /// assigned ranges cannot: there is one line per range, and a player with six of them
        /// had five lines of text in a box built for three. A TextMeshPro that overflows is
        /// centred on its box rather than clipped by it, so it spilled equally above and
        /// below, over the line explaining the ranges and over the sliders that set them.
        ///
        /// Written from here because BSML will not bind a numeric attribute, which is the
        /// same reason the progress bar's fill is driven from code. One and a fifth of the
        /// font per line is TextMeshPro's own spacing at these sizes, and the extra unit
        /// keeps a descender off the row below.
        /// </remarks>
        private static void Height(GameObject row, string content, float fontSize)
        {
            if (row == null)
            {
                return;
            }
            var layout = row.GetComponent<LayoutElement>();
            if (layout == null)
            {
                return;
            }
            var lines = 1;
            foreach (var c in content)
            {
                if (c == '\n')
                {
                    lines++;
                }
            }
            var wanted = lines * fontSize * 1.2f + 1f;
            if (Mathf.Abs(layout.preferredHeight - wanted) > 0.05f)
            {
                layout.preferredHeight = wanted;
            }
        }

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
            // A row a range, hidden from wherever the ranges run out. Touching the list
            // first is what fills the memo the rows read from.
            var assignments = AssignmentList;
            var shown = _rangeLines.Count;
            Show(_range0Row, shown > 0);
            Show(_range1Row, shown > 1);
            Show(_range2Row, shown > 2);
            Show(_range3Row, shown > 3);
            Show(_range4Row, shown > 4);
            Show(_range5Row, shown > 5);
            Show(_range6Row, shown > 6);
            Show(_range7Row, shown > 7);
            Show(_range8Row, shown > 8);
            Show(_range9Row, shown > 9);
            Show(_range10Row, shown > 10);
            Show(_range11Row, shown > 11);
            // Only the overflow and the nothing-assigned line land here now.
            Show(_assignmentsRow, assignments);
            Height(_assignmentsRow, assignments, 2.8f);
            Show(_assignedHeaderRow, AssignedHeader);
            // An armed button that is never pressed again goes back to saying X, so a panel
            // left open does not keep a delete primed behind the player.
            if (_armedKey.Length > 0 && --_armedLeft <= 0)
            {
                _armedKey = "";
                // Published, or the label keeps saying "Sure?" until something else happens
                // to bump the version: the labels are only re-read on a bump.
                Advice.Publish();
            }
            // The recorded half grows a line per settings change and is the longer of the two
            // for anyone who has used the mod a while, so it is sized the same way.
            var managed = ManagedList;
            var recorded = ManagedShown().Count;
            Show(_managedHeaderRow, ManagedHeader);
            Show(_managed0Row, recorded > 0);
            Show(_managed1Row, recorded > 1);
            Show(_managed2Row, recorded > 2);
            Show(_managed3Row, recorded > 3);
            Show(_managed4Row, recorded > 4);
            Show(_managed5Row, recorded > 5);
            Show(_managed6Row, recorded > 6);
            Show(_managed7Row, recorded > 7);
            Show(_managed8Row, recorded > 8);
            Show(_managed9Row, recorded > 9);
            Show(_managed10Row, recorded > 10);
            Show(_managed11Row, recorded > 11);
            // Only the "older ones are not listed" line lands here now.
            Show(_managedRow, managed);
            Height(_standingRow, Standing, 2.7f);
            Show(_evidenceRow, EvidenceLine);
            Show(_noteRow, ActionNote);
            Show(_advisoryRow, Advisory);
            Show(_neededRow, RangesNeeded);
            Show(_chartRow, Chart);
            Show(_standingRow, Standing);
            Show(_handsRow, Hands);
            Show(_gainedRow, Gained);

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
                _fitButton.interactable =
                    Recommender.HasRead && !Recommender.Running && Fittable;
            }

            // Gone rather than greyed once the history is described. They were greyed on the
            // grounds that a control which does nothing invites a player to wonder what they
            // broke -- but a greyed control still says there is something here to do, and the
            // press it invites could only report that the range was already covered. Four
            // rows saying "finished" is worse than four rows absent.
            //
            // They come back if a range is removed, since that is a stretch wanting an answer
            // again, so nothing about this is one-way.
            Counted();
            var history = Recommender.HasRead && _looseRuns > 0;
            Show(_assignRow, history);
            Show(_fromSlider, history);
            Show(_untilSlider, history);
            Show(_profileList, history);

            // Greyed, not hidden, while a step is running: a row that vanishes for the
            // duration of a read and comes back is harder to read than one that waits.
            var needed = history && !Recommender.Running;
            Grey(_fromSlider, needed);
            // Never live: it reports where the range has to end rather than asking.
            Grey(_untilSlider, false);
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
        /// <summary>
        /// Heads the range controls, and only when they have work to do.
        /// </summary>
        /// <remarks>
        /// The fit depends on runs having settings behind them, and a player who fits without
        /// saying is told only that no group is large enough -- which describes the symptom
        /// and not the cause. It goes quiet once every run is covered, which is where a player
        /// who keeps using the mod ends up: the journal records settings as they change, so
        /// the unassigned pile only ever shrinks.
        /// </remarks>
        [UIValue("ranges-needed")]
        public string RangesNeeded
        {
            get
            {
                if (!Recommender.HasRead || Advice.UnknownRuns == 0)
                {
                    return "";
                }
                Counted();
                return _looseRuns == 0
                    ? ""
                    : $"{_looseRuns} runs have no range and will be left out of the fit. "
                      + "Say what they were played on below.";
            }
        }

        [UIValue("headline")]
        public string Headline => Recommender.HasRead
            ? Progress.Headline
            : "Read your replays on the Fit tab.";

        [UIValue("hands")]
        public string Hands => Progress.Hands;

        [UIValue("gained")]
        public string Gained => Progress.Overall;

        [UIValue("chart")]
        public string Chart => Progress.Chart;

        [UIValue("standing")]
        public string Standing => Progress.Standing;

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
        /// <summary>
        /// How much of the unrecorded history the ranges actually reach.
        /// </summary>
        /// <remarks>
        /// It used to say only how many runs predate the mod, which reads as a job one range
        /// finishes. It is not: a range covering ten weeks of a fifteen-month history covers
        /// ten weeks of runs, and a player who assigned one and saw it hold a quarter of the
        /// number quoted had no way to tell whether the rest were unreachable or merely
        /// unassigned. Touching AssignmentList first is what refreshes the count.
        /// </remarks>
        [UIValue("assigned-header")]
        public string AssignedHeader => Recommender.HasRead
            ? "Ranges you assigned, for replays from before the mod was installed"
            : "";

        [UIValue("managed-header")]
        public string ManagedHeader
        {
            get
            {
                var n = ManagedShown().Count;
                return n == 0
                    ? ""
                    : $"Ranges the mod recorded for itself, since it was installed ({n})";
            }
        }

        /// <summary>
        /// The recorded half of the history, listed the same way the assigned half is.
        /// </summary>
        /// <remarks>
        /// Deliberately identical in shape to the assigned list and deliberately not editable.
        /// The point of showing it is that a player can see the mod has this half in hand and
        /// stop wondering whether it wants a range drawn over it -- which is what four inert
        /// ranges on one machine turned out to be.
        /// </remarks>
        private int _managedFor = -1;
        private string _managedList = "";

        [UIValue("managed")]
        public string ManagedList
        {
            get
            {
                // Memoised on the same counter the assigned list uses, because building it
                // reads the journal off disk and the draw asks for it once a frame. The
                // recorded history only changes when a settings change is written, and that
                // bumps the counter.
                if (_managedFor == Advice.Version)
                {
                    return _managedList;
                }
                _managedFor = Advice.Version;
                _managedList = BuildManagedList();
                return _managedList;
            }
        }

        private readonly List<Recommender.Managed> _managedShown =
            new List<Recommender.Managed>();

        /// <summary>The recorded ranges that actually have a row, newest last.</summary>
        private List<Recommender.Managed> ManagedShown()
        {
            var unused = ManagedList;
            return _managedShown;
        }

        private string BuildManagedList()
        {
            _managedShown.Clear();
            if (!Recommender.HasRead)
            {
                return "";
            }
            var list = Recommender.ManagedRanges();
            var kept = new List<Recommender.Managed>();
            for (var i = 0; i < list.Count; i++)
            {
                // A recorded stretch nobody played through says nothing: it is a number
                // typed, looked at and typed back, and on the machine this was written for
                // it was seven of the fifteen rows. The settings in use right now stay
                // whether or not they have been played on yet, because "what am I on at the
                // moment" is the one row worth showing empty.
                if (list[i].Runs > 0 || i == list.Count - 1)
                {
                    kept.Add(list[i]);
                }
            }

            // The newest that will fit, because the reason to reach for one of these is to
            // put a grip back on, and the grip a player wants back is rarely the one from
            // fourteen changes ago. Still oldest first within the rows, so the column reads
            // the same way the assigned list above it does.
            var from = Math.Max(0, kept.Count - RangeSlots);
            for (var i = from; i < kept.Count; i++)
            {
                _managedShown.Add(kept[i]);
            }

            var lines = new List<string>();
            for (var i = 0; i < _managedShown.Count; i++)
            {
                var m = _managedShown[i];
                var last = i == _managedShown.Count - 1 && from + i == kept.Count - 1;
                var held = m.Runs == 1 ? "1 replay" : $"{m.Runs} replays";
                lines.Add($"{Shown.Moment(m.From)} - "
                          + (last ? "now" : Shown.Moment(m.To))
                          + $"  L {Short(m.LeftRotation)} R {Short(m.RightRotation)}  {held}");
            }
            lines.Reverse();
            _managedShown.Reverse();
            _managedLines = lines;
            return from == 0
                ? ""
                : $"{from} older recorded range(s) are not listed.";
        }

        private List<string> _managedLines = new List<string>();

        private string ManagedText(int slot)
        {
            var unused = ManagedList;
            return slot < _managedLines.Count ? _managedLines[slot] : "";
        }

        [UIValue("evidence")]
        public string EvidenceLine
        {
            get
            {
                if (!Recommender.HasRead || Advice.UnknownRuns == 0)
                {
                    return Recommender.HasRead ? Advice.Evidence : "";
                }
                Counted();
                var unused = _looseRuns;
                // Counted against what the sliders can reach, not against every old replay.
                // Replays outside that window used to get a sentence of their own; it named a
                // number nobody could act on, and reading it as a job left undone is the
                // natural mistake. They are in the log for anyone who wants them.
                var reachable = Advice.UnknownRuns - Advice.Unreachable;
                return unused == 0
                    ? $"All {reachable} replays from before the mod was installed have a range."
                    : $"{reachable - unused} of {reachable} replays from before the mod was "
                      + $"installed have a range. {unused} still need one.";
            }
        }

        /// <summary>
        /// What the three controls describe, said once each.
        /// </summary>
        /// <remarks>
        /// They used to carry the number of the range being built, which read as a step in a
        /// sequence of four rather than as three fields and a button. The list they add to is
        /// not numbered either, so the number matched nothing a player could point at.
        ///
        /// "Historical" because that is the whole of what these govern: play from before the
        /// mod was installed. Everything since is recorded as it happens and none of these
        /// controls touch it.
        /// </remarks>
        [UIValue("from-label")]
        public string FromLabel => "Historical replays starting";

        [UIValue("until-label")]
        public string UntilLabel => "Historical replays ending";

        [UIValue("profile-label")]
        public string ProfileLabel => "Controller settings used";

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
                // The same window the sliders address, so the chart under them is a picture
                // of the history they can reach. Drawn over everything, a year of assignable
                // play sat squeezed against the right-hand edge of three years of bars, and
                // the gap a player was looking for was a column wide.
                var sessions = new List<Advice.Span>();
                foreach (var sitting in Advice.Sessions)
                {
                    if (sitting.Start >= Advice.ChartFrom)
                    {
                        sessions.Add(sitting);
                    }
                }
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
                       + $"{runs} runs from {Shown.Day(first)} to "
                       + $"{Shown.Day(sessions[sessions.Count - 1].Start)}";
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

        /// <summary>The sittings a range can still say anything about.</summary>
        /// <remarks>
        /// From the first launch with this mod, settings are recorded as they change, and a
        /// recorded setting outranks anything assigned afterwards. So a range drawn over that
        /// stretch is inert: it reads as an answer, it is stored like one, and the fit never
        /// consults it.
        ///
        /// The sliders used to reach across all of it, which is not a thing to explain in a
        /// hint -- it is a control offering to do something it cannot. They move over this
        /// instead, so the only ranges that can be drawn are ones that govern something, and
        /// the list stops filling up with entries doing nothing.
        ///
        /// Every unrecorded sitting, not only the ones still unassigned: a range already drawn
        /// is one a player may want to redraw or extend, and a slider that cannot reach back
        /// over it makes correcting a mistake harder than making one. It is fixed once a
        /// history is read, and empty for anyone who installed the mod before they started
        /// playing, which greys the controls out entirely.
        /// </remarks>
        internal static volatile List<Span> Governable = new List<Span>();

        /// <summary>A gap this long ends a sitting.</summary>
        internal static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(30);

        /// <summary>Runs with no recorded settings, which are the only ones the panel governs.</summary>
        internal static volatile int UnknownRuns;

        /// <summary>Unrecorded runs too old for the sliders to reach, and so left out.</summary>
        internal static volatile int Unreachable;

        /// <summary>
        /// Where the assignable window starts, and with it the timeline.
        /// </summary>
        /// <remarks>
        /// DateTime.MinValue when nothing was capped, so the chart draws the whole history
        /// without a special case for the player who has less than a year of it.
        /// </remarks>
        internal static volatile object ChartFromBox = DateTime.MinValue;

        internal static DateTime ChartFrom
        {
            get => (DateTime)ChartFromBox;
            set => ChartFromBox = value;
        }

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
