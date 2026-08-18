# Plain Text to HTML Converter

A .NET 8 console application that converts legacy plain-text, list-style
content in a database column into HTML and writes the result back to the
same row.

Built for migrating free-text responsibility data into rich-text editors
such as CKEditor, which expect real markup (`<ol>`, `<ul>`, `<p>`) rather
than plain text with inline numbering.

## What it does

For each row it processes:

1. Fetches a list of rows (`PkId`, `PlainText`) via a fetch stored procedure.
2. Skips rows where the text already starts with `<` (assumed already
   converted — safe to re-run).
3. Converts the plain text into HTML using `HtmlConverter`.
4. Writes the result back via an update stored procedure.
5. Logs the outcome per row to `conversion_log.csv`, writes runtime logs to
   `Logs/yyyy-MM-dd.txt` via Serilog, and prints a console summary.

Before any rows are processed, the application runs a lightweight SQL
**connectivity test** — see [Connection handling](#connection-handling).

## Conversion rules

The source text is typed by humans, so the list-marker style is not fixed.
`HtmlConverter` detects what is actually present in each block and renders
matching HTML:

| Detected style | Examples | Rendered |
|---|---|---|
| Numbered | `1.` `1 .` `1)` `(1)` `01.` `10.` `9 Text` | `<ol>` |
| Letter | `a.` `a)` `A.` `(A)` | `<ol type="a">` / `<ol type="A">` |
| Roman | `i.` `ii.` `iii.` / `I.` `II.` | `<ol type="i">` / `<ol type="I">` |
| Bullets (inline) | `•` `·` `▪` `◦` | `<ul>` |
| Bullets (line-start) | `-` `*` `–` `—` at the start of a line | `<ul>` |
| Plain paragraphs | no markers recognized | `<p>` |

Additional behaviours:

- A space between the number and the delimiter (`9 . Text`) is tolerated.
- Numbered items containing nested `•`/`·` sub-bullets are rendered as a
  nested `<ul>` inside the `<li>`.
- Lists missing their first marker (text starts directly at "2.") → the
  text before the first valid marker becomes item 1.
- Non-breaking spaces are normalized so pasted text does not break detection.

### Anti‑false‑positive guard

Candidate markers are validated as a strictly ascending sequence
(1,2,3… / a,b,c… / i,ii,iii…) starting at 1 or 2, and at least two valid
markers are required to classify a block as a list. This prevents a stray
digit or letter inside normal prose (a date, a quantity, a reference
number) from causing a false split.

Because only **line‑start** dashes/asterisks are treated as bullets,
mid‑sentence hyphens (`well-known`, `5-10`) are never converted to list
items. A single lone dash line is not treated as a bullet either; a single
unambiguous glyph (`•`) still is.

All extracted text is HTML-encoded (decoded first to avoid double-encoding
source values already stored as entities) so raw `&`, `<`, `>`, `"` don't
break the resulting markup.

## Connection handling

The connection string is read from `appsettings.json` and is **not**
hard-coded in C#. `SqlClient`'s connection string natively supports both
connection styles, so either form works:

| Style | Example `Data Source` | Notes |
|---|---|---|
| Named instance | `ACLDMS01\SQLEXPRESS` | Uses SQL Server Browser (UDP 1434) over TCP. Also works via Shared Memory when the app runs on the SQL host using the short machine name. |
| Explicit TCP port | `ACLDMS01,1433` | Bypasses SQL Server Browser entirely; recommended for remote connections if you know the port. |

Before fetching records the application:

1. Validates that `ConnectionStrings:DefaultConnection` is present.
2. Logs non-secret connection diagnostics (server, database, encryption,
   trust-server-certificate, connect timeout). **The password and the full
   connection string are never logged.**
3. Attempts to open a SQL connection as a connectivity test.
4. If the connection fails, the application stops before processing any
   records.

On failure, the error is classified to distinguish the likely cause:
DNS/server resolution, SQL instance not found, TCP failure, SQL login
failure, SSL/certificate failure, database-not-found/access-denied, or
timeout. The full exception chain (and each SQL error number, for
`SqlException`) is logged, preserving the original exception as the inner
exception.

### Connection failure diagnosis

Run with the `--diag` flag to also log a network probe that isolates the
failure layer before SQL even connects:

```
SQL Server: ACLDMS01\SQLEXPRESS
Database: AXI_TALENT
Connection encryption: Enabled
TrustServerCertificate: Enabled
Connection timeout: 30 s
[Network probe] Host: ACLDMS01, Instance: SQLEXPRESS
[Network probe] DNS resolved: ACLDMS01 -> 192.168.6.120
[Network probe] TCP 192.168.6.120:1433 reachable: True (15 ms)
SQL connection test failed: SQL Server not found or not accessible...
```

## Prerequisites

- .NET 8 SDK (to build)
- Network access to the target database
- A DB login with read access to the source table and execute permission on
  the two stored procedures

## Configuration

All settings live in `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Data Source=YOUR_SERVER\\INSTANCE;Initial Catalog=YOUR_DB;User ID=sa;Password=***;Connect Timeout=30;Encrypt=True;TrustServerCertificate=True;"
  }
}
```

| Setting | Description |
|---|---|
| `ConnectionStrings:DefaultConnection` | Connection string to the target database |

Recommended connection-string guidance:

- Use `Connect Timeout=30` (or similar) rather than `0` so the tool does not
  hang indefinitely when SQL Server is unreachable.
- For an explicit **TCP port**, use `Data Source=HOST,PORT` (e.g.
  `ACLDMS01,1433`) — this avoids dependency on SQL Server Browser.
- For a local **named instance** on the same server, use `HOST\INSTANCE`
  (e.g. `ACLDMS01\SQLEXPRESS`), which connects via Shared Memory.
- Keep `Encrypt=True;TrustServerCertificate=True` as a safety net; it is
  harmless over Shared Memory and covers the case where SqlClient falls back
  to TCP.
- Put the real password only in the deployed file — never in code or the
  repository.

## Stored procedures

The tool relies on two stored procedures whose names are hardcoded in
`DatabaseService.cs`:

| Stored procedure | Purpose | Expected signature |
|---|---|---|
| `SPM_GetResponsibilityPlanText` | Fetch rows to convert | Returns `PkId`, `PlainText` |
| `SPM_UpdateResponsibilityHtml` | Write converted HTML back | Parameters `@Id`, `@convertedHtmlText` |

If your procedure names or parameter names differ, update them in
`DatabaseService.cs`.

## Usage

Always dry-run first:

```bash
dotnet run -- --dry-run
```

Review the generated `conversion_log.csv` (written to the output directory)
— check row counts by outcome (`Numbered` / `Bullet` / `Paragraph` /
`Skipped`) and manually review any row flagged with `Flagged=true` (fell
back to a plain `<p>`) where you'd expect numbering, since that usually
means the source text uses a pattern this tool doesn't yet recognize.

Runtime logs are also written to `Logs/yyyy-MM-dd.txt` (created
automatically) and mirrored to the console.

Once satisfied, run for real:

```bash
dotnet run
```

Optional flags:

| Flag | Description | Default |
|---|---|---|
| `--dry-run` | Convert and log only, no DB writes | off |
| `--connection-string "..."` | Override the connection string in config | — |
| `--batch-size N` | Rows per checkpoint | 500 |
| `--diag` | Log full connection network probe + verbose diagnostics | off |

## Publishing

A plain console application — publish with the standard .NET command:

```bash
dotnet publish -c Release -o ./publish
```

The `.csproj` has no self-contained/single-file settings configured; add
them if you need a runtime-bundled deployment.

## Known limitations

- Source text where a number runs directly into the next word with no
  separator at all (e.g. a typo like `21To Perform...`) won't be detected
  as a new list item and will merge into the previous item's text.
- Sub-heading-style items (a bolded heading followed by bullets) are
  converted as plain list-item text, not styled headings.
- Detection is heuristic; a rare prose run such as "1 apple and 2 oranges"
  may still be read as a list (the ascending-sequence rule is the guard).
- Designed for one column / one table per run via the fetch stored
  procedure. Point the procedure at a different table and re-run for each
  column you need to convert.

## Post-run verification

- Spot-check a sample of converted rows directly in the database.
- Render a few of the same records in your actual rich-text UI to confirm
  they display as expected.
- Cross-check the log's `Paragraph`-outcome rows against the original
  source text to confirm they genuinely had no numbering, rather than an
  unhandled pattern.