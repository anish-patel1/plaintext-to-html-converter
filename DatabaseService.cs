using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;

public class DatabaseService
{
    private readonly string _connectionString;
    private readonly ILogger<DatabaseService> _logger;

    public DatabaseService(string connectionString, ILogger<DatabaseService> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task<List<PlainTextRecord>> GetPlainTextAsync()
    {
        var records = new List<PlainTextRecord>();

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
                _logger.LogError(ex, "SQL connection failed while fetching plain-text records.");
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
