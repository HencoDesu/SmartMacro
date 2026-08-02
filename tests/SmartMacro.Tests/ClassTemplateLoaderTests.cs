using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Vision;

namespace SmartMacro.Tests;

// W0.1: string-keyed template loading — filename stem IS the tag. The loader reads raw
// bytes without decoding, so dummy byte content is enough for these tests.
public class ClassTemplateLoaderTests
{
    private static string CreateTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"smartmacro-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Test]
    public async Task Load_ScansPngFiles_KeyedByFilenameStem()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Лучник.png"), [1, 2, 3]);
            File.WriteAllBytes(Path.Combine(dir, "Жрец.png"), [4, 5]);

            var loader = new ClassTemplateLoader(dir, NullLogger<ClassTemplateLoader>.Instance);

            await Assert.That(loader.Templates).Count().IsEqualTo(2);
            await Assert.That(loader.Templates["Лучник"]).IsEquivalentTo(new byte[] { 1, 2, 3 });
            await Assert.That(loader.Templates["Жрец"]).IsEquivalentTo(new byte[] { 4, 5 });
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Load_IgnoresNonPngFiles()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Лучник.png"), [1]);
            File.WriteAllBytes(Path.Combine(dir, "readme.txt"), [2]);
            File.WriteAllBytes(Path.Combine(dir, "Шаман.jpg"), [3]);

            var loader = new ClassTemplateLoader(dir, NullLogger<ClassTemplateLoader>.Instance);

            await Assert.That(loader.Templates).Count().IsEqualTo(1);
            await Assert.That(loader.Templates.ContainsKey("Лучник")).IsTrue();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Test]
    public async Task Load_MissingDirectory_YieldsEmptySet()
    {
        var missingDir = Path.Combine(Path.GetTempPath(), $"smartmacro-missing-{Guid.NewGuid():N}");

        var loader = new ClassTemplateLoader(missingDir, NullLogger<ClassTemplateLoader>.Instance);

        await Assert.That(loader.Templates).IsEmpty();
    }

    [Test]
    public async Task Load_Keys_AreCaseSensitive()
    {
        var dir = CreateTempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "Boss.png"), [1]);

            var loader = new ClassTemplateLoader(dir, NullLogger<ClassTemplateLoader>.Instance);

            await Assert.That(loader.Templates.ContainsKey("Boss")).IsTrue();
            await Assert.That(loader.Templates.ContainsKey("boss")).IsFalse();
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
