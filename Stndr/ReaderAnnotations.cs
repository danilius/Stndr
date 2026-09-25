using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Stndr;

public enum ReaderAnnotationKind { Bookmark, Highlight, Note }
public enum ReaderHighlightColor { Yellow, Green, Blue, Pink, Purple }

[Flags]
public enum ReaderAnnotationVisibility
{
    None = 0,
    Bookmarks = 1,
    Highlights = 2,
    Notes = 4,
    All = Bookmarks | Highlights | Notes
}

public sealed class ReaderAnnotationSegment
{
    public string Reference { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

public sealed class ReaderAnnotation
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public ReaderAnnotationKind Kind { get; set; }
    public string WorkTitle { get; set; } = string.Empty;
    public string BookKey { get; set; } = string.Empty;
    public string StartReference { get; set; } = string.Empty;
    public string EndReference { get; set; } = string.Empty;
    public string SelectedText { get; set; } = string.Empty;
    public string Note { get; set; } = string.Empty;
    public ReaderHighlightColor HighlightColor { get; set; } = ReaderHighlightColor.Yellow;
    public List<ReaderAnnotationSegment> Segments { get; set; } = new();
    public List<string> Tags { get; set; } = new();
    public bool IsFavourite { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime ModifiedAtUtc { get; set; } = DateTime.UtcNow;

    [System.Text.Json.Serialization.JsonIgnore]
    public string DisplayReference => string.Equals(StartReference, EndReference, StringComparison.Ordinal) ||
        string.IsNullOrWhiteSpace(EndReference) ? StartReference : $"{StartReference}–{EndReference}";
}

internal sealed class ReaderAnnotationService
{
    private const string FileName = "annotations.json";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _gate = new();
    private string? _filePath;
    private List<ReaderAnnotation> _annotations = new();
    private ReaderAnnotationVisibility _visibility = ReaderAnnotationVisibility.All;

    public IReadOnlyList<ReaderAnnotation> Annotations
    {
        get { lock (_gate) { return _annotations.ToArray(); } }
    }
    public ReaderAnnotationVisibility Visibility
    {
        get { lock (_gate) { return _visibility; } }
    }


    public void SetStorageRootFolder(string? dataFolder)
    {
        lock (_gate)
        {
            _filePath = string.IsNullOrWhiteSpace(dataFolder) ? null : Path.Combine(Path.GetFullPath(dataFolder), FileName);
            var document = LoadCore(_filePath);
            _annotations = document.Annotations;
            _visibility = document.Visibility;
        }
    }

    public void SetVisibility(ReaderAnnotationVisibility visibility)
    {
        lock (_gate)
        {
            _visibility = visibility;
            SaveCore();
        }
    }

    public ReaderAnnotation Add(ReaderAnnotation annotation)
    {
        lock (_gate)
        {
            annotation.Id = string.IsNullOrWhiteSpace(annotation.Id) ? Guid.NewGuid().ToString("N") : annotation.Id;
            annotation.CreatedAtUtc = annotation.CreatedAtUtc == default ? DateTime.UtcNow : annotation.CreatedAtUtc;
            annotation.ModifiedAtUtc = DateTime.UtcNow;
            _annotations.Add(annotation);
            SaveCore();
            return annotation;
        }
    }

    public bool UpdateNote(string id, string note)
    {
        lock (_gate)
        {
            var annotation = _annotations.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.Ordinal));
            if (annotation is null || annotation.Kind != ReaderAnnotationKind.Note) return false;
            annotation.Note = note.Trim();
            annotation.ModifiedAtUtc = DateTime.UtcNow;
            SaveCore();
            return true;
        }
    }

    public bool Remove(string id)
    {
        lock (_gate)
        {
            var removed = _annotations.RemoveAll(item => string.Equals(item.Id, id, StringComparison.Ordinal)) > 0;
            if (removed) SaveCore();
            return removed;
        }
    }

    internal ReaderAnnotation? FindDuplicate(ReaderAnnotation candidate)
    {
        lock (_gate)
        {
            return _annotations.FirstOrDefault(item =>
                item.Kind == candidate.Kind &&
                string.Equals(item.WorkTitle, candidate.WorkTitle, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.StartReference, candidate.StartReference, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(item.EndReference, candidate.EndReference, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(Normalize(item.SelectedText), Normalize(candidate.SelectedText), StringComparison.Ordinal));
        }
    }

    private static ReaderAnnotationDocument LoadCore(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return new();
        try
        {
            return JsonSerializer.Deserialize<ReaderAnnotationDocument>(File.ReadAllText(filePath), JsonOptions) ?? new();
        }
        catch { return new(); }
    }

    private void SaveCore()
    {
        if (string.IsNullOrWhiteSpace(_filePath)) return;
        var folder = Path.GetDirectoryName(_filePath);
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var temporaryPath = _filePath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(new ReaderAnnotationDocument
        {
            Annotations = _annotations,
            Visibility = _visibility
        }, JsonOptions));
        File.Move(temporaryPath, _filePath, overwrite: true);
    }

    private static string Normalize(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private sealed class ReaderAnnotationDocument
    {
        public int Version { get; set; } = 1;
        public List<ReaderAnnotation> Annotations { get; set; } = new();
        public ReaderAnnotationVisibility Visibility { get; set; } = ReaderAnnotationVisibility.All;
    }
}
