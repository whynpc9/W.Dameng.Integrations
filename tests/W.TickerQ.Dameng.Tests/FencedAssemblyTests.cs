#if TICKERQ_F14
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using TickerQ.DependencyInjection;
using TickerQ.EntityFrameworkCore.DependencyInjection;
using TickerQ.Utilities.Interfaces;
using Xunit;
using Xunit.Abstractions;

namespace W.TickerQ.Dameng.Tests;

public sealed class FencedAssemblyTests(ITestOutputHelper output)
{
    [Fact]
    public void Loaded_assemblies_match_the_locally_built_fenced_packages()
    {
        const string version = "10.1.1-dameng.1";
        var feed = Path.Combine(LiveDatabase.FindWorktree().FullName, ".local", "tickerq-f14", "feed");
        Compare("W.TickerQ.Core", typeof(TickerQServiceExtensions).Assembly);
        Compare("W.TickerQ.Utilities", typeof(ITimeTickerClaimStore).Assembly);
        Compare("W.TickerQ.EntityFrameworkCore", typeof(ServiceExtension).Assembly);

        void Compare(string packageId, Assembly loaded)
        {
            var fileName = Path.GetFileName(loaded.Location);
            using var archive = ZipFile.OpenRead(Path.Combine(feed, $"{packageId}.{version}.nupkg"));
            var entry = archive.GetEntry($"lib/net10.0/{fileName}");
            Assert.NotNull(entry);
            using var packed = entry.Open();
            using var actual = File.OpenRead(loaded.Location);
            var hash = Convert.ToHexString(SHA256.HashData(actual));
            Assert.Equal(Convert.ToHexString(SHA256.HashData(packed)), hash);
            output.WriteLine($"{packageId} {version} {fileName} SHA256 {hash}");
        }
    }
}
#endif
