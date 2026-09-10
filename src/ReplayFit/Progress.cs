using System;
using System.Collections.Generic;
using UnityEngine;

namespace ReplayFit
{
    /// <summary>
    /// Whether any of this has actually helped, answered from the player's own history.
    /// </summary>
    /// <remarks>
    /// The fit tab argues that a setting is better. This one checks. They are different
    /// claims and only the second is worth anything on its own: a search that maximises a
    /// model's own objective will always report a gain, and did report one for months while
    /// the model's sign was inverted and every recommendation pointed the wrong way.
    ///
    /// So nothing here asks the model. It compares recorded cut distances before and after,
    /// on maps the player has played on both sides of the change, and that pairing is the
    /// whole design: cut distance is dominated by which map was played, so an unpaired
    /// average across a library is mostly a statement about map choice. Held against
    /// themselves, twelve maps said more than twelve hundred unpaired runs could.
    ///
    /// What it cannot separate is practice. The runs after a change are also later ones, and
    /// a player improves. That belongs in the readme rather than on the panel: it was written
    /// beside the number at first and cut, on the grounds that anyone reading a before-and-
    /// after comparison already knows later means more practised.
    /// </remarks>
    internal static class Progress
    {
        /// <summary>Cuts a map needs on each side before it can be compared with itself.</summary>
        /// <remarks>
        /// A map played once each side gives a difference of two runs, which is mostly how
        /// that day went. This is per era and per hand, so it is roughly one full run.
        /// </remarks>
        private const int MinCutsPerEra = 300;

        /// <summary>Maps in common before the comparison is worth showing at all.</summary>
        private const int MinPairedMaps = 5;

        /// <summary>
        /// One short line each, rather than one paragraph.
        /// </summary>
        /// <remarks>
        /// A sentence long enough to need wrapping is a sentence nobody reads through a
        /// headset at arm's length. Splitting the answer into a heading and a line per hand
        /// also puts the two numbers under one another, which is the comparison a player
        /// actually wants to make.
        /// </remarks>
        internal static volatile string Headline = "";
        internal static volatile string Hands = "";
        internal static volatile string Overall = "";
        internal static volatile string Chart = "";
        internal static volatile string Standing = "";

        /// <summary>Recompute the whole report. Called on the worker, after a read.</summary>
        internal static void Measure(List<ReplayCuts.Extraction> runs)
        {
            try
            {
                var epochs = OffsetJournal.Read();
                var since = epochs.Count > 0 ? epochs[0].From : (DateTime?)null;
                Compare(runs, since);
                Chart = OverTime(runs, since);
                Standing = Distance(runs, epochs);
            }
            catch (Exception e)
            {
                // A report that cannot be built is not worth taking a read down with it.
                Plugin.Log.Warn($"could not measure progress: {e.Message}");
                Headline = "Could not work that out; see the log.";
                Hands = Overall = Chart = Standing = "";
            }
        }

        private struct Centring
        {
            public double Weight;
            public double Distance;
            public double Points;
        }

        private static Centring Measure(List<CutSample> cuts)
        {
            var c = new Centring();
            foreach (var cut in cuts)
            {
                var d = Mathf.Abs(cut.Signed);
                c.Weight += cut.Multiplier;
                c.Distance += cut.Multiplier * d;
                c.Points += cut.Multiplier * OffsetSearch.AccuracyPoints(d);
            }
            return c;
        }

        private static void Say(string headline, string hands = "", string overall = "")
        {
            Headline = headline;
            Hands = hands;
            Overall = overall;
        }

