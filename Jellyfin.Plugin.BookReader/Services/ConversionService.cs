using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.BookReader.Services;

public enum ConversionState
{
    NotStarted,
    Running,
    Done,
    Failed,
    ToolMissing
}

public record ConversionStatus(ConversionState State, string? OutputPath, string? Error);

/// <summary>
/// Converts formats browsers can't render (MOBI, AZW3, FB2, DjVu...) into EPUB
/// or PDF once, then serves the cached result forever after.
/// </summary>
public class ConversionService
{
    private readonly ILogger<ConversionService> _logger;
    private readonly ConcurrentDictionary<string, Task<ConversionStatus>> _jobs = new();

    public ConversionService(ILogger<ConversionService> logger)
    {
        _logger = logger;
    }

    private static string CacheRoot
    {
        get
        {
            var dir = Path.Combine(Plugin.Instance!.PluginDataPath, "converted");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

    public static string? FindCalibre()
    {
        var cfg = Plugin.Instance?.Configuration.CalibreConvertPath;
        return FindTool(cfg, "ebook-convert", new[]
        {
            "/usr/bin/ebook-convert", "/opt/calibre/ebook-convert", "/usr/local/bin/ebook-convert",
            "/Applications/calibre.app/Contents/MacOS/ebook-convert",
            @"C:\Program Files\Calibre2\ebook-convert.exe", @"C:\Program Files (x86)\Calibre2\ebook-convert.exe"
        });
    }

    public static string? FindDdjvu()
    {
        var cfg = Plugin.Instance?.Configuration.DdjvuPath;
        return FindTool(cfg, "ddjvu", new[] { "/usr/bin/ddjvu", "/usr/local/bin/ddjvu", "/opt/homebrew/bin/ddjvu", @"C:\Program Files (x86)\DjVuLibre\ddjvu.exe" });
    }

    /// <summary>The kind of file the conversion produces (epub or pdf).</summary>
    public static ReaderKind OutputKind(string sourcePath) => BookFormats.IsDjvu(sourcePath) ? ReaderKind.Pdf : ReaderKind.Epub;

    /// <summary>Returns current status without starting work.</summary>
    public ConversionStatus Peek(string sourcePath)
    {
        var output = OutputPathFor(sourcePath);
        if (File.Exists(output))
        {
            return new ConversionStatus(ConversionState.Done, output, null);
        }

        if (_jobs.TryGetValue(output, out var job))
        {
            if (!job.IsCompleted)
            {
                return new ConversionStatus(ConversionState.Running, null, null);
            }

            return job.Result;
        }

        return new ConversionStatus(ConversionState.NotStarted, null, null);
    }

    /// <summary>Starts a conversion if needed and returns immediately.</summary>
    public ConversionStatus Start(string sourcePath)
    {
        var current = Peek(sourcePath);
        if (current.State is ConversionState.Done or ConversionState.Running)
        {
            return current;
        }

        var output = OutputPathFor(sourcePath);
        _jobs[output] = Task.Run(() => RunAsync(sourcePath, output));
        return new ConversionStatus(ConversionState.Running, null, null);
    }

    private async Task<ConversionStatus> RunAsync(string source, string output)
    {
        var isDjvu = BookFormats.IsDjvu(source);
        var tool = isDjvu ? FindDdjvu() : FindCalibre();
        if (tool is null)
        {
            var name = isDjvu ? "DjVuLibre (ddjvu)" : "Calibre (ebook-convert)";
            return new ConversionStatus(ConversionState.ToolMissing, null, $"{name} is not installed on the server.");
        }

        var temp = output + ".part" + Path.GetExtension(output);
        var psi = new ProcessStartInfo(tool)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (isDjvu)
        {
            psi.ArgumentList.Add("-format=pdf");
            psi.ArgumentList.Add("-quality=85");
            psi.ArgumentList.Add(source);
            psi.ArgumentList.Add(temp);
        }
        else
        {
            psi.ArgumentList.Add(source);
            psi.ArgumentList.Add(temp);
            psi.ArgumentList.Add("--no-default-epub-cover");
        }

        var timeout = TimeSpan.FromMinutes(Math.Max(1, Plugin.Instance!.Configuration.ConversionTimeoutMinutes));
        _logger.LogInformation("Book Reader: converting {Source}", source);

        try
        {
            using var proc = Process.Start(psi)!;
            var stderrTask = proc.StandardError.ReadToEndAsync();
            _ = proc.StandardOutput.ReadToEndAsync();
            using var cts = new CancellationTokenSource(timeout);
            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(true); } catch { /* already gone */ }
                return new ConversionStatus(ConversionState.Failed, null, "Conversion timed out.");
            }

            if (proc.ExitCode != 0 || !File.Exists(temp))
            {
                var err = await stderrTask.ConfigureAwait(false);
                _logger.LogWarning("Book Reader: conversion failed for {Source}: {Err}", source, err);
                TryDelete(temp);
                return new ConversionStatus(ConversionState.Failed, null, "The converter could not read this file. It may be DRM-protected or damaged.");
            }

            File.Move(temp, output, true);
            return new ConversionStatus(ConversionState.Done, output, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Book Reader: conversion crashed for {Source}", source);
            TryDelete(temp);
            return new ConversionStatus(ConversionState.Failed, null, ex.Message);
        }
    }

    private static string OutputPathFor(string source)
    {
        var info = new FileInfo(source);
        var raw = source + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
        var ext = BookFormats.IsDjvu(source) ? ".pdf" : ".epub";
        return Path.Combine(CacheRoot, hash + ext);
    }

    private static string? FindTool(string? configured, string exe, IEnumerable<string> knownLocations)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
        {
            return configured;
        }

        var names = OperatingSystem.IsWindows() ? new[] { exe + ".exe" } : new[] { exe };
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var n in names)
            {
                var candidate = Path.Combine(dir, n);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return knownLocations.FirstOrDefault(File.Exists);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
    }
}
