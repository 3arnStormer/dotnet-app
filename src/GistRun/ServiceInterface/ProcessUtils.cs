using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using ServiceStack.Text;

namespace GistRun
{
    /// <summary>
    /// Async Process Helper
    /// - https://gist.github.com/Indigo744/b5f3bd50df4b179651c876416bf70d0a
    /// </summary>
    public static class ProcessUtils
    {
        /// <summary>
        /// Run a Process asynchronously
        /// </summary>
        public static async Task<ProcessResult> RunAsync(ProcessStartInfo startInfo, int? timeoutMs = null,
            Action<string> onOut=null, Action<string> onError=null)
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            
            using var process = new Process 
            {
                StartInfo = startInfo, 
                EnableRaisingEvents = true,
            };
            
            // List of tasks to wait for a whole process exit
            var processTasks = new List<Task>();

            // === EXITED Event handling ===
            var processExitEvent = new TaskCompletionSource<object>();
            process.Exited += (sender, args) => {
                processExitEvent.TrySetResult(true);
            };
            processTasks.Add(processExitEvent.Task);

            long callbackTicks = 0;

            // === STDOUT handling ===
            var stdOutBuilder = StringBuilderCache.Allocate();
            var stdOutCloseEvent = new TaskCompletionSource<bool>();
            process.OutputDataReceived += (s, e) => {
                if (e.Data == null)
                {
                    stdOutCloseEvent.TrySetResult(true);
                }
                else
                {
                    stdOutBuilder.AppendLine(e.Data);
                    if (onOut != null)
                    {
                        var swCallback = Stopwatch.StartNew();
                        onOut(e.Data);
                        callbackTicks += swCallback.ElapsedTicks;
                    }
                }
            };

            processTasks.Add(stdOutCloseEvent.Task);

            // === STDERR handling ===
            var stdErrBuilder = StringBuilderCacheAlt.Allocate();
            var stdErrCloseEvent = new TaskCompletionSource<bool>();
            process.ErrorDataReceived += (s, e) => {
                if (e.Data == null)
                {
                    stdErrCloseEvent.TrySetResult(true);
                }
                else
                {
                    stdErrBuilder.AppendLine(e.Data);
                    if (onError != null)
                    {
                        var swCallback = Stopwatch.StartNew();
                        onError(e.Data);
                        callbackTicks += swCallback.ElapsedTicks;
                    }
                }
            };

            processTasks.Add(stdErrCloseEvent.Task);

            // === START OF PROCESS ===
            var sw = Stopwatch.StartNew();
            var result = new ProcessResult {
                StartAt = DateTime.UtcNow,
            };
            if (!process.Start())
            {
                result.ExitCode = process.ExitCode;
                return result;
            }

            // Reads the output stream first as needed and then waits because deadlocks are possible
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            // === ASYNC WAIT OF PROCESS ===

            // Process completion = exit AND stdout (if defined) AND stderr (if defined)
            var processCompletionTask = Task.WhenAll(processTasks);

            // Task to wait for exit OR timeout (if defined)
            var awaitingTask = timeoutMs.HasValue
                ? Task.WhenAny(Task.Delay(timeoutMs.Value), processCompletionTask)
                : Task.WhenAny(processCompletionTask);

            // Let's now wait for something to end...
            if ((await awaitingTask.ConfigureAwait(false)) == processCompletionTask)
            {
                // -> Process exited cleanly
                result.ExitCode = process.ExitCode;
            }
            else
            {
                // -> Timeout, let's kill the process
                try
                {
                    process.Kill();
                }
                catch
                {
                    // ignored
                }
            }

            // Read stdout/stderr
            result.EndAt = DateTime.UtcNow;
            if (callbackTicks > 0)
            {
                var callbackMs = (callbackTicks / Stopwatch.Frequency) * 1000;
                result.CallbackDurationMs = callbackMs;
                result.DurationMs = sw.ElapsedMilliseconds - callbackMs;
            }
            else
            {
                result.DurationMs = sw.ElapsedMilliseconds;
            }
            result.StdOut = StringBuilderCache.ReturnAndFree(stdOutBuilder);
            result.StdErr = StringBuilderCacheAlt.ReturnAndFree(stdErrBuilder);

            return result;
        }

        /// <summary>
        /// Run process result
        /// </summary>
        public class ProcessResult
        {
            /// <summary>
            /// Exit code
            /// <para>If NULL, process exited due to timeout</para>
            /// </summary>
            public int? ExitCode { get; set; } = null;

            /// <summary>
            /// Standard error stream
            /// </summary>
            public string StdErr { get; set; }

            /// <summary>
            /// Standard output stream
            /// </summary>
            public string StdOut { get; set; }
            
            /// <summary>
            /// UTC Start
            /// </summary>
            public DateTime StartAt { get; set; }
            
            /// <summary>
            /// UTC End
            /// </summary>
            public DateTime EndAt { get; set; }
            
            /// <summary>
            /// Duration (ms)
            /// </summary>
            public long DurationMs { get; set; }
            
            /// <summary>
            /// Duration (ms)
            /// </summary>
            public long? CallbackDurationMs { get; set; }
        }
    }
}