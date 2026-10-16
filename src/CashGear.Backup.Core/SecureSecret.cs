using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace CashGear.Backup.Core;

/// <summary>
/// A password or key in a pinned buffer that is zeroed on <see cref="Dispose"/>. <see cref="ToString"/>
/// and the debugger show <c>***</c>, and there is no property that exposes the value, so logging or
/// serializing the object never leaks it. Call <see cref="Reveal"/> only where the value is handed to
/// the engine (an environment variable or a request body) and never log the result.
/// </summary>
[DebuggerDisplay("***")]
public sealed class SecureSecret : IDisposable
{
    private readonly Lock _lock = new();
    private char[]? _chars;

    public SecureSecret(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            throw new ArgumentException("A secret cannot be empty.", nameof(value));
        }

        _chars = GC.AllocateUninitializedArray<char>(value.Length, pinned: true);
        value.CopyTo(_chars);
    }

    public bool IsDisposed
    {
        get
        {
            lock (_lock)
            {
                return _chars is null;
            }
        }
    }

    /// <summary>An independent copy the caller owns (and must dispose).</summary>
    public SecureSecret Copy()
    {
        lock (_lock)
        {
            return new SecureSecret(Chars());
        }
    }

    /// <summary>The plaintext as a managed string, which cannot be zeroed: keep its lifetime short.</summary>
    public string Reveal()
    {
        lock (_lock)
        {
            return new string(Chars());
        }
    }

    public override string ToString() => "***";

    public void Dispose()
    {
        lock (_lock)
        {
            if (_chars is null)
            {
                return;
            }

            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_chars.AsSpan()));
            _chars = null;
        }
    }

    private ReadOnlySpan<char> Chars() => _chars ?? throw new ObjectDisposedException(nameof(SecureSecret));
}
