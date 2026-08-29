using System;
using System.Collections.Generic;
using UnityEngine;

namespace ControllerAutoAdjust
{
    /// <summary>
    /// Asks whether a run of sessions was really played on one set of settings.
    /// </summary>
    /// <remarks>
    /// Nothing in a replay records the offsets it was played on, so using a player's history
    /// rests on their grip not having moved across it. The player can be asked, and should
    /// be, but an answer about three months ago is a memory rather than a fact -- and getting
    /// it wrong does not fail loudly. It pools cuts from two different grips and fits a
    /// compromise that suits neither, while every number downstream still looks reasonable.
    ///
    /// The settings themselves are unrecoverable, but a *change* in them is not: the residual
    /// each session asks for shifts when the grip underneath it moves. So the claim becomes
    /// checkable. Where the sessions disagree, the history is cut there rather than averaged
    /// across.
    ///
    /// The threshold is deliberately shy. A false positive throws away real history and
    /// leaves the player waiting twenty runs for an answer they could have had; a false
    /// negative costs a fit that is somewhat off. Neither is free, but the first is worse and
    /// the second is caught later by the sessions after the journal starts.
    /// </remarks>
    internal static class ChangeDetector
    {
        /// <summary>Sessions either side of a split before it can be judged.</summary>
        private const int MinSessionsPerSide = 3;

        /// <summary>
        /// How many standard errors apart the two halves must sit.
        /// </summary>
        /// <remarks>
        /// Session-to-session spread of the fitted turn was measured at 1.1 to 2.6 degrees on
        /// unchanged settings, so ordinary variation is large and a shy threshold is what
        /// keeps that from reading as a change.
        /// </remarks>
        private const float Threshold = 5f;

        internal struct Session
        {
            public DateTime Day;
            public Vector2 Turn;
            public int Cuts;
        }

        internal struct Verdict
        {
            public bool Split;
            public DateTime At;
            public float Statistic;
            public float GapDegrees;
            public int Sessions;
        }

        internal static Verdict Scan(List<Session> sessions)
        {
            var verdict = new Verdict { Sessions = sessions.Count };
            if (sessions.Count < MinSessionsPerSide * 2)
            {
                return verdict;
            }
            sessions.Sort((a, b) => a.Day.CompareTo(b.Day));

            for (var k = MinSessionsPerSide; k <= sessions.Count - MinSessionsPerSide; k++)
            {
                var before = Summarise(sessions, 0, k);
                var after = Summarise(sessions, k, sessions.Count);

                // Pooled spread across both halves: a real change moves the mean, and a
                // player simply being erratic widens both halves without separating them.
                var n1 = k;
                var n2 = sessions.Count - k;
                var pooled = Mathf.Sqrt(
                    (before.Variance * (n1 - 1) + after.Variance * (n2 - 1)) / (n1 + n2 - 2));
                if (pooled < 1e-6f)
                {
                    continue;
                }
                var gap = (after.Mean - before.Mean).magnitude;
                var statistic = gap / (pooled * Mathf.Sqrt(1f / n1 + 1f / n2));

                if (statistic > verdict.Statistic)
                {
                    verdict.Statistic = statistic;
                    verdict.At = sessions[k].Day;
                    verdict.GapDegrees = gap * Mathf.Rad2Deg;
                }
            }
            verdict.Split = verdict.Statistic > Threshold;
            return verdict;
        }

        private struct Half
        {
            public Vector2 Mean;
            public float Variance;
        }

        private static Half Summarise(List<Session> sessions, int from, int to)
        {
            var mean = Vector2.zero;
            for (var i = from; i < to; i++)
            {
                mean += sessions[i].Turn;
            }
            mean /= to - from;

            var variance = 0f;
            for (var i = from; i < to; i++)
            {
                variance += (sessions[i].Turn - mean).sqrMagnitude;
            }
            // Over both components, so the spread is comparable to the gap it is judging.
            variance = to - from > 1 ? variance / (to - from - 1) / 2f : 0f;
            return new Half { Mean = mean, Variance = variance };
        }
    }
}
