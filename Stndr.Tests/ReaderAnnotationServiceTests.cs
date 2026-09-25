using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Stndr.Tests;

public sealed class ReaderAnnotationServiceTests
{
    [Fact]
    public void Annotations_and_visibility_round_trip_through_the_data_folder()
    {
        var folder = CreateTemporaryFolder();
        try
        {
            var service = new ReaderAnnotationService();
            service.SetStorageRootFolder(folder);
            var note = service.Add(new ReaderAnnotation
            {
                Kind = ReaderAnnotationKind.Note,
                WorkTitle = "Berakhot",
                BookKey = "Berakhot|he|Test",
                StartReference = "2a.1",
                EndReference = "2a.1",
                SelectedText = "מאימתי קורין",
                Note = "Opening question",
                Segments =
                {
                    new ReaderAnnotationSegment
                    {
                        Reference = "2a.1",
                        Language = "he",
                        Text = "מאימתי קורין"
                    }
                }
            });
            service.SetVisibility(ReaderAnnotationVisibility.Bookmarks | ReaderAnnotationVisibility.Notes);

            var reloaded = new ReaderAnnotationService();
            reloaded.SetStorageRootFolder(folder);

            var saved = Assert.Single(reloaded.Annotations);
            Assert.Equal(note.Id, saved.Id);
            Assert.Equal("Opening question", saved.Note);
            Assert.Equal("מאימתי קורין", Assert.Single(saved.Segments).Text);
            Assert.Equal(
                ReaderAnnotationVisibility.Bookmarks | ReaderAnnotationVisibility.Notes,
                reloaded.Visibility);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Duplicate_matching_ignores_incidental_whitespace()
    {
        var service = new ReaderAnnotationService();
        var existing = service.Add(new ReaderAnnotation
        {
            Kind = ReaderAnnotationKind.Bookmark,
            WorkTitle = "Genesis",
            StartReference = "1.1",
            EndReference = "1.1",
            SelectedText = "In the beginning"
        });

        var duplicate = service.FindDuplicate(new ReaderAnnotation
        {
            Kind = ReaderAnnotationKind.Bookmark,
            WorkTitle = "genesis",
            StartReference = "1.1",
            EndReference = "1.1",
            SelectedText = " In   the\nbeginning "
        });

        Assert.Equal(existing.Id, duplicate?.Id);
    }

    [Fact]
    public void Notes_can_be_updated_and_annotations_removed()
    {
        var service = new ReaderAnnotationService();
        var note = service.Add(new ReaderAnnotation
        {
            Kind = ReaderAnnotationKind.Note,
            WorkTitle = "Genesis",
            StartReference = "1.1",
            EndReference = "1.1",
            Note = "First"
        });

        Assert.True(service.UpdateNote(note.Id, " Revised "));
        Assert.Equal("Revised", service.Annotations.Single().Note);
        Assert.True(service.Remove(note.Id));
        Assert.Empty(service.Annotations);
    }

    private static string CreateTemporaryFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Stndr.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }
}
