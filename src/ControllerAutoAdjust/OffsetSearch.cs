using System.Collections.Generic;
using UnityEngine;

namespace ControllerAutoAdjust
{
    /// <summary>One scored cut, reduced to what an offset can act on.</summary>
    internal struct CutSample
    {
        public int Hand;
        public float Signed;
        public float AcrossX;
        public float AcrossY;
        public float Lever;
        public int Multiplier;

        /// <summary>
        /// Seconds since this hand's previous cut; zero for the first of a run.
        /// </summary>
        /// <remarks>
        /// Not used by the fit. It is here because how fast a hand is being reversed is a
        /// candidate explanation for the scatter the fit cannot reach, and testing that needs
        /// the gap held against cuts from the same map rather than across maps of different
        /// difficulty.
        /// </remarks>
        public float SincePrevious;
    }

    internal struct SearchResult
    {
        public Vector3 Setting;
        public Vector2 Turn;
        public float GainFraction;
        public int Cuts;
        public bool Found;

        /// <summary>How much of the grid was actually distinct, for judging the dedup.</summary>
        public int Candidates;
        public int DistinctTurns;
    }

    /// <summary>
    /// Finds the whole-degree setting that wins the most points on the cuts already played.
    /// </summary>
    /// <remarks>
    /// The settings screen takes integers, so this searches the integer triples themselves
    /// rather than rounding a continuous fit — half a degree is nearly a centimetre of blade
    /// at the distance notes are struck, and rounding is not a small change at that scale.
    ///
    /// Only the two directions across the blade can be identified at all. The cut plane
    /// contains the blade, so the plane normal has no component along it, and sliding the
    /// saber along its own axis cannot change any cut distance. The search reports what the
    /// geometry supports rather than inventing a third number.
    ///
    /// Points are what is maximised, not centring. Least squares would let a single cut 40 cm
    /// out drag the answer around, where the 15-point term stopped caring about that cut at
    /// 30 cm.
    ///
    /// Maximising them is the same thing as maximising accuracy. The gain is reported over
    /// the most those same cuts could have scored, and that ceiling does not move with the
    /// offset, so the ratio is the change in accuracy percent rather than a count of points.
    /// What the search cannot see is the other hundred points a note carries: the swing
    /// angles before and after the cut. It assumes a few degrees of grip does not move them,
    /// which holds while a swing clears both thresholds comfortably and is worth measuring
    /// rather than believing.
    /// </remarks>
    internal static class OffsetSearch
    {
        /// <summary>Beyond this the accuracy term is already zero.</summary>
        internal const float FullAccuracyMetres = 0.30f;

        /// <summary>
        /// How much of the available gain a setting may give up and still count as tied,
        /// with the tie going to whichever is closest to the player's current grip.
        /// </summary>
        /// <remarks>
        /// Measured against the gain, not against the total score. Scaled to the total this
        /// was 0.02%, which sounds negligible and is roughly a tenth of the whole prize --
        /// the self-test caught it immediately, giving up two degrees of Y to sit one degree
        /// nearer in Z. The point of the tie-break is to stop the search reporting a Z it
        /// does not believe in, not to talk it out of the answer.
        /// </remarks>
        internal const float TieTolerance = 0.02f;

        /// <summary>
        /// The 15-point term, rounded as the game rounds it.
        /// </summary>
        /// <remarks>
        /// Verified offline by rebuilding a replay's final score to the point, 582,075
        /// against 582,075. Getting the rounding wrong costs about 0.13%, which reads as a
        /// rounding quibble and is actually a different model.
        /// </remarks>
        internal static float AccuracyPoints(float distance)
        {
            return Mathf.Round(15f * (1f - Mathf.Clamp01(distance / FullAccuracyMetres)));
        }

        /// <summary>Distance from centre this cut would have had, under a saber-frame turn.</summary>
        /// <remarks>
        /// The sign is the whole content of this function and was wrong for a long time.
        /// Turning the saber by a small local rotation t moves a point a distance L along the
        /// blade by <c>t x (0,0,L)</c>, which is <c>L(ty, -tx, 0)</c>; along the cut normal m
        /// that is <c>L(ty*mx - tx*my)</c>. The cut point moves and the note does not, so the
        /// gap between them changes by the negative of that, leaving a plus here.
        ///
        /// Subtracting instead returns the exact negative of the right answer, because the
        /// shift is linear in the turn. It does not look like a failure: the search still
        /// converges, still reports a gain, and recommends moving the grip as far the wrong
        /// way as it should have moved the right way. Nothing internal can catch it, which is
        /// why <see cref="SearchSelfTest"/> now builds a cut by moving a saber in three
        /// dimensions and checks this against it.
        /// </remarks>
        internal static float DistanceUnder(CutSample c, Vector2 turn)
        {
            var moved = c.Signed + c.Lever * (turn.x * c.AcrossY - turn.y * c.AcrossX);
            return Mathf.Abs(moved);
        }

