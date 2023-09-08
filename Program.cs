using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using System.Collections.Concurrent;
using System.CommandLine;
using System.CommandLine.IO;
using System.CommandLine.Rendering;
using System.CommandLine.Rendering.Views;

var root = new RootCommand();

var pathArg = new Argument<string>("path", () => ".", "Directory to analyze.");
root.AddArgument(pathArg);

var searchPatternOption = new Option<string>("--pattern", () => "**", "Search pattern to filter files.");
root.AddOption(searchPatternOption);

var excludePatternOption = new Option<string[]>("--exclude", "Exclusion pattern to filter files.");
root.AddOption(excludePatternOption);

var groupingOption = new Option<Grouping>("--grouping", () => Grouping.Extension, "Grouping mode.");
root.AddOption(groupingOption);

var readBufferSizeOption = new Option<int>("--buffer-length", () => 4096, "Size of read buffer in bytes.");
root.AddOption(readBufferSizeOption);

var threadsOption = new Option<int>("--threads", () => 1, "Number of threads to read files.");
root.AddOption(threadsOption);

string[] defaultExclusions = new[]
{
    ".git",
    ".vs",
    ".vscode",

    "**/bin/**",
    "**/obj/**",
    "**/node_modules/**",
    "**.dll",
    "*.pdb",
    "**.wasm",
    "**.bin",
    "**.obj",
    "**.so",
    "**.min.*",
    "**.map",
    "**.g.cs",

    "**.cache",
    "**.sqlite",
    "**.lock",

    "**.jpg",
    "**.gif",
    "**.gifv",
    "**.png",
    "**.webm",
    "**.webp",
    "**.mp3",
    "**.avi",
    "**.mp4",
    "**.mkv",

    "**.zip",
    "**.tgz",
    "**.gz",
    "**.7z",
};

root.SetHandler(async context =>
{
	// bind parameters
	var folder = context.BindingContext.ParseResult.GetValueForArgument(pathArg);
	var searchPattern = context.BindingContext.ParseResult.GetValueForOption(searchPatternOption);
	var excludePatterns = context.BindingContext.ParseResult.GetValueForOption(excludePatternOption);
	var grouping = context.BindingContext.ParseResult.GetValueForOption(groupingOption);
	var readBufferSize = context.BindingContext.ParseResult.GetValueForOption(readBufferSizeOption);
	var threads = context.BindingContext.ParseResult.GetValueForOption(threadsOption);

	// setup console
	var console = new SystemConsole();
	var region = new Region(0, 0, Console.WindowWidth, Console.WindowHeight);
	var renderer = new ConsoleRenderer(console);

	// echo parameters
	console.WriteLine($"Folder: {Path.GetFullPath(folder)}");
	console.WriteLine($"Pattern: {searchPattern}");

	// find files
	console.WriteLine($"Collecting files...");
	var matcher = new Matcher();
	matcher.AddInclude(searchPattern);
	matcher.AddExcludePatterns(defaultExclusions);
	if (excludePatterns != null)
	{
		matcher.AddExcludePatterns(excludePatterns);
	}
    var results = matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(folder)));
	var files = results.Files.Select(f => Path.Combine(folder, f.Path)).ToList();
	console.WriteLine($"Total number of files: {files.Count:N0}");
	var queue = new ConcurrentQueue<string>(files);

	// process files
	Dictionary<string, int> CountByGrouping = new(StringComparer.OrdinalIgnoreCase);
	Dictionary<string, int> LineCountByGrouping = new(StringComparer.OrdinalIgnoreCase);
	await Task.WhenAll(Enumerable.Repeat(1, threads).Select(_ => WorkAsync()));

	// render results
	var items = new List<SummaryItem>();
	foreach (var (key, value) in LineCountByGrouping.OrderByDescending(kv => kv.Value))
	{
		items.Add(new SummaryItem(key, CountByGrouping[key], value));
	}

	var view = new TableView<SummaryItem>();
	view.AddColumn(r => r.Group, grouping switch
	{
		Grouping.Extension => "Extension",
		Grouping.Directory => "Directory",
		_ => throw new NotSupportedException(),
	});
	view.AddColumn(r => r.NumberOfFiles, "Files");
	view.AddColumn(r => r.NumberOfLines, "Lines");
	view.Items = items;
	view.Render(renderer, region);

	async Task WorkAsync()
	{
		Dictionary<string, int> countByExtension = new(StringComparer.OrdinalIgnoreCase);
		Dictionary<string, int> lineCountByExtension = new(StringComparer.OrdinalIgnoreCase);

		byte[] bufferBackend = new byte[readBufferSize];
		Memory<byte> buffer = new(bufferBackend);

		while (queue.TryDequeue(out var file))
		{
			// group
			var key = grouping switch
			{
				Grouping.Extension => Path.GetExtension(file),
				Grouping.Directory => Path.GetDirectoryName(file),
				_ => throw new NotSupportedException(),
			};

			if (!countByExtension.TryGetValue(key, out int fileCount))
			{
				fileCount = 0;
			}
			countByExtension[key] = fileCount + 1;

			// count lines
			if (!lineCountByExtension.TryGetValue(key, out int lineCount))
			{
				lineCount = 0;
			}
			try
			{
				var fileLineCount = await CountLinesAsync(file, default);
				lineCountByExtension[key] = lineCount + fileLineCount;
			}
			catch (FileNotFoundException)
			{
				console.Error.WriteLine($"File '{file}' was not found.");
			}
		}

		// summarize results
		lock (CountByGrouping)
		{
			foreach (var (key, value) in countByExtension)
			{
				if (!CountByGrouping.TryGetValue(key, out int fileCount))
				{
					fileCount = 0;
				}
				CountByGrouping[key] = fileCount + value;
			}
		}

		lock (LineCountByGrouping)
		{
			foreach (var (key, value) in lineCountByExtension)
			{
				if (!LineCountByGrouping.TryGetValue(key, out int lineCount))
				{
					lineCount = 0;
				}
				LineCountByGrouping[key] = lineCount + value;
			}
		}

		async ValueTask<int> CountLinesAsync(string path, CancellationToken cancellationToken)
		{
			await using var stream = File.OpenRead(path);
			var count = 0;

			while (true)
			{
				var readBytes = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken);
				if (readBytes == 0)
				{
					break;
				}

				count += buffer.Span.Count((byte)'\n');
			}

			return count;
		}
	}
});

await root.InvokeAsync(args);
