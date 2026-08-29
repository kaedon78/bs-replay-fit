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

            Plugin.Log.Info(ok ? "search self-test: PASS" : "search self-test: FAIL");
            return ok;
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
                    // Arranged so that applying `wanted` leaves only the residual.
                    Signed = residual + lever * (wanted.x * my - wanted.y * mx),
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
