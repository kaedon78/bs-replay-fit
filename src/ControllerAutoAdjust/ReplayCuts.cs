using System;
using System.Collections.Generic;
using UnityEngine;

namespace ControllerAutoAdjust
{
    /// <summary>
    /// Turns a replay into the same per-cut numbers the live recorder produces.
    /// </summary>
    /// <remarks>
    /// A replay does not record where the notes were, so their centres have to be rebuilt:
    /// column and row are fixed by Beat Saber's grid, and depth comes from the cut's timing
    /// error once the map's note speed is fitted. Two free numbers over hundreds of cuts.
    ///
    /// This is the part the live path does not need and the part most able to be quietly
    /// wrong, so both traps found offline are kept explicit. Chain links do not sit at the
    /// grid cell their packed id names, so they are left out entirely: for a while they were
    /// only kept out of the depth fit, and the samples happily used the same wrong centre.
    /// And the timing
    /// deviation runs positive when the cut was *early*, opposite to how it reads -- the fit
    /// discovers that on its own by returning a negative speed, which is why the sign is
    /// fitted rather than assumed.
    /// </remarks>
    internal static class ReplayCuts
    {
        private static readonly float[] ColumnX = { -0.9f, -0.3f, 0.3f, 0.9f };
        private static readonly float[] RowY = { 0.85f, 1.40f, 1.90f };

        /// <summary>Chain links sit at interpolated positions, not their named grid cell.</summary>
        private const int ChainLinkScoringType = 8;

        internal struct HandCuts
        {
            public List<CutSample> Cuts;
            public float NoteSpeed;
            public float MedianResidual;
        }

        internal class Extraction
        {
            /// <summary>When it was played, for grouping sessions.</summary>
            public System.DateTime Played;

            /// <summary>False when the epoch was assumed rather than recorded.</summary>
            public bool FromJournal;

            /// <summary>The journalled settings, meaningful only when FromJournal.</summary>
            public OffsetJournal.Epoch Epoch;
            public string Song = "";
            public string Difficulty = "";
            public int Score;
            public bool Clean;
            public HandCuts Left;
            public HandCuts Right;
        }

        internal static Extraction Extract(Bsor.Replay replay, int minCuts = 30)
        {
            var result = new Extraction
            {
                Song = replay.Info.SongName,
                Difficulty = replay.Info.Difficulty,
                Score = replay.Info.Score,
                Clean = replay.Info.Clean,
            };

            var good = new List<Bsor.Note>();
            foreach (var n in replay.Notes)
            {
                if (n.HasCut && n.Event == Bsor.NoteEvent.Good)
                {
                    good.Add(n);
                }
            }
            good.Sort((a, b) => a.EventTime.CompareTo(b.EventTime));
            var multipliers = Multipliers(good.Count);

            result.Left = ForHand(replay, good, multipliers, 0, minCuts);
            result.Right = ForHand(replay, good, multipliers, 1, minCuts);
            return result;
        }

        /// <summary>
        /// Combo multiplier per note of an unbroken run.
        /// </summary>
        /// <remarks>
        /// The ramp is offset by one against the obvious reading: the note completing a tier
        /// is already scored at the new multiplier. Verified by rebuilding a replay's final
        /// score to the point. Computed over good cuts only, so a run with misses is
        /// approximated -- it only weights the fit, and the weights are nearly all 8 anyway.
        /// </remarks>
        private static int[] Multipliers(int count)
        {
            var m = new int[count];
            for (var i = 0; i < count; i++)
            {
                var combo = i + 2;
                m[i] = combo <= 2 ? 1 : combo <= 6 ? 2 : combo <= 14 ? 4 : 8;
            }
            return m;
        }

        private static HandCuts ForHand(
            Bsor.Replay replay, List<Bsor.Note> good, int[] multipliers, int colour, int minCuts)
        {
            var result = new HandCuts
            {
                Cuts = new List<CutSample>(),
            };

            // Chain links are dropped here rather than later, so the minimum-cuts test
            // counts what will actually be used. Excluding them further down left a hand
            // that cleared the threshold only on notes that were then thrown away.
            var index = new List<int>();
            for (var i = 0; i < good.Count; i++)
            {
                if (good[i].Colour == colour
                    && good[i].ScoringType != ChainLinkScoringType)
                {
                    index.Add(i);
                }
            }
            if (index.Count < minCuts || replay.FrameCount < 2)
            {
                return result;
            }

            var n = index.Count;
            var normal = new Vector3[n];
            var point = new Vector3[n];
            var reported = new float[n];
            var drift = new float[n];
            var xy = new Vector2[n];

            for (var i = 0; i < n; i++)
            {
                var note = good[index[i]];
                normal[i] = note.Cut.CutNormal.normalized;
                point[i] = note.Cut.CutPoint;
                reported[i] = note.Cut.CutDistanceToCenter;
                drift[i] = note.Cut.TimeDeviation;
                xy[i] = new Vector2(
                    ColumnX[Mathf.Clamp(note.Column, 0, 3)],
                    RowY[Mathf.Clamp(note.Row, 0, 2)]);
            }

            FitDepthAndSpeed(normal, point, reported, drift, xy,
                             out var depth, out var speed);
            result.NoteSpeed = speed;

            var residuals = new List<float>(n);
            var positions = colour == 0 ? replay.LeftHand : replay.RightHand;
            var rotations = colour == 0 ? replay.LeftRotation : replay.RightRotation;

            for (var i = 0; i < n; i++)
            {
                var centre = Centre(xy[i], drift[i], depth, speed);
                var raw = Vector3.Dot(centre - point[i], normal[i]);
                residuals.Add(Mathf.Abs(Mathf.Abs(raw) - reported[i]));

                // The reconstruction supplies the side, the game supplies the distance. Only
                // the side needs rebuilding: which of the two the note centre sits on is a
                // sign, robust to the centimetre of error the reconstruction carries, while
                // the distance is recorded exactly and is what the accuracy term is scored
                // on. Using our own magnitude threw that away and cost about 10 mm a cut
                // against effects a few times that size.
                var signed = raw < 0f ? -reported[i] : reported[i];

                var when = good[index[i]].EventTime;
                var pose = Slerp(replay.FrameTimes, replay.FrameCount, rotations, when);
                var grip = Lerp(replay.FrameTimes, replay.FrameCount, positions, when);
                var across = Quaternion.Inverse(pose) * normal[i];
                var blade = pose * Vector3.forward;

                result.Cuts.Add(new CutSample
                {
                    Hand = colour,
                    Signed = signed,
                    AcrossX = across.x,
                    AcrossY = across.y,
                    // Measured to where the blade crossed, which the replay records, and not
                    // to the note's centre, which is rebuilt. The centre's depth comes from
                    // speed times timing error, and at seventeen metres a second a twenty
                    // millisecond deviation puts it a third of a metre further down the lane.
                    // That is true of the note and false of the moment arm, and it does not
                    // show up in the residual check: that measures error along the cut
                    // normal, which for a vertical swing is horizontal, while the depth error
                    // is almost at right angles to it. Three cuts in ten came out with a
                    // lever no sabre could have, some of them behind the hand.
                    Lever = Vector3.Dot(point[i] - grip, blade),
                    Multiplier = multipliers[index[i]],
                    SincePrevious = i == 0 ? 0f : when - good[index[i - 1]].EventTime,
                });
            }

            residuals.Sort();
            result.MedianResidual = residuals[residuals.Count / 2];
            return result;
        }

