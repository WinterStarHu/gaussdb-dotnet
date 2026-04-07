using System.Diagnostics;

var parsedArgs = ParseArgs(args);
var target = GetArg("target", "Default");
var apiKey = GetArg("apiKey");
var noPush = GetBoolArg("noPush");
var framework = GetArg("framework");
var version = Environment.GetEnvironmentVariable("VERSION");
var stable = GetBoolArg("stable") || !string.IsNullOrEmpty(version);
var runningOnGithubActions = Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true";

Console.WriteLine($$"""
Arguments:

target: {{target}}
stable: {{stable}}
noPush: {{noPush}}
framework: {{framework}}
args:
{{string.Join("\n", args)}}

""");

var solutionPath = "./GaussDB.slnx";
string[] buildProjects =
[
    "./example/GetStarted/GetStarted.csproj",
    "./src/GaussDB/GaussDB.csproj",
    "./src/GaussDB.DependencyInjection/GaussDB.DependencyInjection.csproj",
    "./src/GaussDB.GeoJSON/GaussDB.GeoJSON.csproj",
    "./src/GaussDB.Json.NET/GaussDB.Json.NET.csproj",
    "./src/GaussDB.NetTopologySuite/GaussDB.NetTopologySuite.csproj",
    "./src/GaussDB.NodaTime/GaussDB.NodaTime.csproj",
    "./src/GaussDB.OpenTelemetry/GaussDB.OpenTelemetry.csproj",
    "./test/GaussDB.Benchmarks/GaussDB.Benchmarks.csproj",
    "./test/GaussDB.DependencyInjection.Tests/GaussDB.DependencyInjection.Tests.csproj",
    "./test/GaussDB.NativeAotTests/GaussDB.NativeAotTests.csproj",
    "./test/GaussDB.PluginTests/GaussDB.PluginTests.csproj",
    "./test/GaussDB.Specification.Tests/GaussDB.Specification.Tests.csproj",
    "./test/GaussDB.Tests/GaussDB.Tests.csproj"
];
string[] srcProjects =
[
    "./src/GaussDB/GaussDB.csproj"
];
string[] testProjects =
[
    "./test/GaussDB.Tests/GaussDB.Tests.csproj",
    "./test/GaussDB.DependencyInjection.Tests/GaussDB.DependencyInjection.Tests.csproj"
];

CleanupArtifacts();

switch (target)
{
    case "build":
        await RunBuildAsync();
        break;
    case "test":
        await RunBuildAsync();
        await RunTestsAsync();
        break;
    case "pack":
        await RunBuildAsync();
        await RunPackAsync();
        break;
    case "Default":
        await RunBuildAsync();
        await RunPackAsync();
        break;
    default:
        throw new InvalidOperationException($"Unknown target: {target}");
}

return;

string? GetArg(string key, string? defaultValue = null)
    => parsedArgs.TryGetValue(key, out var value) ? value : defaultValue;

bool GetBoolArg(string key)
{
    if (!parsedArgs.TryGetValue(key, out var value))
        return false;

    return string.IsNullOrEmpty(value) || bool.TryParse(value, out var boolValue) && boolValue;
}

static Dictionary<string, string?> ParseArgs(string[] arguments)
{
    var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
    foreach (var argument in arguments)
    {
        if (!argument.StartsWith("--", StringComparison.Ordinal))
            continue;

        var separatorIndex = argument.IndexOf('=');
        if (separatorIndex < 0)
        {
            result[argument[2..]] = string.Empty;
            continue;
        }

        var key = argument[2..separatorIndex];
        var value = argument[(separatorIndex + 1)..];
        result[key] = value;
    }

    return result;
}

void CleanupArtifacts()
{
    if (Directory.Exists("./artifacts/packages"))
        Directory.Delete("./artifacts/packages", true);
}

async Task RunBuildAsync()
{
    WriteTaskBanner("build", "build", true);
    try
    {
        if (string.IsNullOrWhiteSpace(framework))
        {
            await ExecuteProcessAsync("dotnet", ["build", solutionPath]);
            return;
        }

        foreach (var project in GetBuildProjects(framework!))
        {
            await ExecuteProcessAsync("dotnet", ["build", project, "-f", framework!]);
        }
    }
    finally
    {
        WriteTaskBanner("build", "build", false);
    }
}

