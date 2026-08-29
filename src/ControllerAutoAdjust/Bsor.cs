using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace ControllerAutoAdjust
{
    /// <summary>
    /// Reads BeatLeader's local replay files.
    /// </summary>
    /// <remarks>
    /// Live capture is exact and stamps the settings each cut was made under, but it starts
    /// from nothing: twenty-odd runs before it can say anything, and again after every
    /// change. A player's existing replays already answer the question on the day the mod is
    /// installed, so both paths exist and are cross-checked against each other.
    ///
    /// What replays cannot supply is the offset in force when they were played. Nothing
    /// records it, so anything derived from them rests on the settings not having moved
    /// across them -- true for a player who set their grip once, and false the moment an
    /// auto-adjuster starts working. Live cuts carry the stamp and supersede these.
    ///
    /// Strict about the magic and version, lenient about everything after: these come off a
    /// live game installation and a partially written one is unremarkable.
    /// </remarks>
    internal static class Bsor
    {
        private const int Magic = 0x442D3D69;
        private const byte SupportedVersion = 1;

        /// <summary>Bytes per frame: time, fps, then three pose pairs of seven floats.</summary>
        private const int FrameBytes = 4 + 4 + 3 * 7 * 4;

        /// <summary>Record sizes for the blocks this reader skips rather than decodes.</summary>
        private static readonly Dictionary<int, int> SkippableBlocks =
            new Dictionary<int, int> { { 3, 20 }, { 4, 8 }, { 5, 12 } };

        internal enum NoteEvent { Good = 0, Bad = 1, Miss = 2, Bomb = 3 }

        /// <summary>Buffers reused across a whole pass, instead of allocated per replay.</summary>
        /// <remarks>
        /// A replay's frame stream is a couple of megabytes and its pose arrays another
        /// megabyte and a half. Everything over 85 KB lands on the large object heap, which is
        /// not compacted and is only reclaimed on a gen-2 collection -- so reading a few
        /// hundred replays threw well over a gigabyte of multi-megabyte blocks at a heap that
        /// gave none of it back promptly. The working set reached 3 GB to keep about 14 KB of
        /// cuts per replay.
        ///
        /// Nothing here is a cache: it is one replay's worth of room, grown to the largest
        /// seen and written over each time. The arrays outlive their replay, so
        /// <see cref="Replay.FrameCount"/> is what says how much of them is real -- their
        /// length is whatever the biggest replay so far needed.
        /// </remarks>
        internal sealed class Scratch
        {
            internal byte[] Bytes = Array.Empty<byte>();
            internal float[] Times = Array.Empty<float>();
            internal Vector3[] Left = Array.Empty<Vector3>();
            internal Vector3[] Right = Array.Empty<Vector3>();
            internal Quaternion[] LeftRotation = Array.Empty<Quaternion>();
            internal Quaternion[] RightRotation = Array.Empty<Quaternion>();

            internal void Fit(int frames, int bytes)
            {
                if (Bytes.Length < bytes)
                {
                    Bytes = new byte[bytes];
                }
                if (Times.Length >= frames)
                {
                    return;
                }
                Times = new float[frames];
                Left = new Vector3[frames];
                Right = new Vector3[frames];
                LeftRotation = new Quaternion[frames];
                RightRotation = new Quaternion[frames];
            }
        }

        internal class Info
        {
            public string GameVersion = "";
            public string Timestamp = "";
            public string SongHash = "";
            public string SongName = "";
            public string Difficulty = "";
            public string Mode = "";
            public string Modifiers = "";
            public int Score;
            public float JumpDistance;
            public bool LeftHanded;
            public float Height;
            public float FailTime;
            public float Speed;

            public bool Failed => FailTime > 0f;

            /// <summary>
            /// Whether the timings and geometry mean what the chart says.
            /// </summary>
            /// <remarks>
            /// Only what actually corrupts a cut. A speed modifier rescales every interval and
            /// practice mode lets a passage be replayed until it is learned, so both change
            /// what the numbers mean.
            ///
            /// Nothing else does. NoFail, no-bombs, no-obstacles and a failed run all leave
            /// the cuts that happened exactly as they were -- and rejecting them threw away
            /// well over half of a real library for no reason. A run that ended early is
            /// simply a shorter run.
            /// </remarks>
            public bool Clean
            {
                get
                {
                    foreach (var m in Modifiers.Split(','))
                    {
                        switch (m)
                        {
                            case "FS": case "SF": case "SS": case "SC": case "PM":
                                return false;
                        }
                    }
                    return Speed == 0f;
                }
            }
        }

        internal struct Cut
        {
            public float SaberSpeed;
            public int SaberType;
            public float TimeDeviation;
            public Vector3 CutPoint;
            public Vector3 CutNormal;
            public float CutDistanceToCenter;
            public float BeforeCutRating;
            public float AfterCutRating;
        }

        internal struct Note
        {
            public int NoteId;
            public float EventTime;
            public NoteEvent Event;
            public bool HasCut;
            public Cut Cut;

            public int Column => (NoteId / 1000) % 10;
            public int Row => (NoteId / 100) % 10;
            public int Colour => (NoteId / 10) % 10;
            public int ScoringType => NoteId / 10000;
        }

        internal class Replay
        {
            public Info Info = new Info();
            public float[] FrameTimes = Array.Empty<float>();
            public Vector3[] LeftHand = Array.Empty<Vector3>();
            public Vector3[] RightHand = Array.Empty<Vector3>();
            public Quaternion[] LeftRotation = Array.Empty<Quaternion>();
            public Quaternion[] RightRotation = Array.Empty<Quaternion>();
            public List<Note> Notes = new List<Note>();

            /// <summary>
            /// How many frames are real. The arrays are borrowed and may be longer.
            /// </summary>
            /// <remarks>
            /// Reading past this finds the tail of some previous, longer replay, which is
            /// plausible data in the wrong place -- and a binary search over it returns a
            /// confident answer from another song.
            /// </remarks>
            public int FrameCount;
        }

        internal static Replay Parse(string path, Scratch scratch = null)
        {
            scratch = scratch ?? new Scratch();
            using (var stream = File.OpenRead(path))
            using (var r = new BinaryReader(stream, Encoding.UTF8))
            {
                if (r.ReadInt32() != Magic)
                {
                    throw new InvalidDataException("not a BSOR file");
                }
                var version = r.ReadByte();
                if (version != SupportedVersion)
                {
                    throw new InvalidDataException($"unsupported BSOR version {version}");
                }

                var replay = new Replay();
                while (stream.Position < stream.Length)
                {
                    var block = r.ReadByte();
                    if (block == 0)
                    {
                        replay.Info = ReadInfo(r);
                    }
                    else if (block == 1)
                    {
                        ReadFrames(r, replay, scratch);
                    }
                    else if (block == 2)
                    {
                        ReadNotes(r, replay);
                    }
                    else if (SkippableBlocks.TryGetValue(block, out var size))
                    {
                        var count = r.ReadInt32();
                        if (count < 0)
                        {
                            throw new InvalidDataException($"implausible record count {count}");
                        }
                        stream.Seek((long)count * size, SeekOrigin.Current);
                    }
                    else
                    {
                        // An unknown block cannot be skipped: its length is not encoded, so
                        // the cursor would land mid-record. Stop and keep what was read.
                        break;
                    }
                }
                return replay;
            }
        }

        private static string Text(BinaryReader r)
        {
            var length = r.ReadInt32();
            if (length < 0 || length > 1 << 20)
            {
                throw new InvalidDataException($"implausible string length {length}");
            }
            return Encoding.UTF8.GetString(r.ReadBytes(length));
        }

        private static Vector3 Vec3(BinaryReader r) =>
            new Vector3(r.ReadSingle(), r.ReadSingle(), r.ReadSingle());

        private static Info ReadInfo(BinaryReader r)
        {
            var info = new Info();
            Text(r);                        // mod version
            info.GameVersion = Text(r);
            info.Timestamp = Text(r);
            Text(r);                        // player id
            Text(r);                        // player name
            Text(r);                        // platform
            Text(r);                        // tracking system
            Text(r);                        // hmd
            Text(r);                        // controller
            info.SongHash = Text(r);
            info.SongName = Text(r);
            Text(r);                        // mapper
            info.Difficulty = Text(r);
            info.Score = r.ReadInt32();
            info.Mode = Text(r);
            Text(r);                        // environment
            info.Modifiers = Text(r);
            info.JumpDistance = r.ReadSingle();
            info.LeftHanded = r.ReadByte() != 0;
            info.Height = r.ReadSingle();
            r.ReadSingle();                 // start time
            info.FailTime = r.ReadSingle();
            info.Speed = r.ReadSingle();
            return info;
        }

        private static void ReadFrames(BinaryReader r, Replay replay, Scratch scratch)
        {
            var count = r.ReadInt32();
            if (count < 0)
            {
                throw new InvalidDataException($"implausible frame count {count}");
            }
            var wanted = count * FrameBytes;
            scratch.Fit(count, wanted);
            var raw = scratch.Bytes;
            if (r.Read(raw, 0, wanted) != wanted)
            {
                throw new InvalidDataException("replay ended mid-frame");
            }

            replay.FrameCount = count;
            replay.FrameTimes = scratch.Times;
            replay.LeftHand = scratch.Left;
            replay.RightHand = scratch.Right;
            replay.LeftRotation = scratch.LeftRotation;
            replay.RightRotation = scratch.RightRotation;

            for (var i = 0; i < count; i++)
            {
                var at = i * FrameBytes;
                replay.FrameTimes[i] = BitConverter.ToSingle(raw, at);
                // Layout: time, fps, head(3)+rot(4), left(3)+rot(4), right(3)+rot(4).
                var left = at + 8 + 7 * 4;
                var right = left + 7 * 4;
                replay.LeftHand[i] = ReadVec(raw, left);
                replay.LeftRotation[i] = ReadQuat(raw, left + 12);
                replay.RightHand[i] = ReadVec(raw, right);
                replay.RightRotation[i] = ReadQuat(raw, right + 12);
            }
        }

        private static Vector3 ReadVec(byte[] b, int at) => new Vector3(
            BitConverter.ToSingle(b, at),
            BitConverter.ToSingle(b, at + 4),
            BitConverter.ToSingle(b, at + 8));

        private static Quaternion ReadQuat(byte[] b, int at) => new Quaternion(
            BitConverter.ToSingle(b, at),
            BitConverter.ToSingle(b, at + 4),
            BitConverter.ToSingle(b, at + 8),
            BitConverter.ToSingle(b, at + 12));

        private static void ReadNotes(BinaryReader r, Replay replay)
        {
            var count = r.ReadInt32();
            if (count < 0)
            {
                throw new InvalidDataException($"implausible note count {count}");
            }
            for (var i = 0; i < count; i++)
            {
                var note = new Note
                {
                    NoteId = r.ReadInt32(),
                    EventTime = r.ReadSingle(),
                };
                r.ReadSingle();                        // spawn time
                var raw = r.ReadInt32();
                note.Event = raw >= 0 && raw <= 3 ? (NoteEvent)raw : NoteEvent.Bad;

                if (raw == 0 || raw == 1)
                {
                    r.ReadByte(); r.ReadByte(); r.ReadByte(); r.ReadByte();  // the four OK flags
                    var cut = new Cut { SaberSpeed = r.ReadSingle() };
                    Vec3(r);                                                 // saber direction
                    cut.SaberType = r.ReadInt32();
                    cut.TimeDeviation = r.ReadSingle();
                    r.ReadSingle();                                          // cut dir deviation
                    cut.CutPoint = Vec3(r);
                    cut.CutNormal = Vec3(r);
                    cut.CutDistanceToCenter = r.ReadSingle();
                    r.ReadSingle();                                          // cut angle
                    cut.BeforeCutRating = r.ReadSingle();
                    cut.AfterCutRating = r.ReadSingle();
                    note.Cut = cut;
                    note.HasCut = true;
                }
                replay.Notes.Add(note);
            }
        }
    }
}
