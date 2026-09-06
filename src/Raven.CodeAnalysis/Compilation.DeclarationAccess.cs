using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Raven.CodeAnalysis;

public partial class Compilation
{
    private readonly SemaphoreSlim _sourceDeclarationAccessGate = new(1, 1);
    private readonly AsyncLocal<int> _sourceDeclarationAccessDepth = new();
    private SourceDeclarationAccessLease? _activeSourceDeclarationAccessLease;

    // Before shared declarations are complete, binding one tree can expand macros
    // in another. Acquire this lease before any document semantic lease so those
    // two paths cannot take the declaration and document locks in opposite orders.
    internal IDisposable? EnterSourceDeclarationAccess(CancellationToken cancellationToken, bool tryEnter = false)
    {
#if NET11_0_OR_GREATER
        // Browser workers without threads cannot use synchronous semaphore waits,
        // even when the semaphore is available. No cross-thread exclusion is needed.
        if (!RuntimeFeature.IsMultithreadingSupported)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SourceDeclarationAccessLease.Empty;
        }
#endif
        if (_sourceDeclarationsComplete || _sourceDeclarationAccessDepth.Value > 0)
            return SourceDeclarationAccessLease.Empty;

        if (tryEnter)
        {
            if (!_sourceDeclarationAccessGate.Wait(0, cancellationToken))
                return null;
        }
        else
        {
            _sourceDeclarationAccessGate.Wait(cancellationToken);
        }

        if (_sourceDeclarationsComplete)
        {
            _sourceDeclarationAccessGate.Release();
            return SourceDeclarationAccessLease.Empty;
        }
        _sourceDeclarationAccessDepth.Value++;
        var lease = new SourceDeclarationAccessLease(this, releaseDepth: true, releaseGate: true);
        _activeSourceDeclarationAccessLease = lease;
        return lease;
    }

    internal async ValueTask<IDisposable?> EnterSourceDeclarationAccessAsync(CancellationToken cancellationToken, bool tryEnter = false)
    {
#if NET11_0_OR_GREATER
        if (!RuntimeFeature.IsMultithreadingSupported)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return SourceDeclarationAccessLease.Empty;
        }
#endif
        if (_sourceDeclarationsComplete || _sourceDeclarationAccessDepth.Value > 0)
            return SourceDeclarationAccessLease.Empty;

        if (tryEnter)
        {
            if (!await _sourceDeclarationAccessGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
                return null;
        }
        else
        {
            await _sourceDeclarationAccessGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (_sourceDeclarationsComplete)
        {
            _sourceDeclarationAccessGate.Release();
            return SourceDeclarationAccessLease.Empty;
        }
        // The caller establishes ambient ownership in its own execution context.
        var lease = new SourceDeclarationAccessLease(this, releaseDepth: false, releaseGate: true);
        _activeSourceDeclarationAccessLease = lease;
        return lease;
    }

    internal IDisposable EnterAmbientSourceDeclarationAccess()
    {
        _sourceDeclarationAccessDepth.Value++;
        return new SourceDeclarationAccessLease(this, releaseDepth: true, releaseGate: false);
    }

    private sealed class SourceDeclarationAccessLease(Compilation? compilation, bool releaseDepth, bool releaseGate) : IDisposable
    {
        public static readonly IDisposable Empty = new SourceDeclarationAccessLease(null, false, false);
        private Compilation? _compilation = compilation;
        private int _releaseGate = releaseGate ? 1 : 0;

        public void ReleaseGate()
        {
            if (Volatile.Read(ref _compilation) is { } owner)
                ReleaseGate(owner);
        }

        private void ReleaseGate(Compilation owner)
        {
            if (Interlocked.Exchange(ref _releaseGate, 0) == 0)
                return;
            Interlocked.CompareExchange(ref owner._activeSourceDeclarationAccessLease, null, this);
            owner._sourceDeclarationAccessGate.Release();
        }

        public void Dispose()
        {
            var owner = Interlocked.Exchange(ref _compilation, null);
            if (owner is null)
                return;
            if (releaseDepth)
                owner._sourceDeclarationAccessDepth.Value--;
            ReleaseGate(owner);
        }
    }
}
