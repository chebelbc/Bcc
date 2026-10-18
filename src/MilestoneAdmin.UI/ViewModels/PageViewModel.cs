using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using MilestoneAdmin.Application.Execution;
using MilestoneAdmin.Domain.Changes;
using MilestoneAdmin.Domain.Classification;
using MilestoneAdmin.Domain.Errors;
using MilestoneAdmin.UI.Mvvm;

namespace MilestoneAdmin.UI.ViewModels
{
    public abstract class PageViewModel : ObservableObject
    {
        private bool _isBusy;
        private string _busyText;
        private AdminError _error;
        private ReviewViewModel _review;

        protected PageViewModel(AppServices services, string title)
        {
            Services = services;
            Title = title;
            DismissErrorCommand = new RelayCommand(() => Error = null);
            ShowErrorDetailsCommand = new RelayCommand(() => Services.Dialogs.ShowError(Error), () => Error != null);
        }

        public AppServices Services { get; }

        public string Title { get; }

        public bool IsBusy
        {
            get => _isBusy;
            set => Set(ref _isBusy, value);
        }

        public string BusyText
        {
            get => _busyText;
            set => Set(ref _busyText, value);
        }

        /// <summary>The last error, shown as a banner with an expandable details dialog.</summary>
        public AdminError Error
        {
            get => _error;
            set => Set(ref _error, value);
        }

        /// <summary>Review &amp; Apply overlay for configuration changes.</summary>
        public ReviewViewModel Review
        {
            get => _review;
            set => Set(ref _review, value);
        }

        public bool IsConnected => Services.Session.IsConnected;

        public bool ExpertMode => Services.Settings.ExpertMode;

        public ICommand DismissErrorCommand { get; }

        public ICommand ShowErrorDetailsCommand { get; }

        public virtual Task OnNavigatedToAsync() => Task.CompletedTask;

        public virtual void OnSessionChanged()
        {
            Raise(nameof(IsConnected));
            Raise(nameof(ExpertMode));
        }

        protected async Task<bool> Busy(string text, Func<Task> action)
        {
            IsBusy = true;
            BusyText = text;
            try
            {
                await action();
                return true;
            }
            finally
            {
                IsBusy = false;
                BusyText = null;
            }
        }
    }

    public enum ReviewStage
    {
        Review,
        Previewing,
        Applying,
        Done
    }

    public sealed class BulkResultRow : ObservableObject
    {
        public string Name { get; set; }
        public BulkItemStatus Status { get; set; }
        public string Message { get; set; }
        public AdminError Error { get; set; }

        /// <summary>The object this row refers to (used to retry failed items).</summary>
        public object Item { get; set; }

        public string StatusText => Status == BulkItemStatus.Succeeded ? "✓ Succeeded" : Status == BulkItemStatus.Failed ? "✗ Failed" : Status.ToString();
    }

    public sealed class ChangeRow
    {
        public string Target { get; set; }
        public string Operation { get; set; }
        public string Property { get; set; }
        public string Current { get; set; }
        public string New { get; set; }
        public bool Changed { get; set; }
    }

    /// <summary>
    /// Review &amp; Apply: what will change (diff), warnings, generated PowerShell (Expert Mode), optional
    /// PowerShell -WhatIf, strong confirmation for destructive operations, then per-object results with retry/export.
    /// </summary>
    public sealed class ReviewViewModel : ObservableObject
    {
        private readonly AppServices _services;
        private readonly Func<CancellationToken, Task<List<string>>> _whatIf;
        private readonly Func<IProgress<BulkProgress>, CancellationToken, Task<List<BulkResultRow>>> _apply;
        private readonly Action _closed;
        private ReviewStage _stage = ReviewStage.Review;
        private string _confirmationText;
        private double _progress;
        private string _progressText;
        private CancellationTokenSource _cts;
        private string _resultSummary;
        private readonly Action<List<object>> _retryFailed;

