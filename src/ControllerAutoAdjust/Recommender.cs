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
        /// How many replays to read, newest first.
        /// </summary>
        /// <remarks>
        /// Reading every replay a BSManager install has kept meant parsing two thousand frame
        /// streams -- minutes of work and gigabytes of churn -- for an answer that stops
        /// moving well before that. The offline sweep settled by around 273, and the fit is
        /// bounded by how much a hand drifts rather than how many cuts are thrown at it.
        /// </remarks>
        private const int MostRecentReplays = 400;

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
                var files = Discover();
                if (files.Count == 0)
                {
                    Plugin.Log.Info(
                        "no replays found; the live recorder is the only source of cuts here");
                    return;
                }

                var groups = new List<(OffsetJournal.Epoch Epoch, List<ReplayCuts.Extraction> Runs)>();
                var used = 0;
                var skipped = 0;
                var unknownEpoch = 0;
                var speeds = new List<float>();
                var residuals = new List<float>();

                foreach (var file in files)
                {
                    Bsor.Replay replay;
                    try
                    {
                        replay = Bsor.Parse(file);
                    }
                    catch
                    {
                        // Roughly a tenth of a live install's replays do not parse; a
                        // partially written one is unremarkable.
                        skipped++;
                        continue;
                    }

                    if (replay.Info.Mode != "Standard" || !replay.Info.Clean)
                    {
                        skipped++;
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
                        skipped++;
                        continue;
                    }
                    cuts.Played = when;
                    cuts.FromJournal = fromJournal;
                    Bucket(groups, epoch).Add(cuts);
                    speeds.Add(cuts.Left.NoteSpeed);
                    residuals.Add(cuts.Left.MedianResidual);
                    used++;
                }

                Plugin.Log.Info(
                    $"replays: {used} used, {skipped} skipped, of {files.Count} found");
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

                residuals.Sort();
                Plugin.Log.Info(
                    $"note-centre reconstruction: median residual " +
                    $"{residuals[residuals.Count / 2] * 1000f:F1} mm, " +
                    $"fitted note speed {speeds.Average():F1} m/s");

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
                    Report("   left", leftCuts, group.Epoch.LeftRotation, true, group.Epoch, trusted);
                    Report("   right", rightCuts, group.Epoch.RightRotation, false, group.Epoch, trusted);
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
            var answer = Preferences.PriorReplayAnswer;
            if (answer == Preferences.PriorReplays.DifferentConfig)
            {
                Plugin.Log.Warn(
                    $"{runs.Count} replays predate the journal and you have said they were " +
                    "played on different settings, so they are reported but not recommended from");
                return;
            }

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
            if (verdict.Split)
            {
                Plugin.Log.Warn(
                    $"these replays do NOT look like one set of settings: the residual shifts " +
                    $"{verdict.GapDegrees:F1} deg around {verdict.At:yyyy-MM-dd} " +
                    $"(statistic {verdict.Statistic:F1}). Pooling across that fits a compromise " +
                    "suiting neither side.");
            }
            else if (answer == Preferences.PriorReplays.Unanswered)
            {
                Plugin.Log.Info(
                    $"{verdict.Sessions} sessions predate the journal and look consistent " +
                    $"(largest shift {verdict.GapDegrees:F1} deg, statistic {verdict.Statistic:F1}), " +
                    "but nobody has confirmed they were played on the current settings");
            }
            else
            {
                Plugin.Log.Info(
                    $"{verdict.Sessions} sessions predate the journal; you have said they used " +
                    $"the current settings and the data agrees (largest shift " +
                    $"{verdict.GapDegrees:F1} deg, statistic {verdict.Statistic:F1})");
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
        /// The settings a replay from before the journal is assumed to have been played on.
        /// </summary>
        /// <remarks>
        /// The current ones, which is a guess and is reported as such. It is right for a
        /// player who set their grip once and wrong for one who has been experimenting, and
        /// nothing in the replay can tell the two apart.
        /// </remarks>
        private static OffsetJournal.Epoch Assumed(OffsetState.Reading r) =>
            new OffsetJournal.Epoch
            {
                LeftRotation = r.Left.TypedRotation,
                RightRotation = r.Right.TypedRotation,
                LegacyRotation = r.LegacyRotation,
                LegacyValid = r.LegacyValid,
                AlternativeHandling = r.AlternativeHandling,
            };

        private static void Report(
            string name, List<CutSample> cuts, Vector3 current, bool left,
            OffsetJournal.Epoch epoch, bool trusted = true)
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
            return files
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(MostRecentReplays)
                .ToList();
        }
    }
}
