This folder contains the seed SQLite database for AiFoundryUI.

- File name: aifLocal.db
- Purpose: provide an initial, empty database so the app runs out-of-the-box and the file is included in source control.
- At build, the project copies this DB to the output directory using PreserveNewest to avoid overwriting a DB that was updated by the app at runtime.

If you delete the DB at runtime, the app will recreate schema automatically on next run.
