using System;
using System.Collections.Generic;
using System.Linq;
using MilestoneAdmin.Protocol.Json;

namespace MilestoneAdmin.Protocol
{
    /// <summary>
    /// Wire protocol between the GUI and MilestoneAdmin.Worker: one UTF-8 JSON document per line on the worker's
    /// stdin (requests) and stdout (events). Every request has an "id"; every event that belongs to a request echoes it.
    /// </summary>
    public static class MessageTypes
    {
        public const string ProtocolVersion = "1";

        // GUI -> worker
        public const string Initialize = "initialize";
        public const string Environment = "environment";
        public const string Discover = "discover";
        public const string Invoke = "invoke";
        public const string Cancel = "cancel";
        public const string ReleaseHandles = "releaseHandles";
        public const string Shutdown = "shutdown";

        // worker -> GUI
        public const string Ready = "ready";
        public const string Progress = "progress";
        public const string Stream = "stream";
        public const string ErrorRecord = "errorRecord";
        public const string Result = "result";
        public const string Failed = "failed";
        public const string Log = "log";
    }

    public sealed class PipelineStep
    {
        public PipelineStep(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) throw new ArgumentException("Command name is required.", nameof(command));
            Command = command;
        }

        public string Command { get; }

        /// <summary>Ordered parameters. Names must be full parameter names (no abbreviations).</summary>
        public List<KeyValuePair<string, ParameterValue>> Parameters { get; } = new List<KeyValuePair<string, ParameterValue>>();

        public PipelineStep With(string name, ParameterValue value)
        {
            Parameters.Add(new KeyValuePair<string, ParameterValue>(name, value));
            return this;
        }

        public JsonObject ToJson()
        {
            var p = new JsonObject();
            foreach (var kv in Parameters) p.Set(kv.Key, kv.Value.ToJson());
            return new JsonObject { { "command", Command }, { "parameters", p } };
        }

