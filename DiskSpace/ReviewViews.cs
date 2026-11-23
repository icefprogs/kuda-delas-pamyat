using System.Windows.Controls;

namespace DiskSpace;

public partial class MainWindow
{
    private sealed record Usage(DateTimeOffset Since, Dictionary<string, DateTimeOffset> Last);
    private Usage usage = new(DateTimeOffset.Now, new(StringComparer.OrdinalIgnoreCase));
    private FileRow[]? reviewFiles;
    private AppEntry[] reviewApps = [];
    private int reviewDays = 90;
    private bool showApps;
    private bool reviewLoading;

    private void LoadUsage()
    {
        if (demo) return;
        var saved = store.Read("usage.json", usage);
        usage = saved with { Last = new Dictionary<string, DateTimeOffset>(saved.Last, StringComparer.OrdinalIgnoreCase) };
    }

    private async Task ObserveAppsAsync()
    {
        if (demo || !settings.Watch) return;
        var paths = await Task.Run(Review.ProcessPaths);
        var now = DateTimeOffset.Now;
        foreach (var path in paths) usage.Last[path] = now;
        usage = usage with { Last = usage.Last.OrderByDescending(item => item.Value).Take(3000)
            .ToDictionary(item => item.Key, item => item.Value, StringComparer.OrdinalIgnoreCase) };
        TrySave(() => store.Save("usage.json", usage));
    }

    private async Task LoadReviewAsync()
    {
        if (reviewLoading) return;
        reviewLoading = true;
        try
        {
            var days = reviewDays;
            var summaries = scans.Values.ToArray();
            await RunWork("Проверяем файлы и установленные приложения…", async token =>
            {
                if (demo)
                {
                    reviewFiles = summaries.SelectMany(scan => scan.AllFiles).Take(6)
                        .Select((file, index) => file with { Changed = DateTime.UtcNow.AddDays(-days - (index + 1) * 17) }).ToArray();
                    reviewApps = [new("DaVinci Resolve", "Blackmagic Design", @"C:\Program Files\Blackmagic Design", null, 4L << 30, DateTime.Today.AddDays(-185)),
                        new("OBS Studio", "OBS Project", @"C:\Program Files\obs-studio", null, 340L << 20, DateTime.Today.AddDays(-75))];
                    return;
                }
                var results = await Task.Run(() => (Files: Review.OldFiles(summaries.SelectMany(scan => scan.AllFiles), days, token),
                    Apps: Review.Apps(token)), token);
                reviewFiles = results.Files;
                reviewApps = results.Apps;
            });
            if (active == "Ревизия") Render();
        }
        finally { reviewLoading = false; }
    }

    private sealed record ReviewFile(FileRow File)
    {
        public string Name => File.Name;
        public string Path => File.Path;
        public string Size => Bytes(File.Bytes);
        public string Changed => File.Changed.ToLocalTime().ToString("dd.MM.yyyy");
    }

    private sealed record ReviewApp(AppEntry App, DateTimeOffset? Seen)
    {
        public string Name => App.Name;
        public string Publisher => App.Publisher;
        public string Size => App.Bytes > 0 ? Bytes(App.Bytes) : "—";
        public string Installed => App.Installed?.ToString("dd.MM.yyyy") ?? "—";
        public string Last => Seen is { } date ? Date(date) : "Не замечен";
    }

    private DateTimeOffset? LastObserved(AppEntry app)
    {
        if (app.Executable is not null && usage.Last.TryGetValue(app.Executable, out var direct)) return direct;
        if (app.Location is null || app.Location.Length <= 3) return null;
        return usage.Last.Where(entry => LocalStore.IsUnder(entry.Key, app.Location))
            .Select(entry => (DateTimeOffset?)entry.Value).Max();
    }