        /// <summary>The same maps, before and after the mod started keeping records.</summary>
        private static void Compare(List<ReplayCuts.Extraction> runs, DateTime? since)
        {
            if (!since.HasValue)
            {
                Say("No settings change recorded yet.",
                    "This fills in once you have played on both sides of one.");
                return;
            }

            var before = new Dictionary<string, List<ReplayCuts.Extraction>>();
            var after = new Dictionary<string, List<ReplayCuts.Extraction>>();
            var played = 0;
            foreach (var run in runs)
            {
                var side = run.Played >= since.Value ? after : before;
                if (run.Played >= since.Value)
                {
                    played++;
                }
                if (!side.TryGetValue(run.Song, out var list))
                {
                    side[run.Song] = list = new List<ReplayCuts.Extraction>();
                }
                list.Add(run);
            }

            if (played == 0)
            {
                Say("No runs yet since the mod was installed.",
                    "Play a few and this will say whether your cuts improved.");
                return;
            }

            // Per hand, the mean of each map's own before-and-after difference. Averaging the
            // differences rather than differencing the averages is what holds map choice out
            // of it: a map only ever contributes a comparison with itself.
            var moved = new double[2];
            var gained = new double[2];
            var compared = new int[2];
            var better = new int[2];
            var maps = new HashSet<string>();
            foreach (var pair in before)
            {
                if (!after.TryGetValue(pair.Key, out var later))
                {
                    continue;
                }
                for (var hand = 0; hand < 2; hand++)
                {
                    // Each hand is its own comparison. A map can hold enough cuts for one
                    // and not the other, and counting it for both would put a difference
                    // into an average that no cuts stand behind.
                    var was = Cuts(pair.Value, hand);
                    var now = Cuts(later, hand);
                    if (was.Count < MinCutsPerEra || now.Count < MinCutsPerEra)
                    {
                        continue;
                    }
                    var a = Measure(was);
                    var b = Measure(now);
                    var delta = b.Distance / b.Weight - a.Distance / a.Weight;
                    moved[hand] += delta;
                    // In points rather than centimetres, since that is what a score is made
                    // of. The accuracy term is one fifteenth of a note's hundred and fifteen.
                    gained[hand] += (b.Points / b.Weight - a.Points / a.Weight) / 115.0;
                    compared[hand]++;
                    better[hand] += delta < 0 ? 1 : 0;
                    maps.Add(pair.Key);
                }
            }

            var least = Math.Min(compared[0], compared[1]);
            if (least < MinPairedMaps)
            {
                Say($"Only {maps.Count} map(s) played before and after.",
                    $"Needs {MinPairedMaps}. Replay some maps you played before.");
                return;
            }

            // Both hands together, weighted by how many maps each could actually be judged
            // on, since a note is one hand's or the other's.
            var accuracy = (gained[0] + gained[1]) / (compared[0] + compared[1]) * 100.0;
            // Named, and dated. "Before and after" begged the question of before and after
            // what: the split is the first entry in the journal, which is the moment this
            // mod first knew what the settings were, and a reader has no way to guess that.
            Say($"{maps.Count} maps played both before and since\n"
                + $"Replay Fit started recording, {Shown.Day(since.Value)}",
                $"Left  {Describe(moved[0] / compared[0])} ({better[0]}/{compared[0]})"
                + $"      Right  {Describe(moved[1] / compared[1])} ({better[1]}/{compared[1]})",
                $"About {accuracy:+0.00;-0.00}% accuracy");
        }

        private static string Describe(double metres)
        {
            var cm = Math.Abs(metres) * 100.0;
            return metres < 0 ? $"{cm:F1} cm closer" : $"{cm:F1} cm further out";
        }

        private static List<CutSample> Cuts(List<ReplayCuts.Extraction> runs, int hand)
        {
            var all = new List<CutSample>();
            foreach (var run in runs)
            {
                var cuts = hand == 0 ? run.Left.Cuts : run.Right.Cuts;
                if (cuts != null)
                {
                    all.AddRange(cuts);
                }
            }
            return all;
        }

        /// <summary>Baseline to show before the first recorded settings change.</summary>
        private const int BaselineDays = 45;