        public ReviewViewModel(AppServices services, ChangePlan plan,
            Func<CancellationToken, Task<List<string>>> whatIf,
            Func<IProgress<BulkProgress>, CancellationToken, Task<List<BulkResultRow>>> apply,
            Action closed,
            Action<List<object>> retryFailed = null)
        {
            _retryFailed = retryFailed;
            _services = services;
            Plan = plan;
            _whatIf = whatIf;
            _apply = apply;
            _closed = closed;
            foreach (var i in plan.Items)
            {
                if (i.Properties.Count == 0) Changes.Add(new ChangeRow { Target = i.TargetName, Operation = i.Operation, Changed = true });
                foreach (var p in i.Properties)
                    Changes.Add(new ChangeRow { Target = i.TargetName, Operation = i.Operation, Property = p.DisplayName, Current = p.Current == null && i.Operation == "Add" ? string.Empty : p.CurrentText, New = p.NewText, Changed = p.IsChanged || i.Operation != "Update" });
            }

            WhatIfCommand = new AsyncCommand(RunWhatIfAsync, () => Stage == ReviewStage.Review && _whatIf != null && Plan.WhatIfAvailable);
            ApplyCommand = new AsyncCommand(ApplyAsync, () => Stage == ReviewStage.Review && CanApply);
            CancelCommand = new RelayCommand(Cancel, () => Stage != ReviewStage.Done);
            CloseCommand = new RelayCommand(() => _closed?.Invoke());
            CopyScriptCommand = new RelayCommand(() => _services.Dialogs.CopyToClipboard(Plan.ScriptPreview), () => !string.IsNullOrEmpty(Plan.ScriptPreview));
            ExportScriptCommand = new RelayCommand(() => _services.Dialogs.SaveText("PowerShell script (*.ps1)|*.ps1", "XProtectChange.ps1", Plan.ScriptPreview), () => !string.IsNullOrEmpty(Plan.ScriptPreview));
            ExportFailedCommand = new RelayCommand(ExportFailed, () => Results.Any(r => r.Status == BulkItemStatus.Failed || r.Status == BulkItemStatus.Cancelled));
            RetryFailedCommand = new RelayCommand(() => _retryFailed(Results.Where(r => r.Status == BulkItemStatus.Failed || r.Status == BulkItemStatus.Cancelled).Select(r => r.Item).Where(i => i != null).ToList()),
                () => _retryFailed != null && IsDone && Results.Any(r => (r.Status == BulkItemStatus.Failed || r.Status == BulkItemStatus.Cancelled) && r.Item != null));
            ShowItemErrorCommand = new RelayCommand(o => _services.Dialogs.ShowError((o as BulkResultRow)?.Error), o => (o as BulkResultRow)?.Error != null);
        }

        public ChangePlan Plan { get; }

        public string Title => Plan.Title;

        public RiskLevel Risk => Plan.Risk;

        public string RiskText
        {
            get
            {
                var def = Domain.Classification.ClassificationRules.Embedded.Definition(Plan.Risk);
                return $"{Plan.Risk} ({def.Matrix}) - {def.Description}";
            }
        }

        public ObservableCollection<ChangeRow> Changes { get; } = new ObservableCollection<ChangeRow>();

        public ObservableCollection<string> WhatIfLines { get; } = new ObservableCollection<string>();

        public ObservableCollection<BulkResultRow> Results { get; } = new ObservableCollection<BulkResultRow>();

        public bool ExpertMode => _services.Settings.ExpertMode;

        public string PreviewLabel => Plan.PreviewKind == PreviewKind.PowerShellWhatIf
            ? "PowerShell WhatIf - output produced by MilestonePSTools running the command with -WhatIf."
            : Plan.WhatIfAvailable
                ? "Validated preview - computed by the application from current values. Use 'Preview with PowerShell -WhatIf' for MilestonePSTools' own preview."
                : "Validated preview - computed by the application. The command used does not support PowerShell -WhatIf, so no dry run is available.";

        public bool RequiresStrongConfirmation => Plan.RequiresStrongConfirmation;

        public string ConfirmationPrompt => $"Type {Plan.ConfirmationPhrase} to confirm";

        public string ConfirmationText
        {
            get => _confirmationText;
            set
            {
                if (Set(ref _confirmationText, value)) CommandManager.InvalidateRequerySuggested();
            }
        }

        public bool CanApply => Plan.ChangedItemCount > 0 && (!RequiresStrongConfirmation || string.Equals((ConfirmationText ?? string.Empty).Trim(), Plan.ConfirmationPhrase, StringComparison.Ordinal));

