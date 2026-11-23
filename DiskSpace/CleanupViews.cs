using System.Windows.Controls;
using System.Windows.Data;
using Microsoft.Win32;

namespace DiskSpace;

public partial class MainWindow
{
    private async Task<bool> FindCleanup(bool automatic = false)
    {
        var ok = await RunWork("Ищем временные файлы…", async token =>
        {
            if (demo) { candidates = DemoCandidates(); return; }
            candidates = await Task.Run(() => Cleanup.Find(settings.MinimumAge, Exclusions, token), token);
        });
        if (ok && !automatic) SetStatus($"Кандидатов: {candidates?.Length ?? 0:N0}");
        if (active == "Очистка" || active == "Обзор") Render();
        return ok;
    }

    private async Task ExecuteCleanup(Candidate[] plan, bool automatic = false)
    {
        if (plan.Length == 0) { SetStatus("Выберите временные файлы"); return; }
        if (!automatic && MessageBox.Show(this,
            $"Удалить {plan.Length:N0} временных файлов ({Bytes(plan.Sum(item => item.Bytes))}) безвозвратно?",
            "Подтвердите очистку", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;

        long removed = 0;
        var before = volumes.Where(volume => volume.Ready).ToDictionary(volume => volume.Id, volume => volume.Free);
        var limit = (long)settings.LimitMb * 1024 * 1024;
        var days = settings.MinimumAge;
        var exclusions = Exclusions;
        var complete = await RunWork("Выполняем очистку…", async token =>
        {
            foreach (var item in plan)
            {
                token.ThrowIfCancellationRequested();
                if (automatic && (!settings.Watch || !settings.AutoClean || !Idle()
                    || System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus != System.Windows.Forms.PowerLineStatus.Online)) break;
                if (automatic && item.Bytes > limit - removed) continue;
                var pending = new CleanupLog(DateTimeOffset.Now, item.Path, "Начато · проверьте состояние файла", 0);
                journal.Add(pending);
                if (!demo)
                {
                    try { PersistJournal(); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                    {
                        journal.Remove(pending);
                        throw new IOException("Очистка остановлена. Не удалось записать журнал.", error);
                    }
                }
                var result = await Task.Run(() => Cleanup.Remove(item, days, exclusions), CancellationToken.None);
                journal.Remove(pending);
                journal.Add(result);
                if (!demo) PersistJournal();
                removed += result.Bytes;
            }
        });
        if (complete)
        {
            await RefreshVolumes(false);
            var measured = volumes.Where(volume => volume.Ready && before.ContainsKey(volume.Id))
                .Sum(volume => volume.Free - before[volume.Id]);
            SetStatus($"Обработано {Bytes(removed)} · изменение свободного места {Diff(measured)}");
        }
        candidates = null;
        if (active == "Очистка" || active == "Обзор") Render();
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

    private void RenderCleanup()
    {
        Heading("Очистка", "Предпросмотр временных файлов и системные инструменты Windows");
        var actions = Actions();
        actions.Children.Add(ActionButton("Найти, что можно очистить", async () => await FindCleanup(), true));
        actions.Children.Add(ActionButton("Память Windows", () => { if (!demo) Cleanup.OpenWindowsCleanup(); }));
        actions.Children.Add(ActionButton("Системная очистка", async () => await SystemCleanup()));
        Page.Children.Add(actions);
        var info = new StackPanel();
        info.Children.Add(Text(candidates is null ? "План очистки" : Bytes(candidates.Sum(candidate => candidate.Bytes)) + " к просмотру", 21, bold: true));
        AddText(info, candidates is null
            ? $"Проверяем обычные .tmp в папке Temp текущего пользователя, которым больше {settings.MinimumAge} дней."
            : $"{candidates.Length:N0} кандидатов. Выберите файлы для окончательного удаления.",
            margin: new Thickness(0, 4, 0, 0));
        Page.Children.Add(Card(info));
        if (candidates is { Length: > 0 } plan)
        {
            var rows = plan.Select(item => new CleanupRow(item)).ToArray();
            var table = Table(rows, ("Файл", "Name", 210), ("На диске", "Size", 100), ("Изменён", "Date", 90),
                ("Причина", "Reason", 300), ("Путь", "Path", 320));
            table.IsReadOnly = false;
            foreach (var column in table.Columns) column.IsReadOnly = true;
            table.Columns.Insert(0, new DataGridCheckBoxColumn { Header = "Выбор", Width = 68,
                Binding = new Binding(nameof(CleanupRow.Selected)) { Mode = BindingMode.TwoWay, UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged } });
            Page.Children.Add(table);
            var planActions = Actions();
            planActions.Children.Add(ActionButton("Удалить выбранные…", async () =>
            {
                table.CommitEdit(DataGridEditingUnit.Cell, true);
                table.CommitEdit(DataGridEditingUnit.Row, true);
                await ExecuteCleanup(plan.Where(item => item.Selected).ToArray());
            }, true));
            planActions.Children.Add(ActionButton("Снять все отметки", () => { foreach (var item in plan) item.Selected = false; Render(); }));
            Page.Children.Add(planActions);
        }
        Page.Children.Add(Text("Журнал очистки", 19, bold: true));
        if (journal.Count == 0) AddText(Page, "Выполненных операций пока нет.", margin: new Thickness(0, 5, 0, 0));
        else Page.Children.Add(Table(journal.AsEnumerable().Reverse().Take(200).Select(item => new LogRow(item)).ToArray(),
            ("Время", "Date", 110), ("Результат", "Status", 265), ("Объём", "Size", 100), ("Путь", "Path", 450)));
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
            if (int.TryParse(Prompt("Минимальный возраст, дней", settings.MinimumAge.ToString()), out var age) && age is >= 7 and <= 365)
            { candidates = null; Save(settings with { MinimumAge = age }); }
            else SetStatus("Допустимый возраст — от 7 до 365 дней");
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
        if (enabled && candidates is null) { SetStatus("Сначала запустите поиск кандидатов в разделе «Очистка»"); Render(); return; }
        if (enabled && MessageBox.Show(this,
            $"Разрешить автоматическое удаление подходящих .tmp в простое? Минимальный возраст {settings.MinimumAge} дней, лимит {settings.LimitMb} МБ за запуск.",
            "Автоочистка", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
        { Render(); return; }
        Save(settings with { AutoClean = enabled, Watch = settings.Watch || enabled });
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
        scans.Clear();
        candidates = null;
        duplicateRows = null;
        TrySave(() =>
        {
            foreach (var name in new[] { "history.json", "cleanup.json", "history.json.new", "cleanup.json.new" })
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
        new(@"C:\Demo\Temp\installer.tmp", 175_112_192, 175_112_000, DateTime.UtcNow.AddDays(-18), "След установки · демо", true),
        new(@"C:\Demo\Temp\render.tmp", 28_311_552, 28_311_000, DateTime.UtcNow.AddDays(-25), "Временный рендер · демо", true)
    ];
}