        /// <summary>
        /// Centring either side of the change, as a row of bars.
        /// </summary>
        /// <remarks>
        /// Not the whole history, which was the first attempt and could not work: fifteen
        /// months of play against eleven days of using this mod puts the entire mod era in
        /// two percent of the chart, which is less than one column. The bars showed a year of
        /// unrelated noise and one sliver of the thing they were drawn for.
        ///
        /// So the window starts a few weeks before the first recorded change and runs to the
        /// last run played. That is enough of a baseline to judge against and leaves every
        /// column with real play behind it.
        ///
        /// Plotted in centimetres from the note centre, inverted so a taller bar is the
        /// better one. It was a share of the accuracy points at first, which is the same fact
        /// in a unit nobody has a feel for: "64% of the accuracy points" said nothing about
        /// what was being drawn. Centimetres are what the rest of the tab already speaks in.
        ///
        /// Scaled between the best and worst columns rather than from zero, because the range
        /// of a real history is a couple of centimetres and from zero it is a flat line. That
        /// exaggerates, so both ends are printed underneath: the bars give the shape and the
        /// numbers give it its size.
        ///
        /// The cell width has to be at least the glyph width. These are full-width block
        /// characters, so squeezing them into a narrower cell to fit more columns does not
        /// make a denser chart, it makes the bars overlap and merge into one solid band.
        ///
        /// Which sets the trade the column count is really making. A bar is only as tall as
        /// its font, and the cells have to fit across the panel, so taller bars are bought
        /// with fewer of them.
        ///
        /// Not one for one, though: the ink of these glyphs is about two thirds of a cell, so
        /// the font can run a quarter wider than the cell before neighbours touch. Both
        /// numbers come from what has actually been seen -- a font one and a half times its
        /// cell merged into a solid band, the same size as its cell left obvious gaps. Sixteen
        /// columns of 5.2 carrying a 6.6 font is half again the height of twenty of 4.2, and
        /// costs three and a half days of play per column instead of three, which is still
        /// finer than the fortnight the change took.
        /// </remarks>
        private static string OverTime(List<ReplayCuts.Extraction> runs, DateTime? since)
        {
            const int Columns = 16;
            const string Blocks = "▁▂▃▅▆▇█";
            if (runs.Count < 20)
            {
                return "";
            }

            var first = DateTime.MaxValue;
            var last = DateTime.MinValue;
            foreach (var run in runs)
            {
                if (run.Played < first) first = run.Played;
                if (run.Played > last) last = run.Played;
            }
            if (since.HasValue)
            {
                var from = since.Value.AddDays(-BaselineDays);
                if (from > first)
                {
                    first = from;
                }
            }
            var width = Math.Max((last - first).TotalDays, 1);

            var buckets = new Centring[Columns];
            foreach (var run in runs)
            {
                if (run.Played < first)
                {
                    continue;
                }
                var at = (int)((run.Played - first).TotalDays / width * (Columns - 1));
                at = Mathf.Clamp(at, 0, Columns - 1);
                for (var hand = 0; hand < 2; hand++)
                {
                    var cuts = hand == 0 ? run.Left.Cuts : run.Right.Cuts;
                    if (cuts == null)
                    {
                        continue;
                    }
                    var m = Measure(cuts);
                    buckets[at].Weight += m.Weight;
                    buckets[at].Distance += m.Distance;
                }
            }

            var cm = new double[Columns];
            var best = double.MaxValue;
            var worst = double.MinValue;
            for (var i = 0; i < Columns; i++)
            {
                if (buckets[i].Weight <= 0)
                {
                    cm[i] = double.NaN;
                    continue;
                }
                cm[i] = buckets[i].Distance / buckets[i].Weight * 100.0;
                if (cm[i] < best) best = cm[i];
                if (cm[i] > worst) worst = cm[i];
            }
            if (worst <= best)
            {
                return "";
            }

            // Inverted: a shorter distance is the better result, and a chart whose good news
            // points downwards is read wrongly however it is labelled.
            var bar = new char[Columns];
            for (var i = 0; i < Columns; i++)
            {
                bar[i] = double.IsNaN(cm[i])
                    ? ' '
                    : Blocks[Mathf.Clamp(
                        (int)Math.Round((worst - cm[i]) / (worst - best) * (Blocks.Length - 1)),
                        0, Blocks.Length - 1)];
            }

            // Labels smaller than the bars, so the chart is what is looked at and the words
            // are there to say what it is.
            return "<size=45%>How close your cuts landed to the note centre, "
                   + "taller is better</size>\n"
                   + $"<mspace=5.2>{new string(bar)}</mspace>\n"
                   + $"<size=45%>{Shown.Day(first)} to {Shown.Day(last)}   "
                   + $"best {best:F1} cm, worst {worst:F1} cm</size>";
        }

        /// <summary>Runs on one setting before its residual is worth quoting.</summary>
        private const int MinRunsToJudge = 10;