    private void RenderReview()
    {
        Heading("Ревизия", "Найдите старые файлы и проверьте, какие программы ещё нужны");
        var bar = Actions();
        bar.Children.Add(ActionButton("Давние файлы", () => { showApps = false; Render(true); }, !showApps));
        bar.Children.Add(ActionButton("Установленные приложения", () => { showApps = true; Render(true); }, showApps));
        bar.Children.Add(ActionButton("Обновить список", async () => await LoadReviewAsync()));
        Page.Children.Add(bar);
        if (showApps)
        {
            AddText(Page, $"Наблюдение с {usage.Since.ToLocalTime():dd.MM.yyyy}. Здесь показаны только процессы, которые приложение успело заметить. Дата установки может обновиться при обслуживании программы.",
                margin: new Thickness(0, 0, 0, 12));
            var rows = reviewApps.Select(app => new ReviewApp(app, LastObserved(app)))
                .OrderBy(row => row.Seen.HasValue).ThenBy(row => row.Seen)
                .ThenBy(row => row.App.Installed ?? DateTime.MaxValue).ThenByDescending(row => row.App.Bytes).ToArray();
            var table = Table(rows, ("Приложение", "Name", 224), ("Размер", "Size", 96), ("Установка / обновление", "Installed", 158),
                ("Запуск замечен", "Last", 132), ("Издатель", "Publisher", 148));
            Page.Children.Add(table);
            Page.Children.Add(ActionButton("Удалить приложение в настройках Windows", () =>
            {
                if (!demo)
                {
                    var result = Review.LaunchAppsSettings();
                    SetStatus(result ?? "Откройте «Установленные приложения» в настройках Windows");
                }
            }));
            return;
        }

        var range = Actions();
        foreach (var days in new[] { 30, 90, 180, 365 })
            range.Children.Add(ActionButton($"> {days} дн.", async () => { reviewDays = days; await LoadReviewAsync(); }, days == reviewDays));
        Page.Children.Add(range);
        AddText(Page, "Список по дате изменения. Проверьте содержимое перед переносом в корзину.",
            margin: new Thickness(0, 0, 0, 12));
        if (reviewFiles is not { Length: > 0 })
        {
            AddText(Page, reviewFiles is null ? "Ищем в загрузках, документах и результатах анализа…" : "Подходящих файлов от 1 МБ не найдено");
            return;
        }
        var files = Table(reviewFiles.Select(file => new ReviewFile(file)), ("Файл", "Name", 235), ("На диске", "Size", 102),
            ("Изменён", "Changed", 108), ("Путь", "Path", 408));
        Page.Children.Add(files);
        var actions = Actions();
        actions.Children.Add(ActionButton("Открыть в Проводнике", () =>
        {
            if (!demo && files.SelectedItem is ReviewFile row) OpenExplorer(row.Path);
        }));
        var recycle = ActionButton("В корзину…", async () =>
        {
            if (files.SelectedItem is ReviewFile row) await RecycleItem(row.File);
        }, true);
        recycle.IsEnabled = false;
        files.SelectionChanged += (_, _) => recycle.IsEnabled = files.SelectedItem is ReviewFile;
        actions.Children.Add(recycle);
        Page.Children.Add(actions);
    }

    private async Task RecycleItem(FileRow row)
    {
        if (current is not null) { SetStatus("Сначала завершите текущую операцию"); return; }
        var restriction = demo ? null : Recycle.Validate(row.Path, row.Folder);
        if (restriction is not null) { SetStatus(restriction); return; }
        if (MessageBox.Show(this,
            $"Переместить в корзину?\n\n{row.Path}\n{Bytes(row.Bytes)}\n\nЕсли корзина недоступна, Windows отдельно спросит о безвозвратном удалении.",
            row.Folder ? "Папка" : "Файл", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        var owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        string? result = null;
        var ok = await RunWork("Перемещаем в корзину…", async _ =>
        {
            if (!demo) result = await Task.Run(() => Recycle.Move(row.Path, row.Folder, owner));
        });
        if (!ok) return;
        if (result is not null) { SetStatus(result); return; }
        bool Removed(string path) => LocalStore.SamePath(path, row.Path) || (row.Folder && LocalStore.IsUnder(path, row.Path));
        foreach (var (id, scan) in scans.ToArray())
        {
            if (Removed(scan.Scope)) { scans.Remove(id); continue; }
            if (!LocalStore.IsUnder(row.Path, scan.Scope)) continue;
            scans[id] = scan with
            {
                Bytes = Math.Max(0, scan.Bytes - row.Bytes),
                Rows = scan.Rows.Where(item => !Removed(item.Path)).ToArray(),
                AllFiles = scan.AllFiles.Where(item => !Removed(item.Path)).ToArray(),
                Folders = scan.Folders.Where(item => !Removed(item.Path)).Select(item =>
                    LocalStore.IsUnder(row.Path, item.Path) ? item with { Bytes = Math.Max(0, item.Bytes - row.Bytes) } : item).ToArray()
            };
        }
        reviewFiles = reviewFiles?.Where(item => !Removed(item.Path)).ToArray();
        if (!demo) recycleContents = await Task.Run(Recycle.Count);
        journal.Add(new(DateTimeOffset.Now, row.Path, demo ? "Демо · имитация" : "Обработано через корзину Windows", row.Bytes));
        TrySave(PersistJournal);
        await RefreshVolumes(false);
        Render();
        SetStatus(demo ? "Демо · имитация удаления" : "Объект убран. Освобождение места проверяется после очистки корзины.");
    }
}
