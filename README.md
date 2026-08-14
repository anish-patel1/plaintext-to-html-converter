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

## Conversion rules

Source text is inconsistent across real-world data, so the following marker
styles are recognized:

- `1. Text...` — number, period, space
- `1.Text...` — number, period, no space
- `1) Text...` — number, closing parenthesis
- `1 Text...` — bare number followed by a capital letter, no punctuation
- Numbered items containing nested `•` / `·` sub-bullets → rendered as a
  nested `<ul>` inside the `<li>`
- Plain paragraphs with no numbering → wrapped in `<p>`
- Text with only `•`/`·` bullets and no numbering → wrapped in `<ul>`
- Lists missing their first marker (text starts directly at "2.") → the
  text before the first valid marker becomes item 1

Candidate numbered markers are validated as a strictly ascending sequence
(1, 2, 3, 4…) before being treated as real delimiters, and at least two
valid markers are required to classify the text as a numbered list. This
prevents a stray digit inside normal prose (a date, a quantity, a reference
number) from causing a false split.

All extracted text is HTML-encoded (decoded first to avoid double-encoding
source values already stored as entities) so raw `&`, `<`, `>`, `"` don't
break the resulting markup.

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
    "DefaultConnection": "Server=YOUR_SERVER;Database=YOUR_DB;User Id=...;Password=...;TrustServerCertificate=True;"
  }
}
```

| Setting | Description |
|---|---|
| `ConnectionStrings:DefaultConnection` | Connection string to the target database |

Set this to your target database before running. Example used during
development targets `DB_NAME` on a local SQL Express instance.

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
