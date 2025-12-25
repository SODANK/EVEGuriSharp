using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace EVESharp.Destiny
{
    /// <summary>
    /// Builds Destiny binary packets (the same format UpdateReader can parse)
    /// so they can be sent to the client inside the ballpark snapshot or
    /// in incremental updates.
    /// 
    /// FIXED VERSION: Uses explicit field writing to avoid struct layout issues
    /// </summary>
    public static class DestinyBinaryEncoder
    {
        /// <summary>
        /// Build a full-state Destiny packet containing all given balls.
        /// PacketType:
        ///   0 = full state snapshot
        ///   1 = incremental update (same wire format, different semantics)
        /// </summary>
        public static byte[] BuildFullState(IEnumerable<Ball> balls, int stamp, byte packetType = 0)
        {
            if (balls == null)
                throw new ArgumentNullException(nameof(balls));

            using MemoryStream ms = new MemoryStream();
            using BinaryWriter writer = new BinaryWriter(ms);

            // -----------------------------------------------------------------
            // 1) Destiny header (matches Header struct)
            // -----------------------------------------------------------------
            writer.Write(packetType);    // byte PacketType
            writer.Write(stamp);         // int Stamp

            Console.WriteLine($"[DestinyEncoder] Header: packetType={packetType}, stamp={stamp}");

            // -----------------------------------------------------------------
            // 2) All balls
            // -----------------------------------------------------------------
            int ballCount = 0;
            foreach (Ball ball in balls)
            {
                if (ball == null)
                    continue;

                WriteBallExplicit(writer, ball);
                ballCount++;
            }

            Console.WriteLine($"[DestinyEncoder] Wrote {ballCount} balls, total size = {ms.Position} bytes");

            writer.Flush();
            return ms.ToArray();
        }

        /// <summary>
        /// Convenience helper for single ball encoding
        /// </summary>
        public static byte[] BuildSingleBall(Ball ball, int stamp, byte packetType = 0)
        {
            if (ball == null)
                throw new ArgumentNullException(nameof(ball));

            return BuildFullState(new[] { ball }, stamp, packetType);
        }

        // =====================================================================
        // Explicit field writing (avoids struct layout ambiguity)
        // =====================================================================

        private static void WriteBallExplicit(BinaryWriter writer, Ball ball)
        {
            if (ball.Header == null)
                throw new InvalidOperationException("Ball.Header must be non-null before encoding.");

            var h = ball.Header;

            // -------------------------
            // BallHeader (explicit order must match UpdateReader)
            // -------------------------
            writer.Write((byte)h.Flags);           // BallFlag : byte
            writer.Write(h.ItemId);                // long (8 bytes)
            WriteVector3(writer, h.Location);       // 24 bytes (3 doubles)
            writer.Write((byte)h.Mode);            // BallMode : byte
            writer.Write(h.Radius);                // float (4 bytes)

            Console.WriteLine($"[DestinyEncoder]   Ball {h.ItemId}: Flags={h.Flags}, Mode={h.Mode}, Pos=({h.Location.X:F0},{h.Location.Y:F0},{h.Location.Z:F0}), R={h.Radius}");

            // -------------------------
            // ExtraBallHeader (if mode != Rigid)
            // -------------------------
            if (h.Mode != BallMode.Rigid)
            {
                var extra = ball.ExtraHeader ?? new ExtraBallHeader();
                writer.Write(extra.AllianceId);        // long (8 bytes)
                writer.Write((byte)extra.CloakMode);   // CloakMode : byte
                writer.Write(extra.CorporationId);     // int (4 bytes)
                writer.Write(extra.Harmonic);          // float (4 bytes)
                writer.Write(extra.Mass);              // double (8 bytes)

                Console.WriteLine($"[DestinyEncoder]     ExtraHeader: alliance={extra.AllianceId}, corp={extra.CorporationId}, cloak={extra.CloakMode}");
            }

            // -------------------------
            // BallData (if IsFree flag is set)
            // -------------------------
            if (h.Flags.HasFlag(BallFlag.IsFree))
            {
                var data = ball.Data ?? new BallData();
                writer.Write(data.MaxVelocity);        // float
                writer.Write(data.SpeedFraction);      // float
                writer.Write(data.Unk03);              // float
                WriteVector3(writer, data.Velocity);    // 24 bytes

                Console.WriteLine($"[DestinyEncoder]     BallData: maxVel={data.MaxVelocity}, speedFrac={data.SpeedFraction}");
            }

            // -------------------------
            // FormationId (always a single byte)
            // -------------------------
            writer.Write(ball.FormationId);

            // -------------------------
            // Mode-specific state
            // -------------------------
            switch (h.Mode)
            {
                case BallMode.Follow:
                case BallMode.Orbit:
                    WriteFollowState(writer, ball.FollowState);
                    break;

                case BallMode.Formation:
                    WriteFormationState(writer, ball.FormationState);
                    break;

                case BallMode.Troll:
                    WriteTrollState(writer, ball.TrollState);
                    break;

                case BallMode.Missile:
                    WriteMissileState(writer, ball.MissileState);
                    break;

                case BallMode.Goto:
                    WriteGotoState(writer, ball.GotoState);
                    break;

                case BallMode.Warp:
                    WriteWarpState(writer, ball.WarpState);
                    break;

                case BallMode.Mushroom:
                    WriteMushroomState(writer, ball.MushroomState);
                    break;

                case BallMode.Stop:
                case BallMode.Field:
                case BallMode.Rigid:
                    // no extra state for these
                    break;
            }

            // -------------------------
            // MiniBalls
            // -------------------------
            if (h.Flags.HasFlag(BallFlag.HasMiniBalls))
            {
                MiniBall[] minis = ball.MiniBalls ?? Array.Empty<MiniBall>();
                writer.Write((short)minis.Length);

                for (int i = 0; i < minis.Length; i++)
                    WriteMiniBall(writer, minis[i]);
            }
        }

        // =====================================================================
        // Vector3 helper
        // =====================================================================
        private static void WriteVector3(BinaryWriter writer, Vector3 v)
        {
            writer.Write(v.X);
            writer.Write(v.Y);
            writer.Write(v.Z);
        }

        // =====================================================================
        // State struct writers (CORRECTED to match actual struct definitions)
        // =====================================================================
        private static void WriteFollowState(BinaryWriter writer, FollowState state)
        {
            // FollowState: UnkFollowId (long), UnkRange (float)
            writer.Write(state.UnkFollowId);
            writer.Write(state.UnkRange);
        }

        private static void WriteFormationState(BinaryWriter writer, FormationState state)
        {
            // FormationState: Unk01 (long), Unk02 (float), Unk03 (float)
            writer.Write(state.Unk01);
            writer.Write(state.Unk02);
            writer.Write(state.Unk03);
        }

        private static void WriteTrollState(BinaryWriter writer, TrollState state)
        {
            // TrollState: Unk01 (float)
            writer.Write(state.Unk01);
        }

        private static void WriteMissileState(BinaryWriter writer, MissileState state)
        {
            // MissileState: UnkFollowId (long), Unk01 (float), UnkSourceId (long),
            //               Unk02 (float), Unk03 (Vector3)
            writer.Write(state.UnkFollowId);
            writer.Write(state.Unk01);
            writer.Write(state.UnkSourceId);
            writer.Write(state.Unk02);
            WriteVector3(writer, state.Unk03);
        }

        private static void WriteGotoState(BinaryWriter writer, GotoState state)
        {
            // GotoState: Location (Vector3) - 24 bytes
            WriteVector3(writer, state.Location);
            Console.WriteLine($"[DestinyEncoder]     GotoState: target=({state.Location.X:F0},{state.Location.Y:F0},{state.Location.Z:F0})");
        }

        private static void WriteWarpState(BinaryWriter writer, WarpState state)
        {
            // WarpState: Location (Vector3), EffectStamp (int), 
            //            Unk01 (long), FollowId (long), OwnerId (long)
            WriteVector3(writer, state.Location);
            writer.Write(state.EffectStamp);
            writer.Write(state.Unk01);
            writer.Write(state.FollowId);
            writer.Write(state.OwnerId);
        }

        private static void WriteMushroomState(BinaryWriter writer, MushroomState state)
        {
            // MushroomState: Unk01 (float), Unk02 (long), Unk03 (float), Unk04 (long)
            writer.Write(state.Unk01);
            writer.Write(state.Unk02);
            writer.Write(state.Unk03);
            writer.Write(state.Unk04);
        }

        private static void WriteMiniBall(BinaryWriter writer, MiniBall mini)
        {
            // MiniBall: Offset (Vector3), Radius (float)
            WriteVector3(writer, mini.Offset);
            writer.Write(mini.Radius);
        }

        // =====================================================================
        // Legacy method using Marshal (kept for reference/comparison)
        // =====================================================================
        private static void WriteStruct<T>(Stream stream, T value)
        {
            object boxed = value;

            if (boxed == null)
                boxed = Activator.CreateInstance(typeof(T))!;

            Type type = boxed.GetType();
            int size  = Marshal.SizeOf(type);

            byte[] buffer = new byte[size];
            IntPtr ptr    = IntPtr.Zero;

            try
            {
                ptr = Marshal.AllocHGlobal(size);
                if (ptr == IntPtr.Zero)
                    throw new Exception("Failed to allocate unmanaged memory for destiny struct write");

                Marshal.StructureToPtr(boxed, ptr, false);
                Marshal.Copy(ptr, buffer, 0, size);

                stream.Write(buffer, 0, size);
            }
            finally
            {
                if (ptr != IntPtr.Zero)
                    Marshal.FreeHGlobal(ptr);
            }
        }
    }
}