        public static PipelineStep FromJson(JsonObject o)
        {
            var step = new PipelineStep(o.GetString("command") ?? throw new ProtocolException("Pipeline step requires 'command'."));
            var p = o.GetObject("parameters");
            if (p != null)
            {
                foreach (var kv in p) step.With(kv.Key, ParameterValue.FromJson(kv.Value));
            }

            return step;
        }
    }

    public sealed class OutputOptions
    {
        /// <summary>Properties to read from each output object. Empty = the worker's safe default (simple-typed properties only).</summary>
        public List<string> Properties { get; } = new List<string>();

        /// <summary>Nesting depth for complex property values (0 = only a type/string summary for nested objects).</summary>
        public int MaxDepth { get; set; } = 1;

        /// <summary>Register returned objects in the worker handle table so later requests can pass them back.</summary>
        public bool RegisterHandles { get; set; } = true;

        /// <summary>Return values of properties whose names look like secrets. Only for Sensitive operations confirmed by the user.</summary>
        public bool AllowSensitive { get; set; }

        /// <summary>Upper bound on returned items; the result reports truncation.</summary>
        public int MaxItems { get; set; } = 200000;

        public JsonObject ToJson() => new JsonObject
        {
            { "properties", new JsonArray(Properties.Select(p => (JsonNode)p)) },
            { "maxDepth", MaxDepth },
            { "registerHandles", RegisterHandles },
            { "allowSensitive", AllowSensitive },
            { "maxItems", MaxItems }
        };

        public static OutputOptions FromJson(JsonObject o)
        {
            var r = new OutputOptions();
            if (o == null) return r;
            var props = o.GetArray("properties");
            if (props != null) r.Properties.AddRange(props.Strings());
            r.MaxDepth = (int)o.GetInt64("maxDepth", 1);
            r.RegisterHandles = o.GetBool("registerHandles", true);
            r.AllowSensitive = o.GetBool("allowSensitive");
            r.MaxItems = (int)o.GetInt64("maxItems", 200000);
            return r;
        }
    }

    public sealed class InvokeRequest
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public List<PipelineStep> Pipeline { get; } = new List<PipelineStep>();

        /// <summary>secret id -> secret value. Never logged; scrubbed from all worker output.</summary>
        public Dictionary<string, string> Secrets { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>Must be true for any pipeline containing a command that is not read-only (defense in depth).</summary>
        public bool AllowStateChange { get; set; }

        /// <summary>Run the final step with -WhatIf (only valid when that command supports ShouldProcess).</summary>
        public bool WhatIf { get; set; }

        public int TimeoutSeconds { get; set; } = 600;

        /// <summary>Capture the verbose stream ($VerbosePreference = Continue) for this request.</summary>
        public bool Verbose { get; set; }

        /// <summary>Human-readable operation name for worker diagnostics (no secrets).</summary>
        public string Operation { get; set; }

        public OutputOptions Output { get; set; } = new OutputOptions();

        public JsonObject ToJson()
        {
            var secrets = new JsonObject();
            foreach (var kv in Secrets) secrets.Set(kv.Key, kv.Value);
            return new JsonObject
            {
                { "type", MessageTypes.Invoke },
                { "id", Id },
                { "operation", Operation },
                { "pipeline", new JsonArray(Pipeline.Select(s => (JsonNode)s.ToJson())) },
                { "secrets", secrets },
                { "allowStateChange", AllowStateChange },
                { "whatIf", WhatIf },
                { "timeoutSeconds", TimeoutSeconds },
                { "verbose", Verbose },
                { "output", Output.ToJson() }
            };
        }

        public static InvokeRequest FromJson(JsonObject o)
        {
            var r = new InvokeRequest
            {
                Id = o.GetString("id") ?? throw new ProtocolException("Request requires 'id'."),
                Operation = o.GetString("operation"),
                AllowStateChange = o.GetBool("allowStateChange"),
                WhatIf = o.GetBool("whatIf"),
                TimeoutSeconds = (int)o.GetInt64("timeoutSeconds", 600),
                Verbose = o.GetBool("verbose"),
                Output = OutputOptions.FromJson(o.GetObject("output"))
            };
            var steps = o.GetArray("pipeline") ?? throw new ProtocolException("Invoke requires 'pipeline'.");
            foreach (var s in steps)
                r.Pipeline.Add(PipelineStep.FromJson(s.AsObject() ?? throw new ProtocolException("Pipeline steps must be objects.")));
            var secrets = o.GetObject("secrets");
            if (secrets != null)
            {
                foreach (var kv in secrets) r.Secrets[kv.Key] = kv.Value.AsString() ?? string.Empty;
            }

            if (r.Pipeline.Count == 0) throw new ProtocolException("Pipeline must contain at least one command.");
            return r;
        }

        /// <summary>A copy of the request JSON with secret values replaced, safe for diagnostics.</summary>
        public JsonObject ToRedactedJson()
        {
            var j = ToJson();
            var secrets = new JsonObject();
            foreach (var kv in Secrets) secrets.Set(kv.Key, "********");
            j.Set("secrets", secrets);
            return j;
        }
    }

    /// <summary>Serialized PowerShell ErrorRecord / exception. Built from object properties, never from console text.</summary>
    public sealed class ErrorInfo
    {
        public string Message { get; set; }
        public string FullyQualifiedErrorId { get; set; }
        public string Category { get; set; }
        public string Reason { get; set; }
        public string TargetName { get; set; }
        public string TargetType { get; set; }
        public string ExceptionType { get; set; }
        public string ExceptionMessage { get; set; }
        public List<string> ExceptionTypeChain { get; } = new List<string>();
        public List<string> InnerMessages { get; } = new List<string>();
        public string ScriptStackTrace { get; set; }
        public string PositionMessage { get; set; }
        public string CommandName { get; set; }
        public string RecommendedAction { get; set; }
        public string StackTrace { get; set; }
        public bool IsTerminating { get; set; }

        public JsonObject ToJson() => new JsonObject
        {
            { "message", Message },
            { "fullyQualifiedErrorId", FullyQualifiedErrorId },
            { "category", Category },
            { "reason", Reason },
            { "targetName", TargetName },
            { "targetType", TargetType },
            { "exceptionType", ExceptionType },
            { "exceptionMessage", ExceptionMessage },
            { "exceptionTypeChain", new JsonArray(ExceptionTypeChain.Select(x => (JsonNode)x)) },
            { "innerMessages", new JsonArray(InnerMessages.Select(x => (JsonNode)x)) },
            { "scriptStackTrace", ScriptStackTrace },
            { "positionMessage", PositionMessage },
            { "commandName", CommandName },
            { "recommendedAction", RecommendedAction },
            { "stackTrace", StackTrace },
            { "isTerminating", IsTerminating }
        };

        public static ErrorInfo FromJson(JsonObject o)
        {
            if (o == null) return null;
            var e = new ErrorInfo
            {
                Message = o.GetString("message"),
                FullyQualifiedErrorId = o.GetString("fullyQualifiedErrorId"),
                Category = o.GetString("category"),
                Reason = o.GetString("reason"),
                TargetName = o.GetString("targetName"),
                TargetType = o.GetString("targetType"),
                ExceptionType = o.GetString("exceptionType"),
                ExceptionMessage = o.GetString("exceptionMessage"),
                ScriptStackTrace = o.GetString("scriptStackTrace"),
                PositionMessage = o.GetString("positionMessage"),
                CommandName = o.GetString("commandName"),
                RecommendedAction = o.GetString("recommendedAction"),
                StackTrace = o.GetString("stackTrace"),
                IsTerminating = o.GetBool("isTerminating")
            };
            var chain = o.GetArray("exceptionTypeChain");
            if (chain != null) e.ExceptionTypeChain.AddRange(chain.Strings());
            var inner = o.GetArray("innerMessages");
            if (inner != null) e.InnerMessages.AddRange(inner.Strings());
            return e;
        }

        public static ErrorInfo FromMessage(string message, string exceptionType = null, bool terminating = true) =>
            new ErrorInfo { Message = message, ExceptionMessage = message, ExceptionType = exceptionType, IsTerminating = terminating };
    }

    /// <summary>Worker -> GUI event. The concrete payload depends on <see cref="Type"/>.</summary>
    public sealed class WorkerEvent
    {
        public WorkerEvent(JsonObject json)
        {
            Json = json ?? throw new ArgumentNullException(nameof(json));
        }

        public JsonObject Json { get; }

        public string Type => Json.GetString("type");

        public string RequestId => Json.GetString("id");

        public static WorkerEvent Parse(string line)
        {
            var node = JsonNode.Parse(line);
            return new WorkerEvent(node.AsObject() ?? throw new ProtocolException("Worker events must be JSON objects."));
        }
    }
}
