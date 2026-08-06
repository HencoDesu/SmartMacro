using Microsoft.Extensions.Logging.Abstractions;
using SmartMacro.Contracts.Settings;
using SmartMacro.Resources;
using SmartMacro.Settings;

namespace SmartMacro.Tests.Settings;

// Хранилище настроек. Проверяется то, ради чего оно устроено именно так, а не иначе:
//
//   * демон обязан подниматься БЕЗ панели и создавать файл сам (иначе первый запуск на новой
//     машине требует открыть панель, а панель — по требованию);
//   * нечитаемый файл не роняет демон и НЕ ПЕРЕЗАПИСЫВАЕТСЯ (пользователь правил его блокнотом и
//     поставил лишнюю запятую — «программа молча вернула всё к заводскому» здесь потеря работы);
//   * правка файла руками подхватывается наблюдателем (папку открывают кнопкой, и правка блокнотом
//     не должна теряться при следующем сохранении из панели);
//   * отвергнутые настройки НЕ пишутся — договорённость та же, что у SaveMacro.
public class SettingsStoreTests
{
    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"smartmacro-settings-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static SettingsStore Open(string directory) =>
        new(directory, NullLogger<SettingsStore>.Instance);

    [Test]
    public async Task Constructor_CreatesTheFileWithDefaultsWhenItIsMissing()
    {
        var directory = TempDirectory();
        try
        {
            using var store = Open(directory);

            var path = Path.Combine(directory, SettingsStore.FileName);
            await Assert.That(File.Exists(path)).IsTrue();
            await Assert.That(store.Current.Profiles).Count().IsEqualTo(1);
            await Assert.That(store.Current.Profiles[0].ProcessName).IsEqualTo("elementclient_64");
            // Записанное обязано читаться обратно тем же сериализатором.
            await Assert.That(AppSettingsJson.Deserialize(File.ReadAllText(path)).Watch.ProcessPollIntervalSeconds)
                .IsEqualTo(1);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Файл СТАРОЙ формы: скобка пробуждения лежала полями профиля. Без разового чтения она стала
    // бы неизвестными членами — а их System.Text.Json молча пропускает, — и у всех, кто уже
    // пользуется программой, окна перестали бы просыпаться при первом сохранении из панели.
    //
    // ⚠️ Файл при этом НЕ ПЕРЕПИСЫВАЕТСЯ: инвариант «существующий файл конструктор не трогает»
    // сильнее удобства, а миграция живёт в памяти до первого сохранения.
    [Test]
    public async Task Constructor_AdoptsTheWakeBracketFromAnOldFileWithoutRewritingIt()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, SettingsStore.FileName);
            const string legacy = """
                {
                  "Profiles": [
                    {
                      "ProcessName": "elementclient_64",
                      "ActivationLParam": 37336,
                      "SettleDelayMs": 200,
                      "DeactivationDelayMs": 100
                    }
                  ]
                }
                """;
            File.WriteAllText(path, legacy);

            using var store = Open(directory);

            var hook = store.Current.FindHook("elementclient_64")!;
            await Assert.That(hook.ActivationLParam).IsEqualTo(37336u);
            await Assert.That(hook.SettleMs).IsEqualTo(200);
            await Assert.That(hook.DeactivateMs).IsEqualTo(100);
            await Assert.That(File.ReadAllText(path)).IsEqualTo(legacy);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Constructor_ReadsAnExistingFileAndDoesNotTouchIt()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, SettingsStore.FileName);
            var custom = AppSettings.Default with
            {
                Watch = new WatchSettings { ProcessPollIntervalSeconds = 7, WindowPollIntervalSeconds = 9 },
            };
            File.WriteAllText(path, AppSettingsJson.Serialize(custom));
            var writtenAt = File.GetLastWriteTimeUtc(path);

            using var store = Open(directory);

