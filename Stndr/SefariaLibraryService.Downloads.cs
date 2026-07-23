using System.Threading;
using System.Threading.Tasks;

namespace Stndr;

public sealed partial class SefariaLibraryService
{
    /// <summary>
    /// Schemas come from the offline library. Per-book schema downloads are retired.
    /// </summary>
    public Task EnsureSchemaDownloadedAsync(string title, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Warm the offline schema cache if present; no network download.
        _ = GetBookSchema(title);
        return Task.CompletedTask;
    }
}
