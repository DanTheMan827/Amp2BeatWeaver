using System.IO;

// Only the extension methods required by DtxCS are included here.
static class StreamExtensions
{
  public static byte[] ReadBytes(this Stream s, int count)
  {
    int realCount = (int)((s.Position + count > s.Length) ? (s.Length - s.Position) : count);
    byte[] ret = new byte[realCount];
    s.Read(ret, 0, realCount);
    return ret;
  }
}