        public ReviewStage Stage
        {
            get => _stage;
            private set
            {
                if (Set(ref _stage, value))
                {
                    Raise(nameof(IsReviewing));
                    Raise(nameof(IsRunning));
                    Raise(nameof(IsDone));
                    CommandManager.InvalidateRequerySuggested();
                }
            }
        }

        public bool IsReviewing => Stage == ReviewStage.Review;

        public bool IsRunning => Stage == ReviewStage.Previewing || Stage == ReviewStage.Applying;

        public bool IsDone => Stage == ReviewStage.Done;

        public double Progress
        {
            get => _progress;
            private set => Set(ref _progress, value);
        }

        public string ProgressText
        {
            get => _progressText;
            private set => Set(ref _progressText, value);
        }

        public string ResultSummary
        {
            get => _resultSummary;
            private set => Set(ref _resultSummary, value);
        }

        public ICommand WhatIfCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand CloseCommand { get; }
        public ICommand CopyScriptCommand { get; }
        public ICommand ExportScriptCommand { get; }
        public ICommand ExportFailedCommand { get; }
        public ICommand ShowItemErrorCommand { get; }
        public ICommand RetryFailedCommand { get; }

        private async Task RunWhatIfAsync()
        {
            Stage = ReviewStage.Previewing;
            ProgressText = "Running MilestonePSTools with -WhatIf (nothing is changed)...";
            _cts = new CancellationTokenSource();
            try
            {
                var lines = await _whatIf(_cts.Token);
                WhatIfLines.Clear();
                foreach (var l in lines) WhatIfLines.Add(l);
                if (lines.Count == 0) WhatIfLines.Add("(MilestonePSTools reported no actions.)");
                Raise(nameof(PreviewLabel));
            }
            catch (OperationCanceledException)
            {
                WhatIfLines.Add("Preview cancelled.");
            }
            finally
            {
                Stage = ReviewStage.Review;
                ProgressText = null;
            }
        }

        private async Task ApplyAsync()
        {
            if (!RequiresStrongConfirmation && Plan.Risk != RiskLevel.ReadOnly && !_services.Dialogs.Confirm(Plan.Title, $"Apply {Plan.ChangedItemCount} change(s) now?")) return;
            Stage = ReviewStage.Applying;
            _cts = new CancellationTokenSource();
            var progress = new Progress<BulkProgress>(p =>
            {
                Progress = p.Percent;
                ProgressText = p.Current == null ? $"{p.Completed} of {p.Total} done" : $"{p.Completed} of {p.Total} done - {p.Current}" + (p.Failed > 0 ? $" ({p.Failed} failed)" : string.Empty);
            });
            try
            {
                var rows = await _apply(progress, _cts.Token);
                Results.Clear();
                foreach (var r in rows) Results.Add(r);
                var ok = rows.Count(r => r.Status == BulkItemStatus.Succeeded || r.Status == BulkItemStatus.SucceededWithWarnings);
                var failed = rows.Count(r => r.Status == BulkItemStatus.Failed);
                var cancelled = rows.Count(r => r.Status == BulkItemStatus.Cancelled);
                ResultSummary = $"{ok} succeeded, {failed} failed" + (cancelled > 0 ? $", {cancelled} cancelled" : string.Empty) + ".";
            }
            finally
            {
                Stage = ReviewStage.Done;
                Progress = 100;
            }
        }

        private void Cancel()
        {
            if (IsRunning) _cts?.Cancel();
            else _closed?.Invoke();
        }

        private void ExportFailed()
        {
            var csv = Domain.Csv.CsvParser.Write(new[] { "Item", "Status", "Error", "PowerShell error" },
                Results.Where(r => r.Status == BulkItemStatus.Failed || r.Status == BulkItemStatus.Cancelled)
                    .Select(r => (IReadOnlyList<string>)new[] { r.Name, r.Status.ToString(), r.Error?.Summary ?? r.Message ?? string.Empty, r.Error?.PowerShellMessage ?? string.Empty }));
            _services.Dialogs.SaveText("CSV file (*.csv)|*.csv", "failed-items.csv", csv);
        }

        public static List<BulkResultRow> Rows<T>(BulkResult<T> result) =>
            result.Items.Select(i => new BulkResultRow { Name = i.Name, Status = i.Status, Error = i.Error, Message = i.Error?.Summary ?? i.Message, Item = i.Item }).ToList();
    }
}
