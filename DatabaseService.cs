using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

public class DatabaseService
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseService> _logger;
    private readonly SqlConnectionStringBuilder _csb;

    public DatabaseService(string connectionString, ILogger<DatabaseService> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
        _csb = new SqlConnectionStringBuilder(connectionString);
    }

    /// <summary>
    /// Lightweight connectivity test. Opens and closes a SQL connection so the
    /// application can stop before processing records if the server is unreachable.
    /// </summary>
    public async Task<bool> TestConnectionAsync(bool diag = false)
    {
        LogConnectionDiagnostics();
        await LogNetworkDiagnosticsAsync(diag);

        var sw = Stopwatch.StartNew();
        try
        {
            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.OpenAsync();
            }

            sw.Stop();
            _logger.LogInformation("SQL connection test succeeded in {Elapsed} ms.", sw.ElapsedMilliseconds);
            return true;
        }
        catch (Exception ex)
        {
            sw.Stop();
            LogConnectionFailureDetails(ex, sw.ElapsedMilliseconds);
            return false;
        }
    }

    /// <summary>
    /// Logs non-secret connection diagnostics. Never logs the password or the
    /// full connection string (which contains credentials).
    /// </summary>
    private void LogConnectionDiagnostics()
    {
        _logger.LogInformation("Opening SQL connection...");
        _logger.LogInformation("SQL Server: {Server}", string.IsNullOrWhiteSpace(_csb.DataSource) ? "(unknown)" : _csb.DataSource);
        _logger.LogInformation("Database: {Database}", string.IsNullOrWhiteSpace(_csb.InitialCatalog) ? "(default)" : _csb.InitialCatalog);
        _logger.LogInformation("Connection encryption: {Encrypt}", _csb.Encrypt ? "Enabled" : "Disabled");
        _logger.LogInformation("TrustServerCertificate: {Trust}", _csb.TrustServerCertificate ? "Enabled" : "Disabled");
        _logger.LogInformation("Connection timeout: {Timeout} s", _csb.ConnectTimeout);
    }

    /// <summary>
    /// Isolates the failure layer (DNS vs TCP vs instance lookup) before SqlClient
    /// even attempts to connect. Never logs the password or full connection string.
    /// </summary>
    private async Task LogNetworkDiagnosticsAsync(bool verbose)
    {
        var dataSource = string.IsNullOrWhiteSpace(_csb.DataSource) ? string.Empty : _csb.DataSource;

        string host;
        string? instance = null;
        int? port = null;

        // Parse DataSource: "host\instance" (named) or "host,port" (explicit TCP).
        var backslash = dataSource.IndexOf('\\');
        var comma = dataSource.IndexOf(',');
        if (backslash > 0)
        {
            host = dataSource.Substring(0, backslash);
            instance = dataSource.Substring(backslash + 1);
        }
        else if (comma > 0)
        {
            host = dataSource.Substring(0, comma);
            port = int.TryParse(dataSource.Substring(comma + 1), out var p) ? p : null;
        }
        else
        {
            host = dataSource;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            _logger.LogWarning("[Network probe] No host present in Data Source; cannot probe DNS/TCP.");
            return;
        }

        _logger.LogInformation("[Network probe] Host: {Host}, Instance: {Instance}, Port: {Port}",
            host, instance ?? "(none)", port?.ToString() ?? "(none, named instance)");

        // 1. DNS resolution
        var ips = new List<string>();
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host);
            foreach (var a in addresses)
                ips.Add(a.ToString());
            _logger.LogInformation("[Network probe] DNS resolved: {Host} -> {Ips}", host, string.Join(", ", ips));
        }
        catch (Exception ex)
        {
            _logger.LogError("[Network probe] DNS resolution failed for {Host}: {Message}", host, ex.Message);
            return;
        }

        if (ips.Count == 0)
        {
            _logger.LogError("[Network probe] DNS resolved but no addresses returned for {Host}.", host);
            return;
        }

        // 2. TCP reachability probe
        // Explicit port -> probe exactly that port. Named instance -> probe 1433 as a
        // general reachability indicator (instance resolution also needs SQL Browser UDP 1434).
        var probeTarget = port ?? 1433;
        _logger.LogInformation("[Network probe] Probing TCP {Host}:{ProbeTarget} ...", host, probeTarget);
        var probeSw = Stopwatch.StartNew();
        try
        {
            using (var client = new TcpClient())
            {
                var connectTask = client.ConnectAsync(host, probeTarget);
                var done = await Task.WhenAny(connectTask, Task.Delay(TimeSpan.FromSeconds(5)));
                if (done != connectTask)
                    _logger.LogWarning("[Network probe] TCP probe to {Host}:{ProbeTarget} timed out after 5 s.", host, probeTarget);
                else
                {
                    await connectTask; // propagate any connect error
                    probeSw.Stop();
                    _logger.LogInformation("[Network probe] TCP {Host}:{ProbeTarget} reachable in {Elapsed} ms.", host, probeTarget, probeSw.ElapsedMilliseconds);
                }
            }
        }
        catch (Exception ex)
        {
            probeSw.Stop();
            _logger.LogError("[Network probe] TCP {Host}:{ProbeTarget} unreachable in {Elapsed} ms: {Message}",
                host, probeTarget, probeSw.ElapsedMilliseconds, ex.Message);
        }

        if (instance != null && port == null && verbose)
            _logger.LogInformation("[Network probe] Named instance '{Instance}' requires SQL Server Browser (UDP 1434) to resolve its TCP port.", instance);
    }

    /// <summary>
    /// Logs the full exception chain and, for SqlException, every SQL error code,
    /// so the exact failure layer (DNS/TCP/instance/login/SSL/db-access/timeout)
    /// is visible without exposing credentials.
    /// </summary>
    private void LogConnectionFailureDetails(Exception ex, long elapsedMs)
    {
        _logger.LogError("SQL connection failed after {Elapsed} ms. Diagnostic: {Diagnostic}",
            elapsedMs, ClassifyConnectionError(ex));

        var current = ex;
        int depth = 0;
        while (current != null && depth < 8)
        {
            if (current is SqlException sqlEx)
            {
                _logger.LogError("  [{Depth}] SqlException HResult=0x{HR:X8}: {Message}", depth, sqlEx.HResult, sqlEx.Message);
                foreach (SqlError err in sqlEx.Errors)
                {
                    _logger.LogError("      SqlError #{Number} (State {State}, Class {Class}): {Message}",
                        err.Number, err.State, err.Class, err.Message);
                }
            }
            else
            {
                _logger.LogError("  [{Depth}] {Type} HResult=0x{HR:X8}: {Message}",
                    depth, current.GetType().Name, current.HResult, current.Message);
            }
            current = current.InnerException;
            depth++;
        }
    }

    /// <summary>
    /// Maps a connection failure to a clear, human-readable cause so the DBA can
    /// act quickly. The original exception is always preserved (it is logged by
    /// the caller via LogError and kept as the inner exception).
    /// </summary>
    private static string ClassifyConnectionError(Exception ex)
    {
        var sqlEx = ex as SqlException ?? ex.GetBaseException() as SqlException;

        if (sqlEx != null)
        {
            switch (sqlEx.Number)
            {
                case 18456:
                case 18452:
                case 18461:
                case 18470:
                case 18486:
                case 18488:
                    return "SQL login failed. Check user name, password, and SQL login permissions.";
                case 4060:
                    return "Database not found or access denied. Check the Initial Catalog and database permissions.";
                case -2:
                    return "Timeout while connecting to SQL Server. Server did not respond within the connect timeout.";
                case 26:
                    return "SQL Server not found or not accessible. Check the hostname/instance/port and that SQL Server is reachable.";
                case 40:
                case 53:
                    return "TCP connection failure. Host is reachable but SQL Server is not listening on the given port.";
            }

            var msg = sqlEx.Message ?? string.Empty;
            if (msg.Contains("certificate", StringComparison.OrdinalIgnoreCase))
                return "SSL/certificate failure. Check Encrypt, TrustServerCertificate, and server certificates.";
            if (msg.Contains("login", StringComparison.OrdinalIgnoreCase))
                return "SQL login failure.";
            if (msg.Contains("server", StringComparison.OrdinalIgnoreCase) &&
                (msg.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                 msg.Contains("not accessible", StringComparison.OrdinalIgnoreCase) ||
                 msg.Contains("locating", StringComparison.OrdinalIgnoreCase)))
                return "SQL Server not found / not accessible. Check hostname, instance, and port (or SQL Server Browser for named instances).";
        }

        if (ex is SocketException)
            return "TCP/network connection failure (socket error).";

        return "Unexpected connection error. See inner exception for details.";
    }

    public async Task<List<PlainTextRecord>> GetPlainTextAsync()
    {
        var records = new List<PlainTextRecord>();

        LogConnectionDiagnostics();
        _logger.LogInformation("Opening SQL connection for fetching plain-text records...");

        using (var conn = new SqlConnection(_connectionString))
        {
            try
            {
                await conn.OpenAsync();
                _logger.LogInformation("SQL connection established.");
            }
            catch (Exception ex)
            {
                LogConnectionFailureDetails(ex, 0);
                throw;
            }

            // Placeholder fetch SP name — confirm before first real run.
            using (var cmd = new SqlCommand("SPM_GetResponsibilityPlanText", conn))
            {
                cmd.CommandType = CommandType.StoredProcedure;

                using (var reader = await cmd.ExecuteReaderAsync())
                {
                    while (await reader.ReadAsync())
                    {
                        var record = new PlainTextRecord
                        {
                            PkId = reader.GetInt32(reader.GetOrdinal("PkId")),
                            PlainText = reader.IsDBNull(reader.GetOrdinal("PlainText"))
                                ? string.Empty
                                : reader.GetString(reader.GetOrdinal("PlainText"))
                        };

                        // Idempotency: skip rows already converted to HTML.
                        if (record.PlainText.TrimStart().StartsWith("<", StringComparison.Ordinal))
                            continue;

                        records.Add(record);
                    }
                }
            }
        }

        return records;
    }

    public async Task<bool> UpdateHtmlAsync(int id, string html)
    {
        try
        {
            _logger.LogInformation("Opening SQL connection for update of PkId {PkId}...", id);

            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.OpenAsync();
                _logger.LogInformation("SQL connection established for update of PkId {PkId}.", id);

                // Placeholder update SP name — confirm before first real run.
                using (var cmd = new SqlCommand("SPM_UpdateResponsibilityHtml", conn))
                {
                    cmd.CommandType = CommandType.StoredProcedure;
                    cmd.Parameters.AddWithValue("@Id", id);
                    cmd.Parameters.AddWithValue("@convertedHtmlText", html ?? string.Empty);

                    await cmd.ExecuteNonQueryAsync();
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Update failed for PkId {PkId}.", id);
            return false;
        }
    }
}

public class PlainTextRecord
{
    public int PkId { get; set; }
    public string PlainText { get; set; } = string.Empty;
}