async Task RunTestsAsync()
{
    WriteTaskBanner("test", "dotnet test", true);
    try
    {
        foreach (var project in testProjects)
        {
            var commandArgs = new List<string>
            {
                "test",
                "--blame",
                "--collect",
                "XPlat Code Coverage;Format=cobertura,opencover;ExcludeByAttribute=ExcludeFromCodeCoverage,Obsolete,GeneratedCode,CompilerGenerated",
                "--logger",
                runningOnGithubActions ? "GitHubActions" : "console;verbosity=d",
                "-v:d",
                project
            };

            if (!string.IsNullOrWhiteSpace(framework))
            {
                commandArgs.Insert(1, framework!);
                commandArgs.Insert(1, "-f");
            }

            await ExecuteProcessAsync("dotnet", commandArgs);
        }
    }
    finally
    {
        WriteTaskBanner("test", "dotnet test", false);
    }
}

async Task RunPackAsync()
{
    WriteTaskBanner("pack", "dotnet pack", true);
    try
    {
        var outputDirectory = "./artifacts/packages";
        Directory.CreateDirectory(outputDirectory);

        foreach (var project in srcProjects)
        {
            var commandArgs = new List<string>
            {
                "pack",
                project,
                "-o",
                outputDirectory
            };

            if (stable)
            {
                if (!string.IsNullOrEmpty(version))
                {
                    commandArgs.Add("-p");
                    commandArgs.Add($"VersionPrefix={version}");
                }
            }
            else
            {
                commandArgs.Add("--version-suffix");
                commandArgs.Add($"preview-{DateTime.UtcNow:yyyyMMdd-HHmmss}");
            }

            await ExecuteProcessAsync("dotnet", commandArgs);
        }

        if (noPush)
        {
            Console.WriteLine("Skip push there's noPush specified");
            return;
        }

        apiKey ??= Environment.GetEnvironmentVariable("NUGET_API_KEY");
        if (string.IsNullOrEmpty(apiKey))
        {
            Console.WriteLine("Skip push since there's no apiKey found");
            return;
        }

        foreach (var file in Directory.GetFiles(outputDirectory, "*.nupkg"))
        {
            await RetryAsync(async () =>
            {
                await ExecuteProcessAsync("dotnet",
                [
                    "nuget",
                    "push",
                    file,
                    "-s",
                    "https://api.nuget.org/v3/index.json",
                    "-k",
                    apiKey!,
                    "--skip-duplicate"
                ]);
            });
        }
    }
    finally
    {
        WriteTaskBanner("pack", "dotnet pack", false);
    }
}

async Task RetryAsync(Func<Task> action, int maxAttempts = 3)
{
    Exception? lastException = null;
    for (var attempt = 1; attempt <= maxAttempts; attempt++)
    {
        try
        {
            await action();
            return;
        }
        catch (Exception ex) when (attempt < maxAttempts)
        {
            lastException = ex;
            await Task.Delay(TimeSpan.FromSeconds(attempt));
        }
    }

    throw lastException ?? new InvalidOperationException("RetryAsync failed without an exception.");
}

void WriteTaskBanner(string name, string description, bool executing)
{
    var suffix = executing ? "executing" : "executed";
    Console.WriteLine($@"===== Task [{name}] {description} {suffix} ======");
}

async Task ExecuteProcessAsync(string fileName, IReadOnlyList<string> arguments)
{
    Console.WriteLine("Executing command:");
    Console.WriteLine($"    {fileName} {string.Join(" ", arguments.Select(QuoteArgument))}");
    Console.WriteLine();

    var startInfo = new ProcessStartInfo
    {
        FileName = fileName,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        UseShellExecute = false
    };

    foreach (var argument in arguments)
        startInfo.ArgumentList.Add(argument);

    using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
    process.OutputDataReceived += (_, e) =>
    {
        if (!string.IsNullOrEmpty(e.Data))
            Console.WriteLine(e.Data);
    };
    process.ErrorDataReceived += (_, e) =>
    {
        if (!string.IsNullOrEmpty(e.Data))
            Console.Error.WriteLine(e.Data);
    };

    if (!process.Start())
        throw new InvalidOperationException($"Failed to start process {fileName}.");

    process.BeginOutputReadLine();
    process.BeginErrorReadLine();
    await process.WaitForExitAsync();

    if (process.ExitCode != 0)
        throw new Exception($"Command failed with exit code {process.ExitCode}: {fileName} {string.Join(" ", arguments.Select(QuoteArgument))}");

    Console.WriteLine();
}

string QuoteArgument(string value)
    => value.Contains(' ') || value.Contains(';') || value.Contains('"')
        ? $"\"{value.Replace("\"", "\\\"")}\""
        : value;

IEnumerable<string> GetBuildProjects(string targetFramework)
{
    foreach (var project in buildProjects)
    {
        if (targetFramework == "net8.0" &&
            project.Equals("./test/GaussDB.NativeAotTests/GaussDB.NativeAotTests.csproj", StringComparison.OrdinalIgnoreCase))
        {
            continue;
        }

        yield return project;
    }
}
