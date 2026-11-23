using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace Cya2.Application.Services;

public sealed record ImportPreview(byte[] Data, string FileName, string ContentType, DateTime CreatedAtUtc, string OwnerUserId, string ImportType)
{
    public DateOnly? MinimumDonationDate { get; init; }
}

public sealed class ImportPreviewStore
{
    private readonly int _maximumRetainedPreviews;
    private readonly long _maximumRetainedBytes;
    private readonly ConcurrentDictionary<string, ImportPreview> _previews = new(StringComparer.Ordinal);
    private long _retainedBytes;
    private readonly HashSet<string> _claimed = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);

    public ImportPreviewStore(IOptions<ImportUploadOptions> options, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        var settings = options.Value;
        if (settings.MaximumRetainedPreviews <= 0 || settings.MaximumRetainedPreviewBytes <= 0)
            throw new InvalidOperationException("Preview retention limits must be greater than zero.");

        _maximumRetainedPreviews = settings.MaximumRetainedPreviews;
        _maximumRetainedBytes = settings.MaximumRetainedPreviewBytes;
    }

    public bool TryRemoveOwned(string previewId, string ownerUserId, string importType, out ImportPreview? preview, Func<ImportPreview, bool>? canRemove = null)
    {
        lock (_previews)
        {
            RemoveExpired(_clock.GetUtcNow().UtcDateTime, Lifetime);
            if (_claimed.Contains(previewId) || !_previews.TryGetValue(previewId, out var candidate) ||
                !string.Equals(candidate.OwnerUserId, ownerUserId, StringComparison.Ordinal) ||
                !string.Equals(candidate.ImportType, importType, StringComparison.OrdinalIgnoreCase))
            {
                preview = null;
                return false;
            }

            if (canRemove is not null && !canRemove(candidate))
            {
                preview = null;
                return false;
            }

            if (!_previews.TryRemove(previewId, out preview) || preview is null)
                return false;

            _retainedBytes -= preview.Data.LongLength;
            return true;
        }
    }

    public bool TryClaimOwned(string previewId, string ownerUserId, string importType, out ImportPreview? preview, Func<ImportPreview, bool> canClaim)
    {
        lock (_previews)
        {
            RemoveExpired(_clock.GetUtcNow().UtcDateTime, Lifetime);
            preview = null;
            if (_claimed.Contains(previewId) || !_previews.TryGetValue(previewId, out var candidate) ||
                !string.Equals(candidate.OwnerUserId, ownerUserId, StringComparison.Ordinal) ||
                !string.Equals(candidate.ImportType, importType, StringComparison.OrdinalIgnoreCase) || !canClaim(candidate))
                return false;
            _claimed.Add(previewId);
            preview = candidate;
            return true;
        }
    }

    public void ReleaseClaim(string previewId, bool consumed)
    {
        lock (_previews)
        {
            if (!_claimed.Remove(previewId)) return;
            if (consumed && _previews.TryRemove(previewId, out var preview))
                _retainedBytes -= preview.Data.LongLength;
        }
    }

    public bool TrySet(string previewId, ImportPreview preview)
    {
        lock (_previews)
        {
            if (_previews.Count >= _maximumRetainedPreviews || _retainedBytes > _maximumRetainedBytes - preview.Data.LongLength)
                RemoveExpired(_clock.GetUtcNow().UtcDateTime, Lifetime);
            if (_previews.Count >= _maximumRetainedPreviews ||
                _retainedBytes > _maximumRetainedBytes - preview.Data.LongLength)
            {
                return false;
            }

            if (_previews.TryAdd(previewId, preview))
            {
                _retainedBytes += preview.Data.LongLength;
                return true;
            }

            return false;
        }
    }

    public bool TryRemove(string previewId, out ImportPreview? preview)
    {
        lock (_previews)
        {
            if (_claimed.Contains(previewId)) { preview = null; return false; }
            if (!_previews.TryRemove(previewId, out preview) || preview is null)
                return false;

            _retainedBytes -= preview.Data.LongLength;
            return true;
        }
    }

    public int RemoveExpired(DateTime utcNow, TimeSpan lifetime)
    {
        var removed = 0;
        foreach (var entry in _previews)
        {
            if (utcNow - entry.Value.CreatedAtUtc < lifetime)
            {
                continue;
            }

            lock (_previews)
            {
                if (!_claimed.Contains(entry.Key) && _previews.TryRemove(new KeyValuePair<string, ImportPreview>(entry.Key, entry.Value)))
                {
                    _retainedBytes -= entry.Value.Data.LongLength;
                    removed++;
                }
            }
        }

        return removed;
    }
}
