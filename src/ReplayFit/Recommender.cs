using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace ReplayFit
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
        /// How many replays one pass will reduce that it has not reduced before.
        /// </summary>
        /// <remarks>
        /// A budget on the expensive half, not a cap on how far back the read reaches. A
        /// cached replay costs a file stat and a dictionary lookup, so the walk covers the
        /// whole library every time; only parsing is rationed.
        ///
        /// The effect is that a large library reduces itself over several visits rather than
        /// stalling one. Newest first, so the runs that matter most to the current grip are
        /// the ones reduced first, and each later pass reaches further back.
        /// </remarks>
        private const int MaxNewReductions = 1200;

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

        /// <summary>Which step the panel is on, so its status can sit beside that step.</summary>
        /// <remarks>
        /// Kept after the step finishes rather than reset to none: the line a step leaves
        /// behind belongs where the step was, and moving it back to the top the moment the
        /// worker exits would take the answer away from the button that produced it.
        /// </remarks>
        internal enum Step { None, Reading, Fitting }

        private static volatile Step _step = Step.None;

        internal static Step Phase => _step;

        internal static int RunsRead => _read.Count;

        internal static bool HasRead => _read.Count > 0;

        /// <summary>How many runs an assignment actually covers, for showing next to it.</summary>
        private static readonly DateTime Epoch =
            new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>Bounds a timestamp has to fall inside to be believed, as unix seconds.</summary>
        /// <remarks>
        /// Wide on purpose. This is here to reject a field that is not seconds at all -- a
        /// zero, or a millisecond value, which is 1000x too large and would otherwise read as
        /// a date sixty thousand years out -- not to second-guess a plausible date.
        /// </remarks>
        private const long EarliestPlausible = 1200000000;  // 2008, before the game existed
        private const long LatestPlausible = 4100000000;    // 2100

        /// <summary>
        /// When a run was actually played, taken from the replay's own header.
        /// </summary>
        /// <remarks>
        /// Not the file's write time. That is only a proxy for when it was played, and a
        /// fragile one: anything that rewrites a .bsor -- a BeatLeader re-sync, a restored
        /// backup, a copied install -- moves it to the present. The run's date then jumps
        /// with it and silently leaves whatever range the player assigned it to, while the
        /// assignment itself still reads perfectly plausibly on screen.
        ///
        /// The header is written once, by the game, and never moves again.
        ///
        /// The write time stays as the fallback, because a header this reader cannot make
        /// sense of is not a reason to throw away an otherwise good run. Measured across the
        /// 4998 replays this was developed against, nothing needed it: every one carried a
        /// valid timestamp, within about a minute of its write time.
        /// </remarks>
        private static DateTime PlayedAt(Bsor.Info info, string file)
        {
            if (long.TryParse(info.Timestamp, NumberStyles.None,
                              CultureInfo.InvariantCulture, out var seconds)
                && seconds > EarliestPlausible && seconds < LatestPlausible)
            {
                return Epoch.AddSeconds(seconds);
            }
            return File.GetLastWriteTimeUtc(file);
        }

        /// <summary>What a range holds, and how much of it the range actually governs.</summary>
        /// <remarks>
        /// Two numbers rather than one, because they fail differently and the difference is
        /// the whole message. A range holding nothing is a mistake -- wrong dates, or a
        /// history that was never read. A range whose runs the journal already speaks for is
        /// not a mistake at all: it is simply redundant, because the journal recorded those
        /// settings at the time and recorded fact outranks anything assigned after the event.
        ///
        /// Reported as one number they are indistinguishable, and both read as "0 runs" --
        /// which sends a player back to re-check dates that were right all along.
        /// </remarks>
        internal struct Coverage
        {
            /// <summary>Runs whose date falls inside the range.</summary>
            public int Covered;

            /// <summary>Of those, the ones the journal does not already account for.</summary>
            public int Governed;
        }

        internal static Coverage RunsCoveredBy(Preferences.Assignment a)
        {
            var coverage = new Coverage();
            foreach (var run in _read)
            {
                if (!a.Covers(run.Played))
                {
                    continue;
                }
                coverage.Covered++;
                if (!run.FromJournal)
                {
                    coverage.Governed++;
                }
            }
            return coverage;
        }

        /// <summary>Step one: read the replays. Slow, and only needed once.</summary>
        internal static bool BeginRead() => Start(Step.Reading, reading => Read(reading));

        /// <summary>Step three: fit both hands to whatever step one read.</summary>
        internal static bool BeginFit() => Start(Step.Fitting, reading => Fit(reading));

        /// <summary>
        /// Run a step on a worker, having read the live settings on the main thread.
        /// </summary>
        /// <remarks>
        /// The settings hang off Unity objects, so they are captured here and the worker never
        /// touches the scene.
        /// </remarks>
        private static bool Start(Step phase, Action<OffsetState.Reading> step)
        {
            if (_running || !OffsetState.TryRead(out var reading))
            {
                return false;
            }
            _step = phase;
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
            var cache = CutCache.Load();
            var keep = new Dictionary<string, CutCache.Entry>(StringComparer.OrdinalIgnoreCase);
            var reused = 0;
            var found = new List<ReplayCuts.Extraction>();

            // Counted by reason. "250 skipped" cannot distinguish a library full of One Saber
            // runs from a filter discarding good data, and those want opposite responses.
            var why = new Dictionary<string, int>();
            void Drop(string reason) =>
                why[reason] = why.TryGetValue(reason, out var n) ? n + 1 : 1;

            // Counted and remembered. The count is for this pass's summary; the cache entry
            // is so the next pass spends its parsing budget on files nothing has read yet.
            void Reject(string file, long written, string reason)
            {
                Drop(reason);
                keep[file] = new CutCache.Entry { WrittenTicks = written, Cuts = null };
            }

            var speeds = new List<float>();
            var residuals = new List<float>();

            // One replay's worth of buffers for the whole pass, rather than a fresh few
            // megabytes per file. Measured at about 26 MB retained across 555 replays, against
            // a process that sits above 3 GB for reasons entirely its own.
            var scratch = new Bsor.Scratch();
            var seen = 0;
            var reduced = 0;

            // The whole library, every pass. An assignment exists to speak for history the
            // journal cannot, so a read that stops after the newest few hundred runs leaves
            // the player naming ranges over replays it will never look at -- the range then
            // reads plausibly and covers nothing, which is indistinguishable from having got
            // the dates wrong.
            foreach (var file in files)
            {
                if (++seen % 25 == 0)
                {
                    Advice.Summary = $"Reading replays... {found.Count} usable"
                                     + (reused > 0 ? $" ({reused} cached)" : "");
                    Advice.Progress = (float)seen / files.Count;
                    Advice.Publish();
                }

                // Reduced already, and the file has not moved since? Then the expensive part
                // is done. This is what makes a second read near-instant and a new replay the
                // only thing actually parsed.
                var written = File.GetLastWriteTimeUtc(file).Ticks;
                if (cache.TryGetValue(file, out var cached) && cached.WrittenTicks == written)
                {
                    keep[file] = cached;
                    if (cached.Usable)
                    {
                        found.Add(cached.Cuts);
                        reused++;
                    }
                    continue;
                }

                // Not reduced before, and this pass has already done its share. Left alone
                // rather than dropped: the next pass starts here instead of starting over.
                if (reduced >= MaxNewReductions)
                {
                    continue;
                }
                reduced++;

                Bsor.Replay replay;
                try
                {
                    replay = Bsor.Parse(file, scratch);
                }
                catch
                {
                    // Roughly a tenth of a live install's replays do not parse; a partially
                    // written one is unremarkable.
                    Reject(file, written, "unreadable");
                    continue;
                }

                if (replay.Info.Mode != "Standard")
                {
                    Reject(file, written, $"not Standard ({replay.Info.Mode})");
                    continue;
                }
                if (!replay.Info.Clean)
                {
                    Reject(file, written, "speed or practice modifier");
                    continue;
                }

                var cuts = ReplayCuts.Extract(replay, MinCutsPerHand);
                // Both hands. The threshold is named per hand and was applied to one of
                // them, so a run where the right hand barely played was kept, counted toward
                // the target, and contributed nothing to half the answer. The cache showed
                // one with zero right-hand cuts sitting among the three hundred.
                if (cuts.Left.Cuts == null || cuts.Left.Cuts.Count == 0
                    || cuts.Right.Cuts == null || cuts.Right.Cuts.Count == 0)
                {
                    Reject(file, written, $"under {MinCutsPerHand} cuts a hand");
                    continue;
                }

                // The journal is fact and is resolved now. Anything it does not cover depends
                // on the profile the player names, which is step two, so it waits for the fit.
                cuts.Played = PlayedAt(replay.Info, file);
                found.Add(cuts);
                keep[file] = new CutCache.Entry { WrittenTicks = written, Cuts = cuts };
                speeds.Add(cuts.Left.NoteSpeed);
                residuals.Add(cuts.Left.MedianResidual);
            }

            RejectImpostors(found);

            // Which settings each run was played on is resolved now rather than cached. The
            // journal gains entries and the player changes assignments after a read, so a
            // stored answer would be a stale one that looks exactly like a fresh one.
            foreach (var run in found)
            {
                run.FromJournal = OffsetJournal.TryAt(epochs, run.Played, out var epoch);
                run.Epoch = epoch;
            }

            CutCache.Save(keep);
            _read = found;

            // Measured here rather than when the tab is opened. It walks every cut twice and
            // reads the journal, which is nothing beside the read that just happened and a
            // visible stall if it waits until somebody is looking at it.
            Progress.Measure(found);

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

            Plugin.Log.Info($"{reused} replays reused from cache, {seen - reused} opened");
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

            Advice.Summary = "";
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
            Advice.Recommended = null;
            var advised = false;

            // The bar is divided by work, not by group. The search sweeps the same candidate
            // grid whatever it is given, so its cost is proportional to the cuts in hand --
            // and a run of 206 sessions beside one of 11 is not two equal halves. Split
            // evenly, the bar sat at 50% for almost the whole fit and then finished instantly.
            var ordered = groups.OrderByDescending(g => g.Runs.Count).ToList();

            // Which group is allowed to advise is a separate question from the order they
            // are worked through, and it is neither the biggest nor whichever matches the
            // settings currently in force.
            //
            // Not the biggest: the read reaches back over a whole history, so that is
            // whichever grip was held longest, easily one abandoned months ago.
            //
            // Not the live settings either, which is what this tried first and got wrong.
            // Changing the settings starts a new group at zero runs, so the moment a player
            // acts on advice -- or simply nudges a slider -- the group matching their
            // settings is the one with almost nothing in it, and the fit falls silent while
            // three hundred runs sit in groups it will not read from.
            //
            // Most recent of the groups big enough to trust. A group's answer is absolute:
            // it recovers the grip, and applying its correction to the settings it was played
            // on lands on that same grip whoever asks. So any trusted group can be acted on,
            // and the only thing separating them is age -- the residual drifts a degree or two
            // a month, so the newest trustworthy group describes the grip closest to today's.
            var advisable = -1;
            var newest = DateTime.MinValue;
            for (var i = 0; i < ordered.Count; i++)
            {
                if (ordered[i].Runs.Count < MinRunsToRecommend)
                {
                    continue;
                }
                foreach (var run in ordered[i].Runs)
                {
                    if (run.Played > newest)
                    {
                        newest = run.Played;
                        advisable = i;
                    }
                }
            }

            var totalCuts = 0L;
            foreach (var group in ordered)
            {
                totalCuts += group.Runs.Sum(r => r.Left.Cuts.Count + r.Right.Cuts.Count);
            }
            var soFar = 0L;

            for (var index = 0; index < ordered.Count; index++)
            {
                var group = ordered[index];
                var leftCuts = group.Runs.SelectMany(r => r.Left.Cuts).ToList();
                var rightCuts = group.Runs.SelectMany(r => r.Right.Cuts).ToList();
                var trusted = group.Runs.Count >= MinRunsToRecommend;
                Plugin.Log.Info(
                    $"-- settings left {group.Epoch.LeftRotation} right "
                    + $"{group.Epoch.RightRotation}: {group.Runs.Count} runs"
                    + (trusted ? "" : $" (under {MinRunsToRecommend}: shown, not recommended)"));

                var show = trusted && !advised && index == advisable;
                var scale = totalCuts > 0 ? 1f / totalCuts : 0f;
                var leftFrom = soFar * scale;
                var leftSpan = leftCuts.Count * scale;
                var rightSpan = rightCuts.Count * scale;
                var toLeft = Report(
                    "   left", leftCuts, group.Epoch.LeftRotation, true, group.Epoch,
                    trusted, show, leftFrom, leftSpan);
                var toRight = Report(
                    "   right", rightCuts, group.Epoch.RightRotation, false, group.Epoch,
                    trusted, show, leftFrom + leftSpan, rightSpan);

                // Both hands or neither. Half a recommendation applied is a grip nobody
                // fitted: one hand moved to suit the cuts and the other left where it was.
                if (show && toLeft.HasValue && toRight.HasValue)
                {
                    Advice.Recommended = new Advice.Recommendation
                    {
                        Left = toLeft.Value,
                        Right = toRight.Value,
                        WasLeft = group.Epoch.LeftRotation,
                        WasRight = group.Epoch.RightRotation,
                        AlternativeHandling = group.Epoch.AlternativeHandling,
                    };
                }
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
        /// Whether that earlier part is one grip is measurable, and was measured here for a
        /// while. It is not any more. The check could not see a change in the newest or
        /// oldest few sittings, which is where every recent change is, so it reported "no
        /// split found" exactly when a player had just made one -- and a player asked to
        /// draw the boundary themselves knows their own history better than a statistic run
        /// over eleven runs. Splitting the range by hand replaced it.
        /// </remarks>
        private static string DescribeTheGap(List<ReplayCuts.Extraction> unknown, int total)
        {
            if (unknown.Count == 0)
            {
                return $"All {total} runs have recorded settings. Ready to fit.";
            }
            var known = total - unknown.Count;
            var lead = known > 0
                ? $"{known} runs have recorded settings; {unknown.Count} predate this mod"
                : $"All {unknown.Count} runs predate this mod";

            return lead + " and need a range assigned below.";
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

        /// <summary>Fit one hand, and hand back the setting if it is worth acting on.</summary>
        private static Vector3? Report(
            string name, List<CutSample> cuts, Vector3 current, bool left,
            OffsetJournal.Epoch epoch, bool trusted, bool show, float from, float span)
        {
            if (cuts.Count == 0)
            {
                Plugin.Log.Info($"{name}: no cuts");
                return null;
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
                + $"| worth {found.GainFraction:P3} accuracy | {clock.ElapsedMilliseconds} ms, "
                + $"{found.DistinctTurns} distinct turns of {found.Candidates} settings"
                + (trusted ? "" : " [too few runs to act on]"));

            // The panel shows one answer, so it has to be the one being recommended. Writing
            // every group's line in turn left whichever came last on screen, and groups run
            // largest first, making the last the smallest -- exactly the one too thin to act on.
            if (!show)
            {
                return null;
            }
            var line = trusted
                ? $"{hand}: {current} -> {found.Setting}, worth {found.GainFraction:P2} accuracy"
                : $"{hand}: too few runs to advise ({cuts.Count:N0} cuts)";
            if (left)
            {
                Advice.Left = line;
            }
            else
            {
                Advice.Right = line;
            }
            return trusted ? found.Setting : (Vector3?)null;
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
