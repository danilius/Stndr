using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace Stndr;

public static class CrashLogService
{
    private const int MaxCrashLogs = 50;
    private static readonly object LoggedExceptionsLock = new();
    private static readonly HashSet<int> LoggedExceptions = new();

    public static string CrashLogFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Stndr",
        "crashes");

    public static void WriteCrashLog(Exception exception, string source, bool isTerminating)
    {
        try
        {
            if (!TryMarkExceptionForLogging(exception))
            {
                return;
            }

            Directory.CreateDirectory(CrashLogFolder);
            var fileName = $"crash-{DateTimeOffset.Now:yyyyMMdd-HHmmss-fff}-{Environment.ProcessId}.log";
            var filePath = Path.Combine(CrashLogFolder, fileName);
            File.WriteAllText(filePath, BuildCrashLog(exception, source, isTerminating), Encoding.UTF8);
            PruneOldCrashLogs();
        }
        catch
        {
            // Crash logging is best effort; never let it create a second failure.
        }
    }

    private static bool TryMarkExceptionForLogging(Exception exception)
    {
        var exceptionId = RuntimeHelpers.GetHashCode(exception);
        lock (LoggedExceptionsLock)
        {
            return LoggedExceptions.Add(exceptionId);
        }
    }

    public static void OpenCrashLogFolder()
    {
        Directory.CreateDirectory(CrashLogFolder);
        using var _ = Process.Start(new ProcessStartInfo
        {
            FileName = CrashLogFolder,
            UseShellExecute = true
        });
    }

    private static string BuildCrashLog(Exception exception, string source, bool isTerminating)
    {
        var assembly = Assembly.GetEntryAssembly();
        var version = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly?.GetName().Version?.ToString()
            ?? "unknown";

        var builder = new StringBuilder();
        builder.AppendLine("Stndr crash log");
        builder.AppendLine($"Created local: {DateTimeOffset.Now:O}");
        builder.AppendLine($"Created UTC: {DateTimeOffset.UtcNow:O}");
        builder.AppendLine($"Source: {source}");
        builder.AppendLine($"Terminating: {isTerminating}");
        builder.AppendLine($"App version: {version}");
        builder.AppendLine($"Process ID: {Environment.ProcessId}");
        builder.AppendLine($"OS: {RuntimeInformation.OSDescription}");
        builder.AppendLine($"Architecture: {RuntimeInformation.ProcessArchitecture}");
        builder.AppendLine($".NET: {RuntimeInformation.FrameworkDescription}");
        builder.AppendLine();
        AppendException(builder, exception, 0);
        return builder.ToString();
    }

    private static void AppendException(StringBuilder builder, Exception exception, int depth)
    {
        var prefix = depth == 0 ? string.Empty : $"Inner exception {depth}: ";
        builder.AppendLine($"{prefix}{exception.GetType().FullName}");
        builder.AppendLine(exception.Message);
        builder.AppendLine(exception.StackTrace ?? "(no stack trace)");
        builder.AppendLine();

        if (exception.InnerException is not null)
        {
            AppendException(builder, exception.InnerException, depth + 1);
        }
    }

    private static void PruneOldCrashLogs()
    {
        var logs = Directory.EnumerateFiles(CrashLogFolder, "crash-*.log")
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.CreationTimeUtc)
            .Skip(MaxCrashLogs);

        foreach (var log in logs)
        {
            try
            {
                log.Delete();
            }
            catch
            {
                // best effort
            }
        }
    }
}
