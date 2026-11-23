using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace DiskSpace;

public partial class MainWindow
{

    private bool previewAutomatic;
    private string cleanupResult = "";
    private (long Bytes, long Count) recycleContents;

    private async Task OpenCleanupAsync()
    {
        cleanupOpening = true;
        try
        {
            while (backgroundScan is not null && active == "Очистка") await Task.Delay(120);
            if (active == "Очистка" && current is null) await FindCleanup();
        }
        finally { cleanupOpening = false; }
    }

    private async Task<bool> FindCleanup(bool automatic = false)
    {
        var days = settings.MinimumAge;
        var exclusions = Exclusions;
        var ok = await RunWork("Проверяем временные файлы и кеши…", async token =>
        {
            if (demo) { candidates = DemoCandidates(); recycleContents = (188L * 1024 * 1024, 7); return; }
            var result = await Task.Run(() => (Candidates: Cleanup.Find(days, exclusions, token, automatic), Recycle: Recycle.Count()), token);
            candidates = result.Candidates;
            recycleContents = result.Recycle;
            previewAutomatic = automatic;
        });
        if (ok && !automatic) SetStatus(candidates?.Length > 0 ? $"Найдено {candidates.Length:N0} кандидатов" : "По этим правилам кандидатов нет");
        if (active == "Очистка" || active == "Обзор") Render();
        return ok;
    }

    private async Task RunAutoCleanup(bool force)
    {
        if (!settings.AutoClean || current is not null) return;
        if (!force && !Idle(60_000)) return;
        if (!await FindCleanup(automatic: true)) return;
        var plan = candidates?.Where(item => item.Automatic).ToArray() ?? [];
        if (plan.Length == 0)
        {
            autoReport = new(DateTimeOffset.Now, 0, 0, "Подходящих файлов нет");
            TrySave(() => store.Save("auto-cleanup.json", autoReport));
            if (active == "Очистка" || active == "Настройки") Render();
            return;
        }
        await ExecuteCleanup(plan, automatic: true, force: force);
    }