        /// <summary>
        /// The same sum over flat arrays, which is the form the search runs millions of times.
        /// </summary>
        /// <remarks>
        /// Arrays rather than <c>IReadOnlyList</c>, which costs an interface dispatch per
        /// element when the element count is every cut times every candidate. The per-cut
        /// terms are folded in advance too, since none of them depend on the candidate.
        /// </remarks>
        private static float ScoreFlat(
            float[] signed, float[] alongY, float[] alongX, float[] weight, Vector2 turn)
        {
            var total = 0f;
            for (var i = 0; i < signed.Length; i++)
            {
                var moved = signed[i] + (turn.x * alongY[i] + turn.y * alongX[i]);
                total += weight[i] * AccuracyPoints(moved < 0f ? -moved : moved);
            }
            return total;
        }

        /// <summary>Weighted accuracy points these cuts would have scored under a turn.</summary>
        internal static float Score(IReadOnlyList<CutSample> cuts, Vector2 turn)
        {
            var total = 0f;
            for (var i = 0; i < cuts.Count; i++)
            {
                total += cuts[i].Multiplier * AccuracyPoints(DistanceUnder(cuts[i], turn));
            }
            return total;
        }

        /// <summary>
        /// The turn these cuts are asking for, by weighted least squares.
        /// </summary>
        /// <remarks>
        /// A continuous estimate, used where the question is "what is this group's residual"
        /// rather than "which setting should be typed" -- comparing sessions to each other,
        /// where a grid search's whole-degree steps would quantise away the differences being
        /// looked for. Least squares is the wrong objective for choosing a setting, because a
        /// cut 40 cm out drags it while the 15-point term stopped caring at 30, but for
        /// comparing like with like that bias is the same in every session.
        /// </remarks>
        internal static Vector2 FitTurn(IReadOnlyList<CutSample> cuts)
        {
            // Normal equations for -signed ~ lever * (tx*my - ty*mx), weighted by multiplier.
            double axx = 0, axy = 0, ayy = 0, bx = 0, by = 0;
            for (var i = 0; i < cuts.Count; i++)
            {
                var c = cuts[i];
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
            if (System.Math.Abs(det) < 1e-12)
            {
                return Vector2.zero;
            }
            return new Vector2(
                (float)((ayy * bx - axy * by) / det),
                (float)((axx * by - axy * bx) / det));
        }

        /// <summary>
        /// The best integer setting within <paramref name="capDegrees"/> of the current one.
        /// </summary>
        internal static SearchResult Search(
            IReadOnlyList<CutSample> cuts,
            Vector3 currentSetting,
            bool left,
            Vector3 legacyRotation,
            bool alternativeHandling,
            float capDegrees = 5f,
            int spanDegrees = 9,
            System.Action<float> onProgress = null)
        {
            var result = new SearchResult { Setting = currentSetting, Cuts = cuts.Count };
            if (cuts.Count == 0)
            {
                return result;
            }

            var worth = 0f;
            var maxLever = 0f;
            for (var i = 0; i < cuts.Count; i++)
            {
                worth += cuts[i].Multiplier * 115f;
                maxLever = Mathf.Max(maxLever, Mathf.Abs(cuts[i].Lever));
            }

            // Flattened, and pruned of cuts that no candidate can move into scoring range. A
            // turn shifts a cut by at most lever times the turn's magnitude -- the across-blade
            // normal is a unit vector, so that product bounds it -- and anything already
            // further out than the 30 cm cut-off plus that bound scores zero under every
            // candidate alike. Removing them takes the same constant out of every score
            // instead of changing any comparison between them.
            var reachable = maxLever * capDegrees * Mathf.Deg2Rad;
            var beyond = FullAccuracyMetres + reachable;
            var count = 0;
            for (var i = 0; i < cuts.Count; i++)
            {
                if (Mathf.Abs(cuts[i].Signed) <= beyond)
                {
                    count++;
                }
            }

            var signed = new float[count];
            var alongY = new float[count];
            var alongX = new float[count];
            var weight = new float[count];
            var at = 0;
            for (var i = 0; i < cuts.Count; i++)
            {
                var c = cuts[i];
                if (Mathf.Abs(c.Signed) > beyond)
                {
                    continue;
                }
                signed[at] = c.Signed;
                alongY[at] = c.Lever * c.AcrossY;
                alongX[at] = -c.Lever * c.AcrossX;
                weight[at] = c.Multiplier;
                at++;
            }

            var baseline = ScoreFlat(signed, alongY, alongX, weight, Vector2.zero);

            var origin = new Vector3(
                Mathf.Round(currentSetting.x),
                Mathf.Round(currentSetting.y),
                Mathf.Round(currentSetting.z));

            var scored = new List<(Vector3 Setting, Vector2 Turn, float Score)>();
            for (var dx = -spanDegrees; dx <= spanDegrees; dx++)
            {
                for (var dy = -spanDegrees; dy <= spanDegrees; dy++)
                {
                    for (var dz = -spanDegrees; dz <= spanDegrees; dz++)
                    {
                        var trial = origin + new Vector3(dx, dy, dz);
                        var turn = OffsetMath.TurnVector(
                            currentSetting, trial, left, legacyRotation, alternativeHandling);
                        if (turn.magnitude * Mathf.Rad2Deg <= capDegrees + 1e-4f)
                        {
                            scored.Add((trial, turn, 0f));
                        }
                    }
                }
            }

            // One sweep per *distinct* turn. Many integer triples describe the same rotation
            // -- Z is very nearly free, so its entire range collapses to one turn -- and
            // scoring each separately repeats an identical hundred-thousand-cut sweep about
            // twenty times for one answer. Grouping on the turn that comes out, rather than
            // on which axis is assumed degenerate, keeps the saving wherever the geometry
            // happens to put it.
            var best = baseline;
            var byTurn = new Dictionary<long, float>();
            var done = 0;
            var nextReport = 0;
            for (var i = 0; i < scored.Count; i++)
            {
                if (onProgress != null && ++done >= nextReport)
                {
                    nextReport = done + Mathf.Max(scored.Count / 20, 1);
                    onProgress((float)done / scored.Count);
                }

                var turn = scored[i].Turn;
                // Quantised to a thousandth of a degree. Two turns closer than that cannot
                // move any cut far enough to change a term that is rounded to whole points.
                var key = ((long)Mathf.RoundToInt(turn.x * 57295.8f) << 32)
                          ^ (uint)Mathf.RoundToInt(turn.y * 57295.8f);
                if (!byTurn.TryGetValue(key, out var score))
                {
                    score = ScoreFlat(signed, alongY, alongX, weight, turn);
                    byTurn[key] = score;
                }
                scored[i] = (scored[i].Setting, turn, score);
                if (score > best)
                {
                    best = score;
                }
            }

            // Among settings that score the same, take the one nearest what the player
            // already has. The Z field is very nearly free -- composed after the grip's large
            // X tilt it is mostly roll about the blade, and roll cannot move a cut plane at
            // all -- so the search will happily return a Z seven degrees away that changes
            // the actual rotation by a fifth of a degree. Reporting that as the answer is
            // false precision, and it asks the player to change something for nothing.
            var tolerance = TieTolerance * Mathf.Max(best - baseline, 0f);
            var bestSetting = currentSetting;
            var bestTurn = Vector2.zero;
            var nearest = float.MaxValue;
            foreach (var candidate in scored)
            {
                if (candidate.Score < best - tolerance)
                {
                    continue;
                }
                var change = (candidate.Setting - currentSetting).sqrMagnitude;
                if (change < nearest)
                {
                    nearest = change;
                    bestSetting = candidate.Setting;
                    bestTurn = candidate.Turn;
                }
            }

            result.Setting = bestSetting;
            result.Turn = bestTurn;
            result.GainFraction = worth > 0f
                ? (ScoreFlat(signed, alongY, alongX, weight, bestTurn) - baseline) / worth
                : 0f;
            result.Found = true;
            result.DistinctTurns = byTurn.Count;
            result.Candidates = scored.Count;
            return result;
        }
    }
}
