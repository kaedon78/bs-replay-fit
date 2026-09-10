using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;

namespace ReplayFit
{
    /// <summary>
    /// Records what the controller offsets were, and from when.
    /// </summary>
    /// <remarks>
    /// This is the one thing replays cannot supply. A cut only means something alongside the
    /// offset in force when it was made, and nothing in a replay file records that. Offline
    /// it had to be inferred from the settings not having moved for two and a half months --
    /// which happened to be true, and stops being a usable assumption the moment anything
    /// adjusts them. Every session would become its own epoch and none would hold enough cuts
    /// to fit.
    ///
    /// A journal closes that without recording every cut a second time: a line whenever the
    /// settings are seen to differ from the last one written, and replays attributed to an
    /// epoch by their timestamp.
    ///
    /// Its blind spot is worth stating rather than hiding. It knows nothing before it was
    /// installed, so a player's existing replays can only be used on the assumption their
    /// grip has not changed across them. The mod should say so rather than let a number
    /// imply certainty it does not have.
    /// </remarks>
    internal static class OffsetJournal
    {
        internal const string FileName = "offsets.jsonl";

        internal struct Epoch
        {
            public DateTime From;
            public Vector3 LeftRotation;
            public Vector3 LeftPosition;
            public Vector3 RightRotation;
            public Vector3 RightPosition;
            public Vector3 LegacyRotation;
            public bool LegacyValid;
            public bool AlternativeHandling;

            public Vector3 RotationFor(bool left) => left ? LeftRotation : RightRotation;
        }

        private static string Path => System.IO.Path.Combine(Paths.DataDir, FileName);

        /// <summary>Append the current settings if they differ from the last entry.</summary>
        internal static void RecordIfChanged(OffsetState.Reading reading)
        {
            try
            {
                var entries = Read();
                if (entries.Count > 0)
                {
                    var last = entries[entries.Count - 1];
                    reading = Inherit(last, reading);
                    if (Same(last, reading))
                    {
                        return;
                    }
                    // Say which field moved. An equality that silently disagrees with itself
                    // appends a duplicate epoch every poll, and a journal full of spurious
                    // epochs misattributes replays -- the exact failure it exists to prevent.
                    Plugin.Log.Info("offset journal differs: " + Difference(last, reading));
                }
                File.AppendAllText(Path, Line(reading) + "\n", new UTF8Encoding(false));
                Plugin.Log.Info($"offsets changed, journalled: {OffsetState.Describe(reading)}");
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"could not journal offsets: {e.Message}");
            }
        }

        private static string Difference(Epoch e, OffsetState.Reading r)
        {
            var parts = new List<string>();
            if (!Close(e.LeftRotation, r.Left.TypedRotation, RotationEpsilonDegrees))
            {
                parts.Add($"leftRot {OffsetState.Fmt(e.LeftRotation)} vs {OffsetState.Fmt(r.Left.TypedRotation)}");
            }
            if (!Close(e.LeftPosition, r.Left.TypedPosition, PositionEpsilonMetres))
            {
                parts.Add($"leftPos {OffsetState.Fmt(e.LeftPosition)} vs {OffsetState.Fmt(r.Left.TypedPosition)}");
            }
            if (!Close(e.RightRotation, r.Right.TypedRotation, RotationEpsilonDegrees))
            {
                parts.Add($"rightRot {OffsetState.Fmt(e.RightRotation)} vs {OffsetState.Fmt(r.Right.TypedRotation)}");
            }
            if (!Close(e.RightPosition, r.Right.TypedPosition, PositionEpsilonMetres))
            {
                parts.Add($"rightPos {OffsetState.Fmt(e.RightPosition)} vs {OffsetState.Fmt(r.Right.TypedPosition)}");
            }
            if (e.LegacyValid != r.LegacyValid)
            {
                parts.Add($"legacyValid {e.LegacyValid} vs {r.LegacyValid}");
            }
            if (!Close(e.LegacyRotation, r.LegacyRotation, RotationEpsilonDegrees))
            {
                parts.Add($"legacyRot {OffsetState.Fmt(e.LegacyRotation)} vs {OffsetState.Fmt(r.LegacyRotation)}");
            }
            if (e.AlternativeHandling != r.AlternativeHandling)
            {
                parts.Add($"alt {e.AlternativeHandling} vs {r.AlternativeHandling}");
            }
            return parts.Count == 0 ? "nothing (so the comparison is wrong)" : string.Join(", ", parts);
        }

