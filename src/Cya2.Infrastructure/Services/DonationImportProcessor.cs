using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Cya2.Application.Interfaces;
using Cya2.Application.Diagnostics;
using Cya2.Core.DTOs;
using Cya2.Core.Interfaces;
using Cya2.Core.Services;
using Cya2.Core.Imports;
using System.Text.Json;
using ImportResult = Cya2.Application.Interfaces.ImportResult;

namespace Cya2.Infrastructure.Services
{
    internal sealed class DonationImportProcessor : IDonationImportMaintenanceService, IImportProcessor
    {
        private readonly ILogger<DonationImportProcessor> _logger;
        private readonly IImportProgressService _progressService;
        private readonly IDonationRepository _donationRepository;
        private readonly IImportCacheInvalidator _cacheInvalidator;
        private readonly IDonationWorkbookReader _workbookReader;

        public string ImportType => "donations";

        public DonationImportProcessor(
            IConfiguration config,
            ILogger<DonationImportProcessor> logger,
            IImportProgressService progressService,
            IDonationRepository donationRepository,
            IImportCacheInvalidator cacheInvalidator,
            DonorFrequencyService frequencyService,
            DonorIdentityResolver identityResolver,
            IDonationWorkbookReader? workbookReader = null)
        {
            _logger = logger;
            _progressService = progressService;
            _donationRepository = donationRepository;
            _cacheInvalidator = cacheInvalidator;
            _workbookReader = workbookReader ?? new DonationWorkbookReader();
        }

        public async Task<ImportResult> ProcessAsync(Stream file, string progressId, CancellationToken cancellationToken)
        {
            _progressService.CompleteStep(progressId, "ImportQueued", "Completed");
            _progressService.AddStep(progressId, "ImportWriteGate", "ImportWriteGate");
            using var lease = await FinancialWriteCoordinator.EnterAsync(cancellationToken);
            _progressService.CompleteStep(progressId, "ImportWriteGate", "Completed");
            try { return await ProcessAsync(file, cancellationToken, progressId); }
            catch (ImportOutcomeUnknownException)
            {
                try { _cacheInvalidator.Invalidate(CacheDataDomains.Donations | CacheDataDomains.DonorContacts); }
                catch { _logger.LogWarning("Uncertain donation import cache refresh failed."); }
                throw;
            }
        }

        public async Task<DonationRecategorizationResult> RecategorizeAllDonationsAsync(CancellationToken ct)
        {
            using var lease = await FinancialWriteCoordinator.EnterAsync(ct);
            var updated = await _donationRepository.RecategorizeAllDonationsAsync(ct);
            _cacheInvalidator.Invalidate(CacheDataDomains.Donations | CacheDataDomains.DonorContacts);
            return new DonationRecategorizationResult { DonationDataRowsUpdated = updated };
        }

        private async Task<ImportResult> ProcessAsync(Stream file, CancellationToken ct, string progressId)
        {
            _progressService.AddStep(progressId, "File Validation", "Validating donation rows and source IDs...");
            var validation = _workbookReader.Read(file, ct);
            var result = new ImportResult
            {
                ProgressId = progressId,
                TotalRows = validation.SourceRowCount,
                DuplicateRowsMerged = validation.DuplicateRowsMerged
            };
            if (!validation.IsValid)
            {
                result.Errors.AddRange(validation.Errors.Select(error => error.Message));
                result.FailedRows = validation.Errors.Where(error => error.RowNumber > 1).Select(error => error.RowNumber).Distinct().Count();
                _progressService.AddErrors(progressId, result.Errors);
                _progressService.CompleteStep(progressId, "File Validation", "Failed - Existing data was not changed");
                return result;
            }

            _progressService.CompleteStep(progressId, "File Validation", "Completed");
            foreach (var row in validation.Donations.Where(row => row.SourceRows.Length > 1))
                result.DuplicateReport.Add(new DonationImportReportEntry
                {
                    CanonicalRowNumber = row.SourceRows[0],
                    DuplicateRowNumbers = row.SourceRows.Skip(1).ToArray(),
                    GiftImportId = row.GiftImportId ?? string.Empty,
                    Fund = row.Fund,
                    Amount = row.Amount,
                    DonorName = row.DonorDisplayName,
                    Reason = "Matching gift allocation and source identities; contact observations consolidated."
                });

            var rows = validation.Donations.Select(row => new CanonicalDonationWriteModel
            {
                Donor = new DonorWriteModel
                {
                    Fund = row.Fund,
                    DisplayName = row.DonorDisplayName,
                    IdentityKey = row.IsAnonymous
                        ? row.CreditedDonorSourceId is { } anonymousId ? "anonymous:id:" + anonymousId : "anonymous:unidentified:" + Guid.NewGuid().ToString("N")
                        : "id:" + row.CreditedDonorSourceId,
                    ConstituentId = row.IsAnonymous ? null : row.CreditedDonorSourceId,
                    ResolutionSource = row.IsAnonymous ? "Anonymous" : "SourceId",
                    ResolutionVersion = 2
                },
                Date = row.Date,
                Amount = row.Amount,
                Fund = row.Fund,
                AccountName = row.AccountName,
                GiftImportId = row.GiftImportId,
                ConstituentId = row.ConstituentId,
                SoftCreditRecipientId = row.SoftCreditRecipientId,
                IsSoftCredit = row.IsSoftCredit || row.IsThirdPartySoftCredit,
                IsAnonymous = row.IsAnonymous,
                PaymentMethod = row.PaymentMethod,
                GiftType = row.GiftType,
                InternName = row.InternDisplayName,
                InternSourceId = row.InternSourceId,
                PrimaryAddressee = row.PrimaryAddressee,
                SoftCreditName = row.SoftCreditName,
                SourceEvidenceJson = JsonSerializer.Serialize(row)
            }).ToArray();

            _progressService.SetExpected(progressId, rows.Length);
            ct.ThrowIfCancellationRequested();
            string? phase = null;
            var persisted = await _donationRepository.ReplaceCanonicalDonationsAsync(rows, ct, update =>
            {
                try
                {
                    if (phase != update.Phase)
                    {
                        if (phase is not null) _progressService.CompleteStep(progressId, phase, "Completed");
                        phase = update.Phase;
                        _progressService.AddStep(progressId, phase, phase);
                    }
                    _progressService.UpdateStep(progressId, phase, phase,
                        update.Total is > 0 ? $"{update.Completed:N0} / {update.Total:N0}" : string.Empty);
                    _progressService.SetStatus(progressId, phase);
                }
                catch { _logger.LogWarning("Import progress update failed."); }
            });
            if (phase is not null) _progressService.CompleteStep(progressId, phase, "Completed");
            result.InsertedRows = persisted.Inserted;
            ImportCompletion.Notify(result, progressId, CacheDataDomains.Donations | CacheDataDomains.DonorContacts,
                _cacheInvalidator, _progressService, _logger);
            return result;
        }
    }
}
