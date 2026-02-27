using System;
using System.Collections.Generic;
using System.Linq;
using Company.Observability.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace Company.Observability.Logging;

/// <summary>
/// Serilog enricher that replaces the values of log event properties whose names match a
/// configurable set of sensitive keys with the literal string <c>"***"</c>.
/// </summary>
/// <remarks>
/// Built-in redacted keys cover common PII and credential fields. Additional keys can be
/// supplied via <see cref="RedactionOptions.SensitiveKeys"/>; they are merged with, not
/// replace, the defaults.
/// The sensitive-key <see cref="HashSet{T}"/> is built once at construction time using
/// <see cref="StringComparer.OrdinalIgnoreCase"/> for O(1) look-up on every log event.
/// Any property whose name starts with <c>http.request.headers.</c> and whose suffix
/// matches a sensitive key (e.g. <c>http.request.headers.authorization</c>) is also
/// redacted.
/// This enricher never throws; exceptions are swallowed to protect host stability.
/// </remarks>
public sealed class RedactionEnricher : ILogEventEnricher
{
    private const string RedactedValue = "***";

    private static readonly IReadOnlyList<string> DefaultSensitiveKeys = new[]
    {
        "password", "passwd", "secret", "token", "apikey", "api_key",
        "authorization", "auth", "credential", "credentials",
        "connectionstring", "connection_string",
        "privatekey", "private_key", "clientsecret", "client_secret",
        "personalid", "ssn", "cardnumber", "card_number", "cvv", "pin"
    };

    private const string HttpRequestHeadersPrefix = "http.request.headers.";

    private readonly HashSet<string> _sensitiveKeys;

    /// <summary>
    /// Initialises the enricher with the merged set of default and user-configured sensitive keys.
    /// </summary>
    /// <param name="options">Resolved observability options.</param>
    public RedactionEnricher(ObservabilityOptions options)
    {
        _sensitiveKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in DefaultSensitiveKeys)
        {
            _sensitiveKeys.Add(key);
        }

        if (options.Redaction.SensitiveKeys is { Count: > 0 })
        {
            foreach (var key in options.Redaction.SensitiveKeys)
            {
                if (!string.IsNullOrWhiteSpace(key))
                {
                    _sensitiveKeys.Add(key);
                }
            }
        }
    }

    /// <inheritdoc />
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        try
        {
            // Snapshot the keys to avoid enumerating a live dictionary while modifying it.
            var propertyKeys = logEvent.Properties.Keys.ToList();

            foreach (var key in propertyKeys)
            {
                if (IsSensitive(key))
                {
                    logEvent.AddOrUpdateProperty(
                        new LogEventProperty(key, new ScalarValue(RedactedValue)));
                }
            }
        }
        catch
        {
            // Swallow all exceptions — a logging enricher must never crash the host.
        }
    }

    private bool IsSensitive(string propertyName)
    {
        // Direct match against the sensitive key set.
        if (_sensitiveKeys.Contains(propertyName))
        {
            return true;
        }

        // Match HTTP request header properties (e.g. "http.request.headers.authorization").
        if (propertyName.StartsWith(HttpRequestHeadersPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var headerName = propertyName[HttpRequestHeadersPrefix.Length..];
            return _sensitiveKeys.Contains(headerName);
        }

        return false;
    }
}
