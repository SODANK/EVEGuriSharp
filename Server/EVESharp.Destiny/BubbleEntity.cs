using System;

namespace EVESharp.Destiny
{
    /// <summary>
    /// Runtime entity with mutable movement state, suitable for the physics tick loop.
    /// Wraps data originally from an ItemEntity but adds velocity, mode, etc.
    /// </summary>
    public class BubbleEntity
    {
        public int      ItemID          { get; set; }
        public int      TypeID          { get; set; }
        public int      GroupID         { get; set; }
        public int      CategoryID      { get; set; }
        public string   Name            { get; set; }
        public int      OwnerID         { get; set; }
        public int      CorporationID   { get; set; }
        public int      AllianceID      { get; set; }
        public int      CharacterID     { get; set; }

        // 3-D state (mutable)
        public Vector3  Position        { get; set; }
        public Vector3  Velocity        { get; set; }

        // Movement parameters (doubles to match Apocrypha binary format)
        public BallMode Mode            { get; set; } = BallMode.Stop;
        public BallFlag Flags           { get; set; }
        public double   Radius          { get; set; } = 50.0;
        public double   Mass            { get; set; } = 1000000.0;
        public double   MaxVelocity     { get; set; } = 200.0;
        public double   SpeedFraction   { get; set; }
        public double   Agility         { get; set; } = 1.0;

        // Follow / Orbit targets
        public int      FollowTargetID  { get; set; }
        public double   FollowRange     { get; set; }

        // Goto target
        public Vector3  GotoTarget      { get; set; }

        // Warp target
        public Vector3  WarpTarget      { get; set; }
        public int      WarpEffectStamp { get; set; }

        public bool IsRigid  => Mode == BallMode.Rigid;
        public bool IsPlayer => CharacterID != 0;

        /// <summary>
        /// Build a Ball struct suitable for DestinyBinaryEncoder (Apocrypha format).
        /// </summary>
        public Ball ToBall()
        {
            var ball = new Ball
            {
                Header = new BallHeader
                {
                    ItemId   = ItemID,
                    Mode     = Mode,
                    Radius   = Radius,
                    Location = Position,
                    Flags    = Flags
                },
                FormationId = 0xFF
            };

            if (Mode != BallMode.Rigid)
            {
                ball.ExtraHeader = new ExtraBallHeader
                {
                    Mass          = Mass,
                    CloakMode     = CloakMode.Normal,
                    Harmonic      = 0xFFFFFFFFFFFFFFFF,
                    CorporationId = CorporationID,
                    AllianceId    = AllianceID
                };

                if (Flags.HasFlag(BallFlag.IsFree))
                {
                    ball.Data = new BallData
                    {
                        MaxVelocity   = MaxVelocity,
                        Velocity      = Velocity,
                        UnknownVec    = default,
                        Agility       = Agility,
                        SpeedFraction = SpeedFraction
                    };
                }
            }

            // Mode-specific state
            switch (Mode)
            {
                case BallMode.Follow:
                case BallMode.Orbit:
                    ball.FollowState = new FollowState
                    {
                        FollowId    = FollowTargetID,
                        FollowRange = FollowRange
                    };
                    break;
                case BallMode.Goto:
                    ball.GotoState = new GotoState { Location = GotoTarget };
                    break;
                case BallMode.Warp:
                    ball.WarpState = new WarpState
                    {
                        Location    = WarpTarget,
                        EffectStamp = WarpEffectStamp,
                        FollowRange = 0,
                        FollowId    = 0,
                        OwnerId     = OwnerID
                    };
                    break;
            }

            return ball;
        }
    }
}
