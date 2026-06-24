using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.VSDiagnostics;

namespace Stndr.Benchmarks;

[CPUUsageDiagnoser]
[MemoryDiagnoser]
[SimpleJob(warmupCount: 1, iterationCount: 8)]
public class SefariaLibraryServiceBenchmark
{
    private const int BookCount = 100;

    private SefariaLibraryService _service = null!;
    private string _testDataPath = null!;
    private string _sourcesFolder = null!;

    [GlobalSetup]
    public void Setup() => EnsureBenchmarkData();

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_testDataPath))
        {
            Directory.Delete(_testDataPath, true);
        }
    }

    private void EnsureBenchmarkData()
    {
        if (_service is not null)
        {
            return;
        }

        _testDataPath = Path.Combine(Path.GetTempPath(), "StndrBenchmark_" + Guid.NewGuid());
        _sourcesFolder = Path.Combine(_testDataPath, "sources");
        Directory.CreateDirectory(_sourcesFolder);

        var sampleBookTemplate = """
            {
                "title": "Sample Book {i}",
                "heTitle": "ספר {i}",
                "indexTitle": "Sample Book {i}",
                "heIndexTitle": "ספר {i}",
                "language": "en",
                "versionTitle": "Test Version",
                "categories": ["Torah", "Commentaries"],
                "text": [
                    ["Line 1", "Line 2", "Line 3"],
                    ["Line 4", "Line 5", "Line 6"]
                ]
            }
            """;

        var talmudBookTemplate = """
            {
                "title": "Talmud Sample {i}",
                "heTitle": "תלמוד {i}",
                "language": "he",
                "pages": [
                    {
                        "indexTitle": "Talmud Sample {i}",
                        "heIndexTitle": "תלמוד {i}",
                        "he": ["Hebrew text 1", "Hebrew text 2"],
                        "text": ["English text 1", "English text 2"]
                    }
                ]
            }
            """;

        for (var i = 0; i < BookCount; i++)
        {
            var template = i % 2 == 0 ? sampleBookTemplate : talmudBookTemplate;
            var json = template.Replace("{i}", i.ToString(), StringComparison.Ordinal);
            File.WriteAllText(Path.Combine(_sourcesFolder, $"book_{i}.json"), json, Encoding.UTF8);
        }

        var indexJson = """
            [
              {
                "title": "Torah",
                "category": "Torah",
                "order": 1,
                "contents": [
                  {
                    "title": "Sample Book 0",
                    "order": 1,
                    "categories": ["Torah", "Commentaries"]
                  },
                  {
                    "title": "Talmud Sample 1",
                    "order": 2,
                    "categories": ["Talmud", "Bavli"]
                  }
                ]
              }
            ]
            """;
        File.WriteAllText(Path.Combine(_sourcesFolder, "sefaria_toc.json"), indexJson, Encoding.UTF8);

        _service = new SefariaLibraryService(_testDataPath);
        _service.GetInstalledBooks();
    }

    private void ClearCaches() => _service.SetStorageRootFolder(_testDataPath);

    [IterationSetup(Target = nameof(GetInstalledBooks_Cold))]
    public void ResetCachesForGetInstalledBooksCold() => ClearCaches();

    [IterationSetup(Target = nameof(BuildInstalledTree_Cold))]
    public void ResetCachesForBuildInstalledTreeCold() => ClearCaches();

    [IterationSetup(Target = nameof(GetInstalledBooks_Warm))]
    public void PrimeGetInstalledBooksWarmCache()
    {
        ClearCaches();
        _service.GetInstalledBooks();
    }

    [IterationSetup(Target = nameof(BuildInstalledTree_Warm))]
    public void PrimeBuildInstalledTreeWarmCache()
    {
        ClearCaches();
        _service.BuildInstalledTree();
    }

    [IterationSetup(Target = nameof(RestoreReaderTabs_Warm))]
    public void PrimeRestoreReaderTabsWarmCache()
    {
        ClearCaches();
        _service.GetInstalledBooks();
    }

    [Benchmark(Baseline = true)]
    public List<InstalledSefariaBook> GetInstalledBooks_Cold() => _service.GetInstalledBooks();

    [Benchmark]
    public List<InstalledSefariaBook> GetInstalledBooks_Warm() => _service.GetInstalledBooks();

    [Benchmark]
    public object BuildInstalledTree_Cold() => _service.BuildInstalledTree();

    [Benchmark]
    public object BuildInstalledTree_Warm() => _service.BuildInstalledTree();

    [Benchmark]
    public List<InstalledSefariaBook> RestoreReaderTabs_Warm()
    {
        _service.GetInstalledVersionsForTitle("Sample Book 0");
        _service.GetInstalledVersionsForTitle("Sample Book 2");
        _service.GetInstalledVersionsForTitle("Talmud Sample 1");
        return _service.GetInstalledVersionsForTitle("Sample Book 4");
    }
}