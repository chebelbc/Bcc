using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;
using MilestoneAdmin.Protocol;
using MilestoneAdmin.Protocol.Json;
using MilestoneAdmin.Worker.Engine;

namespace MilestoneAdmin.Worker
{
    /// <summary>
    /// MilestonePSTools worker process. Started by the GUI with redirected stdin/stdout; speaks the JSON-lines protocol
    /// in MilestoneAdmin.Protocol. It never reads commands from anywhere else and never listens on the network.
    /// </summary>
    internal static class Program
    {
        private static readonly object WriteLock = new object();
        private static TextWriter _protocolOut;

        private static int Main(string[] args)
        {
            // Claim stdin/stdout for the protocol before anything else can touch the console. Any code that writes to
            // Console.Out (SDK diagnostics, a stray Console.WriteLine) is redirected to stderr so it cannot corrupt
            // the protocol stream.
            var utf8 = new UTF8Encoding(false);
            var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
            _protocolOut = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = true, NewLine = "\n" };
            var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true };
            Console.SetOut(stderr);
            Console.SetIn(TextReader.Null);

            var queue = new BlockingCollection<JsonObject>();
            var engine = new WorkerEngine();
            var exitCode = 0;

            var executor = new Thread(() =>
            {
                foreach (var request in queue.GetConsumingEnumerable())
                {
                    if (request.GetString("type") == MessageTypes.Shutdown) break;
                    engine.Handle(request, Emit);
                    if (engine.IsUnhealthy)
                    {
                        Log("error", "Engine is unhealthy after a pipeline failed to stop; exiting so the application can restart the worker.");
                        exitCode = 3;
                        break;
                    }
                }
            })
            {
                IsBackground = false,
                Name = "PowerShell executor"
            };
#if NETFRAMEWORK
            executor.SetApartmentState(ApartmentState.STA);
#endif
            executor.Start();

            Emit(new JsonObject
            {
                { "type", MessageTypes.Ready },
                { "protocolVersion", MessageTypes.ProtocolVersion },
                { "workerVersion", typeof(Program).Assembly.GetName().Version.ToString() },
                { "processId", System.Diagnostics.Process.GetCurrentProcess().Id },
                { "is64BitProcess", Environment.Is64BitProcess }
            });

            try
            {
                string line;
                while ((line = stdin.ReadLine()) != null)
                {
                    if (line.Length == 0) continue;
                    JsonObject request;
                    try
                    {
                        request = JsonNode.Parse(line).AsObject() ?? throw new ProtocolException("Requests must be JSON objects.");
                    }
                    catch (Exception ex)
                    {
                        // Never echo the line: it may contain secrets.
                        Emit(new JsonObject { { "type", MessageTypes.Failed }, { "id", JsonNull.Instance }, { "error", ErrorInfo.FromMessage("Malformed request: " + ex.Message, ex.GetType().FullName).ToJson() } });
                        continue;
                    }

                    var type = request.GetString("type");
                    if (type == MessageTypes.Cancel)
                    {
                        engine.Cancel(request.GetString("target"));
                        continue;
                    }

                    queue.Add(request);
                    if (type == MessageTypes.Shutdown) break;
                    if (!executor.IsAlive) break;
                }
            }
            finally
            {
                queue.CompleteAdding();
                // The executor finishes the request in progress (or exits after a shutdown request).
                executor.Join(TimeSpan.FromSeconds(30));
                engine.Dispose();
            }

            return exitCode;
        }

        private static void Emit(JsonObject message)
        {
            var text = message.ToJson();
            lock (WriteLock)
            {
                _protocolOut.WriteLine(text);
            }
        }

        private static void Log(string level, string message)
        {
            Emit(new JsonObject { { "type", MessageTypes.Log }, { "level", level }, { "message", message } });
        }
    }
}
