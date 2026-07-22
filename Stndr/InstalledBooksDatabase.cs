using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace Stndr;

/// <summary>
/// Serializes access to the installed-books manifest so readers and writers cannot
/// open the file at the same time.
/// </summary>
internal sealed class InstalledBooksDatabase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly SemaphoreSlim _fileAccessQueue = new(1, 1);

    public InstalledBooksDatabase(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    public DateTime GetLastWriteTimeUtc()
    {
        _fileAccessQueue.Wait();
        try
        {
            return File.Exists(FilePath)
                ? File.GetLastWriteTimeUtc(FilePath)
                : DateTime.MinValue;
        }
        finally
        {
            _fileAccessQueue.Release();
        }
    }

    public List<InstalledSefariaBook> Read()
    {
        _fileAccessQueue.Wait();
        try
        {
            if (!File.Exists(FilePath))
            {
                return new List<InstalledSefariaBook>();
            }

            var json = File.ReadAllText(FilePath, Encoding.UTF8);
            return JsonSerializer.Deserialize<List<InstalledSefariaBook>>(json)
                ?? new List<InstalledSefariaBook>();
        }
        finally
        {
            _fileAccessQueue.Release();
        }
    }

    public DateTime Write(List<InstalledSefariaBook> installed)
    {
        var json = JsonSerializer.Serialize(installed, JsonOptions);

        _fileAccessQueue.Wait();
        try
        {
            File.WriteAllText(FilePath, json, Encoding.UTF8);
            return File.GetLastWriteTimeUtc(FilePath);
        }
        finally
        {
            _fileAccessQueue.Release();
        }
    }
}