    private async Task ExecuteCleanup(Candidate[] plan, bool automatic = false, bool force = false)
    {
        if (current is not null) { SetStatus("Сначала завершите текущую операцию"); return; }
        if (plan.Length == 0) { SetStatus("Отметьте файлы для очистки"); return; }
        if (!automatic && MessageBox.Show(this,
            $"Удалить {plan.Length:N0} файлов кеша и временных данных безвозвратно?\nОбъём по результатам проверки — {Bytes(plan.Sum(item => item.Bytes))}.",
            "Очистка", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        var before = volumes.Where(volume => volume.Ready).ToDictionary(volume => volume.Id, volume => volume.Free);
        var limit = automatic ? (long)settings.LimitMb * 1024 * 1024 : long.MaxValue;
        var days = settings.MinimumAge;
        var exclusions = Exclusions;
        var logs = new List<CleanupLog>();
        var progress = new Progress<int>(done => SetStatus($"Очистка · обработано {done:N0} из {plan.Length:N0}"));
        long removed = 0;
        int skippedByLimit = 0;
        bool interruptedByInput = false;
        var complete = await RunWork("Выполняем очистку…", async token =>
        {
            await Task.Run(() =>
            {
                foreach (var item in plan)
                {
                    token.ThrowIfCancellationRequested();
                    if (automatic && (!settings.Watch || !settings.AutoClean || (!force && !Idle(60_000))))
                    { interruptedByInput = true; break; }
                    if (automatic && (!item.Automatic || item.Bytes > limit - removed))
                    { skippedByLimit++; continue; }
                    var result = Cleanup.Remove(item, days, exclusions);
                    logs.Add(result);
                    removed += result.Bytes;
                    if (logs.Count % 24 == 0) ((IProgress<int>)progress).Report(logs.Count);
                }
            }, token);
        });

        journal.AddRange(logs);
        if (logs.Count > 0) TrySave(PersistJournal);
        await RefreshVolumes(false);
        var measured = volumes.Where(volume => volume.Ready && before.ContainsKey(volume.Id))
            .Sum(volume => volume.Free - before[volume.Id]);
        var successful = logs.Count(log => log.Status is "Удалён" or "Демо · имитация");
        var skipped = logs.Count - successful + skippedByLimit;
        var phase = complete && !interruptedByInput ? "Готово" : "Прервано";
        cleanupResult = $"{phase} · удалено {successful:N0} ({Bytes(removed)}) · пропущено {skipped:N0}";
        if (skippedByLimit > 0) cleanupResult += $" · лимит {skippedByLimit:N0}";
        if (complete && !demo) cleanupResult += $" · свободное место {Diff(measured)}";
        SetStatus(cleanupResult);
        var removedPaths = logs.Where(log => log.Status is "Удалён" or "Демо · имитация")
            .Select(log => log.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (candidates is not null) candidates = candidates.Where(item => !removedPaths.Contains(item.Path)).ToArray();
        if (automatic)
        {
            var finishedAt = DateTimeOffset.Now;
            autoReport = new(finishedAt, plan.Length, removed, cleanupResult,
                interruptedByInput || !complete ? finishedAt.AddMinutes(5) : null);
            TrySave(() => store.Save("auto-cleanup.json", autoReport));
        }
        if (active == "Очистка" || active == "Обзор" || active == "Настройки") Render();
    }

    private void PersistJournal()
    {
        if (journal.Count > 1000) journal.RemoveRange(0, journal.Count - 1000);
        store.Save("cleanup.json", journal);
    }

    private sealed record CleanupRow(Candidate Item)
    {
        public bool Selected { get => Item.Selected; set => Item.Selected = value; }
        public string Name => Item.Name;
        public string Category => Item.Category;
        public string Path => Item.Path;
        public string Size => Bytes(Item.Bytes);
        public string Reason => Item.Reason;
        public string Date => Item.Changed.ToLocalTime().ToString("dd.MM.yy");
    }

    private sealed record LogRow(CleanupLog Item)
    {
        public string Path => Item.Path;
        public string Status => Item.Status;
        public string Size => Bytes(Item.Bytes);
        public string Date => MainWindow.Date(Item.Time);
    }


    private string? cleanupCategory;

    private void RenderCleanup()
    {
        Heading("Очистка", "Кеши, временные файлы и корзина");
        var plan = candidates ?? [];
        var summary = new Grid();
        summary.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
        summary.ColumnDefinitions.Add(new ColumnDefinition());
        var estimate = new StackPanel();
        AddText(estimate, previewAutomatic ? "Временные файлы и кеш шейдеров" : "Можно освободить");
        estimate.Children.Add(Text(candidates is null ? "—" : Bytes(plan.Sum(item => item.Bytes)), 42, bold: true));
        AddText(estimate, cleanupResult.Length == 0
            ? candidates is null ? "Подготавливаем список при открытии раздела" : $"{plan.Length:N0} файлов в списке"
            : cleanupResult, margin: new Thickness(0, 4, 12, 8));
        var actions = Actions();
        actions.Children.Add(ActionButton("Проверить заново", async () => await FindCleanup()));
        var run = ActionButton("Очистить выбранное…", async () => await ExecuteCleanup(plan.Where(item => item.Selected).ToArray()), true);
        run.IsEnabled = plan.Any(item => item.Selected);
        actions.Children.Add(run);
        estimate.Children.Add(actions);
        summary.Children.Add(estimate);
        var auto = new StackPanel { Margin = new Thickness(16, 0, 0, 0) };
        auto.Children.Add(Text("Автоочистка", 18, bold: true));
        auto.Children.Add(Toggle("Запускать в простое", settings.AutoClean, SetAutoClean));
        AddText(auto, cleanupRulesChanged ? "Подтвердите новые правила для автоочистки" : autoReport.Last == DateTimeOffset.MinValue ? autoReport.Result : $"{Date(autoReport.Last)} · {autoReport.Result}",
            margin: new Thickness(0, 4, 0, 8));
        var force = ActionButton("Запустить сейчас", async () => await RunAutoCleanup(force: true));
        force.IsEnabled = settings.AutoClean;
        force.HorizontalAlignment = HorizontalAlignment.Left;
        auto.Children.Add(force);
        Grid.SetColumn(auto, 1);
        summary.Children.Add(auto);
        Page.Children.Add(Card(summary));

        var disposable = new StackPanel();
        disposable.Children.Add(Text("Источники", 18, bold: true));
        if (candidates is not null && plan.Length == 0)
            AddText(disposable, "По выбранным правилам подходящих файлов нет. Проверьте корзину или откройте системную очистку.",
                margin: new Thickness(0, 8, 0, 0));
        foreach (var group in plan.GroupBy(item => item.Category).OrderByDescending(group => group.Sum(item => item.Bytes)))
        {
            var items = group.ToArray();
            var line = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
            var selection = new CheckBox { Content = $"{group.Key} · {items.Length:N0} файлов",
                IsChecked = items.All(item => item.Selected) ? true : items.All(item => !item.Selected) ? false : null,
                VerticalAlignment = VerticalAlignment.Center };
            selection.Click += (_, _) =>
            {
                foreach (var item in items) item.Selected = selection.IsChecked == true;
                Render();
            };
            var details = ActionButton((cleanupCategory == group.Key ? "Скрыть · " : "Файлы · ") + Bytes(items.Sum(item => item.Bytes)),
                () => { cleanupCategory = cleanupCategory == group.Key ? null : group.Key; Render(); });
            DockPanel.SetDock(details, Dock.Right);
            line.Children.Add(details);
            line.Children.Add(selection);
            disposable.Children.Add(line);
        }
        if (cleanupCategory is not null)
        {
            var rows = plan.Where(item => item.Category == cleanupCategory).Select(item => new CleanupRow(item)).ToArray();
            if (rows.Length != 0)
            {
                var table = Table(rows, ("Файл", "Name", 208), ("На диске", "Size", 108), ("Изменён", "Date", 96),
                    ("Причина", "Reason", 280), ("Путь", "Path", 320));
                table.MaxHeight = 292;
                table.Margin = new Thickness(0, 12, 0, 8);
                table.IsReadOnly = false;
                foreach (var column in table.Columns) column.IsReadOnly = true;
                var check = new FrameworkElementFactory(typeof(CheckBox));
                check.SetBinding(System.Windows.Controls.Primitives.ToggleButton.IsCheckedProperty,
                    new Binding(nameof(CleanupRow.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged });
                check.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
                check.AddHandler(CheckBox.ClickEvent, new RoutedEventHandler((_, _) => run.IsEnabled = plan.Any(item => item.Selected)));
                table.Columns.Insert(0, new DataGridTemplateColumn { Header = "Выбор", Width = 64, CellTemplate = new DataTemplate { VisualTree = check } });
                disposable.Children.Add(table);
            }
        }
        Page.Children.Add(Card(disposable));

        var system = new StackPanel();
        system.Children.Add(Text("Корзина и Windows", 18, bold: true));
        AddText(system, $"В корзине {Bytes(recycleContents.Bytes)} · {recycleContents.Count:N0} элементов",
            margin: new Thickness(0, 4, 0, 12));
        var tools = Actions();
        tools.Children.Add(ActionButton("Открыть корзину", () => { if (!demo) Recycle.Open(); }));
        tools.Children.Add(ActionButton("Очистить корзину…", async () => await EmptyRecycle()));
        tools.Children.Add(ActionButton("Системная очистка", async () => await SystemCleanup()));
        tools.Children.Add(ActionButton("Память Windows", () => { if (!demo) Cleanup.OpenWindowsCleanup(); }));
        system.Children.Add(tools);
        Page.Children.Add(Card(system));

        Page.Children.Add(Text("Журнал очистки", 18, bold: true));
        if (journal.Count == 0) AddText(Page, "Операций пока нет", margin: new Thickness(0, 4, 0, 0));
        else Page.Children.Add(Table(journal.AsEnumerable().Reverse().Take(200).Select(item => new LogRow(item)).ToArray(),
            ("Время", "Date", 112), ("Результат", "Status", 252), ("Объём", "Size", 96), ("Путь", "Path", 448)));
    }

    private async Task EmptyRecycle()
    {
        if (current is not null) { SetStatus("Сначала завершите текущую операцию"); return; }
        if (demo) { recycleContents = (0, 0); cleanupResult = "Демо · очистка корзины"; Render(); return; }
        var owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var previous = recycleContents;
        string? outcome = null;
        var ok = await RunWork("Открыта очистка корзины Windows…", async _ =>
            outcome = await Task.Run(() => Recycle.Empty(owner)));
        recycleContents = await Task.Run(Recycle.Count);
        if (ok)
        {
            cleanupResult = outcome ?? $"Корзина очищена · {Bytes(Math.Max(0, previous.Bytes - recycleContents.Bytes))}";
            journal.Add(new(DateTimeOffset.Now, "Корзина Windows", cleanupResult, Math.Max(0, previous.Bytes - recycleContents.Bytes)));
            TrySave(PersistJournal);
            SetStatus(cleanupResult);
        }
        await RefreshVolumes(false);
        Render();
    }

    private async Task SystemCleanup()
    {
        if (demo) { SetStatus("Демо · системная очистка не запускается"); return; }
        if (Selected is not { Ready: true } disk) { SetStatus("Выберите доступный диск в обзоре"); return; }
        if (disk.Root.Length != 3) { Cleanup.OpenWindowsCleanup(); return; }
        if (MessageBox.Show(this, $"Открыть системную очистку для {disk.Title}? Windows запросит права администратора и предложит категории.",
            "Системная очистка", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        if (current is not null || systemCleaning) return;
        systemCleaning = true;
        int status = -1;
        try
        {
            SetStatus("Открыта штатная очистка Windows");
            status = await Cleanup.RunSystemCleanupAsync(disk.Root, demo);
        }
        catch (System.ComponentModel.Win32Exception) { SetStatus("Доступна очистка в настройках Windows"); Cleanup.OpenWindowsCleanup(); return; }
        finally { systemCleaning = false; }
        await RefreshVolumes();
        SetStatus(status < 0 ? "Системная очистка отменена" : "Системная очистка Windows закрыта");
    }

    private bool systemCleaning;

    private void RenderSettings()
    {
        Heading("Настройки", "Фоновый режим, ограничения очистки и внешний вид");
        var monitor = new StackPanel();
        monitor.Children.Add(Text("Наблюдение", 19, bold: true));
        monitor.Children.Add(Toggle("Фоновый анализ в простое", settings.Watch, enabled => Save(settings with { Watch = enabled })));
        monitor.Children.Add(Toggle("Уведомления о нехватке места и очистке", settings.Notify, enabled => Save(settings with { Notify = enabled })));
        monitor.Children.Add(Toggle("Запускать в трее при входе в Windows", StartupEnabled(), SetStartup));
        Page.Children.Add(Card(monitor));

        var rules = new StackPanel();
        rules.Children.Add(Text("Правила очистки", 19, bold: true));
        rules.Children.Add(Toggle("Автоматически удалять подходящие временные файлы", settings.AutoClean, SetAutoClean));
        AddText(rules, $"Минимальный возраст {settings.MinimumAge} дней · лимит за запуск {settings.LimitMb:N0} МБ",
            margin: new Thickness(0, 3, 0, 9));
        var ruleActions = Actions();
        ruleActions.Children.Add(ActionButton("Изменить возраст", () =>
        {
            if (int.TryParse(Prompt("Минимальный возраст, дней", settings.MinimumAge.ToString()), out var age) && age is >= 1 and <= 365)
            { candidates = null; Save(settings with { MinimumAge = age }); }
            else SetStatus("Допустимый возраст — от 1 до 365 дней");
        }));
        ruleActions.Children.Add(ActionButton("Изменить лимит", () =>
        {
            if (int.TryParse(Prompt("Лимит за запуск, МБ", settings.LimitMb.ToString()), out var limit) && limit is >= 1 and <= 10240)
                Save(settings with { LimitMb = limit });
            else SetStatus("Допустимый лимит — от 1 до 10 240 МБ");
        }));
        ruleActions.Children.Add(ActionButton("Исключить папку из очистки", () =>
        {
            if (demo) return;
            var select = new OpenFolderDialog();
            if (select.ShowDialog(this) != true) return;
            candidates = null;
            Save(settings with { Excluded = Exclusions.Append(select.FolderName).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() });
        }));
        rules.Children.Add(ruleActions);
        foreach (var path in Exclusions)
            rules.Children.Add(ActionButton("Убрать исключение · " + path, () =>
                Save(settings with { Excluded = Exclusions.Where(item => !LocalStore.SamePath(item, path)).ToArray() })));
        Page.Children.Add(Card(rules));

        var look = new StackPanel();
        look.Children.Add(Text("Внешний вид", 19, bold: true));
        var palette = Actions();
        foreach (var theme in new[] { "Системная", "Светлая", "Тёмная" })
            palette.Children.Add(ActionButton(theme, () => Save(settings with { Theme = theme }), settings.Theme == theme));
        look.Children.Add(palette);
        look.Children.Add(Toggle("Анимации интерфейса", settings.Animate, enabled => Save(settings with { Animate = enabled })));
        Page.Children.Add(Card(look));

        var files = new StackPanel();
        files.Children.Add(Text("Локальные данные", 19, bold: true));
        AddText(files, "Снимки и журнал хранятся в профиле Windows.", margin: new Thickness(0, 4, 0, 8));
        var data = Actions();
        data.Children.Add(ActionButton("Открыть папку данных", () =>
        { if (!demo) TrySave(() => { Directory.CreateDirectory(store.Location); OpenExplorer(store.Location); }); }));
        data.Children.Add(ActionButton("Удалить историю и журнал…", ClearHistory));
        if (!demo) data.Children.Add(ActionButton("Открыть деморежим", () =>
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { Arguments = "--demo" })));
        files.Children.Add(data);
        Page.Children.Add(Card(files));
    }

    private CheckBox Toggle(string label, bool value, Action<bool> change)
    {
        var box = new CheckBox { Content = label, IsChecked = value, Margin = new Thickness(0, 8, 0, 4) };
        box.Click += (_, _) => change(box.IsChecked == true);
        return box;
    }


    private void SetAutoClean(bool enabled)
    {
        if (enabled && MessageBox.Show(this,
            $"В простое приложение будет удалять подходящие временные файлы и кеш шейдеров.\nВозраст от {settings.MinimumAge} дней. Лимит {settings.LimitMb} МБ за запуск.",
            "Автоочистка", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        { Render(); return; }
        if (enabled && !settings.AutoClean)
        {
            autoReport = CleanupSchedule.Empty;
            TrySave(() => store.Save("auto-cleanup.json", autoReport));
        }
        cleanupRulesChanged = false;
        Save(settings with { AutoClean = enabled, Watch = settings.Watch || enabled, CleanupConsent = enabled ? 2 : 0 });
        SetStatus(enabled ? "Автоочистка включена · сработает после минуты простоя" : "Автоочистка выключена");
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunEntry = "KudaDelasPamyat";

    private bool StartupEnabled()
    {
        if (demo) return false;
        try { using var key = Registry.CurrentUser.OpenSubKey(RunKey); return key?.GetValue(RunEntry) is not null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    private void SetStartup(bool enabled)
    {
        if (demo) { Render(); return; }
        TrySave(() =>
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) key.SetValue(RunEntry, "\"" + Environment.ProcessPath + "\" --tray");
            else key.DeleteValue(RunEntry, false);
        });
        Save(settings with { Watch = settings.Watch || enabled });
    }

    private void ClearHistory()
    {
        if (current is not null || MessageBox.Show(this, "Удалить локальные снимки, результаты анализа и журнал очистки?",
            "Удаление истории", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        history.Clear();
        journal.Clear();
        usage = new(DateTimeOffset.Now, new(StringComparer.OrdinalIgnoreCase));
        autoReport = CleanupSchedule.Empty;
        scans.Clear();
        candidates = null;
        duplicateRows = null;
        TrySave(() =>
        {
            foreach (var name in new[] { "history.json", "cleanup.json", "usage.json", "auto-cleanup.json", "history.json.new", "cleanup.json.new", "usage.json.new" })
                File.Delete(Path.Combine(store.Location, name));
        });
        Render();
    }

    private void SetupDemo()
    {
        const long gb = 1024L * 1024 * 1024;
        settings = new Preferences(Theme: "Тёмная");
        ApplyTheme();
        volumes =
        [
            new("demo-c", @"C:\", "Система", "NTFS", 475 * gb, 89 * gb, true),
            new("demo-d", @"D:\", "Игры и проекты", "NTFS", 931 * gb, 425 * gb, true),
            new("demo-e", @"E:\", "Архив USB", "exFAT", 465 * gb, 178 * gb, true)
        ];
        selectedVolume = volumes[0].Id;
        var now = DateTimeOffset.Now;
        history = [];
        journal = [];
        candidates = DemoCandidates();
        recycleContents = (188L * 1024 * 1024, 7);
        string[][] labels = [["Windows", "Пользователи", "Программы", "Временные файлы"],
            ["Игры", "Записи", "Проекты", "Кеш сборки"], ["Архивы", "Фотографии", "Резервные копии"]];
        foreach (var volume in volumes)
        {
            var names = labels[Array.IndexOf(volumes, volume)];
            int[] weights = [37, 24, 17, 9];
            var folders = names.Select((label, idx) => new FileRow(Path.Combine(volume.Root, label),
                volume.Used / 100 * weights[idx], volume.Used / 100 * weights[idx], DateTime.MinValue, "Папки", true)).ToArray();
            var files = folders.Select((folder, idx) => new FileRow(Path.Combine(folder.Path, idx == 1 ? "Запись.mp4" : "Пакет.zip"),
                gb * (idx + 2), gb * (idx + 2), now.UtcDateTime.AddHours(-idx), idx == 1 ? "Видео" : "Архивы", false)).ToArray();
            scans.Add(volume.Id, new(volume.Root, volume.Used - 12 * gb, 24_860, 2, files, folders, files, false));
            for (int i = 5; i >= 0; i--)
                history.Add(new(volume.Id, volume.Root, now.AddHours(-i * 5), volume.Free + (i * 3 + i % 2) * gb,
                    volume.Capacity, 0, folders.Select((folder, index) => new FolderSize(folder.Path,
                        folder.Bytes - i * (index + 1) * gb)).ToArray(), i == 5 ? "До обновления" : null));
        }
    }

    private static Candidate[] DemoCandidates() =>
    [
        new(@"C:\Demo\Temp\media-index.tmp", 440_401_920, 440_401_000, DateTime.UtcNow.AddDays(-15), "Временный индекс · демо", true),
        new(@"C:\Demo\DirectX\shader.bin", 175_112_192, 175_112_000, DateTime.UtcNow.AddDays(-18), "Кеш шейдеров · демо", true,
            Rule: new(@"C:\Demo\DirectX", CleanupKind.Shader, "Кеш шейдеров DirectX")),
        new(@"C:\Demo\Browser\data_1", 28_311_552, 28_311_000, DateTime.UtcNow.AddDays(-25), "Кеш браузера · демо", true,
            Rule: new(@"C:\Demo\Browser", CleanupKind.Browser, "Кеш браузера"))
    ];
}
