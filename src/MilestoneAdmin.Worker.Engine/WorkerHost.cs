using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Security;

namespace MilestoneAdmin.Worker.Engine
{
    /// <summary>
    /// Non-interactive PowerShell host for the worker.
    ///
    /// * Text written to the host (Write-Host, and PowerShell's own "What if:" messages from -WhatIf) is forwarded to
    ///   the GUI as stream events. WhatIf output goes to the host UI rather than to a stream, which is why a custom
    ///   host is required to capture it.
    /// * Every prompt (Read-Host, Get-Credential, ShouldProcess confirmations, mandatory-parameter prompts) throws, so a
    ///   command that unexpectedly needs input fails fast with a clear error instead of hanging the worker.
    /// </summary>
    public sealed class WorkerHost : PSHost
    {
        private readonly Guid _id = Guid.NewGuid();
        private readonly WorkerHostUserInterface _ui;

        public WorkerHost(Action<string, string> write)
        {
            _ui = new WorkerHostUserInterface(write);
        }

        public override CultureInfo CurrentCulture => CultureInfo.CurrentCulture;

        public override CultureInfo CurrentUICulture => CultureInfo.CurrentUICulture;

        public override Guid InstanceId => _id;

        public override string Name => "XProtectAdminAssistantWorker";

        public override PSHostUserInterface UI => _ui;

        /// <summary>Emit any text written without a trailing newline (called at the end of every request).</summary>
        public void Flush() => _ui.Flush();

        public override Version Version => typeof(WorkerHost).Assembly.GetName().Version;

        public override void EnterNestedPrompt() => throw new NotSupportedException("Nested prompts are not supported by the worker host.");

        public override void ExitNestedPrompt() => throw new NotSupportedException("Nested prompts are not supported by the worker host.");

        public override void NotifyBeginApplication()
        {
        }

        public override void NotifyEndApplication()
        {
        }

        public override void SetShouldExit(int exitCode)
        {
        }
    }

    public sealed class InteractiveInputNotSupportedException : PSInvalidOperationException
    {
        public InteractiveInputNotSupportedException(string what)
            : base($"The command requested interactive input ({what}), which the XProtect Admin Assistant worker does not support. " +
                   "Provide all required values through the application instead.")
        {
        }
    }

    internal sealed class WorkerHostUserInterface : PSHostUserInterface
    {
        private readonly Action<string, string> _write;
        private readonly WorkerRawUserInterface _raw = new WorkerRawUserInterface();
        private readonly System.Text.StringBuilder _pending = new System.Text.StringBuilder();

        public WorkerHostUserInterface(Action<string, string> write)
        {
            _write = write;
        }

        public override PSHostRawUserInterface RawUI => _raw;

        private void Emit(string stream, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var trimmed = text.TrimEnd('\r', '\n');
            if (trimmed.Length == 0) return;
            // PowerShell writes ShouldProcess -WhatIf output as "What if: ..." through the host UI.
            if (stream == "host" && trimmed.StartsWith("What if:", StringComparison.OrdinalIgnoreCase)) stream = "whatif";
            _write(stream, trimmed);
        }

        /// <summary>Emits every complete line in the buffer; with <paramref name="all"/> also the incomplete remainder.</summary>
        private void Drain(bool all)
        {
            var lines = new System.Collections.Generic.List<string>();
            lock (_pending)
            {
                var text = _pending.ToString();
                var last = text.LastIndexOf('\n');
                string complete;
                if (all)
                {
                    complete = text;
                    _pending.Clear();
                }
                else if (last >= 0)
                {
                    complete = text.Substring(0, last);
                    _pending.Clear();
                    _pending.Append(text.Substring(last + 1));
                }
                else
                {
                    return;
                }

                lines.AddRange(complete.Split('\n'));
            }

            foreach (var l in lines) Emit("host", l);
        }

        public void Flush() => Drain(all: true);

        // Write-Host may write text containing newlines through Write() rather than calling WriteLine(), so lines are
        // split on every newline regardless of which method delivered them.
        public override void Write(string value)
        {
            lock (_pending)
            {
                _pending.Append(value);
            }

            Drain(all: false);
        }

        public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) => Write(value);

        public override void WriteLine(string value)
        {
            lock (_pending)
            {
                _pending.Append(value).Append('\n');
            }

            Drain(all: false);
        }

        public override void WriteErrorLine(string value) => Emit("hostError", value);

        public override void WriteDebugLine(string message)
        {
            // Debug records are captured from the Debug stream.
        }

        public override void WriteProgress(long sourceId, ProgressRecord record)
        {
            // Progress records are captured from the Progress stream.
        }

        public override void WriteVerboseLine(string message)
        {
            // Verbose records are captured from the Verbose stream.
        }

        public override void WriteWarningLine(string message)
        {
            // Warning records are captured from the Warning stream.
        }

        public override Dictionary<string, PSObject> Prompt(string caption, string message, Collection<FieldDescription> descriptions) =>
            throw new InteractiveInputNotSupportedException("prompt: " + caption);

        public override int PromptForChoice(string caption, string message, Collection<ChoiceDescription> choices, int defaultChoice) =>
            throw new InteractiveInputNotSupportedException("confirmation: " + caption);

        public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName) =>
            throw new InteractiveInputNotSupportedException("credential prompt");

        public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName, PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options) =>
            throw new InteractiveInputNotSupportedException("credential prompt");

        public override string ReadLine() => throw new InteractiveInputNotSupportedException("Read-Host");

        public override SecureString ReadLineAsSecureString() => throw new InteractiveInputNotSupportedException("Read-Host -AsSecureString");
    }

    internal sealed class WorkerRawUserInterface : PSHostRawUserInterface
    {
        public override ConsoleColor BackgroundColor { get; set; } = ConsoleColor.Black;
        public override Size BufferSize { get; set; } = new Size(240, 3000);
        public override Coordinates CursorPosition { get; set; }
        public override int CursorSize { get; set; } = 1;
        public override ConsoleColor ForegroundColor { get; set; } = ConsoleColor.Gray;
        public override bool KeyAvailable => false;
        public override Size MaxPhysicalWindowSize => new Size(240, 100);
        public override Size MaxWindowSize => new Size(240, 100);
        public override Coordinates WindowPosition { get; set; }
        public override Size WindowSize { get; set; } = new Size(240, 100);
        public override string WindowTitle { get; set; } = "XProtect Admin Assistant worker";

        public override void FlushInputBuffer()
        {
        }

        public override BufferCell[,] GetBufferContents(Rectangle rectangle) => new BufferCell[0, 0];

        public override KeyInfo ReadKey(ReadKeyOptions options) => throw new InteractiveInputNotSupportedException("ReadKey");

        public override void ScrollBufferContents(Rectangle source, Coordinates destination, Rectangle clip, BufferCell fill)
        {
        }

        public override void SetBufferContents(Coordinates origin, BufferCell[,] contents)
        {
        }

        public override void SetBufferContents(Rectangle rectangle, BufferCell fill)
        {
        }
    }
}