        /// <summary>
        /// How far a recent setting is from what the play on it asked for.
        /// </summary>
        /// <remarks>
        /// The newest setting is usually the least useful one to report: a player who has
        /// just applied a fit has played nothing on it yet, and "2 runs so far" is accurate
        /// and worth nothing. So this walks back to the most recent setting with enough play
        /// behind it and says which one it is talking about.
        ///
        /// Least squares, which the search deliberately does not use: a cut forty centimetres
        /// out drags it, where the points the search maximises stopped caring at thirty. That
        /// is the wrong objective for choosing a setting and the right one for this, which is
        /// only ever comparing like with like and never picks a number anybody types in. It
        /// stays private here for that reason.
        /// </remarks>
        private static string Distance(
            List<ReplayCuts.Extraction> runs, List<OffsetJournal.Epoch> epochs)
        {
            if (epochs.Count == 0)
            {
                return "";
            }

            // Consecutive entries describing the same grip are one setting, not several. The
            // journal gains an entry whenever the platform helper changes its mind about the
            // legacy offset -- it reports the Index's -16.3 when the controllers are awake
            // and nothing when they are not -- so a fortnight on one setting can be a dozen
            // entries. Walking them raw found the newest with ten runs behind it and reported
            // on that, when forty-seven runs of the same grip sat directly above it.
            var starts = new List<int>();
            for (var i = 0; i < epochs.Count; i++)
            {
                if (i == 0 || !SameSetting(epochs[i - 1], epochs[i]))
                {
                    starts.Add(i);
                }
            }

            var newest = 0;
            for (var s = starts.Count - 1; s >= 0; s--)
            {
                var i = starts[s];
                var from = epochs[i].From;
                var to = s + 1 < starts.Count ? epochs[starts[s + 1]].From : DateTime.MaxValue;
                var during = new List<ReplayCuts.Extraction>();
                foreach (var run in runs)
                {
                    if (run.Played >= from && run.Played < to)
                    {
                        during.Add(run);
                    }
                }
                if (s == starts.Count - 1)
                {
                    newest = during.Count;
                }
                if (during.Count < MinRunsToJudge)
                {
                    continue;
                }

                var apart = new float[2];
                for (var hand = 0; hand < 2; hand++)
                {
                    apart[hand] = Mathf.Rad2Deg * Wanted(Cuts(during, hand)).magnitude;
                }
                // Which settings the figure is about, in so many words. It was "over 47
                // runs on the settings from 5 Sep: left wants 2.1 degrees more", which reads
                // as a correction still outstanding when it is a fact about settings the
                // player has already moved on from. Past tense, and the change said plainly.
                var current = s == starts.Count - 1;
                var line = current
                    ? $"Your current settings, over {during.Count} runs:"
                    : $"Measured on the settings from {Shown.Day(from)}, "
                      + $"over {during.Count} runs:";
                line += current
                    ? $"\nleft wants {apart[0]:F1}° more, right {apart[1]:F1}°"
                    : $"\nthose cuts wanted {apart[0]:F1}° more on the left, {apart[1]:F1}° on the right";
                if (!current)
                {
                    line += $"\nYou have changed settings since — {newest} run(s) on the new ones.";
                }
                return line;
            }

            return $"Only {newest} run(s) on your current settings, and not enough on any "
                   + "earlier one to judge it by.";
        }

        /// <summary>
        /// Whether two journal entries describe the same grip a player would recognise.
        /// </summary>
        /// <remarks>
        /// The typed numbers alone, deliberately. What else the entry carries -- which legacy
        /// offset the platform happened to report that minute -- is not something the player
        /// changed, and grouping on it splits one setting into a dozen.
        /// </remarks>
        private static bool SameSetting(OffsetJournal.Epoch a, OffsetJournal.Epoch b)
        {
            return a.LeftRotation == b.LeftRotation
                && a.RightRotation == b.RightRotation
                && a.LeftPosition == b.LeftPosition
                && a.RightPosition == b.RightPosition
                && a.AlternativeHandling == b.AlternativeHandling;
        }

        /// <summary>The turn these cuts are asking for, weighted by the combo multiplier.</summary>
        private static Vector2 Wanted(List<CutSample> cuts)
        {
            double axx = 0, axy = 0, ayy = 0, bx = 0, by = 0;
            foreach (var c in cuts)
            {
                double u = c.Lever * c.AcrossY;
                double v = -c.Lever * c.AcrossX;
                double w = c.Multiplier;
                axx += w * u * u;
                axy += w * u * v;
                ayy += w * v * v;
                // Against the negated distance, matching DistanceUnder: the turn wanted is
                // the one whose shift cancels the gap, not the one that reproduces it.
                bx -= w * u * c.Signed;
                by -= w * v * c.Signed;
            }
            var det = axx * ayy - axy * axy;
            if (Math.Abs(det) < 1e-12)
            {
                return Vector2.zero;
            }
            return new Vector2(
                (float)((ayy * bx - axy * by) / det),
                (float)((axx * by - axy * bx) / det));
        }
    }
}
