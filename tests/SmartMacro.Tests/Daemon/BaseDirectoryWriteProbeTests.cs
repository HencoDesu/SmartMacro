using SmartMacro.Daemon;

namespace SmartMacro.Tests.Daemon;

// Проба пера, на которой держится портативная раскладка: всё состояние демона лежит рядом с его
// exe, поэтому «сюда нельзя писать» обязано выясниться на первой секунде, а не через десять
// минут по кривому симптому.
//
// Отказ в правах ACL тесты не подделывают: чтобы получить каталог, куда нельзя писать, пришлось
// бы править списки доступа на живой файловой системе, а прогон тестов идёт с правами, которые
// эту правку тут же и отменят. Вместо этого берётся отказ, который файловая система выдаёт сама
// и всегда одинаково, — несуществующий диск. Разбирается он тем же catch, что и UnauthorizedAccess.
public class BaseDirectoryWriteProbeTests
{
    [Test]
    public async Task TryVerifyWritable_OnAWritableFolder_Succeeds()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"smartmacro-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var ok = BaseDirectoryWriteProbe.TryVerifyWritable(directory, out var failure);

            await Assert.That(ok).IsTrue();
            await Assert.That(failure).IsNull();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task TryVerifyWritable_LeavesNothingBehind()
    {
        // Демон зовёт пробу на КАЖДОМ старте, включая отвергнутый второй экземпляр. Каталог с
        // мусором вида .smartmacro-write-probe-1234 рядом с exe — не то, что пользователь должен
        // однажды обнаружить.
        var directory = Path.Combine(Path.GetTempPath(), $"smartmacro-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            BaseDirectoryWriteProbe.TryVerifyWritable(directory, out _);

            await Assert.That(Directory.GetFileSystemEntries(directory)).IsEmpty();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task TryVerifyWritable_WhenTheFolderCannotBeCreated_FailsWithAReason()
    {
        var ok = BaseDirectoryWriteProbe.TryVerifyWritable(@"\\?\Q:\smartmacro-nowhere", out var failure);

        await Assert.That(ok).IsFalse();
        // Причина уходит в окно с ошибкой, поэтому пустой она быть не имеет права: «не удалось»
        // без объяснения — ровно та бесполезная диагностика, ради которой проба и заводилась.
        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!).IsNotEmpty();
    }

    [Test]
    public async Task TryVerifyWritable_RejectsBlankDirectories()
    {
        await Assert.That(() => BaseDirectoryWriteProbe.TryVerifyWritable("", out _)).Throws<ArgumentException>();
        await Assert.That(() => BaseDirectoryWriteProbe.TryVerifyWritable("   ", out _)).Throws<ArgumentException>();
    }

    [Test]
    public async Task DescribeFailure_NamesThePathAndWhatToDo()
    {
        var text = BaseDirectoryWriteProbe.DescribeFailure(@"C:\Program Files\SmartMacro\", "Отказано в доступе.");

        // Текст показывается в единственном окне, которое пользователь увидит: без пути он не
        // поймёт, о каком каталоге речь, а без причины — почему именно.
        await Assert.That(text).Contains(@"C:\Program Files\SmartMacro\");
        await Assert.That(text).Contains("Отказано в доступе.");
        await Assert.That(text).Contains("Распакуйте");
    }
}
