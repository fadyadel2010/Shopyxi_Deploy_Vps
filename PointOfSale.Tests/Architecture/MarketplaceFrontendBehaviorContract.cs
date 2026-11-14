using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace PointOfSale.Tests.Architecture;

/// <summary>
/// Executes the existing actual TypeScript/Vitest authority tests instead of
/// requiring historical PASS prose. No skip/fallback/source-string substitute.
/// The fixture runner supplies a pinned offline Node/dependency tree read-only.
/// </summary>
internal static class MarketplaceFrontendBehaviorContract
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly Lazy<string> Runtime = new(VerifyInputs);
    private static readonly HashSet<string> AdditionalAuthorityTests = new(StringComparer.Ordinal) {
        "src/components/PosPermissionRouteGuard.test.tsx",
        "src/components/AuthBootstrap.test.tsx",
        "src/super-admin/auth/SuperAdminProtectedRoute.test.tsx"
    };

    internal static async Task AssertAsync(string relativeTest, int minimumCases)
    {
        if (!(relativeTest.StartsWith("src/marketplace/", StringComparison.Ordinal) || AdditionalAuthorityTests.Contains(relativeTest)) ||
            relativeTest.Contains("..", StringComparison.Ordinal) || relativeTest.Contains('\\') ||
            !(relativeTest.EndsWith(".test.ts", StringComparison.Ordinal) || relativeTest.EndsWith(".test.tsx", StringComparison.Ordinal)) || minimumCases < 1)
            throw new ArgumentException("Exact Marketplace runtime test required.");
        await Gate.WaitAsync();
        try
        {
            var runtime = Runtime.Value; var web = Path.Combine(runtime, "PointOfSale.Web");
            Assert.True(File.Exists(Path.Combine(web, relativeTest)));
            var tempRoot = Environment.GetEnvironmentVariable("Testing__TempRoot") ?? throw new InvalidOperationException("Owned test temp root required.");
            var work = Path.Combine(tempRoot, "frontend-contract-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var output = Path.Combine(work, "result.json"); var config = Path.Combine(work, "vitest.config.mjs");
            var settings = new {
                root = web, cacheDir = Path.Combine(work, "vite-cache"),
                esbuild = new { jsx = "automatic" },
                // Numeric loopback avoids Vite's localhost DNS probe. The owned
                // preload still rejects DNS, listeners and outbound sockets.
                server = new { host = "127.0.0.1" },
                test = new { globals = true, environment = "jsdom", include = new[] { relativeTest },
                    setupFiles = new[] { Path.Combine(web, "src/test/setup.ts") }, cache = false },
                resolve = new { dedupe = new[] { "react", "react-dom" }, alias = new Dictionary<string, string> {
                    ["@"] = Path.Combine(runtime, "PointOfSale.Storefront/src"), ["@web"] = Path.Combine(web, "src"),
                    ["@theme-authority"] = Path.Combine(runtime, "theme-authority") } }
            };
            await File.WriteAllTextAsync(config, "export default " + JsonSerializer.Serialize(settings) + ";\n");
            var start = new ProcessStartInfo(Path.Combine(runtime, "tools/node")) {
                WorkingDirectory = web, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
            };
            foreach (var arg in new[] { Path.Combine(web, "node_modules/vitest/vitest.mjs"), "run", "--config", config,
                "--maxWorkers=1", "--no-file-parallelism", "--reporter=json", "--outputFile=" + output }) start.ArgumentList.Add(arg);
            start.Environment["NODE_OPTIONS"] = "--require=" + Path.Combine(runtime, "tools/no-network.cjs");
            start.Environment["TMPDIR"] = work; start.Environment["TMP"] = work; start.Environment["TEMP"] = work;
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Offline Node process did not start.");
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                // Only this owned child tree, never user processes or browser sessions.
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
                throw new TimeoutException("Marketplace frontend contract exceeded its finite deadline.");
            }
            var logs = await stdout + "\n" + await stderr;
            await File.WriteAllTextAsync(Path.Combine(work, "node.log"), logs);
            Assert.True(process.ExitCode == 0, "Actual frontend contract failed: " + relativeTest + "\n" + logs);
            Assert.True(File.Exists(output), "Actual frontend contract result missing.");
            using var result = JsonDocument.Parse(await File.ReadAllTextAsync(output)); var root = result.RootElement;
            Assert.True(root.GetProperty("success").GetBoolean());
            var count = root.GetProperty("numTotalTests").GetInt32();
            Assert.True(count >= minimumCases); Assert.Equal(count, root.GetProperty("numPassedTests").GetInt32());
            Assert.Equal(0, root.GetProperty("numFailedTests").GetInt32()); Assert.Equal(0, root.GetProperty("numPendingTests").GetInt32());
            var suite = Assert.Single(root.GetProperty("testResults").EnumerateArray());
            Assert.Equal(Path.GetFullPath(Path.Combine(web, relativeTest)), Path.GetFullPath(suite.GetProperty("name").GetString()!));
            var evidence = Environment.GetEnvironmentVariable("APP17D_EVIDENCE_DIR") ?? throw new InvalidOperationException("Test evidence directory required.");
            var receipt = "frontend-contract-" + Guid.NewGuid().ToString("N");
            File.Copy(output, Path.Combine(evidence, receipt + ".results.json"));
            await File.WriteAllTextAsync(Path.Combine(evidence, receipt + ".binding.json"), JsonSerializer.Serialize(new {
                relativeTest, executed = count, nativeExit = process.ExitCode, authoritativeSourceMatched = true,
                nodeSha256 = Hash(Path.Combine(runtime, "tools/node")), lockSha256 = Hash(Path.Combine(web, "package-lock.json")),
                network = "denied by fixture network and retained no-network guard", providerConcurrencyProof = false,
                historicalCertificationClaim = false, applicationStarted = false
            }));
        }
        finally { Gate.Release(); }
    }

    private static string VerifyInputs()
    {
        var runtime = Environment.GetEnvironmentVariable("APP2_FRONTEND_CONTRACT_RUNTIME") ??
            throw new InvalidOperationException("Pinned offline frontend runtime missing; no skip or source-assertion fallback.");
        var source = Environment.GetEnvironmentVariable("POINT_OF_SALE_SOLUTION_ROOT") ?? throw new InvalidOperationException("Authoritative source root required.");
        Assert.Equal(Path.GetFullPath(Path.Combine(source, "..", "frontend-work")), Path.GetFullPath(runtime));
        foreach (var folder in new[] { "PointOfSale.Web", "PointOfSale.Storefront", "theme-authority" })
        {
            Dictionary<string, string> Inventory(string root) => SourceFiles(root)
                .ToDictionary(p => Path.GetRelativePath(root, p), Hash, StringComparer.Ordinal);
            var expected = Inventory(Path.Combine(source, folder)); var actual = Inventory(Path.Combine(runtime, folder));
            Assert.Equal(expected.Count, actual.Count);
            foreach (var row in expected) Assert.True(actual.TryGetValue(row.Key, out var hash) && hash == row.Value, "Frontend execution copy changed: " + folder + "/" + row.Key);
        }
        Assert.True(File.Exists(Path.Combine(runtime, "tools/node")));
        Assert.Equal("b1cbec894e45a5814b6ab756e1e14f8a76516273197e67e0412b57c1e10d0d9f", Hash(Path.Combine(runtime, "tools/node")));
        Assert.True(File.Exists(Path.Combine(runtime, "tools/no-network.cjs")));
        return runtime;
    }
    private static string Hash(string path)
    {
        using var stream = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
    private static IEnumerable<string> SourceFiles(string root)
    {
        foreach (var file in Directory.EnumerateFiles(root)) yield return file;
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if (Path.GetFileName(directory) is "node_modules" or "dist" or "bin" or "obj" or ".git") continue;
            foreach (var file in SourceFiles(directory)) yield return file;
        }
    }
}
