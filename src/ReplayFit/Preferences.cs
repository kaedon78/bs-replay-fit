using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

namespace ReplayFit
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
            WriteKey(key, value?.ToString("o", Culture) ?? "");

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

        /// <summary>One stretch of history and the grip it was played on.</summary>
        internal struct Assignment
        {
            public DateTime From;
            public DateTime To;
            public Vector3 LeftRotation;
            public Vector3 RightRotation;
            public bool AlternativeHandling;

            /// <summary>
            /// Whole timestamps, not dates.
            /// </summary>
            /// <remarks>
            /// A grip changes at a moment. Compared by date, an assignment ending on the 28th
            /// swallows the whole of the 28th including play after the change -- which is the
            /// one boundary a player most often needs to draw.
            /// </remarks>
            public bool Covers(DateTime when) => when >= From && when <= To;
        }

        /// <summary>
        /// What the player has said about the history the journal cannot vouch for.
        /// </summary>
        /// <remarks>
        /// A list rather than one range and one profile, because a single pair cannot describe
        /// a history with a grip change in it -- and the change detector exists precisely
        /// because such histories are common. Each entry is built the same way: set the range,
        /// name the profile, add it.
        ///
        /// Runs no entry covers are left out of the fit entirely. Guessing at them would put
        /// cuts from an unknown grip into a group that claims to know its own, which is the
        /// error the whole epoch mechanism is here to prevent.
        /// </remarks>
        internal static List<Assignment> Assignments
        {
            get
            {
                var list = new List<Assignment>();
                foreach (var raw in ReadAll("assign"))
                {
                    var parts = raw.Split('|');
                    if (parts.Length != 5)
                    {
                        continue;
                    }
                    try
                    {
                        list.Add(new Assignment
                        {
                            From = DateTime.Parse(
                                parts[0], Culture,
                                System.Globalization.DateTimeStyles.RoundtripKind),
                            To = DateTime.Parse(
                                parts[1], Culture,
                                System.Globalization.DateTimeStyles.RoundtripKind),
                            LeftRotation = ParseVec(parts[2]),
                            RightRotation = ParseVec(parts[3]),
                            AlternativeHandling = parts[4] == "1",
                        });
                    }
                    catch
                    {
                        // A hand-edited line should not take the rest of the list with it.
                    }
                }
                list.Sort((a, b) => a.From.CompareTo(b.From));
                return list;
            }
        }

        private static string Serialise(Assignment a) =>
            a.From.ToString("o", Culture) + "|" + a.To.ToString("o", Culture) + "|"
            + Vec(a.LeftRotation) + "|" + Vec(a.RightRotation) + "|"
            + (a.AlternativeHandling ? "1" : "0");

        internal static void AddAssignment(Assignment a)
        {
            AppendKey("assign", Serialise(a));
            Plugin.Log.Info(
                $"assigned {Shown.Day(a.From)} to {Shown.Day(a.To)}: L {a.LeftRotation} R {a.RightRotation}");
        }

        /// <summary>
        /// Drop one assignment, matched on exactly the text it was stored as.
        /// </summary>
        /// <remarks>
        /// By value rather than by position, because the list is shown sorted by date and
        /// stored in the order it was added: an index that means one entry on screen can mean
        /// a different one in the file, and the failure is silent.
        /// </remarks>
        internal static void RemoveAssignment(Assignment a)
        {
            RemoveLine("assign", Serialise(a));
            Plugin.Log.Info($"removed the range {Shown.Day(a.From)} to {Shown.Day(a.To)}");
        }

        internal static void ClearAssignments() => RemoveAll("assign");

        private static System.Globalization.CultureInfo Culture =>
            System.Globalization.CultureInfo.InvariantCulture;

        private static List<string> ReadAll(string key)
        {
            var found = new List<string>();
            try
            {
                if (!File.Exists(Path))
                {
                    return found;
                }
                foreach (var line in File.ReadAllLines(Path))
                {
                    var text = line.Trim();
                    if (text.StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add(text.Substring(key.Length + 1).Trim());
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"could not read preferences: {e.Message}");
            }
            return found;
        }

        private static void AppendKey(string key, string value)
        {
            try
            {
                File.AppendAllText(Path, key + "=" + value + "\n", new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"could not write preferences: {e.Message}");
            }
        }

        /// <summary>Drop the first line holding exactly this key and value.</summary>
        private static void RemoveLine(string key, string value)
        {
            try
            {
                if (!File.Exists(Path))
                {
                    return;
                }
                var wanted = key + "=" + value;
                var kept = new List<string>();
                var dropped = false;
                foreach (var line in File.ReadAllLines(Path))
                {
                    if (!dropped && line.Trim() == wanted)
                    {
                        dropped = true;
                        continue;
                    }
                    kept.Add(line);
                }
                File.WriteAllText(
                    Path, string.Join("\n", kept) + "\n", new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"could not write preferences: {e.Message}");
            }
        }

        private static void RemoveAll(string key)
        {
            try
            {
                if (!File.Exists(Path))
                {
                    return;
                }
                var kept = File.ReadAllLines(Path)
                    .Where(l => !l.Trim().StartsWith(key + "=", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                File.WriteAllText(
                    Path, string.Join("\n", kept) + "\n", new UTF8Encoding(false));
            }
            catch (Exception e)
            {
                Plugin.Log.Error($"could not write preferences: {e.Message}");
            }
        }
    }
}
