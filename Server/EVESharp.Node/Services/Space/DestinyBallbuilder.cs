using EVESharp.Destiny;
using EVESharp.EVE.Data.Inventory.Items;
using System;

namespace EVESharp.Node.Services.Space
{
    /// <summary>
    /// Builds Destiny Ball structures from ItemEntity objects.
    /// Used by beyonce/ballparkSvc to create balls for the destiny binary snapshot.
    /// </summary>
    public static class DestinyBallBuilder
    {
        /// <summary>
        /// Create a Destiny Ball from an ItemEntity.
        /// </summary>
        /// <param name="ent">The item entity (ship, station, etc.)</param>
        /// <param name="isEgo">True if this is the player's own ship</param>
        /// <returns>A fully constructed Ball ready for binary encoding</returns>
        public static Ball FromEntity(ItemEntity ent, bool isEgo)
        {
            // Use double precision for coordinates - EVE uses very large numbers
            double x = ent.X ?? 0.0;
            double y = ent.Y ?? 0.0;
            double z = ent.Z ?? 0.0;

            Console.WriteLine($"[DestinyBallBuilder] Creating ball for entity {ent.ID}, isEgo={isEgo}, pos=({x:F0},{y:F0},{z:F0})");

            // -------------------------------------------------------
            // Ball Header
            // -------------------------------------------------------
            // All balls should have at least IsMassive flag
            // Ego (player ship) also needs IsFree and IsInteractive
            BallFlag flags = BallFlag.IsMassive;
            if (isEgo)
            {
                flags |= BallFlag.IsFree | BallFlag.IsInteractive;
            }

            BallHeader header = new BallHeader
            {
                ItemId   = ent.ID,
                Location = new Vector3 { X = x, Y = y, Z = z },
                Radius   = isEgo ? 50.0f : 500.0f,  // Ship radius vs station/other
                Mode     = BallMode.Stop,           // Not moving initially
                Flags    = flags
            };

            // -------------------------------------------------------
            // Extra Header (required when Mode != Rigid)
            // Mode.Stop requires ExtraHeader
            // -------------------------------------------------------
            ExtraBallHeader extra = new ExtraBallHeader
            {
                AllianceId    = 0,
                CorporationId = 0,
                CloakMode     = CloakMode.Normal,
                Harmonic      = -1.0f,    // Default harmonic value
                Mass          = 1.0
            };

            // -------------------------------------------------------
            // Ball Data (required when IsFree flag is set)
            // -------------------------------------------------------
            BallData data = default;

            if (header.Flags.HasFlag(BallFlag.IsFree))
            {
                data = new BallData
                {
                    MaxVelocity   = 200f,   // Will be overridden by ship type
                    SpeedFraction = 0f,     // Not moving
                    Unk03         = 0f,
                    Velocity      = new Vector3 { X = 0, Y = 0, Z = 0 }
                };
            }

            // -------------------------------------------------------
            // Construct full Destiny Ball
            // -------------------------------------------------------
            Ball ball = new Ball
            {
                Header      = header,
                ExtraHeader = extra,
                Data        = header.Flags.HasFlag(BallFlag.IsFree) ? data : default,
                FormationId = 0,

                // Mode-specific states - not needed for Stop mode
                FollowState    = default,
                FormationState = default,
                MissileState   = default,
                GotoState      = default,
                WarpState      = default,
                TrollState     = default,
                MushroomState  = default,

                MiniBalls = null
            };

            Console.WriteLine($"[DestinyBallBuilder] Ball created: flags={flags}, mode={header.Mode}");

            return ball;
        }

        /// <summary>
        /// Create a station ball with appropriate settings.
        /// Stations are massive, non-moving, global objects.
        /// </summary>
        public static Ball FromStation(ItemEntity stationEnt, int solarSystemID)
        {
            double x = stationEnt.X ?? 0.0;
            double y = stationEnt.Y ?? 0.0;
            double z = stationEnt.Z ?? 0.0;

            Console.WriteLine($"[DestinyBallBuilder] Creating STATION ball {stationEnt.ID} at ({x:F0},{y:F0},{z:F0})");

            BallHeader header = new BallHeader
            {
                ItemId   = stationEnt.ID,
                Location = new Vector3 { X = x, Y = y, Z = z },
                Radius   = 5000.0f,  // Station radius
                Mode     = BallMode.Rigid,  // Stations are rigid - no movement
                Flags    = BallFlag.IsGlobal | BallFlag.IsMassive
            };

            // Rigid balls don't need ExtraHeader or BallData
            Ball ball = new Ball
            {
                Header      = header,
                ExtraHeader = null,  // Not needed for Rigid
                Data        = default,
                FormationId = 0,

                FollowState    = default,
                FormationState = default,
                MissileState   = default,
                GotoState      = default,
                WarpState      = default,
                TrollState     = default,
                MushroomState  = default,

                MiniBalls = null
            };

            return ball;
        }
    }
}