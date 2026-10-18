using System;
using System.Collections.Generic;
using System.Linq;

namespace MilestoneAdmin.Application.Activity
{
    public enum ActivityLevel
    {
        Info,
        Success,
        Warning,
        Error,
        Detail
    }

    public sealed class ActivityEntry
    {
        public DateTimeOffset Time { get; set; }
        public ActivityLevel Level { get; set; }
        public string Message { get; set; }
        public string Operation { get; set; }

        public string Symbol
        {
            get
            {
                switch (Level)
                {
                    case ActivityLevel.Success: return "✓";
                    case ActivityLevel.Error: return "✗";
                    case ActivityLevel.Warning: return "!";
                    default: return " ";
                }
            }
        }

        public override string ToString() => $"{Time:HH:mm:ss} {Symbol} {Message}";
    }

    /// <summary>
    /// Operational feedback for the Activity panel ("Retrieving recording servers... 3 found."). This is not a console:
    /// it shows messages produced by the application and selected PowerShell streams, never accepts input.
    /// </summary>
    public sealed class ActivityLog
    {
        private readonly object _lock = new object();
        private readonly List<ActivityEntry> _entries = new List<ActivityEntry>();
        private readonly int _capacity;

        public ActivityLog(int capacity = 5000)
        {
            _capacity = capacity;
        }

        public event EventHandler<ActivityEntry> EntryAdded;

        public IReadOnlyList<ActivityEntry> Entries
        {
            get
            {
                lock (_lock) return _entries.ToList();
            }
        }

        public ActivityEntry Add(ActivityLevel level, string message, string operation = null)
        {
            var e = new ActivityEntry { Time = DateTimeOffset.Now, Level = level, Message = message, Operation = operation };
            lock (_lock)
            {
                _entries.Add(e);
                if (_entries.Count > _capacity) _entries.RemoveRange(0, _entries.Count - _capacity);
            }

            EntryAdded?.Invoke(this, e);
            return e;
        }

        public ActivityEntry Info(string message, string operation = null) => Add(ActivityLevel.Info, message, operation);

        public ActivityEntry Success(string message, string operation = null) => Add(ActivityLevel.Success, message, operation);

        public ActivityEntry Warning(string message, string operation = null) => Add(ActivityLevel.Warning, message, operation);

        public ActivityEntry Error(string message, string operation = null) => Add(ActivityLevel.Error, message, operation);

        public ActivityEntry Detail(string message, string operation = null) => Add(ActivityLevel.Detail, message, operation);

        public void Clear()
        {
            lock (_lock) _entries.Clear();
        }
    }
}
