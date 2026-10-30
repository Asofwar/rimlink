using System.Numerics;
using ZstdSharp.Unsafe;

namespace Rimlink.Server.Application.Saves;

/// <summary>
/// One save compressed against another as a zstd reference prefix, the way <c>zstd --patch-from</c> works. <see cref="ZstdSharp.Compressor"/>
/// has no prefix API, hence the low-level calls. A prefix lasts for one frame, so a context is never reused.
/// </summary>
public static unsafe class SaveDelta
{
    public const int DeltaLevel = 9;
    /// <summary>A delta larger than this share of the full file (1/4) means the colony has drifted from its base.</summary>
    public const int RebaseRatio = 4;
    /// <summary>A reference prefix holds both files in memory: a larger save stays a full file.</summary>
    public const long MaxRawBytes = 128 * 1024 * 1024;

    private const int MinWindowLog = 23;
    private const int MaxWindowLog = 31;

    public static byte[] Compress(byte[] reference, byte[] content)
    {
        var context = Methods.ZSTD_createCCtx();
        try
        {
            // Long distance matching only reaches into the prefix when the window covers it.
            int windowLog = Math.Max(MinWindowLog, BitOperations.Log2((ulong)Math.Max(reference.Length, content.Length)) + 2);
            Check(Methods.ZSTD_CCtx_setParameter(context, ZSTD_cParameter.ZSTD_c_compressionLevel, DeltaLevel));
            Check(Methods.ZSTD_CCtx_setParameter(context, ZSTD_cParameter.ZSTD_c_windowLog, windowLog));
            Check(Methods.ZSTD_CCtx_setParameter(context, ZSTD_cParameter.ZSTD_c_enableLongDistanceMatching, 1));
            Check(Methods.ZSTD_CCtx_setParameter(context, ZSTD_cParameter.ZSTD_c_checksumFlag, 1));

            byte[] target = new byte[(int)Methods.ZSTD_compressBound((nuint)content.Length)];
            fixed (byte* prefix = reference, source = content, destination = target)
            {
                Check(Methods.ZSTD_CCtx_refPrefix(context, prefix, (nuint)reference.Length));
                int written = (int)Check(Methods.ZSTD_compress2(context, destination, (nuint)target.Length, source, (nuint)content.Length));
                return target[..written];
            }
        }
        finally
        {
            Methods.ZSTD_freeCCtx(context);
        }
    }

    /// <exception cref="InvalidDataException">The delta is damaged, or this is not its reference (the frame checksum says so).</exception>
    public static byte[] Decompress(byte[] reference, byte[] delta)
    {
        var context = Methods.ZSTD_createDCtx();
        try
        {
            Check(Methods.ZSTD_DCtx_setParameter(context, ZSTD_dParameter.ZSTD_d_windowLogMax, MaxWindowLog));
            fixed (byte* prefix = reference, source = delta)
            {
                // Unknown and error sizes are the two largest values of the type.
                ulong size = Methods.ZSTD_getFrameContentSize(source, (nuint)delta.Length);
                if (size > MaxRawBytes) throw new InvalidDataException("the delta does not state a usable content size");
                byte[] target = new byte[(int)size];
                fixed (byte* destination = target)
                {
                    Check(Methods.ZSTD_DCtx_refPrefix(context, prefix, (nuint)reference.Length));
                    Check(Methods.ZSTD_decompressDCtx(context, destination, (nuint)target.Length, source, (nuint)delta.Length));
                }
                return target;
            }
        }
        finally
        {
            Methods.ZSTD_freeDCtx(context);
        }
    }

    private static nuint Check(nuint result)
    {
        if (Methods.ZSTD_isError(result)) throw new InvalidDataException(Methods.ZSTD_getErrorName(result));
        return result;
    }
}
