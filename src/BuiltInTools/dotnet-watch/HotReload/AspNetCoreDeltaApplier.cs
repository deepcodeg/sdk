﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.EditAndContinue;
using Microsoft.Extensions.Tools.Internal;

namespace Microsoft.DotNet.Watcher.Tools
{
    internal class AspNetCoreDeltaApplier : IDeltaApplier
    {
        private readonly IReporter _reporter;
        private Task _task;
        private NamedPipeServerStream _pipe;

        public AspNetCoreDeltaApplier(IReporter reporter)
        {
            _reporter = reporter;
        }

        public async ValueTask InitializeAsync(DotNetWatchContext context, CancellationToken cancellationToken)
        {
            if (_pipe is not null)
            {
                await _pipe.DisposeAsync();
            }

            _pipe = new NamedPipeServerStream("netcore-hot-reload", PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            _task = _pipe.WaitForConnectionAsync(cancellationToken);

            if (context.Iteration == 0)
            {
                var deltaApplier = Path.Combine(AppContext.BaseDirectory, "hotreload", "Microsoft.Extensions.AspNetCoreDeltaApplier.dll");
                context.ProcessSpec.EnvironmentVariables.DotNetStartupHooks.Add(deltaApplier);
            }
        }

        public async ValueTask<bool> Apply(DotNetWatchContext context, string changedFile, ManagedModuleUpdates2 updates, CancellationToken cancellationToken)
        {
            if (!_task.IsCompletedSuccessfully || !_pipe.IsConnected)
            {
                // The client isn't listening
                _reporter.Verbose("No client connected to receive delta updates.");
                return false;
            }

            var payload = new UpdatePayload
            {
                ChangedFile = changedFile,
                Deltas = updates.Updates.Select(c => new UpdateDelta
                {
                    ModuleId = c.Module,
                    ILDelta = c.ILDelta.ToArray(),
                    MetadataDelta = c.MetadataDelta.ToArray(),
                    UpdatedMethods = c.UpdatedMethods.ToArray(),
                }),
            };

            // Jank mode. We should send this in a better (not json) format
            await JsonSerializer.SerializeAsync(_pipe, payload, cancellationToken: cancellationToken);
            await _pipe.FlushAsync(cancellationToken);

            var result = ApplyResult.Failed;
            var bytes = ArrayPool<byte>.Shared.Rent(1);
            try
            {
                using var cancellationTokenSource = new CancellationTokenSource(2000);
                var numBytes = await _pipe.ReadAsync(bytes, cancellationTokenSource.Token);

                if (numBytes == 1)
                {
                    result = (ApplyResult)bytes[0];
                }
            }
            catch (Exception ex)
            {
                // Log it, but we'll treat this as a failed apply.
                _reporter.Verbose(ex.Message);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(bytes);
            }

            if (result == ApplyResult.Failed)
            {
                return false;
            }

            if (context.BrowserRefreshServer != null)
            {
                if (result == ApplyResult.Success_RefreshBrowser)
                {
                    await context.BrowserRefreshServer.ReloadAsync(cancellationToken);
                }
                else if (result == ApplyResult.Success)
                {
                    await context.BrowserRefreshServer.SendMessage(JsonSerializer.SerializeToUtf8Bytes(new HotReloadApplied()));
                }
            }

            return true;
        }

        public async ValueTask ReportDiagnosticsAsync(DotNetWatchContext context, IEnumerable<string> diagnostics, CancellationToken cancellationToken)
        {
            if (context.BrowserRefreshServer != null)
            {
                var message = JsonSerializer.SerializeToUtf8Bytes(new HotReloadDiagnostics
                {
                    Diagnostics = diagnostics
                }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

                await context.BrowserRefreshServer.SendMessage(message, cancellationToken);
            }
        }
        
        public readonly struct HotReloadDiagnostics
        {
            public string Type => "HotReloadDiagnosticsv1";

            public IEnumerable<string> Diagnostics { get; init; }
        }

        public readonly struct HotReloadApplied
        {
            public string Type => "HotReloadApplied";
        }
    }
}
