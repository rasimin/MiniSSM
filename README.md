# MiniSSMS

MiniSSMS is an experimental Windows desktop SQL Server client inspired by the workflow of SQL Server Management Studio (SSMS).

> **Important:** MiniSSMS is **not an official Microsoft product**, is not affiliated with or endorsed by Microsoft, and is not intended to replace SSMS in production environments.

## What makes it different?

The main capability this project highlights is its SQL editor autocomplete experience.

Compared with the default suggestions available in the original SSMS editor, MiniSSMS aims to provide more context-aware and proactive completion, including:

- SQL Server/T-SQL keywords, data types, and built-in functions.
- Table and column suggestions resolved from aliases and the active query scope.
- Column expansion for CTEs, derived tables, `SELECT *`, `APPLY`, and table-valued functions.
- Metadata-aware suggestions across databases.
- Foreign-key-aware Smart Auto-JOIN suggestions.
- Contextual table suggestions for `INSERT`, `FROM`, `JOIN`, and `UPDATE` statements.
- Object and column information through editor hover support.
- Redgate-style SQL snippet completion, for example typing `ap` to insert `ALTER PROCEDURE` and then choose an object from the next suggestion list.

Autocomplete quality depends on the metadata available to the connected SQL Server and the permissions of the current login. This project does not claim to be a benchmarked replacement for SSMS IntelliSense; the editor is an ongoing experiment focused on a different autocomplete workflow.

## Features

- Dark-mode WPF desktop interface.
- Multiple SQL query tabs backed by Monaco Editor through WebView2.
- SQL Server Object Explorer for servers, databases, tables, views, routines, functions, columns, indexes, and triggers.
- Query execution with result and message panels.
- Safety confirmation for `UPDATE` and `DELETE` statements without a `WHERE` clause.
- Query execution history stored locally.
- SQL Agent Monitor for service status, jobs, schedules, steps, history, start/stop, and enable/disable operations.
- SQL Trace / Profiler monitoring.
- Schema import with dependency-aware execution and retry support.
- Excel-to-table import.
- Object search and script generation helpers.

## AI development disclosure

This project was developed **entirely with AI assistance**. The source code, UI work, feature implementation, debugging, and documentation were produced through an AI-driven development workflow and are shared as an experimental project.

Please review and test the code carefully before using it against important databases. AI-generated software can contain defects, incomplete behavior, or unsafe assumptions.

## Requirements

- Windows.
- .NET 9 SDK.
- SQL Server access.
- Microsoft WebView2 Runtime.

The application uses `Microsoft.Data.SqlClient` for SQL Server connections and WebView2 to host the SQL editor.

## Run locally

```powershell
git clone https://github.com/rasimin/MiniSSM.git
cd MiniSSM
dotnet restore
dotnet run --project SSMS.csproj
```

To build a verification output:

```powershell
dotnet build -o .\obj\verify-build --no-restore
```

## Keyboard shortcuts

### Query editor

| Shortcut | Action |
| --- | --- |
| `F5` | Execute query |
| `Ctrl+F5` | Parse/check syntax |
| `Ctrl+L` | Display estimated execution plan |
| `Ctrl+Alt+L` | Execute with actual execution plan |
| `Ctrl+Space` | Open SQL autocomplete suggestions |
| `Ctrl+Shift+F` | Format SQL |
| `Shift+Alt+F` | Format document in the editor |
| `Ctrl+K` | Comment selected SQL |
| `Ctrl+Shift+K` | Uncomment selected SQL |

### Application and query tabs

| Shortcut | Action |
| --- | --- |
| `Ctrl+N` | Open a new query tab |
| `Ctrl+S` | Save the current query |
| `Ctrl+Shift+S` | Save the current query as a new file |
| `Ctrl+O` | Open a SQL file |
| `F8` | Show or hide Object Explorer |
| `Ctrl+Shift+R` | Refresh the selected Object Explorer node |

The editor also provides the usual Monaco Editor commands such as undo, redo, find, and replace.

### SQL snippet abbreviations

The completion shown in the editor is called **SQL code snippet completion**. Type an abbreviation and select the suggestion with `Enter` or `Tab`.

| Abbreviation | Inserts |
| --- | --- |
| `ap` | `ALTER PROCEDURE` |
| `av` | `ALTER VIEW` |
| `af` | `ALTER FUNCTION` |
| `at` | `ALTER TABLE` |
| `dp` | `DROP PROCEDURE` |
| `dv` | `DROP VIEW` |
| `dfn` | `DROP FUNCTION` |
| `dt` | `DROP TABLE` |
| `cp` | `CREATE PROCEDURE` template |
| `ct` | `CREATE TABLE` template |
| `cv` | `CREATE VIEW` template |
| `cf` | `CREATE FUNCTION` template |
| `ssf` | `SELECT TOP 50 * FROM` |
| `sf` | `SELECT * FROM` |
| `se` | `SELECT` |
| `ii` | `INSERT INTO` |
| `ud` | `UPDATE ... SET ... WHERE` |
| `df` | `DELETE FROM ... WHERE` |
| `ij` | `INNER JOIN ... ON` |
| `lj` | `LEFT JOIN ... ON` |
| `rj` | `RIGHT JOIN ... ON` |
| `fj` | `FULL OUTER JOIN ... ON` |
| `cj` | `CROSS JOIN` |
| `ca` | `CROSS APPLY` |
| `oa` | `OUTER APPLY` |
| `wh` | `WHERE` |
| `ob` | `ORDER BY` |
| `gb` | `GROUP BY` |
| `nolock` / `n` | `WITH (NOLOCK)` |
| `te` | `TRUNCATE TABLE` |
| `bt` | `BEGIN TRANSACTION` |
| `cmt` | `COMMIT TRANSACTION` |
| `rbt` | `ROLLBACK TRANSACTION` |
| `tc` | `TRY ... CATCH` block |
| `iff` | `IF ... BEGIN ... END` block |

## Safety and limitations

MiniSSMS can execute SQL and perform SQL Agent operations on the connected server. Always verify the active server and database before running commands.

This is an early-stage project. Some SSMS features are not implemented, and behavior may vary depending on SQL Server version, instance configuration, permissions, and available metadata.

Use a non-production environment for evaluation until the project has been reviewed and approved for your organization.

## Project status

Active experimental development. Feedback, bug reports, and improvement ideas are welcome.

## Disclaimer

SSMS, SQL Server, and Microsoft are trademarks of Microsoft Corporation. MiniSSMS is an independent, unofficial project.
