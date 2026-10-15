using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace CashGear.Backup.Engine.Kopia.IntegrationTests.Infrastructure;

/// <summary>
/// Captures everything the engine logs (Debug and up) so tests can scan it for secrets, and mirrors it to
/// the test output for diagnosis.
/// </summary>
internal sealed class TestLog : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();
    private readonly ITestOutputHelper? _output = TestContext.Current.TestOutputHelper;

    public TestLog() => Factory = LoggerFactory.Create(b => b.AddProvider(this).SetMinimumLevel(LogLevel.Debug));

    public ILoggerFactory Factory { get; }

    public string AllText => string.Join(Environment.NewLine, _lines);

    public bool Contains(string text) => _lines.Any(l => l.Contains(text, StringComparison.Ordinal));

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose() => Factory.Dispose();

    private void Write(string line)
    {
        _lines.Enqueue(line);
        try
        {
            _output?.WriteLine(line);
        }
        catch (InvalidOperationException)
        {
            // The test already finished; background polling can still log.
        }
    }

    private sealed class Logger(TestLog log, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var text = $"{DateTime.Now:HH:mm:ss.fff} {logLevel,-11} {category.Split('.')[^1]}: {formatter(state, exception)}";
            log.Write(exception is null ? text : text + Environment.NewLine + exception);
        }
    }
}