            await Assert.That(store.Current.Watch.ProcessPollIntervalSeconds).IsEqualTo(7);
            await Assert.That(File.GetLastWriteTimeUtc(path)).IsEqualTo(writtenAt);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Кривой файл — это правка руками, а не повод обнулить чужую работу.
    [Test]
    public async Task Constructor_KeepsDefaultsAndTheBrokenFileWhenItDoesNotParse()
    {
        var directory = TempDirectory();
        try
        {
            var path = Path.Combine(directory, SettingsStore.FileName);
            const string broken = "{ \"Watch\": { \"ProcessPollIntervalSeconds\": 3, } ";
            File.WriteAllText(path, broken);

            using var store = Open(directory);

            await Assert.That(store.Current.Watch.ProcessPollIntervalSeconds).IsEqualTo(1);
            // Главное: файл на месте и не переписан — пользователь чинит запятую, а не набирает
            // всё заново.
            await Assert.That(File.ReadAllText(path)).IsEqualTo(broken);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task SaveAsync_WritesAndRaisesChanged()
    {
        var directory = TempDirectory();
        try
        {
            using var store = Open(directory);
            AppSettings? seen = null;
            store.SettingsChanged += s => seen = s;

            var issues = await store.SaveAsync(AppSettings.Default with
            {
                Watch = new WatchSettings { ProcessPollIntervalSeconds = 4, WindowPollIntervalSeconds = 6 },
            });

            await Assert.That(issues).IsEmpty();
            await Assert.That(store.Current.Watch.ProcessPollIntervalSeconds).IsEqualTo(4);
            await Assert.That(seen?.Watch.WindowPollIntervalSeconds).IsEqualTo(6);

            var onDisk = AppSettingsJson.Deserialize(File.ReadAllText(store.FilePath));
            await Assert.That(onDisk.Watch.ProcessPollIntervalSeconds).IsEqualTo(4);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Непустой список = НЕ записано. Та же договорённость, что у SaveMacro, и проверять её надо
    // именно по диску: «отвергли, но всё-таки записали» — самый неприятный вид полуработы.
    [Test]
    public async Task SaveAsync_RejectedSettingsAreNotWritten()
    {
        var directory = TempDirectory();
        try
        {
            using var store = Open(directory);
            var before = File.ReadAllText(store.FilePath);

            var issues = await store.SaveAsync(AppSettings.Default with
            {
                Watch = new WatchSettings { ProcessPollIntervalSeconds = 0, WindowPollIntervalSeconds = 2 },
            });

            await Assert.That(issues).IsNotEmpty();
            await Assert.That(store.Current.Watch.ProcessPollIntervalSeconds).IsEqualTo(1);
            await Assert.That(File.ReadAllText(store.FilePath)).IsEqualTo(before);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task ResetAsync_RestoresDefaults()
    {
        var directory = TempDirectory();
        try
        {
            using var store = Open(directory);
            await store.SaveAsync(AppSettings.Default with { Profiles = [] });
            await Assert.That(store.Current.Profiles).IsEmpty();

            var restored = await store.ResetAsync();

            await Assert.That(restored.Profiles).Count().IsEqualTo(1);
            await Assert.That(store.Current.Profiles[0].ProcessName).IsEqualTo("elementclient_64");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // Правка блокнотом: событие — это ровно то, чем правка руками доезжает до открытой панели.
    //
    // ⚠️ Отметка времени файла двигается ЯВНО, и без этого тест плавал. Подавление эха в
    // хранилище сравнивает LastWriteTimeUtc с подписью, поставленной, когда оно писало само, — а
    // конструктор пишет файл, если его нет. Тест писал следом через микросекунды, обе записи
    // попадали в один тик отметки, и правка гасилась как своя же. Событие не приходило ВООБЩЕ,
    // поэтому увеличение срока ожидания не помогало (проверено: с пятью секундами и [Retry(2)]
    // падали все три попытки, с тридцатью — те же три).
    //
    // Сдвиг отметки — не поблажка тесту, а устранение того, чего в жизни не бывает: человек в
    // блокноте не сохраняет файл через микросекунду после демона. Всё остальное тест проверяет
    // по-настоящему — наблюдатель обязан сработать, дребезг отгаснуть, HasFileChanged признать
    // изменение, Load разобрать файл, а событие дойти до подписчика.
    [Test]
    public async Task ExternalEdit_IsPickedUpByTheWatcher()
    {
        var directory = TempDirectory();
        try
        {
            using var store = Open(directory);
            var changed = new TaskCompletionSource<AppSettings>(TaskCreationOptions.RunContinuationsAsynchronously);
            store.SettingsChanged += s => changed.TrySetResult(s);

            await File.WriteAllTextAsync(store.FilePath, AppSettingsJson.Serialize(AppSettings.Default with
            {
                Watch = new WatchSettings { ProcessPollIntervalSeconds = 11, WindowPollIntervalSeconds = 13 },
            }));

            // Отодвигаем отметку от той, что поставил конструктор: см. пояснение выше.
            File.SetLastWriteTimeUtc(store.FilePath, DateTime.UtcNow.AddSeconds(1));

            var seen = await changed.Task.WaitAsync(TimeSpan.FromSeconds(15));

            await Assert.That(seen.Watch.ProcessPollIntervalSeconds).IsEqualTo(11);
            await Assert.That(store.Current.Watch.WindowPollIntervalSeconds).IsEqualTo(13);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
