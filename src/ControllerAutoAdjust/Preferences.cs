using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace ControllerAutoAdjust
{
    /// <summary>The few answers only the player can give.</summary>
    internal static class Preferences
    {
        internal const string FileName = "preferences.txt";

        /// <summary>
        /// Whether replays from before the journal were played on the settings in force now.
        /// </summary>
        /// <remarks>
        /// Unanswerable from the files themselves -- nothing in a replay records the offsets
        /// -- and the difference matters: months of history either count or they do not.
        /// Asking is the only way to know, and <see cref="Unanswered"/> is kept distinct from
        /// "no" so that a recommendation built on an unconfirmed assumption can say so rather
        /// than presenting itself as settled.
        /// </remarks>
        private static string Path => System.IO.Path.Combine(Paths.DataDir, FileName);

        /// <summary>
        /// The stretch of history the player says was played on one grip.
        /// </summary>
        /// <remarks>
        /// A range rather than a yes/no, because the honest answer is usually neither. A
        /// player who changed their grip in July has six weeks of usable history and six
        /// weeks that would poison the fit, and asking them to discard all of it or none is
        /// asking the wrong question.
        ///
        /// Null means unbounded on that side, which is also the state before anyone has
        /// answered -- told apart from a deliberate choice by
        /// <see cref="RangeProfileAnswered"/>, so a recommendation resting on an unconfirmed
        /// assumption can say so.
        /// </remarks>
        internal static DateTime? RangeStart
        {
            get => ReadDate("rangeStart");
            set => WriteDate("rangeStart", value);
        }

        internal static DateTime? RangeEnd
        {
            get => ReadDate("rangeEnd");
            set => WriteDate("rangeEnd", value);
        }

        /// <summary>
        /// The grip the ranged replays were played on, stored as values not a name.
        /// </summary>
        /// <remarks>
        /// Storing which profile was picked looked simpler and is wrong twice over. Profile
        /// indices are not unique -- built-in and custom profiles both start at zero, so the
        /// list showed two "#0" -- and profiles are editable, so a name resolved later can
        /// mean different numbers than it did when it was chosen. Taking a copy at the moment
        /// of choosing records what the player actually meant.
        ///
        /// Absent means "as they are now", which is also the state before anyone has said.
        /// </remarks>
        internal static bool TryGetRangeGrip(
            out Vector3 leftRotation, out Vector3 rightRotation, out bool alternativeHandling)
        {
            leftRotation = Vector3.zero;
            rightRotation = Vector3.zero;
            alternativeHandling = true;
            var parts = ReadKey("rangeGrip").Split('|');
            if (parts.Length != 3)
            {
                return false;
            }
            try
            {
                leftRotation = ParseVec(parts[0]);
                rightRotation = ParseVec(parts[1]);
                alternativeHandling = parts[2] == "1";
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static void SetRangeGrip(Vector3? left, Vector3? right, bool alternativeHandling)
        {
            WriteKey("rangeGrip", left.HasValue && right.HasValue
                ? Vec(left.Value) + "|" + Vec(right.Value) + "|" + (alternativeHandling ? "1" : "0")
                : "");
        }

        private static string Vec(Vector3 v)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            return v.x.ToString("R", c) + "," + v.y.ToString("R", c) + "," + v.z.ToString("R", c);
        }

        private static Vector3 ParseVec(string raw)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            var n = raw.Split(',');
            return new Vector3(
                float.Parse(n[0], c), float.Parse(n[1], c), float.Parse(n[2], c));
        }

        internal static bool InRange(DateTime when)
        {
            var start = RangeStart;
            var end = RangeEnd;
            if (start.HasValue && when.Date < start.Value.Date)
            {
                return false;
            }
            return !end.HasValue || when.Date <= end.Value.Date;
        }

        private static DateTime? ReadDate(string key)
        {
            var raw = ReadKey(key);
            return DateTime.TryParse(
                raw, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : (DateTime?)null;
        }

        private static void WriteDate(string key, DateTime? value) =>
            WriteKey(key, value?.ToString("yyyy-MM-dd") ?? "");

        private static string ReadKey(string key)
        {
            try
            {
                if (!File.Exists(Path))
                {
                    return "";
                }
                foreach (var line in File.ReadAllLines(Path))
                {
                    var text = line.Trim();
                    if (text.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                    {
                        return text.Substring(key.Length + 1).Trim();
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"could not read preferences: {e.Message}");
            }
            return "";
        }

        /// <summary>Rewrite one key, keeping the rest of the file intact.</summary>
        private static void WriteKey(string key, string value)
        {
            try
            {
                var lines = File.Exists(Path)
                    ? new List<string>(File.ReadAllLines(Path))
                    : new List<string>();
                var replaced = false;
                for (var i = 0; i < lines.Count; i++)
                {
                    if (lines[i].Trim().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                    {
                        lines[i] = $"{key}={value}";
                        replaced = true;
                    }
                }
                if (!replaced)
                {
                    lines.Add($"{key}={value}");
                }
                File.WriteAllText(
                    Path, string.Join("\n", lines) + "\n", new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"could not write preferences: {e.Message}");
            }
        }

        /// <summary>Whether the player has said anything about the ranged replays yet.</summary>
        internal static bool RangeProfileAnswered => ReadKey("rangeGrip").Length > 0;
    }
}
