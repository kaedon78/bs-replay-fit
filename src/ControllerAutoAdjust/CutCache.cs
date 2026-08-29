using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace ControllerAutoAdjust
{
    /// <summary>
    /// Keeps the reduced form of each replay, so it is parsed once and not once per session.
    /// </summary>
    /// <remarks>
    /// Reading is the expensive step by an order of magnitude -- a minute and a half against
    /// twenty seconds to fit -- and it produces about fourteen kilobytes per replay from two
    /// and a half megabytes of frame stream. Holding that in memory only meant paying the full
    /// cost again after every restart, for files that had not changed.
    ///
    /// Keyed per file and by its write time, which makes the read incremental rather than
    /// all-or-nothing: a new replay is parsed, the rest come off disk, and a file edited or
    /// replaced falls out of the cache on its own.
    ///
    /// What is deliberately *not* stored is which settings each run was played on. That comes
    /// from the journal and from what the player has assigned, both of which change after a
    /// read -- a cached epoch would be a stale answer to a question whose answer moved, and it
    /// would look no different from a fresh one. It is resolved when it is used.
    /// </remarks>
    internal static class CutCache
    {
        internal const string FileName = "cuts.cache";

        private const int Magic = 0x43414341;

        /// <summary>
        /// Bump when the reduced form changes meaning.
        /// </summary>
        /// <remarks>
        /// A stale cache does not fail: it fits confidently on numbers computed by code that
        /// no longer exists. Anything that alters what a cut sample means -- the geometry, the
        /// note-centre reconstruction, the filters -- has to land here too.
        ///
        /// Version 2: chain links left out of the samples rather than only out of the depth
        /// fit, the cut distance taken from the game rather than from our reconstruction of
        /// it, and the minimum-cuts threshold applied to both hands instead of the left.
        ///
        /// Version 3: the moment arm measured to the recorded cut point rather than to the
        /// rebuilt note centre, and the gap since the hand's previous cut carried alongside.
        ///
        /// Version 4: that moment arm put back. The plane rotates about the grip and the note
        /// is what is being measured from it, so the arm is the note's position and is not
        /// bounded by the sabre's length. Version 3 was a fix for a problem that was not one.
        /// </remarks>
        private const int Version = 4;

        private static string Path => System.IO.Path.Combine(Paths.DataDir, FileName);

        internal struct Entry
        {
            public long WrittenTicks;
            public ReplayCuts.Extraction Cuts;
        }

        internal static Dictionary<string, Entry> Load()
        {
            var found = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (!File.Exists(Path))
                {
                    return found;
                }
                using (var stream = File.OpenRead(Path))
                using (var r = new BinaryReader(stream, Encoding.UTF8))
                {
                    if (r.ReadInt32() != Magic || r.ReadInt32() != Version)
                    {
                        Plugin.Log.Info("cut cache is from an older build; ignoring it");
                        return found;
                    }
                    var count = r.ReadInt32();
                    for (var i = 0; i < count; i++)
                    {
                        var file = r.ReadString();
                        var entry = new Entry
                        {
                            WrittenTicks = r.ReadInt64(),
                            Cuts = new ReplayCuts.Extraction
                            {
                                Played = new DateTime(r.ReadInt64(), DateTimeKind.Utc),
                                Song = r.ReadString(),
                                Left = ReadHand(r),
                                Right = ReadHand(r),
                            },
                        };
                        found[file] = entry;
                    }
                }
                Plugin.Log.Info($"cut cache: {found.Count} replays already reduced");
            }
            catch (Exception e)
            {
                // A half-written cache is not worth recovering; it costs one slow read.
                Plugin.Log.Warn($"cut cache unreadable, starting fresh: {e.Message}");
                found.Clear();
            }
            return found;
        }

        internal static void Save(Dictionary<string, Entry> entries)
        {
            try
            {
                var temporary = Path + ".tmp";
                using (var stream = File.Create(temporary))
                using (var w = new BinaryWriter(stream, Encoding.UTF8))
                {
                    w.Write(Magic);
                    w.Write(Version);
                    w.Write(entries.Count);
                    foreach (var pair in entries)
                    {
                        w.Write(pair.Key);
                        w.Write(pair.Value.WrittenTicks);
                        w.Write(pair.Value.Cuts.Played.Ticks);
                        w.Write(pair.Value.Cuts.Song ?? "");
                        WriteHand(w, pair.Value.Cuts.Left);
                        WriteHand(w, pair.Value.Cuts.Right);
                    }
                }
                // Written aside and moved, so an interrupted save leaves the previous cache
                // intact rather than a truncated one that reads as real.
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                }
                File.Move(temporary, Path);
                Plugin.Log.Info($"cut cache: {entries.Count} replays saved");
            }
            catch (Exception e)
            {
                Plugin.Log.Warn($"could not save the cut cache: {e.Message}");
            }
        }

        private static ReplayCuts.HandCuts ReadHand(BinaryReader r)
        {
            var hand = new ReplayCuts.HandCuts
            {
                NoteSpeed = r.ReadSingle(),
                MedianResidual = r.ReadSingle(),
                Cuts = new List<CutSample>(),
            };
            var count = r.ReadInt32();
            for (var i = 0; i < count; i++)
            {
                hand.Cuts.Add(new CutSample
                {
                    Hand = r.ReadInt32(),
                    Signed = r.ReadSingle(),
                    AcrossX = r.ReadSingle(),
                    AcrossY = r.ReadSingle(),
                    Lever = r.ReadSingle(),
                    Multiplier = r.ReadInt32(),
                    SincePrevious = r.ReadSingle(),
                });
            }
            return hand;
        }

        private static void WriteHand(BinaryWriter w, ReplayCuts.HandCuts hand)
        {
            w.Write(hand.NoteSpeed);
            w.Write(hand.MedianResidual);
            var cuts = hand.Cuts ?? new List<CutSample>();
            w.Write(cuts.Count);
            foreach (var c in cuts)
            {
                w.Write(c.Hand);
                w.Write(c.Signed);
                w.Write(c.AcrossX);
                w.Write(c.AcrossY);
                w.Write(c.Lever);
                w.Write(c.Multiplier);
                w.Write(c.SincePrevious);
            }
        }
    }
}
