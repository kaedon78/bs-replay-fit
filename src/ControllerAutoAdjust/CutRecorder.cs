using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ControllerAutoAdjust
{
    /// <summary>
    /// Records, per scored cut, the five numbers an offset can act on.
    /// </summary>
    /// <remarks>
    /// The offline version of this reconstructed each note's centre from the grid and a
    /// fitted note speed, which is where its two worst traps lived -- chain links are not at
    /// the grid cell their id names, and the replay's timing error runs positive when early,
    /// opposite to how it reads. Neither survives here: <c>NoteCutInfo.notePosition</c> is
    /// the note's actual centre, so the signed distance is exact rather than reconstructed.
    ///
    /// That gives a free self-check. The game reports <c>cutDistanceToCenter</c> for the same
    /// cut, and it must equal the magnitude of the signed distance computed here. Offline
    /// that check carried 5-10 mm of reconstruction error; here any disagreement at all means
    /// this file is wrong.
    ///
    /// The sign is the point of the exercise. An offset that rescues a cut which passed left
    /// of centre ruins one that passed right, so a magnitude alone -- which is all the game
    /// displays -- cannot say which way to move.
    /// </remarks>
    internal class CutRecorder : MonoBehaviour
    {
        private const float LookForControllerEvery = 1f;

        // Cuts are also flushed on this interval, not only when the song ends. A song
        // is minutes of accumulation and a crash would take all of it, which is the one
        // failure that costs data rather than time.
        private const float FlushEvery = 15f;
        private const int FlushAfterCuts = 200;

        private ScoreController _controller;
        private Saber _left;
        private Saber _right;
        private float _nextLook;
        private float _nextFlush;

        /// <summary>Geometry captured at the cut, waiting for the multiplier that only
        /// arrives once the follow-through has been scored.</summary>
        private readonly Dictionary<ScoringElement, Pending> _inFlight =
            new Dictionary<ScoringElement, Pending>();

        // File.AppendAllText(Encoding.UTF8) writes a byte-order mark, and a BOM ahead
        // of the first line makes that line fail to parse as JSON while every later
        // line is fine -- which reads as one corrupt record, not an encoding choice.
        private static readonly UTF8Encoding NoBom = new UTF8Encoding(false);

        private readonly List<string> _lines = new List<string>();
        private string _sessionFile;

        // Told apart deliberately: no cuts at all and plenty of cuts that all scored
        // badly look identical in the output file, and have opposite fixes.
        private int _scored;
        private int _good;

        private struct Pending
        {
            public float Time;
            public int Hand, Column, Row, Direction, ScoringType;
            public float Signed, AcrossX, AcrossY, Lever, ReportedDistance;
        }

        private void Update()
        {
            if (Time.unscaledTime >= _nextFlush)
            {
                _nextFlush = Time.unscaledTime + FlushEvery;
                Flush();
            }
            if (Time.unscaledTime < _nextLook)
            {
                return;
            }
            _nextLook = Time.unscaledTime + LookForControllerEvery;

            // No Zenject dependency for this: one lookup a second while idle is cheaper than
            // taking on SiraUtil before the shape of the mod is settled.
            var found = Live<ScoreController>();
            if (found == _controller)
            {
                return;
            }
            Detach();
            if (found != null)
            {
                Attach(found);
            }
        }

        private static T Live<T>() where T : UnityEngine.Object
        {
            // FindObjectsOfTypeAll also returns prefabs and assets, which are not in a scene.
            return Resources.FindObjectsOfTypeAll<T>()
                .FirstOrDefault(o => o is Component c && c.gameObject.scene.isLoaded);
        }

        internal const string MarkerName = "record-cuts.on";

        /// <summary>
        /// Whether to record cuts as they are played.
        /// </summary>
        /// <remarks>
        /// Off unless asked for. This exists so the mod still has a source of cuts if replays
        /// ever stop being available, and while they are available nothing reads what it
        /// writes -- so left on it hooks the scoring path and writes a file every few seconds
        /// of every session, for data no one wants. A player should not pay that for a spare
        /// tyre.
        /// </remarks>
        internal static bool Enabled() =>
            System.IO.File.Exists(System.IO.Path.Combine(Paths.DataDir, MarkerName));

        private void Attach(ScoreController controller)
        {
            if (!Enabled())
            {
                return;
            }
            var sabers = Resources.FindObjectsOfTypeAll<Saber>()
                .Where(s => s.gameObject.scene.isLoaded)
                .ToList();
            _left = sabers.FirstOrDefault(s => s.saberType == SaberType.SaberA);
            _right = sabers.FirstOrDefault(s => s.saberType == SaberType.SaberB);
            if (_left == null || _right == null)
            {
                Plugin.Log.Warn("found a ScoreController but not both sabers; not recording");
                return;
            }

            _controller = controller;
            _controller.scoringForNoteStartedEvent += OnCut;
            _controller.scoringForNoteFinishedEvent += OnScored;
            _sessionFile = null;
            Plugin.Log.Info("recording cuts");
        }

        private void Detach()
        {
            if (_controller != null)
            {
                _controller.scoringForNoteStartedEvent -= OnCut;
                _controller.scoringForNoteFinishedEvent -= OnScored;
                _controller = null;
            }
            _inFlight.Clear();
            Flush();
            if (_scored > 0)
            {
                Plugin.Log.Info($"scored notes {_scored}, of which good cuts {_good}");
                _scored = _good = 0;
            }
        }

        /// <summary>
        /// Guarded because it is called by the game, not by us.
        /// </summary>
        /// <remarks>
        /// An exception raised inside a game callback does not stay in this mod. It unwinds
        /// through whatever called it, which here is the scoring path a note goes through
        /// every time it is hit. The same mistake in the anchor handler left a player unable
        /// to use their controllers, so nothing that happens in here is allowed out.
        /// </remarks>
        private void OnCut(ScoringElement element)
        {
            try
            {
                Cut(element);
            }
            catch (Exception e)
            {
                Plugin.Log.Warn($"could not record a cut: {e.Message}");
            }
        }

        private void OnScored(ScoringElement element)
        {
            try
            {
                Scored(element);
            }
            catch (Exception e)
            {
                Plugin.Log.Warn($"could not score a cut: {e.Message}");
            }
        }

        private void Cut(ScoringElement element)
        {
            _scored++;
            if (!(element is GoodCutScoringElement good))
            {
                return;
            }
            _good++;
            try
            {
                var cut = good.cutScoreBuffer.noteCutInfo;
                var saber = cut.saberType == SaberType.SaberA ? _left : _right;
                if (saber == null)
                {
                    return;
                }

                var normal = cut.cutNormal.normalized;
                var toCentre = cut.notePosition - cut.cutPoint;

                // Where the blade was, in the saber's own frame: the offset is expressed in
                // that frame, so the plane normal has to be too.
                var rotation = saber.transform.rotation;
                var across = Quaternion.Inverse(rotation) * normal;
                var blade = (saber.saberBladeTopPos - saber.saberBladeBottomPos).normalized;

                var note = element.noteData;
                _inFlight[element] = new Pending
                {
                    Time = note.time,
                    Hand = cut.saberType == SaberType.SaberA ? 0 : 1,
                    Column = note.lineIndex,
                    Row = (int)note.noteLineLayer,
                    Direction = (int)note.cutDirection,
                    ScoringType = (int)note.scoringType,
                    Signed = Vector3.Dot(toCentre, normal),
                    AcrossX = across.x,
                    AcrossY = across.y,
                    // How far along the blade the note sat, which is what scales a rotation's
                    // effect where a translation's is flat.
                    Lever = Vector3.Dot(cut.notePosition - saber.transform.position, blade),
                    ReportedDistance = cut.cutDistanceToCenter,
                };
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"capture failed: {e.Message}");
            }
        }

        private void Scored(ScoringElement element)
        {
            if (!_inFlight.TryGetValue(element, out var p))
            {
                return;
            }
            _inFlight.Remove(element);

            // Built explicitly rather than with a format string. `{11:F6}}}` looks like a
            // value followed by two closing braces and is not: .NET reads the first `}}` as
            // an escaped brace *inside* the format specifier, making it `F6}`, a custom
            // format whose characters render as themselves. The field came out as the
            // literal text "F6" with no exception raised.
            var line = new StringBuilder(220)
                .Append("{\"t\":").Append(Num(p.Time, "F4"))
                .Append(",\"hand\":").Append(p.Hand)
                .Append(",\"col\":").Append(p.Column)
                .Append(",\"row\":").Append(p.Row)
                .Append(",\"dir\":").Append(p.Direction)
                .Append(",\"st\":").Append(p.ScoringType)
                .Append(",\"signed\":").Append(Num(p.Signed, "F6"))
                .Append(",\"mx\":").Append(Num(p.AcrossX, "F6"))
                .Append(",\"my\":").Append(Num(p.AcrossY, "F6"))
                .Append(",\"lever\":").Append(Num(p.Lever, "F5"))
                .Append(",\"mult\":").Append(element.multiplier)
                .Append(",\"dist\":").Append(Num(p.ReportedDistance, "F6"))
                .Append('}')
                .ToString();
            _lines.Add(line);

            if (_lines.Count >= FlushAfterCuts)
            {
                Flush();
            }
        }

        /// <summary>
        /// The settings these cuts were recorded under, written once per file.
        /// </summary>
        /// <remarks>
        /// A cut only means something alongside the offset in force when it was made. Offline
        /// this had to be inferred from settings not having changed for two months, which
        /// stops working the moment anything adjusts them -- every session becomes its own
        /// epoch and none has enough cuts to fit. Stamped here, sessions can be pooled or
        /// separated on fact.
        /// </remarks>
        private static string Header()
        {
            if (!OffsetState.TryRead(out var r))
            {
                return "{\"header\":1,\"offsets\":\"unavailable\"}";
            }
            return new StringBuilder(320)
                .Append("{\"header\":1,\"alt\":").Append(r.AlternativeHandling ? "true" : "false")
                .Append(",\"helper\":\"").Append(r.PlatformHelper).Append('"')
                .Append(",\"legacyValid\":").Append(r.LegacyValid ? "true" : "false")
                .Append(",\"legacyRot\":").Append(Vec(r.LegacyRotation))
                .Append(",\"leftRot\":").Append(Vec(r.Left.TypedRotation))
                .Append(",\"leftPos\":").Append(Vec(r.Left.TypedPosition))
                .Append(",\"rightRot\":").Append(Vec(r.Right.TypedRotation))
                .Append(",\"rightPos\":").Append(Vec(r.Right.TypedPosition))
                .Append(",\"synthetic\":").Append(SwingHarness.Enabled() ? "true" : "false")
                .Append('}')
                .ToString();
        }

        private static string Vec(Vector3 v) =>
            "[" + Num(v.x, "F4") + "," + Num(v.y, "F4") + "," + Num(v.z, "F4") + "]";

        private static string Num(float v, string format) =>
            v.ToString(format, CultureInfo.InvariantCulture);

        internal void Flush()
        {
            if (_lines.Count == 0)
            {
                return;
            }
            try
            {
                if (_sessionFile == null)
                {
                    var tag = SwingHarness.Enabled() ? "synthetic" : "cuts";
                    _sessionFile = Path.Combine(
                        Paths.DataDir, $"{tag}-{DateTime.Now:yyyyMMdd-HHmmss}.jsonl");
                    File.AppendAllText(_sessionFile, Header() + "\n", NoBom);
                }
                File.AppendAllText(_sessionFile, string.Join("\n", _lines) + "\n", NoBom);
                Plugin.Log.Info(
                    $"wrote {_lines.Count} cuts to {Path.GetFileName(_sessionFile)} "
                    + $"({_good} good of {_scored} scored so far)");
                _lines.Clear();
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"could not write cuts: {e.Message}");
            }
        }

        private void OnDestroy() => Detach();
    }
}
