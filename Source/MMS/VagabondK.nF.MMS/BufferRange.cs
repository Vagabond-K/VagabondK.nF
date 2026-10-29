using System;

namespace VagabondK.nF.MMS
{
    internal static class BufferRange
    {
        internal static void Check(byte[] buffer, int offset, int count)
        {
            if (buffer == null) throw new ArgumentNullException("buffer");
            if (offset < 0 || count < 0 || offset > buffer.Length || count > buffer.Length - offset)
                throw new ArgumentOutOfRangeException("offset/count");
        }
    }
}
