using System; 
using EVESharp.Common.Checksum;
using EVESharp.Types.Serialization;

namespace EVESharp.Types;

public class PySubStream : PyDataType
{
    private byte []    mByteStream;
    private PyDataType mCurrentStream;
    private bool       mIsUnmarshaled;
    private PyDataType mOriginalStream;

    public PyDataType Stream
    {
        get
{
    if (!this.mIsUnmarshaled)
    {
        try
        {
            this.mOriginalStream = this.mCurrentStream =
                Unmarshal.ReadFromByteArray(this.mByteStream);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[PySubStream] Unmarshal failed, substituting PyNone: " + ex);
            this.mOriginalStream = this.mCurrentStream = new PyNone();
        }
    }

    return this.mCurrentStream;
}

    }

    public byte[] ByteStream
{
    get
    {
        try
        {
            if (this.mByteStream != null &&
                (this.mIsUnmarshaled == false || this.mCurrentStream == this.mOriginalStream))
                return this.mByteStream;

            this.mOriginalStream = this.mCurrentStream;
            return this.mByteStream = Marshal.ToByteArray(this.mCurrentStream);
        }
        catch (Exception ex)
        {
            Console.WriteLine("[PySubStream] ByteStream generation FAILED, substituting empty stream: " + ex);
            return Array.Empty<byte>();
        }
    }
}


    public PySubStream (byte [] from)
    {
        this.mIsUnmarshaled = false;
        this.mByteStream    = from;
    }

    public PySubStream (PyDataType stream)
    {
        this.mIsUnmarshaled  = true;
        this.mOriginalStream = this.mCurrentStream = stream;
    }

    public override int GetHashCode ()
    {
        return (int) CRC32.Checksum (this.ByteStream) ^ 0x35415879;
    }
}