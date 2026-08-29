using System;
using System.Collections.Generic;
using UnityEngine;

namespace ControllerAutoAdjust
{
    /// <summary>
    /// Checks the search recovers an offset that was put there on purpose.
    /// </summary>
    /// <remarks>
    /// Real cuts cannot test this. They carry whatever offset the player actually has, which
    /// is the unknown being solved for, so agreeing with them proves only self-consistency.
    /// Cuts built around a chosen setting have a right answer to be wrong about.
    ///
    /// Both hands are tested, and that is the point rather than thoroughness. The left hand's
    /// mirror -- the game negates Y and Z of the typed rotation for that hand only -- is
    /// exactly what went wrong in the offline version of this, and it went wrong invisibly:
    /// a sign-flipped correction produces no error, just a setting that quietly does nothing.
    /// A round trip through the same conversion the search uses would have caught it in
    /// seconds.
    ///
    /// The third case has no offset at all. A search that finds one anyway is fitting noise,
    /// and the gain it reports would look exactly like a real finding.
    /// </remarks>
    internal static class SearchSelfTest
    {
        private const int Samples = 8000;

        /// <summary>Spread of cut distance that no setting can remove, from replay data.</summary>
        private const float ScatterMetres = 0.14f;

        internal static bool Run()
        {
            var current = new Vector3(47f, -5f, 0f);
            var legacy = Vector3.zero;
            var ok = true;

            ok &= Case("right hand", new Vector3(49f, -9f, -6f), current, false, legacy);
            ok &= Case("left hand (mirrored)", new Vector3(44f, -10f, -4f), current, true, legacy);
            ok &= Case("right, Valve legacy", new Vector3(50f, -7f, -3f), current, false,
                       new Vector3(-16.3f, 0f, 0f));
            ok &= NullCase("no offset present", current, true, legacy);

            ok &= Equivalent("Z only, right hand", new Vector3(49f, -9f, -6f),
                             new Vector3(49f, -9f, 2f), false);
            ok &= Equivalent("Z only, left hand", new Vector3(44f, -10f, -4f),
                             new Vector3(44f, -10f, 3f), true);
            ok &= Equivalent("one degree of Y is not free", new Vector3(49f, -9f, -6f),
                             new Vector3(49f, -8f, -6f), false, expectSame: false);

            ok &= MovesTheRightWay("a turned saber cuts where the geometry says, right", false);
            ok &= MovesTheRightWay("a turned saber cuts where the geometry says, left", true);

            Plugin.Log.Info(ok ? "search self-test: PASS" : "search self-test: FAIL");
            return ok;
        }

        /// <summary>
        /// Check the cut model against a saber actually moved in three dimensions.
        /// </summary>
        /// <remarks>
        /// Every other case here builds its cuts with the same functions it then verifies, so
        /// a sign error cancels and they all pass. One did, for the whole life of the offline
        /// version and the port of it: the predicted shift was subtracted where it should
        /// have been added, which returns the exact negative of the right answer, and a
        /// search that recommends moving the grip as far the wrong way as it should have gone
        /// the right way still converges and still reports a gain.
        ///
        /// So this builds a cut without asking the model anything. It puts a saber in the
        /// world under one setting, works out where the note sits relative to the blade,
        /// turns the saber to a second setting, and measures the gap again by rotating
        /// vectors. Only then does it ask <see cref="OffsetSearch.DistanceUnder"/> what it
        /// thinks, and the two have to agree.
        ///
        /// The tolerance is loose against the linearisation and tight against the failure.
        /// Over these turns the second-order term dropped by the model is worth about 3 mm
        /// at worst, against a shift of up to 30 mm; the sign this exists to catch inverts
        /// that shift and so lands about 60 mm out. Five millimetres is clear of the
        /// approximation and an order of magnitude inside the bug.
        /// </remarks>
        private static bool MovesTheRightWay(string name, bool left)
        {
            const float Tolerance = 0.005f;
            var rng = new System.Random(90210);
            var worst = 0f;
            var largestShift = 0f;

            for (var trial = 0; trial < 40; trial++)
            {
                // A root pose that is not the identity, since it has to cancel.
                var root = Quaternion.Euler(
                    Range(rng, -25f, 25f), Range(rng, -40f, 40f), Range(rng, -15f, 15f));
                var current = new Vector3(
                    Range(rng, 35f, 52f), Range(rng, -12f, 4f), Range(rng, -8f, 8f));
                var candidate = current + new Vector3(
                    Range(rng, -2f, 2f), Range(rng, -2f, 2f), Range(rng, -2f, 2f));

                var before = root * OffsetMath.Applied(current, left, Vector3.zero, true);
                var after = root * OffsetMath.Applied(candidate, left, Vector3.zero, true);

                // The grip stays put: only the rotation is being changed.
                var grip = new Vector3(Range(rng, -.3f, .3f), Range(rng, .8f, 1.3f),
                                       Range(rng, -.2f, .2f));
                var centre = grip + before * new Vector3(
                    Range(rng, -.06f, .06f), Range(rng, -.06f, .06f), Range(rng, .6f, 1.0f));

                // The cut plane contains the blade, so its normal lies across it, and it is
                // carried by the saber rather than fixed in the world.
                var across = new Vector3(
                    Range(rng, -1f, 1f), Range(rng, -1f, 1f), Range(rng, -.15f, .15f))
                    .normalized;

                var bladeBefore = before * Vector3.forward;
                var bladeAfter = after * Vector3.forward;
                var lever = Vector3.Dot(centre - grip, bladeBefore);

                var signedBefore = Vector3.Dot(
                    centre - grip - lever * bladeBefore, before * across);
                var signedAfter = Vector3.Dot(
                    centre - grip - lever * bladeAfter, after * across);

                var sample = new CutSample
                {
                    Signed = signedBefore,
                    AcrossX = across.x,
                    AcrossY = across.y,
                    Lever = lever,
                    Multiplier = 1,
                };
                var turn = OffsetMath.TurnVector(
                    current, candidate, left, Vector3.zero, true);
                var predicted = OffsetSearch.DistanceUnder(sample, turn);

                worst = Mathf.Max(worst, Mathf.Abs(predicted - Mathf.Abs(signedAfter)));
                largestShift = Mathf.Max(largestShift, Mathf.Abs(signedAfter - signedBefore));
            }

            var pass = worst <= Tolerance;
            Plugin.Log.Info(
                $"  {name}: worst error {worst * 1000f:F2} mm over 40 turns "
                + $"moving cuts by up to {largestShift * 1000f:F0} mm "
                + $"-> {(pass ? "ok" : "FAILED")}");
            return pass;
        }