        /// <summary>Below these, nothing has changed that anyone could have set.</summary>
        /// <remarks>
        /// Explicit tolerances rather than <c>Vector3 ==</c>, whose epsilon is 1e-5 on the
        /// magnitude -- tighter than the precision these values are written at. Round-tripping
        /// a stored 44.00002 through four decimals gives back 44.0, which prints identically,
        /// compares unequal, and appended a duplicate epoch on every poll. The settings screen
        /// takes whole degrees; a hundredth of one is not a change anyone made.
        /// </remarks>
        private const float RotationEpsilonDegrees = 0.01f;
        private const float PositionEpsilonMetres = 0.0001f;

        private static bool Close(Vector3 a, Vector3 b, float tolerance)
        {
            return Mathf.Abs(a.x - b.x) <= tolerance
                && Mathf.Abs(a.y - b.y) <= tolerance
                && Mathf.Abs(a.z - b.z) <= tolerance;
        }

        /// <summary>
        /// Keep the last legacy offset when this read could not determine one.
        /// </summary>
        /// <remarks>
        /// The legacy offset is the game's own per-hardware compensation, not anything the
        /// player sets: a Valve Index contributes -16.3 degrees of X, and asking for it
        /// fails while the controllers are asleep. A failed lookup was being written down as
        /// a measured zero, which is the error this codebase avoids everywhere else -- a
        /// reading that could not be taken is not a reading of nothing.
        ///
        /// Measured on the developing player: of seventy-seven entries, thirty-one recorded
        /// a zero the helper had refused to vouch for, and every one of them was a spurious
        /// epoch for a setting that had not changed. None of them caught a run, which is luck
        /// -- the offset is more than the whole correction being searched for, so a run
        /// attributed to a false zero would be fitted in a frame sixteen degrees out.
        ///
        /// Inheriting rather than skipping the entry, so a genuine change to the typed
        /// numbers during a bad read is still recorded. The value carried forward is the last
        /// one actually measured on this hardware, which is the best estimate available and a
        /// great deal better than a zero nobody observed.
        /// </remarks>
        private static OffsetState.Reading Inherit(Epoch last, OffsetState.Reading now)
        {
            // A deviceless helper reports a valid zero, and means nothing by it: with no
            // device present it returns zeros whatever the hardware would have said. Reading
            // it as a measurement writes a false epoch into the journal every time the game
            // is started without a headset, which is every development launch.
            var measured = now.LegacyValid
                           && now.PlatformHelper != "DevicelessVRHelper";
            if (measured || !last.LegacyValid)
            {
                return now;
            }
            // Rotation only: the journal has never carried the legacy position, and the fit
            // never asks for it.
            now.LegacyRotation = last.LegacyRotation;
            now.LegacyValid = true;
            return now;
        }

        private static bool Same(Epoch e, OffsetState.Reading r)
        {
            return Close(e.LeftRotation, r.Left.TypedRotation, RotationEpsilonDegrees)
                && Close(e.LeftPosition, r.Left.TypedPosition, PositionEpsilonMetres)
                && Close(e.RightRotation, r.Right.TypedRotation, RotationEpsilonDegrees)
                && Close(e.RightPosition, r.Right.TypedPosition, PositionEpsilonMetres)
                && e.LegacyValid == r.LegacyValid
                && Close(e.LegacyRotation, r.LegacyRotation, RotationEpsilonDegrees)
                && e.AlternativeHandling == r.AlternativeHandling;
        }

