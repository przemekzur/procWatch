using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ProcLens;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly Regex SecretArgs = new(
        @"(?ix)(?<prefix>--?(?:token|api[-_]?key|password|passwd|secret|authorization|cookie)(?:=|\s+))(?<value>""[^""]*""|\S+)|(?<bearer>Bearer\s+)\S+",
        RegexOptions.Compiled);

    [STAThread]
    public static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var hasCommand = args.Length > 0 && !args[0].StartsWith('-');
        var command = hasCommand ? args[0].ToLowerInvariant() : "tray";
        var commandArguments = hasCommand ? args.Skip(1).ToArray() : args;
        var machineReadable = command is "agent-snapshot" or "recommendations";

        try
        {
            var settings = AppSettings.Load();
            var options = Options.Parse(commandArguments, settings);
            return command switch
            {
                "tray" => RunTray(options, !args.Contains("--background", StringComparer.OrdinalIgnoreCase)),
                "collect" => CollectAsync(options, CancellationToken.None).GetAwaiter().GetResult(),
                "snapshot" => Snapshot(options),
                "agent-snapshot" => AgentSnapshot(commandArguments, options),
                "recommendations" => Recommendations(commandArguments, options),
                "report" => Report(options),
                "dashboard" => OpenDashboard(options),
                "install" => Install(),
                "uninstall" => Uninstall(),
                "doctor" => Doctor(options),
                "help" or "--help" or "-h" => Help(),
                _ => Unknown(command)
            };
        }
        catch (AgentCliException ex) when (machineReadable)
        {
            Console.Error.WriteLine(ex.Message);
            return ex.ExitCode;
        }
        catch (Exception ex)
        {
            if (machineReadable)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
            MessageBox.Show(ex.Message, "ProcLens", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static int RunTray(Options options, bool openDashboard)
    {
        using var mutex = new Mutex(true, "Local\\ProcLensTray", out var ownsMutex);
        if (!ownsMutex)
        {
            if (openDashboard) OpenDashboard(options);
            return 0;
        }

        Application.Run(new TrayApplicationContext(options, openDashboard));
        return 0;
    }

    private static int Help()
    {
        Console.WriteLine("""
ProcLens - local-first Windows process/session history

  ProcLens tray       [--background]
  ProcLens collect    [--interval 10] [--scan 2] [--data-dir PATH]
  ProcLens dashboard  [--port 4777]
  ProcLens agent-snapshot --minutes N [--data-dir PATH]
  ProcLens recommendations list [--data-dir PATH]
  ProcLens recommendations import --file PATH [--minutes N] [--data-dir PATH]
  ProcLens install | uninstall | doctor

collect records process lifecycle plus periodic resource samples.
snapshot prints the current app/session inventory without writing history.
report summarizes recorded history for the requested time window.
agent-snapshot and recommendations emit versioned JSON to stdout; diagnostics go to stderr.
Agent imports are advisory only and never perform a process action.
The normal app runs silently in the Windows notification area.
""");
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command: {command}");
        return Help();
    }

    internal static async Task<int> CollectAsync(Options options, CancellationToken cancellationToken)
    {
        using var mutex = new Mutex(true, "Local\\ProcLensCollector", out var ownsMutex);
        if (!ownsMutex)
        {
            return 2;
        }

        Directory.CreateDirectory(options.DataDirectory);
        var runId = Guid.NewGuid().ToString("N");
        var bootUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        using var writer = new HistoryStore(options.DataDirectory, JsonOptions, options.RetentionDays);
        using var dashboard = new DashboardServer(options.DataDirectory, options.DashboardPort, options.DashboardToken);
        dashboard.Start(cancellationToken);

        var previous = new Dictionary<ProcessIdentity, ProcessState>();
        var cpu = new SystemCpuMeter();
        var activityInspector = new ProcessActivityInspector();
        var nextSample = DateTime.UtcNow;
        var nextRecommendationAnalysis = DateTime.UtcNow;
        Task? recommendationAnalysis = null;
        var firstScan = true;

        writer.Write(new
        {
            type = "collector_start",
            schemaVersion = AppSettings.CurrentSchemaVersion,
            timeUtc = DateTime.UtcNow,
            runId,
            bootUtc,
            sampleIntervalSeconds = options.IntervalSeconds,
            scanIntervalSeconds = options.ScanSeconds,
            version = typeof(Program).Assembly.GetName().Version?.ToString()
        });

        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var current = Capture(previous, now, activityInspector);
            var currentByPid = BuildPidIndex(current);
            var previousByPid = BuildPidIndex(previous);

            foreach (var state in current.Values.Where(s => !previous.ContainsKey(s.Identity) && s.IsTracked))
                WriteLifecycle(writer, firstScan ? "process_existing" : "process_start", state, currentByPid, runId, now, options);

            foreach (var state in previous.Values.Where(s => !current.ContainsKey(s.Identity) && s.IsTracked))
                WriteLifecycle(writer, "process_stop", state, previousByPid, runId, now, options);

            if (now >= nextSample)
            {
                WriteSystemSample(writer, cpu, runId, bootUtc, now);
                foreach (var state in current.Values
                             .Where(s => s.IsTracked || s.PrivateBytes >= 100L * 1024 * 1024)
                             .OrderByDescending(s => s.PrivateBytes))
                {
                    WriteProcessSample(writer, state, currentByPid, runId, now);
                }
                writer.Flush();
                if (options.RecommendationsEnabled && now >= nextRecommendationAnalysis &&
                    (recommendationAnalysis is null || recommendationAnalysis.IsCompleted))
                {
                    var analysisSnapshot = current.ToDictionary(item => item.Key, item => item.Value);
                    recommendationAnalysis = Task.Run(() => TryUpdateCoreRecommendations(options, analysisSnapshot, now));
                    nextRecommendationAnalysis = now.AddSeconds(options.RecommendationAnalysisCadenceSeconds);
                }
                nextSample = now.AddSeconds(options.IntervalSeconds);
            }

            previous = current;
            firstScan = false;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(options.ScanSeconds), cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        writer.Write(new { type = "collector_stop", timeUtc = DateTime.UtcNow, runId });
        writer.Flush();
        return 0;
    }

    private static Dictionary<ProcessIdentity, ProcessState> Capture(
        Dictionary<ProcessIdentity, ProcessState> previous,
        DateTime now,
        ProcessActivityInspector? activityInspector = null)
    {
        var result = new Dictionary<ProcessIdentity, ProcessState>();
        activityInspector ??= new ProcessActivityInspector();
        var globalActivity = activityInspector.CaptureGlobal(now);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var startUtc = process.StartTime.ToUniversalTime();
                    var identity = new ProcessIdentity(process.Id, startUtc.Ticks);
                    previous.TryGetValue(identity, out var old);
                    var activity = activityInspector.Inspect(process, globalActivity);
                    var state = ProcessState.Capture(process, identity, startUtc, old, now, activity);
                    result[identity] = state;
                }
                catch
                {
                    // Processes can exit or deny access between enumeration and sampling.
                }
            }
        }
        return result;
    }

    private static void WriteLifecycle(
        HistoryStore writer,
        string type,
        ProcessState state,
        IReadOnlyDictionary<int, ProcessState> byPid,
        string runId,
        DateTime now,
        Options options)
    {
        var owner = ResolveOwner(state, byPid);
        var parentStartTicks = byPid.TryGetValue(state.ParentPid, out var parent) && parent.StartUtc <= state.StartUtc
            ? parent.Identity.StartTicks
            : (long?)null;
        writer.Write(new
        {
            type,
            timeUtc = now,
            runId,
            pid = state.Identity.Pid,
            parentPid = state.ParentPid,
            parentStartTicks,
            startUtc = state.StartUtc,
            name = state.Name,
            category = state.Category,
            owner = owner.Label,
            ownerPid = owner.Pid,
            ownerStartTicks = owner.StartTicks,
            unresolvedOwner = owner.Unresolved,
            isForeground = state.IsForeground,
            hasVisibleMainWindow = state.HasVisibleMainWindow,
            sessionId = state.SessionId,
            lastInputAgeSeconds = double.IsFinite(state.LastInputAgeSeconds) ? state.LastInputAgeSeconds : (double?)null,
            processAgeSeconds = Math.Max(0, (now - state.StartUtc).TotalSeconds),
            path = options.CaptureExecutablePaths ? NormalizePath(state.Path) : null,
            command = options.CaptureCommandLines ? SanitizeCommand(state.CommandLine) : null,
            commandHash = state.CommandHash
        });
    }

    private static void WriteProcessSample(
        HistoryStore writer,
        ProcessState state,
        IReadOnlyDictionary<int, ProcessState> byPid,
        string runId,
        DateTime now)
    {
        var owner = ResolveOwner(state, byPid);
        var parentStartTicks = byPid.TryGetValue(state.ParentPid, out var parent) && parent.StartUtc <= state.StartUtc
            ? parent.Identity.StartTicks
            : (long?)null;
        writer.Write(new
        {
            type = "process_sample",
            timeUtc = now,
            runId,
            pid = state.Identity.Pid,
            parentPid = state.ParentPid,
            parentStartTicks,
            startUtc = state.StartUtc,
            name = state.Name,
            category = state.Category,
            owner = owner.Label,
            ownerPid = owner.Pid,
            ownerStartTicks = owner.StartTicks,
            unresolvedOwner = owner.Unresolved,
            privateMb = ToMb(state.PrivateBytes),
            workingSetMb = ToMb(state.WorkingSetBytes),
            cpuPercent = state.CpuPercent,
            readMbPerSec = state.ReadMbPerSec,
            writeMbPerSec = state.WriteMbPerSec,
            pageFaultsPerSec = state.PageFaultsPerSec,
            handles = state.HandleCount,
            threads = state.ThreadCount,
            isForeground = state.IsForeground,
            hasVisibleMainWindow = state.HasVisibleMainWindow,
            sessionId = state.SessionId,
            lastInputAgeSeconds = double.IsFinite(state.LastInputAgeSeconds) ? state.LastInputAgeSeconds : (double?)null,
            processAgeSeconds = Math.Max(0, (now - state.StartUtc).TotalSeconds)
        });
    }

    private static void TryUpdateCoreRecommendations(
        Options options,
        IReadOnlyDictionary<ProcessIdentity, ProcessState> current,
        DateTime now)
    {
        try
        {
            var builder = new AgentSnapshotBuilder(JsonOptions);
            var observations = builder.BuildObservations(
                ReadHistory(options.DataDirectory, now.AddMinutes(-options.RecommendationAnalysisWindowMinutes)),
                current,
                now,
                options.RecommendationAnalysisWindowMinutes,
                options.IntervalSeconds);
            var engine = new RecommendationEngine(new RecommendationEngineOptions
            {
                MinimumInvestigateConfidencePct = options.MinimumDisplayedRecommendationConfidencePct
            });
            using var store = new RecommendationStore(options.DataDirectory, JsonOptions);
            foreach (var recommendation in engine.Generate(observations, now, RecommendationSource.Core))
                store.Upsert(recommendation);
            store.Expire(now);
        }
        catch (Exception exception)
        {
            // Recommendations are shadow analysis. Telemetry collection must continue if they fail.
            Console.Error.WriteLine($"Recommendation analysis skipped: {exception.Message}");
        }
    }

    private static void WriteSystemSample(
        HistoryStore writer,
        SystemCpuMeter cpu,
        string runId,
        DateTime bootUtc,
        DateTime now)
    {
        if (!Native.TryGetPerformance(out var perf))
            return;

        writer.Write(new
        {
            type = "system_sample",
            timeUtc = now,
            runId,
            bootUtc,
            cpuPercent = cpu.Sample(),
            commitGb = BytesFromPages(perf.CommitTotal, perf.PageSize) / 1024d / 1024d / 1024d,
            commitLimitGb = BytesFromPages(perf.CommitLimit, perf.PageSize) / 1024d / 1024d / 1024d,
            commitPeakGb = BytesFromPages(perf.CommitPeak, perf.PageSize) / 1024d / 1024d / 1024d,
            physicalTotalGb = BytesFromPages(perf.PhysicalTotal, perf.PageSize) / 1024d / 1024d / 1024d,
            physicalAvailableGb = BytesFromPages(perf.PhysicalAvailable, perf.PageSize) / 1024d / 1024d / 1024d,
            kernelPagedMb = BytesFromPages(perf.KernelPaged, perf.PageSize) / 1024d / 1024d,
            kernelNonPagedMb = BytesFromPages(perf.KernelNonpaged, perf.PageSize) / 1024d / 1024d,
            processCount = perf.ProcessCount,
            threadCount = perf.ThreadCount,
            handleCount = perf.HandleCount
        });
    }

    private static long BytesFromPages(nuint pages, nuint pageSize) =>
        checked((long)pages * (long)pageSize);

    private static double ToMb(long bytes) => Math.Round(bytes / 1024d / 1024d, 1);

    private static Dictionary<int, ProcessState> BuildPidIndex(IReadOnlyDictionary<ProcessIdentity, ProcessState> snapshot) =>
        snapshot.Values.GroupBy(s => s.Identity.Pid)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.StartUtc).First());

    private static OwnerInfo ResolveOwner(
        ProcessState state,
        IReadOnlyDictionary<int, ProcessState> byPid)
    {
        var current = state;
        var visited = new HashSet<int>();
        for (var depth = 0; depth < 24; depth++)
        {
            var isOwnerRoot = IsOwnerRoot(current);
            if (current.ParentPid <= 0 || !visited.Add(current.ParentPid) ||
                !byPid.TryGetValue(current.ParentPid, out var parent) || parent.StartUtc > current.StartUtc)
            {
                if (isOwnerRoot)
                    return new OwnerInfo(OwnerLabel(current), current.Identity.Pid, current.Identity.StartTicks, false);
                return new OwnerInfo($"unresolved:{state.Name}:{state.Identity.Pid}", null, null, true);
            }

            if (isOwnerRoot && (!IsOwnerRoot(parent) || !IsSameOwnerFamily(current, parent)))
                return new OwnerInfo(OwnerLabel(current), current.Identity.Pid, current.Identity.StartTicks, false);

            current = parent;
        }

        return new OwnerInfo($"unresolved:{state.Name}:{state.Identity.Pid}", null, null, true);
    }

    private static bool IsOwnerRoot(ProcessState state) =>
        ClassificationRules.Current.IsOwnerRoot(state.Name, state.Path, state.CommandLine);

    private static bool IsSameOwnerFamily(ProcessState first, ProcessState second) =>
        string.Equals(OwnerFamily(first), OwnerFamily(second), StringComparison.OrdinalIgnoreCase);

    private static string OwnerFamily(ProcessState state) =>
        ClassificationRules.Current.OwnerLabel(state.Name, state.Path, state.CommandLine) ?? state.Name;

    private static string OwnerLabel(ProcessState state)
    {
        var label = ClassificationRules.Current.OwnerLabel(state.Name, state.Path, state.CommandLine) ?? state.Name;
        return $"{label}:{state.Identity.Pid}";
    }

    private static string? ExtractArg(string? command, string name)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        var match = Regex.Match(command, $@"--{Regex.Escape(name)}(?:=|\s+)(?<v>[^\s""]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["v"].Value : null;
    }

    internal static string SanitizeCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";
        var sanitized = SecretArgs.Replace(command, m =>
            m.Groups["bearer"].Success ? m.Groups["bearer"].Value + "<redacted>" :
            m.Groups["prefix"].Value + "<redacted>");
        sanitized = NormalizePath(sanitized) ?? "";
        return sanitized.Length <= 1024 ? sanitized : sanitized[..1024];
    }

    internal static string? NormalizePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(profile)
            ? value
            : value.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
    }

    internal static string HashCommand(string command)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(command));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    internal static string Classify(string name, string? path, string command)
        => ClassificationRules.Current.Classify(name, path, command);

    internal static bool ShouldTrack(string name, string? path, string command)
        => ClassificationRules.Current.ShouldTrack(name, path, command);

    private static int Snapshot(Options options)
    {
        var snapshot = Capture(new Dictionary<ProcessIdentity, ProcessState>(), DateTime.UtcNow);
        var byPid = BuildPidIndex(snapshot);
        Native.TryGetPerformance(out var perf);
        var rows = snapshot.Values
            .Where(s => s.IsTracked || s.PrivateBytes >= 100L * 1024 * 1024)
            .GroupBy(s => new { s.Category, Owner = ResolveOwner(s, byPid).Label })
            .Select(g => new
            {
                g.Key.Category,
                g.Key.Owner,
                Count = g.Count(),
                PrivateMb = Math.Round(g.Sum(x => x.PrivateBytes) / 1024d / 1024d, 1),
                WorkingSetMb = Math.Round(g.Sum(x => x.WorkingSetBytes) / 1024d / 1024d, 1),
                Pids = string.Join(',', g.Select(x => x.Identity.Pid))
            })
            .OrderByDescending(x => x.PrivateMb)
            .ToList();

        if (perf.PageSize != 0)
        {
            Console.WriteLine($"Commit: {BytesFromPages(perf.CommitTotal, perf.PageSize) / 1024d / 1024d / 1024d:F1} / " +
                              $"{BytesFromPages(perf.CommitLimit, perf.PageSize) / 1024d / 1024d / 1024d:F1} GB | " +
                              $"Available RAM: {BytesFromPages(perf.PhysicalAvailable, perf.PageSize) / 1024d / 1024d / 1024d:F1} GB | " +
                              $"Processes: {perf.ProcessCount}");
        }
        Console.WriteLine();
        Console.WriteLine($"{"Category",-22} {"Owner",-42} {"Count",5} {"Private",10} {"WS",10}  PIDs");
        foreach (var row in rows)
            Console.WriteLine($"{Trim(row.Category, 22),-22} {Trim(row.Owner, 42),-42} {row.Count,5} {row.PrivateMb,8:F1}MB {row.WorkingSetMb,8:F1}MB  {row.Pids}");
        return 0;
    }

    internal static int AgentSnapshot(string[] arguments, Options options)
    {
        ValidateValueOptions(arguments, "--minutes", "--data-dir");
        var now = DateTimeOffset.UtcNow;
        var (document, _) = BuildAgentSnapshot(options, now);
        Console.Out.WriteLine(JsonSerializer.Serialize(document, JsonOptions));
        return 0;
    }

    internal static int Recommendations(string[] arguments, Options options)
    {
        if (arguments.Length == 0)
            throw new AgentCliException(3, "Expected 'recommendations list' or 'recommendations import --file PATH'.");
        return arguments[0].ToLowerInvariant() switch
        {
            "list" => ListRecommendations(arguments.Skip(1).ToArray(), options),
            "import" => ImportRecommendations(arguments.Skip(1).ToArray(), options),
            _ => throw new AgentCliException(3, $"Unknown recommendations subcommand: {arguments[0]}")
        };
    }

    private static int ListRecommendations(string[] arguments, Options options)
    {
        ValidateValueOptions(arguments, "--data-dir");
        var now = DateTimeOffset.UtcNow;
        using var store = new RecommendationStore(options.DataDirectory, JsonOptions);
        var document = new RecommendationListDocument
        {
            GeneratedAtUtc = now,
            Recommendations = store.ListCurrent(now)
                .Where(item => item.Confidence.Pct >= options.MinimumDisplayedRecommendationConfidencePct)
                .OrderByDescending(item => item.Confidence.Pct)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToArray()
        };
        Console.Out.WriteLine(JsonSerializer.Serialize(document, JsonOptions));
        return 0;
    }

    private static void ValidateValueOptions(string[] arguments, params string[] allowedOptions)
    {
        var allowed = allowedOptions.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < arguments.Length; index++)
        {
            var option = arguments[index];
            if (!allowed.Contains(option) || !seen.Add(option))
                throw new AgentCliException(3, $"Unknown or duplicate option: {option}");
            if (++index >= arguments.Length)
                throw new AgentCliException(3, $"Missing value for {option}.");
        }
    }

    private static int ImportRecommendations(string[] arguments, Options options)
    {
        string? file = null;
        for (var index = 0; index < arguments.Length; index++)
        {
            if (arguments[index] == "--file" && index + 1 < arguments.Length && file is null)
            {
                file = Path.GetFullPath(arguments[++index]);
                continue;
            }
            if (arguments[index] is "--data-dir" or "--minutes")
            {
                if (index + 1 >= arguments.Length) throw new AgentCliException(3, $"Missing value for {arguments[index]}.");
                index++;
                continue;
            }
            throw new AgentCliException(3, $"Unknown or duplicate import option: {arguments[index]}");
        }
        if (file is null) throw new AgentCliException(3, "recommendations import requires --file PATH.");
        if (!File.Exists(file)) throw new AgentCliException(3, $"Advisory file does not exist: {file}");
        var fileInfo = new FileInfo(file);
        if (fileInfo.Length is <= 0 or > AgentSnapshotBuilder.MaximumAdvisoryBytes)
            throw new AgentCliException(3, $"Advisory file exceeds the {AgentSnapshotBuilder.MaximumAdvisoryBytes}-byte limit or is empty.");

        var builder = new AgentSnapshotBuilder(JsonOptions);
        var advisory = builder.DeserializeAdvisory(File.ReadAllBytes(file));
        var now = DateTimeOffset.UtcNow;
        var (snapshot, observations) = BuildAgentSnapshot(options, now, builder);
        var recommendations = builder.ValidateAdvisory(
            advisory,
            snapshot,
            observations,
            now,
            options.MinimumDisplayedRecommendationConfidencePct);
        using var store = new RecommendationStore(options.DataDirectory, JsonOptions);
        foreach (var recommendation in recommendations) store.Upsert(recommendation, advisory.SnapshotHash);
        var result = new RecommendationImportResult
        {
            AdvisoryId = advisory.AdvisoryId,
            AcceptedCount = recommendations.Count,
            RecommendationIds = recommendations.Select(item => item.Id).Order(StringComparer.Ordinal).ToArray()
        };
        Console.Out.WriteLine(JsonSerializer.Serialize(result, JsonOptions));
        return 0;
    }

    private static (AgentSnapshotDocument Document, IReadOnlyList<ProcessObservation> Observations) BuildAgentSnapshot(
        Options options,
        DateTimeOffset now,
        AgentSnapshotBuilder? builder = null)
    {
        builder ??= new AgentSnapshotBuilder(JsonOptions);
        var current = Capture(new Dictionary<ProcessIdentity, ProcessState>(), now.UtcDateTime);
        var observations = builder.BuildObservations(
            ReadHistory(options.DataDirectory, now.UtcDateTime.AddMinutes(-options.ReportMinutes)),
            current,
            now,
            options.ReportMinutes,
            options.IntervalSeconds);
        IReadOnlyList<RecommendationRecord> recommendations = [];
        try
        {
            using var store = new RecommendationStore(options.DataDirectory, JsonOptions);
            recommendations = store.ListCurrent(now);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"Current recommendations unavailable: {exception.Message}");
        }
        return (builder.Build(
            now,
            options.ReportMinutes,
            observations,
            recommendations,
            options.MinimumDisplayedRecommendationConfidencePct), observations);
    }

    private static int Report(Options options)
    {
        if (!Directory.Exists(options.DataDirectory))
        {
            Console.Error.WriteLine($"No data directory: {options.DataDirectory}");
            return 1;
        }

        var cutoff = DateTime.UtcNow.AddMinutes(-options.ReportMinutes);
        var samples = new List<ReportSample>();
        var starts = 0;
        var stops = 0;
        var existing = 0;
        foreach (var line in ReadHistory(options.DataDirectory, cutoff))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (!root.TryGetProperty("timeUtc", out var timeNode) || !timeNode.TryGetDateTime(out var time) || time < cutoff) continue;
                var type = root.GetProperty("type").GetString();
                if (type == "process_start") starts++;
                else if (type == "process_stop") stops++;
                else if (type == "process_existing") existing++;
                else if (type == "process_sample")
                {
                    var unresolved = root.TryGetProperty("unresolvedOwner", out var unresolvedNode)
                        ? unresolvedNode.GetBoolean()
                        : root.TryGetProperty("orphan", out var orphanNode) && orphanNode.GetBoolean();
                    samples.Add(new ReportSample(
                        time,
                        root.GetProperty("category").GetString() ?? "unknown",
                        root.GetProperty("owner").GetString() ?? "unknown",
                        root.GetProperty("privateMb").GetDouble(),
                        root.GetProperty("workingSetMb").GetDouble(),
                        root.GetProperty("cpuPercent").GetDouble(),
                        unresolved));
                }
            }
            catch { /* Ignore partial or incompatible historical records. */ }
        }

        Console.WriteLine($"ProcLens report: last {options.ReportMinutes} minutes | existing at collector start {existing} | new starts {starts} | stops {stops} | samples {samples.Count}");
        Console.WriteLine();
        Console.WriteLine($"{"Category",-22} {"Owner",-42} {"Avg private",12} {"Max private",12} {"Avg CPU",9} {"Unresolved",10}");
        var totals = samples.GroupBy(s => new { s.TimeUtc, s.Category, s.Owner })
            .Select(g => new ReportSample(
                g.Key.TimeUtc,
                g.Key.Category,
                g.Key.Owner,
                g.Sum(x => x.PrivateMb),
                g.Sum(x => x.WorkingSetMb),
                g.Sum(x => x.CpuPercent),
                g.Any(x => x.Unresolved)))
            .ToList();
        foreach (var group in totals.GroupBy(s => new { s.Category, s.Owner })
                     .Select(g => new
                     {
                         g.Key.Category,
                         g.Key.Owner,
                         AvgPrivate = g.Average(x => x.PrivateMb),
                         MaxPrivate = g.Max(x => x.PrivateMb),
                         AvgCpu = g.Average(x => x.CpuPercent),
                         Unresolved = g.Any(x => x.Unresolved)
                     })
                     .OrderByDescending(x => x.MaxPrivate))
        {
            Console.WriteLine($"{Trim(group.Category, 22),-22} {Trim(group.Owner, 42),-42} {group.AvgPrivate,10:F1}MB {group.MaxPrivate,10:F1}MB {group.AvgCpu,8:F1}% {group.Unresolved,10}");
        }
        return 0;
    }

    internal static int OpenDashboard(Options options)
    {
        var url = $"http://127.0.0.1:{options.DashboardPort}/#token={Uri.EscapeDataString(options.DashboardToken)}";
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return 0;
    }

    private static int SetStartup(bool enabled)
    {
        TrayApplicationContext.SetStartup(enabled);
        MessageBox.Show(
            enabled ? "ProcLens will start in the notification area when you sign in." : "ProcLens startup has been disabled.",
            "ProcLens", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    private static int Install()
    {
        AppInstaller.Install();
        MessageBox.Show($"ProcLens was installed to:\n{AppInstaller.InstallDirectory}\n\nIt will start with Windows.",
            "ProcLens installed", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    private static int Uninstall()
    {
        AppInstaller.DisableAndRemoveStartup();
        MessageBox.Show("ProcLens startup was removed. Your local history was preserved. You can now delete the application folder.",
            "ProcLens startup removed", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    private static int Doctor(Options options)
    {
        Directory.CreateDirectory(options.DataDirectory);
        var database = Path.Combine(options.DataDirectory, "proclens.db");
        var message = $"Data: {options.DataDirectory}\n" +
                      $"Database: {(File.Exists(database) ? new FileInfo(database).Length / 1024d / 1024d : 0):F1} MB\n" +
                      $"Retention: {options.RetentionDays} days\n" +
                      $"Start with Windows: {(TrayApplicationContext.IsStartupEnabled() ? "Yes" : "No")}\n" +
                      $"Command-line capture: {(options.CaptureCommandLines ? "Enabled" : "Disabled")}";
        MessageBox.Show(message, "ProcLens diagnostics", MessageBoxButtons.OK, MessageBoxIcon.Information);
        return 0;
    }

    private static IEnumerable<string> ReadSharedLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

        while (reader.ReadLine() is { } line)
        {
            yield return line;
        }
    }

    internal static IEnumerable<string> ReadHistory(string dataDirectory, DateTime cutoffUtc)
    {
        foreach (var line in HistoryStore.ReadSince(dataDirectory, cutoffUtc))
            yield return line;

        if (!Directory.Exists(dataDirectory)) yield break;
        foreach (var file in Directory.EnumerateFiles(dataDirectory, "procwatch-*.jsonl").OrderBy(x => x))
        foreach (var line in ReadSharedLines(file))
            yield return line;
    }

    internal static IEnumerable<string> ReadDashboardHistory(string dataDirectory, DateTime cutoffUtc)
    {
        foreach (var line in HistoryStore.ReadDashboardSince(dataDirectory, cutoffUtc))
            yield return line;

        if (!Directory.Exists(dataDirectory)) yield break;
        foreach (var file in Directory.EnumerateFiles(dataDirectory, "procwatch-*.jsonl").OrderBy(x => x))
        foreach (var line in ReadSharedLines(file))
            yield return line;
    }

    private static string Trim(string value, int length) => value.Length <= length ? value : value[..(length - 1)] + "…";
}

internal readonly record struct ProcessIdentity(int Pid, long StartTicks);
internal readonly record struct OwnerInfo(string Label, int? Pid, long? StartTicks, bool Unresolved);
internal readonly record struct ReportSample(DateTime TimeUtc, string Category, string Owner, double PrivateMb, double WorkingSetMb, double CpuPercent, bool Unresolved);

internal sealed class ProcessState
{
    public required ProcessIdentity Identity { get; init; }
    public required DateTime StartUtc { get; init; }
    public required string Name { get; init; }
    public int ParentPid { get; init; }
    public string? Path { get; init; }
    public required string CommandLine { get; init; }
    public required string CommandHash { get; init; }
    public required string Category { get; init; }
    public bool IsTracked { get; init; }
    public long PrivateBytes { get; init; }
    public long WorkingSetBytes { get; init; }
    public double CpuTotalSeconds { get; init; }
    public double CpuPercent { get; init; }
    public ulong ReadBytes { get; init; }
    public ulong WriteBytes { get; init; }
    public double ReadMbPerSec { get; init; }
    public double WriteMbPerSec { get; init; }
    public uint PageFaults { get; init; }
    public double PageFaultsPerSec { get; init; }
    public int HandleCount { get; init; }
    public int ThreadCount { get; init; }
    public DateTime CapturedUtc { get; init; }
    public bool IsForeground { get; init; }
    public bool HasVisibleMainWindow { get; init; }
    public int SessionId { get; init; } = -1;
    public double LastInputAgeSeconds { get; init; } = double.PositiveInfinity;

    public static ProcessState Capture(
        Process process,
        ProcessIdentity identity,
        DateTime startUtc,
        ProcessState? old,
        DateTime now,
        ProcessActivitySnapshot activity)
    {
        var name = process.ProcessName;
        var path = old?.Path ?? Try(() => process.MainModule?.FileName);
        var rawCommand = old?.CommandLine ?? Native.TryGetCommandLine(process.Handle) ?? path ?? name;
        var command = old?.CommandLine ?? Program.SanitizeCommand(rawCommand);
        var parentPid = old?.ParentPid ?? Native.TryGetParentPid(process.Handle);
        var category = old?.Category ?? Program.Classify(name, path, command);
        var cpuTotal = Try(() => process.TotalProcessorTime.TotalSeconds);
        Native.TryGetIo(process.Handle, out var io);
        Native.TryGetMemory(process.Handle, out var memory);
        var elapsed = old is null ? 0 : Math.Max(0.001, (now - old.CapturedUtc).TotalSeconds);

        return new ProcessState
        {
            Identity = identity,
            StartUtc = startUtc,
            Name = name,
            ParentPid = parentPid,
            Path = path,
            CommandLine = command,
            CommandHash = old?.CommandHash ?? Program.HashCommand(command),
            Category = category,
            IsTracked = Program.ShouldTrack(name, path, command) ||
                        path?.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), StringComparison.OrdinalIgnoreCase) == true,
            PrivateBytes = Try(() => process.PrivateMemorySize64),
            WorkingSetBytes = Try(() => process.WorkingSet64),
            CpuTotalSeconds = cpuTotal,
            CpuPercent = old is null ? 0 : Math.Round(Math.Max(0, cpuTotal - old.CpuTotalSeconds) / elapsed / Environment.ProcessorCount * 100, 2),
            ReadBytes = io.ReadTransferCount,
            WriteBytes = io.WriteTransferCount,
            ReadMbPerSec = old is null ? 0 : Math.Round(Math.Max(0, (double)(io.ReadTransferCount - Math.Min(io.ReadTransferCount, old.ReadBytes))) / elapsed / 1024 / 1024, 3),
            WriteMbPerSec = old is null ? 0 : Math.Round(Math.Max(0, (double)(io.WriteTransferCount - Math.Min(io.WriteTransferCount, old.WriteBytes))) / elapsed / 1024 / 1024, 3),
            PageFaults = memory.PageFaultCount,
            PageFaultsPerSec = old is null ? 0 : Math.Round(Math.Max(0, memory.PageFaultCount - Math.Min(memory.PageFaultCount, old.PageFaults)) / elapsed, 2),
            HandleCount = Try(() => process.HandleCount),
            ThreadCount = Try(() => process.Threads.Count),
            CapturedUtc = now,
            IsForeground = activity.IsForeground,
            HasVisibleMainWindow = activity.HasVisibleMainWindow,
            SessionId = activity.SessionId,
            LastInputAgeSeconds = activity.LastInputAgeSeconds
        };
    }

    private static T? Try<T>(Func<T> action)
    {
        try { return action(); }
        catch { return default; }
    }
}

internal sealed class SystemCpuMeter
{
    private ulong _idle;
    private ulong _kernel;
    private ulong _user;
    private bool _initialized;

    public double Sample()
    {
        if (!Native.GetSystemTimes(out var idleTime, out var kernelTime, out var userTime)) return 0;
        var idle = idleTime.Value;
        var kernel = kernelTime.Value;
        var user = userTime.Value;
        if (!_initialized)
        {
            _idle = idle; _kernel = kernel; _user = user; _initialized = true;
            return 0;
        }
        var idleDelta = idle - _idle;
        var totalDelta = (kernel - _kernel) + (user - _user);
        _idle = idle; _kernel = kernel; _user = user;
        return totalDelta == 0 ? 0 : Math.Round((1 - idleDelta / (double)totalDelta) * 100, 2);
    }
}

internal sealed record Options(
    string DataDirectory,
    int IntervalSeconds,
    int ScanSeconds,
    int ReportMinutes,
    int DashboardPort,
    int RetentionDays,
    bool CaptureCommandLines,
    bool CaptureExecutablePaths,
    string DashboardToken,
    bool RecommendationsEnabled,
    int RecommendationAnalysisWindowMinutes,
    int RecommendationAnalysisCadenceSeconds,
    int MinimumDisplayedRecommendationConfidencePct,
    bool ProcessActionsEnabled)
{
    public static Options Parse(string[] args, AppSettings settings)
    {
        var data = AppSettings.DataDirectory;
        var interval = settings.SampleIntervalSeconds;
        var scan = settings.ScanIntervalSeconds;
        var minutes = 60;
        var port = settings.DashboardPort;
        var retention = settings.RetentionDays;
        var captureCommands = settings.CaptureCommandLines;
        var capturePaths = settings.CaptureExecutablePaths;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--data-dir" when i + 1 < args.Length: data = System.IO.Path.GetFullPath(args[++i]); break;
                case "--interval": interval = ParseNumericOption(args, ref i, "--interval"); break;
                case "--scan": scan = ParseNumericOption(args, ref i, "--scan"); break;
                case "--minutes": minutes = ParseNumericOption(args, ref i, "--minutes"); break;
                case "--port": port = ParseNumericOption(args, ref i, "--port"); break;
                case "--retention-days": retention = ParseNumericOption(args, ref i, "--retention-days"); break;
                case "--capture-command-lines": captureCommands = true; break;
                case "--capture-paths": capturePaths = true; break;
            }
        }
        return new Options(data, Math.Clamp(interval, 5, 300), Math.Clamp(scan, 2, 60),
            Math.Clamp(minutes, 1, 43200), Math.Clamp(port, 1024, 65535), Math.Clamp(retention, 1, 365),
            captureCommands, capturePaths, settings.DashboardToken,
            settings.RecommendationsEnabled,
            settings.RecommendationAnalysisWindowMinutes,
            settings.RecommendationAnalysisCadenceSeconds,
            settings.MinimumDisplayedRecommendationConfidencePct,
            settings.ProcessActionsEnabled);
    }

    private static int ParseNumericOption(string[] args, ref int index, string option)
    {
        if (++index >= args.Length)
            throw new AgentCliException(3, $"Missing value for {option}.");
        if (!int.TryParse(args[index], System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
            throw new AgentCliException(3, $"Invalid numeric value for {option}: {args[index]}");
        return value;
    }
}

internal static class Native
{
    private const int ProcessCommandLineInformation = 60;
    private const int ProcessBasicInformation = 0;

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass, IntPtr processInformation, int processInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessIoCounters(IntPtr processHandle, out IoCounters ioCounters);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCounters counters, uint size);

    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool GetPerformanceInfo(out PerformanceInformation performanceInformation, uint size);

    public static string? TryGetCommandLine(IntPtr processHandle)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            NtQueryInformationProcess(processHandle, ProcessCommandLineInformation, IntPtr.Zero, 0, out var length);
            if (length <= 0 || length > 1024 * 1024) return null;
            buffer = Marshal.AllocHGlobal(length);
            var status = NtQueryInformationProcess(processHandle, ProcessCommandLineInformation, buffer, length, out _);
            if (status != 0) return null;
            var value = Marshal.PtrToStructure<UnicodeString>(buffer);
            return value.Buffer == IntPtr.Zero || value.Length == 0 ? null : Marshal.PtrToStringUni(value.Buffer, value.Length / 2);
        }
        catch { return null; }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
    }

    public static int TryGetParentPid(IntPtr processHandle)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            var size = Marshal.SizeOf<ProcessBasicInfo>();
            buffer = Marshal.AllocHGlobal(size);
            var status = NtQueryInformationProcess(processHandle, ProcessBasicInformation, buffer, size, out _);
            if (status != 0) return 0;
            return Marshal.PtrToStructure<ProcessBasicInfo>(buffer).InheritedFromUniqueProcessId.ToInt32();
        }
        catch { return 0; }
        finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
    }

    public static bool TryGetIo(IntPtr processHandle, out IoCounters counters)
    {
        try { return GetProcessIoCounters(processHandle, out counters); }
        catch { counters = default; return false; }
    }

    public static bool TryGetMemory(IntPtr processHandle, out ProcessMemoryCounters counters)
    {
        counters = new ProcessMemoryCounters { Cb = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
        try { return GetProcessMemoryInfo(processHandle, out counters, counters.Cb); }
        catch { counters = default; return false; }
    }

    public static bool TryGetPerformance(out PerformanceInformation info)
    {
        info = new PerformanceInformation { Cb = (uint)Marshal.SizeOf<PerformanceInformation>() };
        try { return GetPerformanceInfo(out info, info.Cb); }
        catch { info = default; return false; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessBasicInfo
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr InheritedFromUniqueProcessId;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct FileTime
{
    public uint Low;
    public uint High;
    public readonly ulong Value => ((ulong)High << 32) | Low;
}

[StructLayout(LayoutKind.Sequential)]
internal struct IoCounters
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ProcessMemoryCounters
{
    public uint Cb;
    public uint PageFaultCount;
    public nuint PeakWorkingSetSize;
    public nuint WorkingSetSize;
    public nuint QuotaPeakPagedPoolUsage;
    public nuint QuotaPagedPoolUsage;
    public nuint QuotaPeakNonPagedPoolUsage;
    public nuint QuotaNonPagedPoolUsage;
    public nuint PagefileUsage;
    public nuint PeakPagefileUsage;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PerformanceInformation
{
    public uint Cb;
    public nuint CommitTotal;
    public nuint CommitLimit;
    public nuint CommitPeak;
    public nuint PhysicalTotal;
    public nuint PhysicalAvailable;
    public nuint SystemCache;
    public nuint KernelTotal;
    public nuint KernelPaged;
    public nuint KernelNonpaged;
    public nuint PageSize;
    public uint HandleCount;
    public uint ProcessCount;
    public uint ThreadCount;
}