        private static float Range(System.Random rng, float lo, float hi) =>
            lo + (float)rng.NextDouble() * (hi - lo);

        /// <summary>
        /// How much two settings actually differ, in the only terms that matter.
        /// </summary>
        /// <remarks>
        /// Z is nearly free and Y is not, and nothing about the typed triples says so. A
        /// grouping that reads them literally splits a history on changes that moved nothing.
        /// </remarks>
        private static bool Equivalent(
            string name, Vector3 a, Vector3 b, bool left, bool expectSame = true)
        {
            var turn = OffsetMath.TurnVector(a, b, left, Vector3.zero, true);
            var degrees = turn.magnitude * Mathf.Rad2Deg;
            var same = degrees <= OffsetMath.SameGripDegrees;
            var pass = same == expectSame;
            Plugin.Log.Info(
                $"  {name}: {a} vs {b} differ by {degrees:F2} deg" +
                $" -> {(pass ? "ok" : "FAILED")}");
            return pass;
        }

        private static bool Case(
            string name, Vector3 truth, Vector3 current, bool left, Vector3 legacy)
        {
            var wanted = OffsetMath.TurnVector(current, truth, left, legacy, true);
            var cuts = Build(wanted, new System.Random(20260829));
            var found = OffsetSearch.Search(cuts, current, left, legacy, true);

            var missDeg = (found.Turn - wanted).magnitude * Mathf.Rad2Deg;

            // Judged on the rotation, not on matching the triple. Many triples produce the
            // same rotation -- Z is nearly free -- so demanding the exact numbers back would
            // fail a correct search for returning a better-behaved answer.
            var pass = missDeg <= 1.0f;
            Plugin.Log.Info(
                $"  {name}: built from {truth}, recovered {found.Setting}" +
                $" — rotation off by {missDeg:F2} deg, gain {found.GainFraction:P3}" +
                $" -> {(pass ? "ok" : "FAILED")}");
            return pass;
        }

        private static bool NullCase(string name, Vector3 current, bool left, Vector3 legacy)
        {
            var cuts = Build(Vector2.zero, new System.Random(7));
            var found = OffsetSearch.Search(cuts, current, left, legacy, true);
            var strayDeg = found.Turn.magnitude * Mathf.Rad2Deg;

            // Some drift is inevitable -- the search will always find the noise's best
            // fitting offset. What matters is that it is small and worth almost nothing.
            var pass = strayDeg <= 1.5f && found.GainFraction <= 0.0015f;
            Plugin.Log.Info(
                $"  {name}: found {strayDeg:F2} deg worth {found.GainFraction:P3}" +
                $" -> {(pass ? "ok" : "FAILED")}");
            return pass;
        }

        /// <summary>
        /// Cuts as they would look if <paramref name="wanted"/> were the correction needed.
        /// </summary>
        private static List<CutSample> Build(Vector2 wanted, System.Random rng)
        {
            var cuts = new List<CutSample>(Samples);
            for (var i = 0; i < Samples; i++)
            {
                // Swing normals cluster rather than spread evenly, so this leans the same
                // way: an evenly covered circle would make the search's job easier than the
                // real one and hide a conditioning problem.
                var angle = (float)(rng.NextDouble() * Math.PI * 2.0);
                var mx = Mathf.Cos(angle);
                var my = Mathf.Sin(angle) * 1.6f;
                var norm = Mathf.Sqrt(mx * mx + my * my);
                mx /= norm;
                my /= norm;

                var lever = 1.0f + 0.2f * Gaussian(rng);
                var residual = ScatterMetres * Gaussian(rng);

                cuts.Add(new CutSample
                {
                    Hand = 0,
                    AcrossX = mx,
                    AcrossY = my,
                    Lever = lever,
                    Multiplier = 8,
                    // Arranged so that applying `wanted` leaves only the residual. The
                    // sign follows DistanceUnder, which adds the shift: a cut the turn is
                    // meant to fix must start displaced the other way.
                    Signed = residual - lever * (wanted.x * my - wanted.y * mx),
                });
            }
            return cuts;
        }

        private static float Gaussian(System.Random rng)
        {
            var u1 = 1.0 - rng.NextDouble();
            var u2 = rng.NextDouble();
            return (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }
    }
}
