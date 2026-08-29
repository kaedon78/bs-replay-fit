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
    }

    internal struct SearchResult
    {
        public Vector3 Setting;
        public Vector2 Turn;
        public float GainFraction;
        public int Cuts;
        public bool Found;
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
        internal static float DistanceUnder(CutSample c, Vector2 turn)
        {
            // Rotating the saber by a small turn moves the plane by reach * (tx*my - ty*mx);
            // a translation's effect would be flat in reach, which is what separates them.
            var moved = c.Signed - c.Lever * (turn.x * c.AcrossY - turn.y * c.AcrossX);
            return Mathf.Abs(moved);
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
            // Normal equations for signed ~ lever * (tx*my - ty*mx), weighted by multiplier.
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
                bx += w * u * c.Signed;
                by += w * v * c.Signed;
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
            int spanDegrees = 9)
        {
            var result = new SearchResult { Setting = currentSetting, Cuts = cuts.Count };
            if (cuts.Count == 0)
            {
                return result;
            }

            var baseline = Score(cuts, Vector2.zero);
            var worth = 0f;
            for (var i = 0; i < cuts.Count; i++)
            {
                worth += cuts[i].Multiplier * 115f;
            }

            var origin = new Vector3(
                Mathf.Round(currentSetting.x),
                Mathf.Round(currentSetting.y),
                Mathf.Round(currentSetting.z));

            var scored = new List<(Vector3 Setting, Vector2 Turn, float Score)>();
            var best = baseline;
            for (var dx = -spanDegrees; dx <= spanDegrees; dx++)
            {
                for (var dy = -spanDegrees; dy <= spanDegrees; dy++)
                {
                    for (var dz = -spanDegrees; dz <= spanDegrees; dz++)
                    {
                        var trial = origin + new Vector3(dx, dy, dz);
                        var turn = OffsetMath.TurnVector(
                            currentSetting, trial, left, legacyRotation, alternativeHandling);
                        if (turn.magnitude * Mathf.Rad2Deg > capDegrees + 1e-4f)
                        {
                            continue;
                        }
                        var score = Score(cuts, turn);
                        scored.Add((trial, turn, score));
                        if (score > best)
                        {
                            best = score;
                        }
                    }
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
            result.GainFraction = worth > 0f ? (Score(cuts, bestTurn) - baseline) / worth : 0f;
            result.Found = true;
            return result;
        }
    }
}
