using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using MilestoneAdmin.Application.Actions;
using MilestoneAdmin.Application.CommandExplorer;
using MilestoneAdmin.Domain.Inventory;
using MilestoneAdmin.UI.Mvvm;

namespace MilestoneAdmin.UI.ViewModels
{
    /// <summary>One quick action button: available, or disabled with the reason as tooltip.</summary>
    public sealed class QuickActionItem : ObservableObject
    {
        private string _reason;

        public QuickAction Action { get; set; }
        public string Title => Action.Title;
        public string Description => Action.Description;
        public bool IsDestructive => Action.StrongConfirmation;

        public string Reason
        {
            get => _reason;
            set
            {
                if (Set(ref _reason, value)) Raise(nameof(ToolTip));
            }
        }

        public string ToolTip => Reason ?? Description ?? Title;
    }

    /// <summary>
    /// Quick actions for one object type on a page: buttons, the typed input form of the chosen action, and the hand-off
    /// to the page's Review overlay. Inputs are generated from the command catalog (same controls as Command Explorer).
    /// </summary>
    public sealed class QuickActionsViewModel : ObservableObject
    {
        private readonly AppServices _services;
        private readonly PageViewModel _host;
        private readonly Func<IReadOnlyList<VmsObject>> _selection;
        private readonly Func<VmsObject> _parent;
        private readonly Func<Task> _afterApply;
        private string _objectType;
        private QuickActionForm _form;
        private string _validation;

        public QuickActionsViewModel(AppServices services, PageViewModel host, string objectType, Func<IReadOnlyList<VmsObject>> selection, Func<Task> afterApply, Func<VmsObject> parent = null)
        {
            _services = services;
            _host = host;
            _selection = selection;
            _afterApply = afterApply;
            _parent = parent ?? (() => null);
            OpenCommand = new RelayCommand(o => Open(o as QuickActionItem), o => o is QuickActionItem i && i.Reason == null && !_host.IsBusy);
            CancelCommand = new RelayCommand(() => Form = null);
            ReviewCommand = new RelayCommand(Review, () => Form != null && !_host.IsBusy);
            ObjectType = objectType;
        }

        public ObservableCollection<QuickActionItem> Actions { get; } = new ObservableCollection<QuickActionItem>();
        public ObservableCollection<FieldViewModel> Fields { get; } = new ObservableCollection<FieldViewModel>();

        public string ObjectType
        {
            get => _objectType;
            set
            {
                if (!Set(ref _objectType, value)) return;
                Form = null;
                Actions.Clear();
                foreach (var a in QuickActionCatalog.For(value)) Actions.Add(new QuickActionItem { Action = a });
                Refresh();
                Raise(nameof(HasActions));
            }
        }

        public bool HasActions => Actions.Count > 0;

        public QuickActionForm Form
        {
            get => _form;
            private set
            {
                if (!Set(ref _form, value)) return;
                Raise(nameof(IsOpen));
                Raise(nameof(FormTitle));
                Raise(nameof(FormDescription));
                Raise(nameof(TargetText));
                Fields.Clear();
                if (value != null)
                    foreach (var f in value.Form.Fields) Fields.Add(new FieldViewModel(f, ObjectsFor(f)));
                Validation = null;
            }
        }

        public bool IsOpen => Form != null;
        public string FormTitle => Form?.Action.Title;
        public string FormDescription => Form?.Action.Description;

        public string TargetText => Form == null
            ? null
            : Form.Action.Selection == ActionSelection.None
                ? $"Command: {Form.Command.Name}"
                : $"{Form.Selection.Count} selected: {string.Join(", ", Form.Selection.Take(5).Select(o => o.Name))}{(Form.Selection.Count > 5 ? ", ..." : string.Empty)}" +
                  (Form.Parent != null ? $" · {Form.Parent.Name}" : string.Empty) + $" · command: {Form.Command.Name}";

        public string Validation
        {
            get => _validation;
            set => Set(ref _validation, value);
        }

        public ICommand OpenCommand { get; }
        public ICommand CancelCommand { get; }
        public ICommand ReviewCommand { get; }

        /// <summary>Re-evaluates which actions are available (catalog, selection count). Call when the selection changes.</summary>
        public void Refresh()
        {
            var count = _selection().Count;
            foreach (var i in Actions)
                i.Reason = QuickActionService.UnavailableReason(i.Action, _services.Session.Catalog)
                           ?? (_services.Session.IsConnected ? null : "Connect first.")
                           ?? QuickActionService.SelectionProblem(i.Action, count)
                           ?? (i.Action.ParentParameter != null && _parent() == null ? "Open the members of a role first." : null);
            CommandManager.InvalidateRequerySuggested();
        }

        private IEnumerable<VmsObject> ObjectsFor(FormField f)
        {
            var inv = _services.Session.Inventory;
            foreach (var t in f.ObjectTypes)
            {
                switch (t)
                {
                    case "RecordingServer": foreach (var o in inv.RecordingServers) yield return o; break;
                    case "Hardware": foreach (var o in inv.Hardware) yield return o; break;
                    case "Camera": foreach (var o in inv.Cameras) yield return o; break;
                    case "Storage": foreach (var o in inv.Storage) yield return o; break;
                }
            }
        }

        private void Open(QuickActionItem item)
        {
            if (item == null) return;
            try
            {
                Form = _services.Actions.CreateForm(item.Action, _selection().ToList(), _parent());
            }
            catch (InvalidOperationException ex)
            {
                item.Reason = ex.Message;
            }
        }

        private void Review()
        {
            foreach (var f in Fields) f.Commit();
            var svc = _services.Actions;
            var errors = svc.Validate(Form);
            if (errors.Count > 0)
            {
                Validation = string.Join(Environment.NewLine, errors);
                return;
            }

            var plan = svc.BuildPlan(Form);
            _host.Review = new ReviewViewModel(_services, plan.Plan,
                plan.Plan.WhatIfAvailable ? ct => svc.RunWhatIfAsync(plan, ct) : (Func<System.Threading.CancellationToken, Task<List<string>>>)null,
                async (p, ct) => ReviewViewModel.Rows(await svc.ApplyAsync(plan, p, ct)),
                async () =>
                {
                    _host.Review = null;
                    Form = null;
                    await _afterApply();
                });
        }
    }
}
