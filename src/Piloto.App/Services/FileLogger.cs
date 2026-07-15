using Microsoft.Extensions.Logging;

namespace Piloto.App.Services;

/// <summary>Logger simples em arquivo (um por dia) na pasta de dados. Sem dependências externas.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly string _pastaLogs;
    private readonly object _lock = new();
    private readonly LogLevel _minimo;

    public FileLoggerProvider(string pastaDados, LogLevel minimo = LogLevel.Information)
    {
        _pastaLogs = Path.Combine(pastaDados, "logs");
        Directory.CreateDirectory(_pastaLogs);
        _minimo = minimo;
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    internal void Escrever(string linha)
    {
        var arquivo = Path.Combine(_pastaLogs, $"piloto-{DateTime.Now:yyyyMMdd}.log");
        lock (_lock)
        {
            try { File.AppendAllText(arquivo, linha + Environment.NewLine); }
            catch { /* nunca deixar o logging derrubar o app */ }
        }
    }

    internal bool Habilitado(LogLevel nivel) => nivel >= _minimo && nivel != LogLevel.None;

    public void Dispose() { }

    private sealed class FileLogger : ILogger
    {
        private readonly FileLoggerProvider _provider;
        private readonly string _categoria;

        public FileLogger(FileLoggerProvider provider, string categoria)
        {
            _provider = provider;
            _categoria = categoria;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => _provider.Habilitado(logLevel);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel)) return;
            var msg = formatter(state, exception);
            var categoriaCurta = _categoria.Split('.').Last();
            var linha = $"{DateTime.Now:HH:mm:ss} [{logLevel,-11}] {categoriaCurta}: {msg}";
            if (exception is not null) linha += Environment.NewLine + exception;
            _provider.Escrever(linha);
        }
    }
}
