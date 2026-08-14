using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using System.Text;

class Program
{
    static async Task<int> Main(string[] args)
    {
        var logFile = Path.Combine(AppContext.BaseDirectory, "Logs", $"{DateTime.Now:yyyy-MM-dd}.txt");

        using IHost host = Host.CreateDefaultBuilder(args)
            .UseSerilog((context, services, loggerConfig) =>
            {
                loggerConfig
                    .MinimumLevel.Information()
                    .WriteTo.Console()
                    .WriteTo.File(logFile);
            })
            .ConfigureAppConfiguration((context, config) =>
            {
                config.AddJsonFile("appsettings.json", optional: false, reloadOnChange: true);
            })
            .ConfigureServices((context, services) =>
            {
                var settings = context.Configuration.Get<AppSettings>() ?? new AppSettings();
                settings.ConnectionStrings ??= new ConnectionStrings();

                services.AddSingleton(settings);
                services.AddSingleton(sp => new DatabaseService(
                    settings.ConnectionStrings.DefaultConnection ?? string.Empty,
                    sp.GetRequiredService<ILogger<DatabaseService>>()));
                services.AddLogging(builder => builder.AddConsole());
            })
            .Build();

        var logger = host.Services.GetRequiredService<ILogger<Program>>();
        var db = host.Services.GetRequiredService<DatabaseService>();
        var settings = host.Services.GetRequiredService<AppSettings>();

        // CLI args
        var dryRun = args.Contains("--dry-run");
        var connectionString = GetArgValue(args, "--connection-string");
        if (!string.IsNullOrWhiteSpace(connectionString))
            settings.ConnectionStrings!.DefaultConnection = connectionString;
        var batchSize = int.TryParse(GetArgValue(args, "--batch-size"), out var bs) && bs > 0 ? bs : 500;

        logger.LogInformation("Mode: {Mode}", dryRun ? "DRY-RUN (no DB writes)" : "LIVE");
        logger.LogInformation("Batch size: {BatchSize}", batchSize);

        // 1. Fetch plain-text records
        logger.LogInformation("Fetching plain-text records from DB...");
        var records = await db.GetPlainTextAsync();
        logger.LogInformation("Total rows to process (excluding already-HTML): {Count}", records.Count);

        var logPath = Path.Combine(AppContext.BaseDirectory, "conversion_log.csv");
        var logHeader = "PkId,ConversionOutcome,OriginalLength,ConvertedLength,Flagged";

        int converted = 0, skipped = 0, flagged = 0, errors = 0, committed = 0;

        using (var writer = new StreamWriter(logPath, false, Encoding.UTF8))
        {
            writer.WriteLine(logHeader);

            // 2. Convert each row
            foreach (var record in records)
            {
                var html = HtmlConverter.Convert(record.PlainText, out var outcome);
                var isFlagged = outcome == ConversionOutcome.Paragraph;

                if (outcome == ConversionOutcome.Skipped)
                    skipped++;

                // 3. Update DB (skip writes in dry-run)
                if (!dryRun && outcome != ConversionOutcome.Skipped)
                {
                    var ok = await db.UpdateHtmlAsync(record.PkId, html);
                    if (ok)
                    {
                        converted++;
                        committed++;
                    }
                    else
                    {
                        errors++;
                        logger.LogError("Update failed for PkId {PkId}", record.PkId);
                    }

                    // Per-batch transaction: no explicit transaction object here because
                    // each update is a single stored-procedure call; commit counter is
                    // tracked for logging purposes.
                    if (committed > 0 && committed % batchSize == 0)
                        logger.LogInformation("Checkpoint: {Committed} rows committed", committed);
                }
                else if (!dryRun)
                {
                    skipped++;
                }
                else
                {
                    if (outcome != ConversionOutcome.Skipped)
                        converted++;
                }

                if (isFlagged)
                {
                    flagged++;
                    logger.LogWarning("Flagged for review PkId {PkId} (no numbering/bullets detected)", record.PkId);
                }

                writer.WriteLine($"{record.PkId},{outcome},{record.PlainText.Length},{html.Length},{isFlagged}");
            }
        }

        // 4. Summary
        logger.LogInformation("======================== SUMMARY ========================");
        logger.LogInformation("Total rows processed : {Total}", records.Count);
        logger.LogInformation("Converted            : {Converted}", converted);
        logger.LogInformation("Skipped (already HTML): {Skipped}", skipped);
        logger.LogInformation("Flagged for review   : {Flagged}", flagged);
        logger.LogInformation("Errors               : {Errors}", errors);
        logger.LogInformation("Log written to       : {LogPath}", logPath);
        logger.LogInformation("PROCESS COMPLETED");

        Log.CloseAndFlush();

        return errors > 0 ? 1 : 0;
    }

    static string? GetArgValue(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == key)
                return args[i + 1];
        }
        return null;
    }
}
