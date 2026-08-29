using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace ControllerAutoAdjust
{
    /// <summary>
    /// Reads the player's replays and works out what their grip is asking for.
    /// </summary>
    /// <remarks>
    /// Replays are the primary source because they already exist. A player installing this
    /// has months of them, so the mod can answer on the first launch instead of asking for
    /// twenty runs before it says anything -- and again after every adjustment.
    ///
    /// The offsets each replay was played under come from the journal. Before the journal
    /// existed there is no record, so those replays are usable only on the assumption the
    /// player's grip has not changed across them. That assumption is stated in the log rather
    /// than folded silently into the number.
    /// </remarks>
    internal class Recommender : MonoBehaviour
    {
        internal const string ExtraFoldersFile = "replay-folders.txt";

        /// <summary>Below this a run is a fragment, and it would be weighted like a map.</summary>
        private const int MinCutsPerHand = 100;

        /// <summary>
        /// How many usable runs to gather before stopping, newest first.
        /// </summary>
        /// <remarks>
        /// Counted in runs that survive filtering rather than files opened. Four hundred
        /// files gave 217 usable here, and a library heavier in One Saber or short runs would
        /// give far fewer -- so a file cap controls the cost but not the evidence.
        ///
        /// Newest first is not only about cost. The residual drifts one to two degrees a
        /// month, so older sessions describe a grip the player has partly moved on from, and
        /// the fit stops improving well before a full library is read: the offline sweep
        /// settled by around 273 runs.
        /// </remarks>
        private const int TargetUsableRuns = 300;

        /// <summary>A ceiling on files opened, so an enormous library cannot stall the read.</summary>
        /// <remarks>
        /// Reading every replay a BSManager install keeps meant parsing two thousand frame
        /// streams: minutes of work and gigabytes of churn. This only binds when the target
        /// above cannot be met, which is itself worth reporting.
        /// </remarks>
        private const int MaxFilesToOpen = 1200;

        /// <summary>
        /// Runs needed before a group's answer is offered rather than merely reported.
        /// </summary>
        /// <remarks>
        /// Measured, not chosen: telling a one-degree change apart takes 10 to 22 runs,
        /// because two runs of the same map on unchanged settings differ by 4 to 6 accuracy
        /// points and the whole prize is about 2. A seven-run group in testing claimed 1.3%
        /// -- four times the ceiling any fixed offset has been measured to reach -- and
        /// disagreed by four degrees with the same hand fitted on 143 runs. Both cannot be
        /// right, and the small one is the one that is wrong.
        /// </remarks>
        private const int MinRunsToRecommend = 20;

        /// <summary>How often to check whether the controllers are up yet.</summary>
        private const float WatchEvery = 2f;

        private Thread _worker;
        private volatile bool _started;
        private float _nextWatch;

        private void Update()
        {
            if (Time.unscaledTime < _nextWatch)
            {
                return;
            }
            _nextWatch = Time.unscaledTime + WatchEvery;

            if (_started)
            {
                return;
            }
            // Read on the main thread: the live settings hang off Unity objects. Keeping
            // them current is SettingsWatcher's job, not this one's.
            if (!OffsetState.TryRead(out var reading))
            {
                return;
            }
            _started = true;
            var epochs = OffsetJournal.Read();
            _worker = new Thread(() => Work(reading, epochs)) { IsBackground = true };
            _worker.Start();
        }

        private static void Work(OffsetState.Reading reading, List<OffsetJournal.Epoch> epochs)
        {
            try
            {
                // A control run, for attributing memory rather than guessing at it. Other
                // mods load leaderboards and scan songs during the same window, so a heap
                // delta measured only with ingestion running cannot say which of them grew.
                // With this marker present the loop is skipped and the same window measured.
                // Measured, not assumed: with this marker the read is skipped and the same
                // window sampled, so growth from other mods loading can be told from growth
                // caused here. It reported 662 MB against 688 MB -- reading 555 replays costs
                // about 26 MB, where the process sits above 3 GB for reasons of its own.
                var control = File.Exists(Path.Combine(Paths.DataDir, "no-ingest.on"));
                if (control)
                {
                    var idle = GC.GetTotalMemory(true);
                    Thread.Sleep(90000);
                    Plugin.Log.Info(
                        $"CONTROL (no ingestion): managed heap {idle / 1048576} MB -> "
                        + $"{GC.GetTotalMemory(true) / 1048576} MB over the same 90 s window");
                    return;
                }

                var files = Discover();
                if (files.Count == 0)
                {
                    Plugin.Log.Info(
                        "no replays found; the live recorder is the only source of cuts here");
                    return;
                }

                // Published before anything is parsed. Session dates are just file
                // timestamps, and the menu needs a range to build its sliders from the moment
                // it is opened -- parsing takes a minute, and a panel built in the meantime
                // gets a zero-length slider and renders nothing at all.
                // Built from every replay found, not from the ones this pass will open. The
                // sliders have to span the player's whole history or the earlier part of it
                // is unreachable, with nothing on screen saying why.
                Advice.Sessions = files
                    .Select(f => File.GetLastWriteTimeUtc(f).Date)
                    .GroupBy(d => d)
                    .OrderBy(g => g.Key)
                    .Select(g => new KeyValuePair<DateTime, int>(g.Key, g.Count()))
                    .ToList();
                Advice.Summary = $"Reading {files.Count} replays...";
                Advice.Publish();

                var groups = new List<(OffsetJournal.Epoch Epoch, List<ReplayCuts.Extraction> Runs)>();
                var used = 0;
                var skipped = 0;
                // Counted by reason. "250 skipped" says nothing about whether that is a
                // library full of One Saber runs or a filter throwing away good data, and
                // those want opposite responses.
                var why = new Dictionary<string, int>();
                void Drop(string reason)
                {
                    skipped++;
                    why[reason] = why.TryGetValue(reason, out var n) ? n + 1 : 1;
                }
                var unknownEpoch = 0;
                var outsideRange = 0;
                var everySession = new List<DateTime>();
                var speeds = new List<float>();
                var residuals = new List<float>();

                // One replay's worth of buffers for the whole pass, rather than a fresh
                // few megabytes per file for the garbage collector to sit on.
                var scratch = new Bsor.Scratch();
                // Managed heap, not working set. The process sits at gigabytes with a large
                // song library loaded, and reading the total told me this ingestion was
                // responsible for all of it -- which it was not. This measures only what this
                // code allocates.
                // Not forced in a normal run: GetTotalMemory(true) runs a full blocking
                // collection, and two of those per ingestion is a real pause to buy a number
                // nobody is reading. Forced only alongside the control, where the comparison
                // needs both ends settled.
                var measuring = File.Exists(Path.Combine(Paths.DataDir, "measure-heap.on"));
                var heapBefore = GC.GetTotalMemory(measuring);
                var seen = 0;
                foreach (var file in files)
                {
                    if (used >= TargetUsableRuns || seen >= MaxFilesToOpen)
                    {
                        break;
                    }
                    // Reading four hundred replays takes about a minute, and a panel that
                    // says nothing for a minute is indistinguishable from one that is broken.
                    if (++seen % 25 == 0)
                    {
                        Advice.Summary = $"Reading replays... {used} of {TargetUsableRuns}";
                        Advice.Publish();
                    }

                    Bsor.Replay replay;
                    try
                    {
                        replay = Bsor.Parse(file, scratch);
                    }
                    catch
                    {
                        // Roughly a tenth of a live install's replays do not parse; a
                        // partially written one is unremarkable.
                        Drop("unreadable");
                        continue;
                    }

                    if (replay.Info.Mode != "Standard")
                    {
                        Drop($"not Standard ({replay.Info.Mode})");
                        continue;
                    }
                    if (!replay.Info.Clean)
                    {
                        Drop("speed or practice modifier");
                        continue;
                    }

                    // Cuts are grouped by the settings they were played under, and each group
                    // is fitted against its own. The residual is the player's grip minus the
                    // offset already applied, so pooling across settings fits a compromise
                    // that suits neither -- but the *answer* is absolute either way, since
                    // applying a group's correction to the settings that group was played on
                    // lands on the same grip whichever group it came from.
                    var when = File.GetLastWriteTimeUtc(file);
                    var fromJournal = OffsetJournal.TryAt(epochs, when, out var epoch);
                    if (!fromJournal)
                    {
                        unknownEpoch++;
                        epoch = Assumed(reading);
                    }

                    var cuts = ReplayCuts.Extract(replay, MinCutsPerHand);
                    if (cuts.Left.Cuts == null || cuts.Left.Cuts.Count == 0)
                    {
                        Drop($"under {MinCutsPerHand} cuts a hand");
                        continue;
                    }
                    cuts.Played = when;
                    cuts.FromJournal = fromJournal;
                    everySession.Add(when.Date);

                    // The range is the player's word about history the journal cannot vouch
                    // for. Replays it does cover are recorded fact and are not up for a vote.
                    if (!fromJournal && !Preferences.InRange(when))
                    {
                        outsideRange++;
                        Drop("outside the chosen dates");
                        continue;
                    }
                    Bucket(groups, epoch).Add(cuts);
                    speeds.Add(cuts.Left.NoteSpeed);
                    residuals.Add(cuts.Left.MedianResidual);
                    used++;
                }

                // Published before the range is applied, so the sliders span the whole
                // history and a narrowed range can always be widened again.
                Advice.Sessions = everySession
                    .GroupBy(d => d)
                    .OrderBy(g => g.Key)
                    .Select(g => new KeyValuePair<DateTime, int>(g.Key, g.Count()))
                    .ToList();

                var heapAfter = GC.GetTotalMemory(measuring);
                Plugin.Log.Info(
                    $"managed heap {heapBefore / 1048576} MB -> {heapAfter / 1048576} MB "
                    + $"over {seen} replays"
                    + (measuring ? " (both settled)" : ""));
                Plugin.Log.Info(
                    $"replays: {used} used, {skipped} skipped, {seen} of {files.Count} opened ("
                    + string.Join(", ", why.OrderByDescending(k => k.Value)
                                           .Select(k => $"{k.Value} {k.Key}")) + ")");
                if (used < TargetUsableRuns && seen >= MaxFilesToOpen)
                {
                    Plugin.Log.Warn(
                        $"stopped after opening {seen} files with only {used} usable runs; "
                        + "the fit is working with less than it asked for");
                }
                Advice.Publish();
                var biggest = why.Count == 0
                    ? ""
                    : why.OrderByDescending(k => k.Value).Select(k => $", {k.Value} {k.Key}").First();
                Advice.Summary = $"{used} runs used, {seen} replays read{biggest}.";
                if (unknownEpoch > 0)
                {
                    Plugin.Log.Warn(
                        $"{unknownEpoch} replays predate the offset journal, so they are used " +
                        "on the assumption the grip has not changed across them");
                }
                if (used == 0)
                {
                    return;
                }

                Advice.Left = "";
                Advice.Right = "";
                residuals.Sort();
                Plugin.Log.Info(
                    $"note-centre reconstruction: median residual " +
                    $"{residuals[residuals.Count / 2] * 1000f:F1} mm, " +
                    $"fitted note speed {speeds.Average():F1} m/s");

                // The panel shows one answer, so it has to be the one being recommended.
                // Writing every group's line in turn left whichever came last on screen --
                // and the last is the smallest, which is exactly the group too thin to act
                // on. The log carries them all; the panel carries the one that counts.
                var advised = false;
                foreach (var group in groups.OrderByDescending(g => g.Runs.Count))
                {
                    // Only the assumed group needs checking. Anything the journal covers is
                    // recorded fact, and re-testing it would only add a chance to be wrong.
                    if (group.Runs.Any(r => !r.FromJournal))
                    {
                        CheckTheAssumption(group.Runs);
                    }
                    var leftCuts = group.Runs.SelectMany(r => r.Left.Cuts).ToList();
                    var rightCuts = group.Runs.SelectMany(r => r.Right.Cuts).ToList();
                    var trusted = group.Runs.Count >= MinRunsToRecommend;
                    Plugin.Log.Info(
                        $"-- settings left {group.Epoch.LeftRotation} right " +
                        $"{group.Epoch.RightRotation}: {group.Runs.Count} runs" +
                        (trusted ? "" : $" (under {MinRunsToRecommend}: shown, not recommended)"));
                    var show = trusted && !advised;
                    Report("   left", leftCuts, group.Epoch.LeftRotation, true, group.Epoch,
                           trusted, show);
                    Report("   right", rightCuts, group.Epoch.RightRotation, false, group.Epoch,
                           trusted, show);
                    advised |= show;
                    Advice.Publish();
                }

                if (!advised)
                {
                    Advice.Left =
                        $"No group has the {MinRunsToRecommend} runs needed to advise from.";
                    Advice.Right = "Play more, or widen the date range.";
                    Advice.Publish();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"replay ingestion failed: {e}");
            }
        }

        /// <summary>
        /// Test the claim that a stretch of history was played on one set of settings.
        /// </summary>
        /// <remarks>
        /// Whatever the player answered, this is what the replays themselves say. An answer
        /// about three months ago is a memory; a residual that jumps on a particular date is
        /// evidence, and it is the one that gets acted on when they disagree.
        /// </remarks>
        private static void CheckTheAssumption(List<ReplayCuts.Extraction> runs)
        {
            var sessions = runs
                .GroupBy(r => r.Played.Date)
                .Where(g => g.Count() >= 2)
                .Select(g => new ChangeDetector.Session
                {
                    Day = g.Key,
                    Turn = OffsetSearch.FitTurn(g.SelectMany(r => r.Left.Cuts).ToList()),
                    Cuts = g.Sum(r => r.Left.Cuts.Count),
                })
                .ToList();

            var verdict = ChangeDetector.Scan(sessions);
            if (verdict.Sessions < 6)
            {
                Plugin.Log.Info(
                    $"only {verdict.Sessions} sessions predate the journal: too few to check " +
                    "whether the settings held steady across them");
                return;
            }
            Advice.Evidence = verdict.Split
                ? $"These replays do not look like one grip: the residual shifts "
                  + $"{verdict.GapDegrees:F1} deg around {verdict.At:yyyy-MM-dd}."
                : $"{verdict.Sessions} earlier sessions look consistent "
                  + $"(largest shift {verdict.GapDegrees:F1} deg).";
            if (verdict.Split)
            {
                Plugin.Log.Warn(
                    $"these replays do NOT look like one set of settings: the residual shifts " +
                    $"{verdict.GapDegrees:F1} deg around {verdict.At:yyyy-MM-dd} " +
                    $"(statistic {verdict.Statistic:F1}). Pooling across that fits a compromise " +
                    "suiting neither side.");
            }
            else if (!Preferences.RangeProfileAnswered)
            {
                Plugin.Log.Info(
                    $"{verdict.Sessions} sessions predate the journal and look consistent " +
                    $"(largest shift {verdict.GapDegrees:F1} deg, statistic {verdict.Statistic:F1}), " +
                    "but nobody has said which profile they were played on");
            }
            else
            {
                Plugin.Log.Info(
                    $"{verdict.Sessions} sessions predate the journal; the profile you named " +
                    $"is consistent with them (largest shift {verdict.GapDegrees:F1} deg, " +
                    $"statistic {verdict.Statistic:F1})");
            }
        }

        /// <summary>Two settings are the same epoch if they put the blade in the same place.</summary>
        /// <remarks>
        /// Grouping on the typed numbers splits history that was never really split. A value
        /// nudged one way and back leaves three journal entries describing one grip, and a
        /// change confined to Z moves the triple without moving anything at all -- composed
        /// after the grip's large X tilt it is mostly roll about the blade, and roll cannot
        /// move a cut plane. Both would fragment a history into groups too small to fit,
        /// which reads as "not enough data" rather than as a grouping mistake.
        ///
        /// Comparing what the settings *do* handles every version of this with one rule, and
        /// correctly keeps apart epochs that differ only in the legacy offset -- the same
        /// typed numbers mean different things with and without a controller detected.
        /// </remarks>
        private const float SamePositionMetres = 0.001f;

        private static bool SameGrip(OffsetJournal.Epoch a, OffsetJournal.Epoch b)
        {
            foreach (var left in new[] { true, false })
            {
                var rotA = OffsetMath.Applied(
                    a.RotationFor(left), left, a.LegacyRotation, a.AlternativeHandling);
                var rotB = OffsetMath.Applied(
                    b.RotationFor(left), left, b.LegacyRotation, b.AlternativeHandling);
                if (Quaternion.Angle(rotA, rotB) > OffsetMath.SameGripDegrees)
                {
                    return false;
                }
                var posA = left ? a.LeftPosition : a.RightPosition;
                var posB = left ? b.LeftPosition : b.RightPosition;
                if ((posA - posB).magnitude > SamePositionMetres)
                {
                    return false;
                }
            }
            return true;
        }

        /// <summary>Cuts from one settings epoch, kept together.</summary>
        private static List<ReplayCuts.Extraction> Bucket(
            List<(OffsetJournal.Epoch Epoch, List<ReplayCuts.Extraction> Runs)> groups,
            OffsetJournal.Epoch epoch)
        {
            foreach (var g in groups)
            {
                if (SameGrip(g.Epoch, epoch))
                {
                    return g.Runs;
                }
            }
            var runs = new List<ReplayCuts.Extraction>();
            groups.Add((epoch, runs));
            return runs;
        }

        /// <summary>
        /// The settings a replay from before the journal was played on.
        /// </summary>
        /// <remarks>
        /// If the player has named the profile, its stored numbers are used -- real values
        /// rather than a guess, and the game still holds them. Failing that, the settings in
        /// force now, which is a guess and is reported as one.
        ///
        /// Naming a profile is evidence, not proof: profiles are editable, so this is what
        /// the profile holds today and only matches history if nobody has changed it since.
        /// The change detector gets to disagree with it either way.
        /// </remarks>
        private static OffsetJournal.Epoch Assumed(OffsetState.Reading r)
        {
            if (Preferences.TryGetRangeGrip(out var left, out var right, out var alternative))
            {
                return new OffsetJournal.Epoch
                {
                    LeftRotation = left,
                    LeftPosition = r.Left.TypedPosition,
                    RightRotation = right,
                    RightPosition = r.Right.TypedPosition,
                    LegacyRotation = r.LegacyRotation,
                    LegacyValid = r.LegacyValid,
                    AlternativeHandling = alternative,
                };
            }
            return new OffsetJournal.Epoch
            {
                LeftRotation = r.Left.TypedRotation,
                LeftPosition = r.Left.TypedPosition,
                RightRotation = r.Right.TypedRotation,
                RightPosition = r.Right.TypedPosition,
                LegacyRotation = r.LegacyRotation,
                LegacyValid = r.LegacyValid,
                AlternativeHandling = r.AlternativeHandling,
            };
        }

        private static void Report(
            string name, List<CutSample> cuts, Vector3 current, bool left,
            OffsetJournal.Epoch epoch, bool trusted = true, bool show = true)
        {
            if (cuts.Count == 0)
            {
                Plugin.Log.Info($"{name}: no cuts");
                return;
            }
            var found = OffsetSearch.Search(
                cuts, current, left, epoch.LegacyRotation, epoch.AlternativeHandling);

            var meanDistance = cuts.Average(c => Mathf.Abs(c.Signed));
            var after = cuts.Average(c => OffsetSearch.DistanceUnder(c, found.Turn));
            Plugin.Log.Info(
                $"{name}: {cuts.Count:N0} cuts | now {current} -> suggest {found.Setting} " +
                $"| turn {found.Turn.x * Mathf.Rad2Deg:F2},{found.Turn.y * Mathf.Rad2Deg:F2} deg " +
                $"| mean cut {meanDistance * 100f:F2} -> {after * 100f:F2} cm " +
                $"| worth {found.GainFraction:P3} of score" +
                (trusted ? "" : " [too few runs to act on]"));

            if (!show)
            {
                return;
            }
            var line = trusted
                ? $"{name.Trim()}: {current} -> {found.Setting}, worth {found.GainFraction:P2}"
                : $"{name.Trim()}: too few runs to advise ({cuts.Count:N0} cuts)";
            if (left)
            {
                Advice.Left = line;
            }
            else
            {
                Advice.Right = line;
            }
        }

        /// <summary>Replay folders: this install's, plus any the player has listed.</summary>
        /// <remarks>
        /// The extra list exists because a BSManager setup keeps a separate tree per game
        /// version, and a player's history is spread across all of them while the mod can
        /// only see the one it is installed in.
        /// </remarks>
        private static List<string> Discover()
        {
            var folders = new List<string>();
            var own = Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "UserData", "BeatLeader", "Replays"));
            if (Directory.Exists(own))
            {
                folders.Add(own);
            }

            var listed = Path.Combine(Paths.DataDir, ExtraFoldersFile);
            if (File.Exists(listed))
            {
                foreach (var line in File.ReadAllLines(listed))
                {
                    var path = line.Trim();
                    if (path.Length > 0 && !path.StartsWith("#") && Directory.Exists(path))
                    {
                        folders.Add(path);
                    }
                }
            }

            var files = new List<string>();
            foreach (var folder in folders)
            {
                try
                {
                    files.AddRange(Directory.GetFiles(folder, "*.bsor"));
                }
                catch (Exception e)
                {
                    Plugin.Log.Warn($"could not list {folder}: {e.Message}");
                }
            }
            // Every replay found, newest first. The caller decides how far down to read;
            // the full list is what the session timeline is built from.
            return files.OrderByDescending(File.GetLastWriteTimeUtc).ToList();
        }
    }
}