        internal static List<Epoch> Read()
        {
            var entries = new List<Epoch>();
            try
            {
                if (!File.Exists(Path))
                {
                    return entries;
                }
                foreach (var line in File.ReadAllLines(Path))
                {
                    if (TryParse(line, out var epoch))
                    {
                        entries.Add(epoch);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"could not read the offset journal: {e.Message}");
            }
            entries.Sort((a, b) => a.From.CompareTo(b.From));
            return entries;
        }

        /// <summary>
        /// Which settings were in force at a moment, or false if the journal cannot say.
        /// </summary>
        /// <remarks>
        /// "Cannot say" is a real answer and is returned rather than guessed. Anything before
        /// the first entry predates the journal, and attributing it to the earliest known
        /// settings would be exactly the assumption this exists to remove.
        /// </remarks>
        internal static bool TryAt(List<Epoch> entries, DateTime when, out Epoch epoch)
        {
            epoch = default;
            if (entries.Count == 0 || when < entries[0].From)
            {
                return false;
            }
            for (var i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i].From <= when)
                {
                    epoch = entries[i];
                    return true;
                }
            }
            return false;
        }

        private static string Line(OffsetState.Reading r)
        {
            return new StringBuilder(320)
                .Append("{\"from\":\"")
                .Append(DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)).Append('"')
                .Append(",\"leftRot\":").Append(Vec(r.Left.TypedRotation))
                .Append(",\"leftPos\":").Append(Vec(r.Left.TypedPosition))
                .Append(",\"rightRot\":").Append(Vec(r.Right.TypedRotation))
                .Append(",\"rightPos\":").Append(Vec(r.Right.TypedPosition))
                .Append(",\"legacyRot\":").Append(Vec(r.LegacyRotation))
                .Append(",\"legacyValid\":").Append(r.LegacyValid ? "true" : "false")
                .Append(",\"alt\":").Append(r.AlternativeHandling ? "true" : "false")
                .Append(",\"helper\":\"").Append(r.PlatformHelper).Append("\"}")
                .ToString();
        }

        private static string Vec(Vector3 v) => "["
            + v.x.ToString("R", CultureInfo.InvariantCulture) + ","
            + v.y.ToString("R", CultureInfo.InvariantCulture) + ","
            + v.z.ToString("R", CultureInfo.InvariantCulture) + "]";

        // Deliberately a hand-rolled reader for the few fields that matter, rather than a
        // JSON dependency for a file this mod is the only writer of.
        /// <summary>
        /// Reads a line by looking for exact substrings, not by parsing JSON.
        /// </summary>
        /// <remarks>
        /// Which means the file is only nominally JSON: the reader wants
        /// <c>"from":"</c> and <c>"legacyValid":true</c> with no space after the colon,
        /// because that is what <see cref="Line"/> writes. Anything that reformats this file
        /// -- a tidy-up through a real JSON library, say, which puts a space after every
        /// colon -- produces a file that still looks correct and that every line of this
        /// method rejects. The failure is silent and total: no entries parse, so the journal
        /// reads as empty and the whole settings history disappears.
        ///
        /// If a migration ever needs to rewrite this file, edit the lines as text and leave
        /// the punctuation alone, or teach this method to parse properly first.
        /// </remarks>
        private static bool TryParse(string line, out Epoch epoch)
        {
            epoch = default;
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("{"))
            {
                return false;
            }
            try
            {
                epoch.From = DateTime.Parse(
                    Field(line, "\"from\":\"", '"'),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind);
                epoch.LeftRotation = Vec(line, "\"leftRot\":");
                epoch.LeftPosition = Vec(line, "\"leftPos\":");
                epoch.RightRotation = Vec(line, "\"rightRot\":");
                epoch.RightPosition = Vec(line, "\"rightPos\":");
                epoch.LegacyRotation = Vec(line, "\"legacyRot\":");
                epoch.LegacyValid = line.Contains("\"legacyValid\":true");
                epoch.AlternativeHandling = line.Contains("\"alt\":true");
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string Field(string line, string key, char end)
        {
            var at = line.IndexOf(key, StringComparison.Ordinal) + key.Length;
            return line.Substring(at, line.IndexOf(end, at) - at);
        }

        private static Vector3 Vec(string line, string key)
        {
            var body = Field(line, key + "[", ']').Split(',');
            return new Vector3(
                float.Parse(body[0], CultureInfo.InvariantCulture),
                float.Parse(body[1], CultureInfo.InvariantCulture),
                float.Parse(body[2], CultureInfo.InvariantCulture));
        }
    }
}
