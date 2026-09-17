using UTStudio.Domain.Acquisition;

namespace UTStudio.Benchmarks;

internal static class BenchmarkData
{
    internal static ConventionalAcquisitionConfiguration Configuration(int count) =>
        new(new PhysicalChannelId(0), count, 50_000_000);

    internal static ConventionalUtFrameMetadata Metadata(int count) => new(
        new UtSourceId("benchmark"), new AcquisitionRunId(new Guid("11111111-1111-1111-1111-111111111111")),
        Configuration(count), DateTimeOffset.UnixEpoch);

    internal static short[] Samples(int count)
    {
        var result = new short[count];
        for (int i = 0; i < result.Length; i++)
        {
            result[i] = (short)((i * 7919 % 65536) - 32768);
        }
        return result;
    }
}
