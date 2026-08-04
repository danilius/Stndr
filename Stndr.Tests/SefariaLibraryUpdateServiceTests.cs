using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Stndr.Tests;

public sealed class SefariaLibraryUpdateServiceTests
{
    [Fact]
    public void Reminder_schedule_ignores_nightly_snapshot_identity_changes()
    {
        var now = new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc);
        var snooze = new LibraryUpdateSnoozeState("etag:yesterdays-dump", now.AddDays(14));

        Assert.True(SefariaLibraryUpdateService.IsSnoozed(snooze, now));
        Assert.False(SefariaLibraryUpdateService.IsSnoozed(snooze, now.AddDays(14)));
    }

    [Fact]
    public void Automatic_check_schedule_is_seeded_from_installed_snapshot_and_respects_later_date()
    {
        var installedUtc = new DateTime(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);
        var nextCheck = SefariaLibraryUpdateService.GetNextAutomaticCheckUtc(installedUtc, 14);

        Assert.Equal(new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc), nextCheck);
        Assert.False(SefariaLibraryUpdateService.IsAutomaticCheckDue(
            nextCheck,
            snoozedUntilUtc: null,
            new DateTime(2026, 8, 4, 12, 0, 0, DateTimeKind.Utc)));
        Assert.True(SefariaLibraryUpdateService.IsAutomaticCheckDue(
            nextCheck,
            snoozedUntilUtc: null,
            nextCheck));

        var later = nextCheck.AddDays(7);
        Assert.False(SefariaLibraryUpdateService.IsAutomaticCheckDue(nextCheck, later, nextCheck));
        Assert.True(SefariaLibraryUpdateService.IsAutomaticCheckDue(nextCheck, later, later));
    }

    [Fact]
    public async Task New_nightly_dump_stays_hidden_until_reminder_date()
    {
        var folder = Path.Combine(Path.GetTempPath(), "stndr-update-schedule-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(SefariaOfflineLibraryPaths.DatabaseFolder(folder));
        await File.WriteAllBytesAsync(SefariaOfflineLibraryPaths.ActiveDatabase(folder), []);
        await File.WriteAllTextAsync(
            SefariaOfflineLibraryPaths.InstallMetadata(folder),
            JsonSerializer.Serialize(new SefariaOfflineLibraryInstallMetadata
            {
                InstalledAtUtc = DateTime.UtcNow.AddDays(-1),
                SourceRemoteETag = "\"installed-dump\""
            }));

        try
        {
            using var service = new SefariaLibraryUpdateService(new FixedUpdateSource(
                new SefariaRemoteSnapshot(
                    new Uri("https://example.test/sefaria-dump.tar.gz"),
                    "\"new-nightly-dump\"",
                    DateTimeOffset.UtcNow,
                    123)));
            var snooze = new LibraryUpdateSnoozeState(
                "etag:\"different-previous-offer\"",
                DateTime.UtcNow.AddDays(7));

            var state = await service.CheckNowAsync(folder, snooze);

            Assert.Equal(SefariaLibraryUpdateMode.Hidden, state.Mode);
            Assert.Contains("paused until", state.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    private sealed class FixedUpdateSource(SefariaRemoteSnapshot snapshot) : ISefariaLibraryUpdateSource
    {
        public Task<SefariaRemoteSnapshot> GetLatestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot);
    }
}
