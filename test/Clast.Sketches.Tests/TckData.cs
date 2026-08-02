namespace Clast.Sketches.Tests;

/// <summary>
/// Loads the serialization snapshots vendored from apache/datasketches-tck.
/// These are sketches produced by the reference Java and C++ implementations,
/// so reading them correctly is the actual definition of compatibility.
/// </summary>
internal static class TckData
{
    private static readonly string Directory =
        Path.Combine(AppContext.BaseDirectory, "data");

    public static byte[] Load(string fileName)
    {
        string path = Path.Combine(Directory, fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                $"Missing TCK snapshot '{fileName}'. Expected it at {path}; check that data\\*.sk is copied to the output directory.",
                path);
        }
        return File.ReadAllBytes(path);
    }

    /// <summary>The distinct-count values the theta snapshots were generated for.</summary>
    public static IEnumerable<object[]> ThetaCounts()
    {
        foreach (int n in new[] { 0, 1, 10, 100, 1000, 10_000, 100_000, 1_000_000 })
        {
            yield return [n];
        }
    }
}
