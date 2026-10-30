using Rimlink.Server.Application.Saves;
using Xunit;

namespace Rimlink.Server.Tests.Saves;

public class SaveDeltaTests
{
    private static byte[] RandomBytes(int size, int seed)
    {
        byte[] bytes = new byte[size];
        new Random(seed).NextBytes(bytes);
        return bytes;
    }

    [Fact]
    public void NearlyIdenticalContentPacksToAlmostNothing()
    {
        byte[] reference = RandomBytes(4_000_000, 1);
        byte[] content = (byte[])reference.Clone();
        for (int i = 0; i < 1000; i++) content[i * 4000] ^= 0xFF;

        byte[] delta = SaveDelta.Compress(reference, content);

        Assert.True(delta.Length < 20_000, $"delta is {delta.Length} bytes");
        Assert.Equal(content, SaveDelta.Decompress(reference, delta));
    }

    [Fact]
    public void UnrelatedContentRoundTrips()
    {
        byte[] reference = RandomBytes(1_000_000, 2);
        byte[] content = RandomBytes(1_000_000, 3);

        Assert.Equal(content, SaveDelta.Decompress(reference, SaveDelta.Compress(reference, content)));
    }

    [Fact]
    public void ContentLargerThanTheReferenceRoundTrips()
    {
        byte[] reference = RandomBytes(1_000_000, 4);
        byte[] content = [.. reference, .. RandomBytes(2_000_000, 5)];

        Assert.Equal(content, SaveDelta.Decompress(reference, SaveDelta.Compress(reference, content)));
    }

    [Fact]
    public void ADamagedDeltaIsInvalidData()
    {
        byte[] reference = RandomBytes(1_000_000, 6);
        byte[] content = RandomBytes(1_000_000, 7);
        byte[] delta = SaveDelta.Compress(reference, content);
        delta[delta.Length / 2] ^= 0xFF;

        Assert.Throws<InvalidDataException>(() => SaveDelta.Decompress(reference, delta));
    }

    [Fact]
    public void ATruncatedDeltaIsInvalidData()
    {
        byte[] reference = RandomBytes(1_000_000, 8);
        byte[] delta = SaveDelta.Compress(reference, RandomBytes(1_000_000, 9));

        Assert.Throws<InvalidDataException>(() => SaveDelta.Decompress(reference, delta[..^10]));
    }

    [Fact]
    public void TheWrongReferenceIsInvalidData()
    {
        byte[] reference = RandomBytes(1_000_000, 10);
        byte[] content = (byte[])reference.Clone();
        content[500_000] ^= 0xFF;
        byte[] delta = SaveDelta.Compress(reference, content);

        Assert.Throws<InvalidDataException>(() => SaveDelta.Decompress(RandomBytes(1_000_000, 11), delta));
    }
}
