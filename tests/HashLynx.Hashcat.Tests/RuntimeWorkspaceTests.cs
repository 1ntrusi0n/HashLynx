namespace HashLynx.Hashcat.Tests;

public sealed class RuntimeWorkspaceTests
{
    [Fact]
    public async Task StagingCopiesSupportAssetsAndNeverCopiesMutableOrPrivateReleaseFiles()
    {
        var root = Path.Combine(Path.GetTempPath(), "HashLynx-runtime-test-" + Guid.NewGuid().ToString("N"));
        var source = Path.Combine(root, "source");
        Directory.CreateDirectory(Path.Combine(source, "OpenCL"));
        Directory.CreateDirectory(Path.Combine(source, "kernels"));
        var executable = Path.Combine(source, "hashcat.exe");
        try
        {
            await File.WriteAllTextAsync(executable, "test executable placeholder");
            await File.WriteAllTextAsync(Path.Combine(source, "OpenCL", "example.cl"), "fixture");
            await File.WriteAllTextAsync(Path.Combine(source, "kernels", "cached.bin"), "fixture");
            await File.WriteAllTextAsync(Path.Combine(source, "hashcat.potfile"), "private fixture");
            foreach (var folder in new[] { "feeds", "pcfg", "tables" })
            {
                Directory.CreateDirectory(Path.Combine(source, folder));
                await File.WriteAllTextAsync(Path.Combine(source, folder, "fixture"), "support asset");
            }
            Directory.CreateDirectory(Path.Combine(source, "cache"));
            await File.WriteAllTextAsync(Path.Combine(source, "cache", "private"), "never stage");
            var before = Directory.GetFiles(source, "*", SearchOption.AllDirectories).Order().ToArray();
            var workspace = new HashcatRuntimeWorkspace(Path.Combine(root, "cache"));
            var runtime = await workspace.PrepareAsync(executable);
            Assert.Equal("fixture", await File.ReadAllTextAsync(Path.Combine(runtime, "OpenCL", "example.cl")));
            Assert.False(File.Exists(Path.Combine(runtime, "hashcat.potfile")));
            Assert.False(Directory.Exists(Path.Combine(runtime, "kernels")));
            Assert.False(Directory.Exists(Path.Combine(runtime, "cache")));
            foreach (var folder in new[] { "feeds", "pcfg", "tables" }) Assert.True(File.Exists(Path.Combine(runtime, folder, "fixture")));
            Assert.Equal(before, Directory.GetFiles(source, "*", SearchOption.AllDirectories).Order());
            Assert.Equal(runtime, await workspace.PrepareAsync(executable));
            await File.AppendAllTextAsync(Path.Combine(source, "feeds", "fixture"), "updated");
            var refreshed = await workspace.PrepareAsync(executable);
            Assert.NotEqual(runtime, refreshed);
            Assert.Equal("support assetupdated", await File.ReadAllTextAsync(Path.Combine(refreshed, "feeds", "fixture")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task InterruptedCopyHasNoReadyMarkerAndCanBeRetried()
    {
        var root = Path.Combine(Path.GetTempPath(), "HashLynx-runtime-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var executable = Path.Combine(root, "hashcat.exe");
        try
        {
            await File.WriteAllTextAsync(executable, "fixture");
            var workspace = new HashcatRuntimeWorkspace(Path.Combine(root, "cache"));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.PrepareAsync(executable, new CancellationToken(true)));
            Assert.Empty(Directory.GetFiles(root, ".hashlynx-ready", SearchOption.AllDirectories));
            var runtime = await workspace.PrepareAsync(executable);
            Assert.True(File.Exists(Path.Combine(runtime, ".hashlynx-ready")));
        }
        finally { Directory.Delete(root, true); }
    }
}
