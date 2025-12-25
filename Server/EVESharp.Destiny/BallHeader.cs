using System.Runtime.InteropServices;

namespace EVESharp.Destiny;

/// <summary>
/// Ball header structure for Destiny binary protocol.
/// 
/// IMPORTANT: For Apocrypha (32-bit) client, ItemId must be 4 bytes (int).
/// Later clients (Crucible+, 64-bit) may use 8 bytes (long).
/// 
/// Wire format (34 bytes total for Apocrypha):
///   Flags:    1 byte  (BallFlag enum)
///   ItemId:   4 bytes (int, ball/item identifier)
///   Location: 24 bytes (Vector3: 3 doubles)
///   Mode:     1 byte  (BallMode enum)
///   Radius:   4 bytes (float)
/// </summary>
[StructLayout (LayoutKind.Sequential, Pack = 1)]
public class BallHeader
{
    public BallFlag Flags;
    
    /// <summary>
    /// Ball/Item ID. For Apocrypha client this is 32-bit.
    /// Solar system IDs (30xxxxxx), ship IDs, etc. all fit in 32 bits.
    /// </summary>
    public int      ItemId;
    
    public Vector3  Location;

    public BallMode Mode;

    public float    Radius;
}