using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SmartMacro.Vision;

// Iterative harness for the vision pipeline. After the class-based identification
// refactor this runner is just the Tesseract coord-reader debug pass — the old name-
// template matrix went away with INameMatcher.
//
// To tune the ClassMatcher: drop a stats-window screenshot per class into samples/
// and use the dump-captures button in the app UI to visualise the binarised region.
//
// Usage:
//   cd "Perfect World Agent"
//   dotnet run --project tools/VisionSampleRunner

var repoRoot = FindRepoRoot();
var samplesDir = Path.Combine(repoRoot, "samples");

if (!Directory.Exists(samplesDir))
{
    Console.Error.WriteLine($"Missing samples dir under {repoRoot}");
    return 1;
}

var samples = Directory.GetFiles(samplesDir, "*.jpg")
    .Select(p => (FileName: Path.GetFileName(p), Bytes: File.ReadAllBytes(p)))
    .ToArray();

Console.WriteLine($"Loaded {samples.Length} samples");
Console.WriteLine();

var debugDir = Path.Combine(samplesDir, ".debug");
Directory.CreateDirectory(debugDir);

// === Coordinate OCR pass ===
Console.WriteLine("=== CoordinateReader (Tesseract) ===");

var tessdataPath = Path.Combine(AppContext.BaseDirectory, "tessdata");
var coordOptions = Options.Create(new CoordinateReaderOptions { TessdataPath = tessdataPath });
using var coordReader = new TesseractCoordinateReader(coordOptions, NullLogger<TesseractCoordinateReader>.Instance);

foreach (var (fileName, bytes) in samples)
{
    var stem = Path.GetFileNameWithoutExtension(fileName);
    try
    {
        File.WriteAllBytes(Path.Combine(debugDir, $"coords-raw-{stem}.png"), coordReader.DebugCrop(bytes));
        File.WriteAllBytes(Path.Combine(debugDir, $"coords-mask-{stem}.png"), coordReader.DebugBinarize(bytes));
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  preprocess failed for {fileName}: {ex.Message}");
    }
}

Console.WriteLine($"{"sample",-50}  result");
Console.WriteLine(new string('-', 75));
foreach (var (fileName, bytes) in samples)
{
    var coords = coordReader.Read(bytes);
    Console.WriteLine($"{Truncate(fileName, 50),-50}  {(coords is null ? "(no match)" : coords.ToString())}");
}

return 0;

static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "SmartMacro.slnx")))
    {
        dir = dir.Parent;
    }
    return dir?.FullName ?? throw new InvalidOperationException("Could not locate repo root.");
}
