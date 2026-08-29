using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace ControllerAutoAdjust
{
    /// <summary>
    /// Reads the player's replays, then works out what their grip is asking for.
    /// </summary>
    /// <remarks>
    /// Three steps, deliberately apart. Reading several hundred replays is a minute and a
    /// half; fitting both hands is twenty seconds; and between them sits a choice only the
    /// player can make -- which stretch of history was played on one grip, and which profile
    /// that was. Doing all three at once meant adjusting the date range by a week cost the
    /// full read again, for cuts that had not changed.
    ///
    /// So the read is cached and the fit runs against it. Changing the range or the profile
    /// re-fits in seconds without touching a file.
    ///
    /// Replays are the source because they already exist: a player installing this has months
    /// of them, and the mod can answer on the first launch rather than asking for twenty runs
    /// before it says anything, and again after every adjustment.
    /// </remarks>
    internal static class Recommender
    {
        internal const string ExtraFoldersFile = "replay-folders.txt";

        /// <summary>Below this a run is a fragment, and it would be weighted like a map.</summary>
        private const int MinCutsPerHand = 100;

        /// <summary>
        /// How many usable runs to gather before stopping, newest first.
        /// </summary>
        /// <remarks>
        /// Counted in runs that survive filtering rather than files opened. Four hundred files
        /// gave 217 usable here, and a library heavier in One Saber or short runs would give
        /// far fewer -- so a file cap bounds the cost without bounding the evidence.
        ///
        /// Newest first is not only about cost. The residual drifts one to two degrees a
        /// month, so older sessions describe a grip the player has partly moved on from, and
        /// the fit stops improving well before a full library is read: the offline sweep
        /// settled by around 273 runs.
        /// </remarks>
        private const int TargetUsableRuns = 300;

        /// <summary>A ceiling on files opened, so an enormous library cannot stall the read.</summary>
        private const int MaxFilesToOpen = 1200;

        /// <summary>
        /// Runs needed before a group's answer is offered rather than merely reported.
        /// </summary>
        /// <remarks>
        /// Measured, not chosen: telling a one-degree change apart takes 10 to 22 runs,
        /// because two runs of the same map on unchanged settings differ by 4 to 6 accuracy
        /// points and the whole prize is about 2. A seven-run group in testing claimed 1.3% --
        /// four times the ceiling any fixed offset has been measured to reach -- and disagreed
        /// by four degrees with the same hand fitted on 143 runs. Both cannot be right, and
        /// the small one is the one that is wrong.
        /// </remarks>
        private const int MinRunsToRecommend = 20;

        private static Thread _worker;
        private static volatile bool _running;

        /// <summary>Every replay read, kept so the fit can be re-run without reading again.</summary>
        private static volatile List<ReplayCuts.Extraction> _read =
            new List<ReplayCuts.Extraction>();

        internal static bool Running => _running;

        internal static int RunsRead => _read.Count;

        internal static bool HasRead => _read.Count > 0;

        /// <summary>Step one: read the replays. Slow, and only needed once.</summary>
        internal static bool BeginRead() => Start(reading => Read(reading));

        /// <summary>Step three: fit both hands to whatever step one read.</summary>
        internal static bool BeginFit() => Start(reading => Fit(reading));

        /// <summary>
        /// Run a step on a worker, having read the live settings on the main thread.
        /// </summary>
        /// <remarks>
        /// The settings hang off Unity objects, so they are captured here and the worker never
        /// touches the scene.
        /// </remarks>
        private static bool Start(Action<OffsetState.Reading> step)
        {
            if (_running || !OffsetState.TryRead(out var reading))
            {
                return false;
            }
            _running = true;
            _worker = new Thread(() =>
            {
                try
                {
                    step(reading);
                }
                catch (Exception e)
                {
                    Plugin.Log.Error($"analysis failed: {e}");
                    Advice.Summary = "Analysis failed; see the log.";
                }
                finally
                {
                    _running = false;
                    Advice.Progress = 0f;
                    Advice.Publish();
                }
            })
            { IsBackground = true };
            _worker.Start();
            return true;
        }

        private static void Read(OffsetState.Reading reading)
        {
            var files = Discover();
            if (files.Count == 0)
            {
                Advice.Summary = "No replays found.";
                Plugin.Log.Info("no replays found; the live recorder is the only source here");
                return;
            }

            var epochs = OffsetJournal.Read();
            var found = new List<ReplayCuts.Extraction>();

            // Counted by reason. "250 skipped" cannot distinguish a library full of One Saber
            // runs from a filter discarding good data, and those want opposite responses.
            var why = new Dictionary<string, int>();
            void Drop(string reason) =>
                why[reason] = why.TryGetValue(reason, out var n) ? n + 1 : 1;

            var speeds = new List<float>();
            var residuals = new List<float>();

            // One replay's worth of buffers for the whole pass, rather than a fresh few
            // megabytes per file. Measured at about 26 MB retained across 555 replays, against
            // a process that sits above 3 GB for reasons entirely its own.
            var scratch = new Bsor.Scratch();
            var seen = 0;

            foreach (var file in files)
            {
                if (found.Count >= TargetUsableRuns || seen >= MaxFilesToOpen)
                {
                    break;
                }
                if (++seen % 25 == 0)
                {
                    Advice.Summary = $"Reading replays... {found.Count} of {TargetUsableRuns}";
                    Advice.Progress = (float)found.Count / TargetUsableRuns;
                    Advice.Publish();
                }

                Bsor.Replay replay;
                try
                {
                    replay = Bsor.Parse(file, scratch);
                }
                catch
                {
                    // Roughly a tenth of a live install's replays do not parse; a partially
                    // written one is unremarkable.
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

                var cuts = ReplayCuts.Extract(replay, MinCutsPerHand);
                if (cuts.Left.Cuts == null || cuts.Left.Cuts.Count == 0)
                {
                    Drop($"under {MinCutsPerHand} cuts a hand");
                    continue;
                }

                // The journal is fact and is resolved now. Anything it does not cover depends
                // on the profile the player names, which is step two, so it waits for the fit.
                var when = File.GetLastWriteTimeUtc(file);
                cuts.Played = when;
                cuts.FromJournal = OffsetJournal.TryAt(epochs, when, out var epoch);
                cuts.Epoch = epoch;
                found.Add(cuts);
                speeds.Add(cuts.Left.NoteSpeed);
                residuals.Add(cuts.Left.MedianResidual);
            }

            RejectImpostors(found);
            _read = found;

            // Built from the runs actually held, not from every file discovered. A third of a
            // library is One Saber, too short or unreadable, and the read stops once it has
            // enough -- so a timeline drawn from filenames offers dates with nothing behind
            // them, and a range picked at either end can select no usable data at all while
            // looking perfectly reasonable.
            Advice.Sessions = Sittings(found);

            // Run now rather than at fit time, so the verdict is on screen while the player
            // is deciding what to tell the panel -- which is the only moment it is any use.
            var unknown = found.Where(r => !r.FromJournal).ToList();
            Advice.UnknownRuns = unknown.Count;
            Advice.Evidence = DescribeTheGap(unknown, found.Count);

            residuals.Sort();

            Plugin.Log.Info(
                $"replays: {found.Count} used, {seen - found.Count} skipped, "
                + $"{seen} of {files.Count} opened ("
                + string.Join(", ", why.OrderByDescending(k => k.Value)
                                       .Select(k => $"{k.Value} {k.Key}")) + ")");
            if (residuals.Count > 0)
            {
                Plugin.Log.Info(
                    $"note-centre reconstruction: median residual "
                    + $"{residuals[residuals.Count / 2] * 1000f:F1} mm, "
                    + $"fitted note speed {speeds.Average():F1} m/s");
            }

            var biggest = why.Count == 0
                ? ""
                : why.OrderByDescending(k => k.Value).Select(k => $", {k.Value} {k.Key}").First();
            Advice.Summary = $"{found.Count} runs read of {seen} replays{biggest}. "
                             + "Set the range below, then fit.";
            Advice.Publish();
        }

        /// <summary>
        /// Drop runs that are not the same hand as the rest.
        /// </summary>
        /// <remarks>
        /// Not a quality filter. Filtering on run quality was measured and does not help: a
        /// failed run, an early exit or an off-range difficulty is still the player's grip,
        /// and excluding them moved the answer a degree or two while improving nothing.
        ///
        /// This is a different question -- whether a run came from the same process at all. A
        /// swing harness driving the sabers along a fixed sweep produced a mean cut distance
        /// of 46 cm against a normal 12 to 15, and it read as an ordinary session to every
        /// other filter here. So would a friend trying the headset, or a one-handed session.
        /// Judged against the player's own median rather than a fixed threshold, since the
        /// whole point is that a grip is personal.
        /// </remarks>
        private static void RejectImpostors(List<ReplayCuts.Extraction> runs)
        {
            if (runs.Count < 8)
            {
                return;
            }
            var means = runs
                .Select(r => r.Left.Cuts.Concat(r.Right.Cuts).Average(c => Mathf.Abs(c.Signed)))
                .ToList();
            var sorted = means.OrderBy(m => m).ToList();
            var median = sorted[sorted.Count / 2];

            // Generous: ordinary bad sessions sit well inside this, and the runs it is meant
            // to catch are not close to it.
            var limit = median * 2.5f;
            var dropped = 0;
            for (var i = runs.Count - 1; i >= 0; i--)
            {
                if (means[i] <= limit)
                {
                    continue;
                }
                Plugin.Log.Warn(
                    $"ignoring {runs[i].Song}: mean cut {means[i] * 100f:F1} cm against a "
                    + $"median of {median * 100f:F1} cm -- that is not the same hand");
                runs.RemoveAt(i);
                dropped++;
            }
            if (dropped > 0)
            {
                Plugin.Log.Info($"{dropped} run(s) set aside as not the player");
            }
        }

        /// <summary>
        /// Group runs into sittings: continuous play, broken by a gap.
        /// </summary>
        /// <remarks>
        /// A settings change happens at a moment, not at midnight. Grouped by calendar day,
        /// an evening's play before a change and the same evening's play after it are one
        /// point that cannot be divided -- so a change made at 22:49 was unsplittable, and
        /// the range sliders could not describe the very split the player had just made.
        /// </remarks>
        private static List<Advice.Span> Sittings(List<ReplayCuts.Extraction> runs)
        {
            var spans = new List<Advice.Span>();
            foreach (var run in runs.OrderBy(r => r.Played))
            {
                if (spans.Count > 0)
                {
                    var last = spans[spans.Count - 1];
                    if (run.Played - last.End <= Advice.SessionGap)
                    {
                        last.End = run.Played;
                        last.Runs++;
                        spans[spans.Count - 1] = last;
                        continue;
                    }
                }
                spans.Add(new Advice.Span
                {
                    Start = run.Played,
                    End = run.Played,
                    Runs = 1,
                });
            }
            return spans;
        }

        private static void Fit(OffsetState.Reading reading)
        {
            var runs = _read;
            if (runs.Count == 0)
            {
                Advice.Summary = "Nothing read yet. Read the replays first.";
                return;
            }

            // Grouped by the settings each run was played on, and each group fitted against
            // its own. The residual is the player's grip minus the offset already applied, so
            // pooling across settings fits a compromise suiting neither -- but the answer is
            // absolute either way, since applying a group's correction to the settings that
            // group was played on lands on the same grip whichever group it came from.
            var groups = new List<(OffsetJournal.Epoch Epoch, List<ReplayCuts.Extraction> Runs)>();
            var assignments = Preferences.Assignments;
            var unassigned = 0;
            var unknownEpoch = 0;

            foreach (var run in runs)
            {
                var epoch = run.Epoch;
                if (!run.FromJournal)
                {
                    unknownEpoch++;
                    // The assignments are the player's word about history the journal cannot
                    // vouch for. Runs it does cover are recorded fact and are not up for a
                    // vote. Anything nobody has spoken for is left out rather than guessed at:
                    // putting cuts from an unknown grip into a group that claims to know its
                    // own is the error this whole mechanism exists to prevent.
                    var matched = false;
                    foreach (var a in assignments)
                    {
                        if (!a.Covers(run.Played))
                        {
                            continue;
                        }
                        epoch = new OffsetJournal.Epoch
                        {
                            LeftRotation = a.LeftRotation,
                            LeftPosition = reading.Left.TypedPosition,
                            RightRotation = a.RightRotation,
                            RightPosition = reading.Right.TypedPosition,
                            LegacyRotation = reading.LegacyRotation,
                            LegacyValid = reading.LegacyValid,
                            AlternativeHandling = a.AlternativeHandling,
                        };
                        matched = true;
                        break;
                    }
                    if (!matched)
                    {
                        unassigned++;
                        continue;
                    }
                }
                Bucket(groups, epoch).Add(run);
            }

            Plugin.Log.Info(
                $"fitting {runs.Count - unassigned} runs in {groups.Count} settings group(s)"
                + (unassigned > 0
                    ? $"; {unassigned} of {unknownEpoch} unrecorded runs have no assigned range"
                    : ""));

            Advice.Left = "";
            Advice.Right = "";
            var advised = false;

            // The bar is divided by work, not by group. The search sweeps the same candidate
            // grid whatever it is given, so its cost is proportional to the cuts in hand --
            // and a run of 206 sessions beside one of 11 is not two equal halves. Split
            // evenly, the bar sat at 50% for almost the whole fit and then finished instantly.
            var ordered = groups.OrderByDescending(g => g.Runs.Count).ToList();
            var totalCuts = 0L;
            foreach (var group in ordered)
            {
                totalCuts += group.Runs.Sum(r => r.Left.Cuts.Count + r.Right.Cuts.Count);
            }
            var soFar = 0L;

            foreach (var group in ordered)
            {
                var leftCuts = group.Runs.SelectMany(r => r.Left.Cuts).ToList();
                var rightCuts = group.Runs.SelectMany(r => r.Right.Cuts).ToList();
                var trusted = group.Runs.Count >= MinRunsToRecommend;
                Plugin.Log.Info(
                    $"-- settings left {group.Epoch.LeftRotation} right "
                    + $"{group.Epoch.RightRotation}: {group.Runs.Count} runs"
                    + (trusted ? "" : $" (under {MinRunsToRecommend}: shown, not recommended)"));

                var show = trusted && !advised;
                var scale = totalCuts > 0 ? 1f / totalCuts : 0f;
                var leftFrom = soFar * scale;
                var leftSpan = leftCuts.Count * scale;
                var rightSpan = rightCuts.Count * scale;
                Report("   left", leftCuts, group.Epoch.LeftRotation, true, group.Epoch,
                       trusted, show, leftFrom, leftSpan);
                Report("   right", rightCuts, group.Epoch.RightRotation, false, group.Epoch,
                       trusted, show, leftFrom + leftSpan, rightSpan);
                soFar += leftCuts.Count + rightCuts.Count;
                advised |= show;
                Advice.Publish();
            }

            if (!advised)
            {
                Advice.Left = $"No group has the {MinRunsToRecommend} runs needed to advise.";
                Advice.Right = "Widen the date range, or play more.";
            }
            Advice.Summary = $"Fitted {runs.Count - unassigned} runs"
                             + (unassigned > 0 ? $"; {unassigned} unassigned and unused." : ".");
            Advice.Publish();
        }

        /// <summary>
        /// What the mod does and does not know about the settings behind these runs.
        /// </summary>
        /// <remarks>
        /// Only runs from before the journal existed need the player to say anything. Once it
        /// has an entry, the settings are recorded fact and no answer of theirs can improve on
        /// it -- so the panel's controls govern the earlier part alone, and saying which part
        /// that is stops them reading as a filter over everything.
        ///
        /// Whether that earlier part is one grip is measurable, and it is still measured --
        /// but to the log, not to the panel. The check cannot see a change in the newest or
        /// oldest few sittings, which includes every change made recently, so it was reporting
        /// "no split found" exactly when a player had just made one. The player knows their
        /// own history better than a statistic run over eleven runs, and they can now say so
        /// directly.
        /// </remarks>
        private static string DescribeTheGap(List<ReplayCuts.Extraction> unknown, int total)
        {
            if (unknown.Count == 0)
            {
                return $"All {total} runs have recorded settings. Nothing to fill in.";
            }
            var known = total - unknown.Count;
            var lead = known > 0
                ? $"{known} runs have recorded settings; {unknown.Count} predate this mod."
                : $"All {unknown.Count} runs predate this mod.";

            var sessions = unknown
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
            if (!verdict.Conclusive)
            {
                Plugin.Log.Info(
                    $"grip-change check: too few sittings ({verdict.Sessions}) to look");
            }
            else if (verdict.Split)
            {
                Plugin.Log.Warn(
                    $"grip-change check: the residual shifts {verdict.GapDegrees:F1} deg around "
                    + $"{verdict.At:d MMM} (statistic {verdict.Statistic:F1})");
            }
            else
            {
                Plugin.Log.Info(
                    $"grip-change check: none found between {verdict.TestableFrom:d MMM} and "
                    + $"{verdict.TestableTo:d MMM}, largest shift {verdict.GapDegrees:F1} deg "
                    + "(changes outside those dates are not visible to it)");
            }
            return lead + " Assign the ranges below.";
        }

        /// <summary>Two settings are the same epoch if they put the blade in the same place.</summary>
        /// <remarks>
        /// Grouping on the typed numbers splits history that was never really split. A value
        /// nudged one way and back leaves three journal entries describing one grip, and a
        /// change confined to Z moves the triple without moving anything at all -- composed
        /// after the grip's large X tilt it is mostly roll about the blade, and roll cannot
        /// move a cut plane. Both would fragment a history into groups too small to fit, which
        /// reads as "not enough data" rather than as a grouping mistake.
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
        /// The settings a run from before the journal was played on.
        /// </summary>
        /// <remarks>
        /// The grip copied from whichever profile the player named, if they named one -- real
        /// values rather than a guess, taken at the moment of choosing so a later edit to that
        /// profile cannot change what they meant. Failing that, the settings in force now,
        /// which is a guess and is reported as one.
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
            OffsetJournal.Epoch epoch, bool trusted, bool show, float from, float span)
        {
            if (cuts.Count == 0)
            {
                Plugin.Log.Info($"{name}: no cuts");
                return;
            }

            var hand = name.Trim();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var found = OffsetSearch.Search(
                cuts, current, left, epoch.LegacyRotation, epoch.AlternativeHandling,
                onProgress: done =>
                {
                    Advice.Summary = $"Fitting the {hand} hand... {done:P0}";
                    Advice.Progress = from + span * done;
                    Advice.Publish();
                });
            clock.Stop();

            var meanDistance = cuts.Average(c => Mathf.Abs(c.Signed));
            var after = cuts.Average(c => OffsetSearch.DistanceUnder(c, found.Turn));
            Plugin.Log.Info(
                $"{name}: {cuts.Count:N0} cuts | now {current} -> suggest {found.Setting} "
                + $"| turn {found.Turn.x * Mathf.Rad2Deg:F2},{found.Turn.y * Mathf.Rad2Deg:F2} deg "
                + $"| mean cut {meanDistance * 100f:F2} -> {after * 100f:F2} cm "
                + $"| worth {found.GainFraction:P3} of score | {clock.ElapsedMilliseconds} ms, "
                + $"{found.DistinctTurns} distinct turns of {found.Candidates} settings"
                + (trusted ? "" : " [too few runs to act on]"));

            // The panel shows one answer, so it has to be the one being recommended. Writing
            // every group's line in turn left whichever came last on screen, and groups run
            // largest first, making the last the smallest -- exactly the one too thin to act on.
            if (!show)
            {
                return;
            }
            var line = trusted
                ? $"{hand}: {current} -> {found.Setting}, worth {found.GainFraction:P2}"
                : $"{hand}: too few runs to advise ({cuts.Count:N0} cuts)";
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
        /// version, and a player's history is spread across all of them while the mod can only
        /// see the one it is installed in.
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
            // Newest first. The caller decides how far down to read; the full list is what the
            // session timeline is built from.
            return files.OrderByDescending(File.GetLastWriteTimeUtc).ToList();
        }
    }
}
