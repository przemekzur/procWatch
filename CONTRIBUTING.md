# Contributing

Contributions are welcome. Before opening a pull request:

1. Keep collection local-only and privacy-safe by default.
2. Run `dotnet test tests/ProcLens.Tests/ProcLens.Tests.csproj -c Release`.
3. Run `dotnet build ProcLens.csproj -c Release -p:TreatWarningsAsErrors=true`.
4. Explain user-visible changes and include dashboard screenshots for UI work.

Avoid adding telemetry, remote listeners, command-line capture defaults, or
platform-specific classifications to the generic built-in rule set.