        private static Vector3 Centre(Vector2 xy, float drift, float depth, float speed) =>
            new Vector3(xy.x, xy.y, depth - speed * drift);

        private static float Cost(
            Vector3[] normal, Vector3[] point, float[] reported, float[] drift,
            Vector2[] xy, float depth, float speed)
        {
            var total = 0f;
            for (var i = 0; i < normal.Length; i++)
            {
                var gap = Mathf.Abs(Vector3.Dot(Centre(xy[i], drift[i], depth, speed) - point[i],
                                                normal[i]));
                var e = gap - reported[i];
                total += e * e;
            }
            return total;
        }

        /// <summary>
        /// Fit the note plane's depth and the map's note speed.
        /// </summary>
        /// <remarks>
        /// A coarse sweep then a refinement, rather than a gradient method. The speed's sign
        /// is not known in advance -- the timing deviation's convention runs the opposite way
        /// to its name -- and a search that brackets both signs cannot be walked into the
        /// wrong basin by a bad starting guess.
        /// </remarks>
        private static void FitDepthAndSpeed(
            Vector3[] normal, Vector3[] point, float[] reported, float[] drift,
            Vector2[] xy, out float depth, out float speed)
        {
            depth = 0f;
            speed = 0f;
            var best = float.MaxValue;

            for (var d = -2f; d <= 3f; d += 0.1f)
            {
                for (var v = -40f; v <= 40f; v += 1f)
                {
                    var cost = Cost(normal, point, reported, drift, xy, d, v);
                    if (cost < best)
                    {
                        best = cost;
                        depth = d;
                        speed = v;
                    }
                }
            }

            var d0 = depth;
            var v0 = speed;
            for (var d = d0 - 0.1f; d <= d0 + 0.1f; d += 0.005f)
            {
                for (var v = v0 - 1f; v <= v0 + 1f; v += 0.05f)
                {
                    var cost = Cost(normal, point, reported, drift, xy, d, v);
                    if (cost < best)
                    {
                        best = cost;
                        depth = d;
                        speed = v;
                    }
                }
            }
        }

        /// <summary>
        /// The frame pair straddling a moment, and how far between them it falls.
        /// </summary>
        /// <remarks>
        /// <paramref name="count"/> rather than the array's length: the frame arrays are
        /// reused between replays and are as long as the longest one seen, so their tail
        /// holds another song's poses. A binary search over that returns a real-looking answer
        /// from the wrong replay.
        /// </remarks>
        private static int Bracket(float[] times, int count, float when, out float f)
        {
            var i = Array.BinarySearch(times, 0, count, when);
            if (i < 0)
            {
                i = ~i - 1;
            }
            i = Mathf.Clamp(i, 0, count - 2);
            var span = Mathf.Max(times[i + 1] - times[i], 1e-9f);
            f = Mathf.Clamp01((when - times[i]) / span);
            return i;
        }

        private static Vector3 Lerp(float[] times, int count, Vector3[] values, float when)
        {
            var i = Bracket(times, count, when, out var f);
            return Vector3.Lerp(values[i], values[i + 1], f);
        }

        private static Quaternion Slerp(float[] times, int count, Quaternion[] values, float when)
        {
            // Frames land about 7 ms apart, so the arc between neighbours is under a degree
            // and a normalised lerp is within rounding of the real slerp. Matched to the
            // offline implementation deliberately, so the two can be compared.
            var i = Bracket(times, count, when, out var f);
            var a = values[i];
            var b = values[i + 1];
            if (a.x * b.x + a.y * b.y + a.z * b.z + a.w * b.w < 0f)
            {
                b = new Quaternion(-b.x, -b.y, -b.z, -b.w);
            }
            var q = new Quaternion(
                a.x + (b.x - a.x) * f,
                a.y + (b.y - a.y) * f,
                a.z + (b.z - a.z) * f,
                a.w + (b.w - a.w) * f);
            var norm = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
            return new Quaternion(q.x / norm, q.y / norm, q.z / norm, q.w / norm);
        }
    }
}
