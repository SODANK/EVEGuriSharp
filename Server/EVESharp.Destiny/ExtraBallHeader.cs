using System.Runtime.InteropServices;

namespace EVESharp.Destiny;

/// <summary>
/// Extra ball header for non-Rigid mode balls.
/// Contains ownership and physics data.
/// 
/// IMPORTANT: For Apocrypha (32-bit) client, AllianceId must be 4 bytes (int).
/// Later clients (Crucible+, 64-bit) may use 8 bytes (long).
/// 
/// Wire format (21 bytes total for Apocrypha):
///   AllianceId:    4 bytes (int)
///   CloakMode:     1 byte  (enum)
///   CorporationId: 4 bytes (int)
///   Harmonic:      4 bytes (float) - shield harmonic, often 0xFF bytes (-1f)
///   Mass:          8 bytes (double)
/// </summary>
[StructLayout (LayoutKind.Sequential, Pack = 1)]
public class ExtraBallHeader
{
    /// <summary>
    /// Alliance ID of the ball's owner. For Apocrypha this is 32-bit.
    /// </summary>
    public int AllianceId;

    public CloakMode CloakMode;
    public int       CorporationId;

    /// <summary>
    /// Shield harmonic value of ball. Often seen as all 0xFF (-1f).
    /// </summary>
    public float Harmonic;
    
    /// <summary>
    /// Mass from type information.
    /// </summary>
    public double Mass;
}
