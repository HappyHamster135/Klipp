# Klipp Project Export Harness

Detta verktyg kor samma `EditorProjectExportService` som Desktop-appen utan att oppna editor-UI:t.
Bygg Klipp Desktop for x64 Debug forst och kor sedan:

```powershell
dotnet restore tools/Klipp.ProjectExportHarness/Klipp.ProjectExportHarness.csproj -p:Platform=x64
dotnet build tools/Klipp.ProjectExportHarness/Klipp.ProjectExportHarness.csproj --no-restore -p:Platform=x64
dotnet exec src/Klipp.Desktop/bin/x64/Debug/net10.0-windows10.0.22621.0/win-x64/Klipp.ProjectExportHarness.dll <project.klippproject> <output.mp4>
```

Verktyget bygger till Desktop-appens outputmapp och ateranvander dess redan byggda beroenden. Det gor att en export kan reproduceras utan att bygga om eller styra ett oppet Klipp-fonster.

Kor `--queue-smoke <project> <output.mp4> <queue-directory>` for att verifiera den persistenta exportkon genom cancel, retry, completion och aterlasning av historiken.
