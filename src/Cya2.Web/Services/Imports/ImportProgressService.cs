using System.Collections.Concurrent;
using System.Collections.Immutable;
using Cya2.Core.Interfaces;

namespace cya2.Services.Imports
{
    public sealed record ImportStepSnapshot(
        DateTime StartedAtUtc,
        DateTime? FinishedAtUtc,
        string Name,
        string Status,
        bool IsCompleted,
        bool IsActive,
        string Details);

    public sealed record ImportProgressSnapshot(
        int TotalRows,
        int InsertedRows,
        int FailedRows,
        int ExpectedRows,
        string Status,
        ImmutableArray<string> Errors,
        bool IsComplete,
        ImmutableArray<ImportStepSnapshot> Steps,
        string ImportType);

    public sealed class ImportProgressService : IImportProgressService
    {
        private sealed class Step
        {
            public DateTime StartedAtUtc { get; set; }
            public DateTime? FinishedAtUtc { get; set; }
            public string Name { get; set; } = string.Empty;
            public string Status { get; set; } = string.Empty;
            public bool IsCompleted { get; set; }
            public bool IsActive { get; set; }
            public string Details { get; set; } = string.Empty;
        }

        private sealed class Progress
        {
            public DateTime LastUpdatedAtUtc { get; set; } = DateTime.UtcNow;
            public int TotalRows { get; set; }
            public int InsertedRows { get; set; }
            public int FailedRows { get; set; }
            public int ExpectedRows { get; set; }
            public string Status { get; set; } = string.Empty;
            public List<string> Errors { get; } = new();
            public bool IsComplete { get; set; }
            public List<Step> Steps { get; } = new();
            public string ImportType { get; init; } = string.Empty;
            public string OwnerUserId { get; init; } = string.Empty;
        }

        private readonly ConcurrentDictionary<string, Progress> _store = new();

        public void Start(string id) => _store[id] = new Progress();

        public void Start(string id, int expectedRows) => _store[id] = new Progress { ExpectedRows = expectedRows };

        public void Start(string id, string importType, string ownerUserId)
            => _store[id] = new Progress { ImportType = importType, OwnerUserId = ownerUserId };

        private void Update(string id, Action<Progress> update)
        {
            if (!_store.TryGetValue(id, out var progress)) return;
            lock (progress)
            {
                update(progress);
                progress.LastUpdatedAtUtc = DateTime.UtcNow;
            }
        }

        public void AddStep(string id, string stepName, string status = "Starting...")
        {
            Update(id, progress =>
            {
                foreach (var step in progress.Steps) step.IsActive = false;
                progress.Steps.Add(new Step { StartedAtUtc = DateTime.UtcNow, Name = stepName, Status = status, IsActive = true });
            });
        }

        public void UpdateStep(string id, string stepName, string status, string? details = null)
        {
            Update(id, progress =>
            {
                var step = progress.Steps.FirstOrDefault(s => s.Name == stepName);
                if (step is null) return;
                step.Status = status;
                if (details is not null) step.Details = details;
            });
        }

        public void CompleteStep(string id, string stepName, string completionStatus, string? details = null)
        {
            Update(id, progress =>
            {
                var step = progress.Steps.FirstOrDefault(s => s.Name == stepName);
                if (step is null) return;
                step.Status = completionStatus;
                step.IsCompleted = true;
                step.FinishedAtUtc = DateTime.UtcNow;
                step.IsActive = false;
                if (details is not null) step.Details = details;
            });
        }

        public void Report(string id, int totalRows, int insertedRows, int failedRows)
            => Report(id, totalRows, insertedRows, failedRows, null);

        public void Report(string id, int totalRows, int insertedRows, int failedRows, string? status)
        {
            Update(id, progress =>
            {
                progress.TotalRows = totalRows;
                progress.InsertedRows = insertedRows;
                progress.FailedRows = failedRows;
                if (!string.IsNullOrEmpty(status)) progress.Status = status;
            });
        }

        public void SetExpected(string id, int expectedRows) => Update(id, progress => progress.ExpectedRows = expectedRows);

        public void SetStatus(string id, string status) => Update(id, progress => progress.Status = status);

        public void AddErrors(string id, IEnumerable<string> errors)
        {
            if (errors is null) return;
            var items = errors.Where(error => !string.IsNullOrEmpty(error)).ToArray();
            Update(id, progress => progress.Errors.AddRange(items));
        }

        public void Complete(string id)
        {
            Update(id, progress =>
            {
                progress.IsComplete = true;
                if (string.IsNullOrEmpty(progress.Status)) progress.Status = "Complete";
                foreach (var step in progress.Steps)
                {
                    step.IsActive = false;
                    step.FinishedAtUtc ??= DateTime.UtcNow;
                }
            });
        }

        public bool Remove(string id) => _store.TryRemove(id, out _);

        public int RemoveExpired(DateTime utcNow, TimeSpan lifetime)
        {
            var removed = 0;
            foreach (var entry in _store)
            {
                lock (entry.Value)
                {
                    if (entry.Value.IsComplete && utcNow - entry.Value.LastUpdatedAtUtc >= lifetime &&
                        _store.TryRemove(new KeyValuePair<string, Progress>(entry.Key, entry.Value)))
                        removed++;
                }
            }
            return removed;
        }

        public ImportProgressSnapshot? Get(string id)
            => _store.TryGetValue(id, out var progress) ? Snapshot(progress) : null;

        public ImportProgressSnapshot? GetOwned(string id, string ownerUserId, string importType)
        {
            var progress = GetOwned(id, ownerUserId);
            return progress is not null && string.Equals(progress.ImportType, importType, StringComparison.OrdinalIgnoreCase)
                ? progress : null;
        }

        public ImportProgressSnapshot? GetOwned(string id, string ownerUserId)
        {
            if (!_store.TryGetValue(id, out var progress) ||
                !string.Equals(progress.OwnerUserId, ownerUserId, StringComparison.Ordinal)) return null;
            return Snapshot(progress);
        }

        private static ImportProgressSnapshot Snapshot(Progress progress)
        {
            lock (progress)
            {
                return new ImportProgressSnapshot(
                    progress.TotalRows, progress.InsertedRows, progress.FailedRows, progress.ExpectedRows,
                    progress.Status, progress.Errors.ToImmutableArray(), progress.IsComplete,
                    progress.Steps.Select(step => new ImportStepSnapshot(
                        step.StartedAtUtc, step.FinishedAtUtc, step.Name, step.Status,
                        step.IsCompleted, step.IsActive, step.Details)).ToImmutableArray(),
                    progress.ImportType);
            }
        }

        public bool RemoveOwned(string id, string ownerUserId)
        {
            if (!_store.TryGetValue(id, out var progress) ||
                !string.Equals(progress.OwnerUserId, ownerUserId, StringComparison.Ordinal)) return false;
            return _store.TryRemove(new KeyValuePair<string, Progress>(id, progress));
        }
    }
}