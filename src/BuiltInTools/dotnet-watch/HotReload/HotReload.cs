// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Tools.Internal;

namespace Microsoft.DotNet.Watcher.Tools
{
    internal class HotReload
    {
        private readonly IReporter _reporter;
        private readonly StaticFileHandler _staticFileHandler;
        private CompilationHandler _compilationHandler;

        public HotReload(IReporter reporter)
        {
            _reporter = reporter;
            _staticFileHandler = new StaticFileHandler(reporter);
        }

        public async ValueTask InitializeAsync(DotNetWatchContext dotNetWatchContext, CancellationToken cancellationToken)
        {
            IDeltaApplier deltaApplier = dotNetWatchContext.DefaultLaunchSettingsProfile.HotReloadProfile == "blazorwasm" ?
                new BlazorWebAssemblyDeltaApplier(_reporter) :
                new AspNetCoreDeltaApplier(_reporter);

            _compilationHandler = new CompilationHandler(deltaApplier, _reporter);
            await _compilationHandler.InitializeAsync(dotNetWatchContext, cancellationToken);
        }

        public async ValueTask<bool> TryHandleFileChange(DotNetWatchContext context, FileItem file, CancellationToken cancellationToken)
        {
            if (await _staticFileHandler.TryHandleFileChange(context, file, cancellationToken))
            {
                return true;
            }

            if (await _compilationHandler.TryHandleFileChange(context, file, cancellationToken)) // This needs to be 6.0
            {
                return true;
            }

            return false;
        }
    }
}
