namespace Deduplicator.Core.Models;

/// <summary>Настройки приложения, сохраняемые в JSON в папке профиля пользователя.</summary>
public sealed class AppSettings
{
    /// <summary>Порог схожести изображений по умолчанию (0..1).</summary>
    public double DefaultImageThreshold { get; set; } = 0.90;

    /// <summary>Порог схожести аудио по умолчанию (0..1).</summary>
    public double DefaultAudioThreshold { get; set; } = 0.85;

    /// <summary>Папка карантина (если null — используется папка по умолчанию в профиле).</summary>
    public string? QuarantinePath { get; set; }

    /// <summary>Удалять в корзину Windows (true) или в карантин-папку (false).</summary>
    public bool UseRecycleBin { get; set; } = true;

    /// <summary>Степень параллелизма для SSD (для HDD принудительно 1).</summary>
    public int MaxParallelism { get; set; } = 4;

    /// <summary>Считать pixel-hash для изображений (дороже, но точнее).</summary>
    public bool ComputePixelHash { get; set; } = false;

    /// <summary>Правило выбора эталона по умолчанию.</summary>
    public RepresentativeRule DefaultRepresentativeRule { get; set; } = RepresentativeRule.Oldest;

    /// <summary>Тема оформления: "Light", "Dark" или "Default" (системная).</summary>
    public string Theme { get; set; } = "Default";

    /// <summary>Путь к fpcalc.exe (если пусто — ищется рядом с приложением).</summary>
    public string? FpcalcPath { get; set; }

    /// <summary>
    /// Искать похожие изображения с учётом поворотов/отражений. По умолчанию выключено:
    /// обычный pHash надёжнее для одинаково ориентированных копий (разный формат/размер/обрезка).
    /// Включайте, если нужно ловить повёрнутые/зеркальные копии.
    /// </summary>
    public bool RotationInvariantImages { get; set; } = false;

    /// <summary>Папки-эталоны: файлы внутри них защищены от удаления (FR-подобно dupeGuru).</summary>
    public List<string> ReferenceFolders { get; set; } = new();

    /// <summary>Минимальный размер файла при сканировании, МБ (0 — без ограничения).</summary>
    public int MinFileSizeMb { get; set; } = 0;

    /// <summary>Пропускать скрытые/системные файлы при сканировании.</summary>
    public bool SkipHiddenSystem { get; set; } = true;

    /// <summary>Расширения, исключаемые из сканирования (через запятую).</summary>
    public string ExcludedExtensions { get; set; } = "";

    /// <summary>Проверять обновления на GitHub (по умолчанию выключено — приложение офлайн).</summary>
    public bool CheckUpdatesEnabled { get; set; } = false;

    /// <summary>Репозиторий обновлений в формате owner/name.</summary>
    public string UpdateRepository { get; set; } = "";

    /// <summary>Показывать ли обучение при первом запуске.</summary>
    public bool OnboardingCompleted { get; set; } = false;

    /// <summary>
    /// Какие категории искать по умолчанию (P0-A «что искать»). Хранится как имена enum FileCategory;
    /// пустой/отсутствующий список трактуется как «все» (обратная совместимость со старыми настройками).
    /// </summary>
    public List<string> IncludedCategories { get; set; } = new();

    /// <summary>Режим «Эко» (этап 2.9): скан идёт с пониженным приоритетом процесса и чтением
    /// содержимого в один поток — слабый ноутбук остаётся отзывчивым. Стабильность важнее скорости.</summary>
    public bool EcoMode { get; set; } = false;

    /// <summary>
    /// 3.4: сохранять переносимый кэш проверки (скрытый файл .dedupindex) в корне съёмных носителей
    /// после успешного скана и читать его при сканировании. Флешка, проверенная на одном компьютере,
    /// на другом сканируется почти мгновенно — хэши не пересчитываются. Это чистый КЭШ: любое
    /// расхождение игнорируется, а безопасность удаления прикрывают побайтовая сверка и
    /// перепроверка перед удалением. По умолчанию включено — кэш безопасен.
    /// </summary>
    public bool PortableIndexEnabled { get; set; } = true;

    /// <summary>
    /// Сторожевой режим (Sentinel-lite, 3.3 облегчённо): пока приложение ОТКРЫТО, следить за
    /// появлением новых файлов-дубликатов в папках <see cref="SentinelFolders"/> и показывать
    /// уведомление. Строго opt-in, фоновой службы нет — при закрытии приложения слежение исчезает.
    /// </summary>
    public bool SentinelEnabled { get; set; } = false;

    /// <summary>Папки, за которыми следит сторожевой режим (с подпапками).</summary>
    public List<string> SentinelFolders { get; set; } = new();

    /// <summary>
    /// Вид правой панели проводника (ночь-2): "icons" — крупные значки, "list" — список,
    /// "columns" — столбцы (как в Finder). Незнакомое значение трактуется как "list".
    /// </summary>
    public string ExplorerViewMode { get; set; } = "list";
}
