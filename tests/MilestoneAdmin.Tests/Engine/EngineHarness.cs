using System;
using System.Collections.Generic;
using System.Linq;
using MilestoneAdmin.Protocol;
using MilestoneAdmin.Protocol.Json;
using MilestoneAdmin.Worker.Engine;

namespace MilestoneAdmin.Tests.Engine
{
    /// <summary>Runs the real worker engine (PowerShell 7 runspace) against the mock MilestonePSTools module.</summary>
    internal sealed class EngineHarness : IDisposable
    {
        public EngineHarness(bool connect = true)
        {
            Engine = new WorkerEngine();
            var init = Send(new JsonObject { { "type", MessageTypes.Initialize }, { "id", "init" }, { "modulePath", TestPaths.MockModule }, { "disableTelemetry", true } });
            var r = init.Last();
            if (r.GetString("type") != MessageTypes.Result || !r.GetBool("success")) throw new InvalidOperationException("Initialize failed: " + r.ToJson());
            if (connect)
            {
                var req = new InvokeRequest { Id = "connect", Operation = "Connect" };
                req.Pipeline.Add(new PipelineStep("Connect-Vms")
                    .With("ServerAddress", ParameterValue.String("http://mgmt.example.local"))
                    .With("Credential", ParameterValue.Credential("admin", "s1"))
                    .With("AcceptEula", ParameterValue.Switch()));
                req.Secrets["s1"] = "Corr3ct-Horse";
                var res = Invoke(req);
                if (!res.Result.GetBool("success")) throw new InvalidOperationException("Connect failed: " + res.Result.ToJson());
            }
        }

        public WorkerEngine Engine { get; }

        public List<JsonObject> Send(JsonObject request)
        {
            var events = new List<JsonObject>();
            Engine.Handle(request, e =>
            {
                lock (events) events.Add(e);
            });
            return events;
        }

        public InvocationOutcome Invoke(InvokeRequest request)
        {
            var events = Send(request.ToJson());
            return new InvocationOutcome(events);
        }

        public InvocationOutcome Invoke(string command, Action<PipelineStep> configure = null, bool allowStateChange = false, bool whatIf = false, Action<InvokeRequest> tweak = null)
        {
            var step = new PipelineStep(command);
            configure?.Invoke(step);
            var req = new InvokeRequest { AllowStateChange = allowStateChange, WhatIf = whatIf };
            req.Pipeline.Add(step);
            tweak?.Invoke(req);
            return Invoke(req);
        }

        public void Dispose() => Engine.Dispose();
    }

    internal sealed class InvocationOutcome
    {
        public InvocationOutcome(List<JsonObject> events)
        {
            Events = events;
            Final = events.LastOrDefault();
        }

        public List<JsonObject> Events { get; }

        public JsonObject Final { get; }

        public JsonObject Result => Final?.GetString("type") == MessageTypes.Result ? Final : null;

        public bool IsFailed => Final?.GetString("type") == MessageTypes.Failed;

        public bool Success => Result?.GetBool("success") == true;

        public string FailureMessage => Final?.GetObject("error")?.GetString("message") ?? Result?.GetObject("terminatingError")?.GetString("message");

        public List<JsonObject> Items => (Result?.GetArray("items") ?? new JsonArray()).Select(i => i.AsObject()).ToList();

        public IEnumerable<string> Stream(string name) => Events.Where(e => e.GetString("type") == MessageTypes.Stream && e.GetString("stream") == name).Select(e => e.GetString("message"));

        public string AllText => string.Join("\n", Events.Select(e => e.ToJson()));
    }
}
