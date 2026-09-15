using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using IFC2RVT.Conversion;

namespace IFC2RVT.UI
{
    /// <summary>
    /// Options dialog, built in code rather than XAML so the add-in stays a single assembly with
    /// no resource-loading surprises when Revit hosts it.
    /// </summary>
    public class ConvertDialog : Window
    {
        readonly ConversionOptions _options;

        readonly TextBox _path = new TextBox { IsReadOnly = true, Margin = new Thickness(0, 0, 6, 0) };
        readonly Dictionary<string, CheckBox> _checks = new Dictionary<string, CheckBox>();
        readonly TextBox _maxElements = new TextBox { Width = 80 };

        public ConversionOptions Result { get; private set; }

        public ConvertDialog(ConversionOptions options)
        {
            _options = options;

            Title = "IFC2RVT — конвертация IFC в нативные элементы";
            Width = 620;
            SizeToContent = SizeToContent.Height;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ResizeMode = ResizeMode.NoResize;

            Content = BuildLayout();
            _path.Text = options.IfcFilePath ?? string.Empty;
        }

        UIElement BuildLayout()
        {
            var root = new StackPanel { Margin = new Thickness(14) };

            root.Children.Add(Header("Файл IFC"));
            root.Children.Add(FileRow());

            root.Children.Add(Header("Создавать нативно"));
            root.Children.Add(Check("walls", "Стены (IfcWall)", _options.ConvertWalls));
            root.Children.Add(Check("slabs", "Перекрытия (IfcSlab)", _options.ConvertSlabs));
            root.Children.Add(Check("columns", "Колонны (IfcColumn)", _options.ConvertColumns));
            root.Children.Add(Check("beams", "Балки и элементы каркаса (IfcBeam, IfcMember)", _options.ConvertBeams));
            root.Children.Add(Check("openings", "Двери и окна (требуют нативных стен)", _options.ConvertOpenings));
            root.Children.Add(Check("spaces", "Помещения (IfcSpace)", _options.ConvertSpaces));
            root.Children.Add(Check("ceilings", "Потолки (IfcCovering)", _options.ConvertCeilings));
            root.Children.Add(Check("grids", "Оси проекта (IfcGrid)", _options.ConvertGrids));
            root.Children.Add(Check("voids", "Вырезать проёмы без заполнения (ниши, отверстия, шахты)", _options.ConvertOpeningVoids));

            root.Children.Add(Header("Остальное"));
            root.Children.Add(Check("fallback", "Всё непреобразованное — в DirectShape", _options.FallbackToDirectShape));
            root.Children.Add(Check("reuse", "Переиспользовать повторяющуюся геометрию (меньше размер файла)", _options.ReuseSharedGeometry));
            root.Children.Add(Check("properties", "Переносить наборы свойств в общие параметры", _options.TransferProperties));
            root.Children.Add(Check("typeProps", "Включая свойства типа (IfcRelDefinesByType)", _options.IncludeTypeProperties));
            root.Children.Add(Check("names", "Подбирать типы по именам Revit из IFC (Семейство:Тип:Id)", _options.UseRevitNameHeuristic));
            root.Children.Add(Check("createTypes", "Создавать недостающие типы дублированием", _options.CreateMissingTypes));
            root.Children.Add(Check("moveToOrigin", "Переносить модель к началу координат", _options.MoveToOrigin));

            root.Children.Add(LimitRow());
            root.Children.Add(Note());
            root.Children.Add(Buttons());

            return new ScrollViewer
            {
                Content = root,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 720
            };
        }

        static TextBlock Header(string text) => new TextBlock
        {
            Text = text,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 12, 0, 6)
        };

        UIElement FileRow()
        {
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var browse = new Button { Content = "Обзор…", Width = 90, Padding = new Thickness(4) };
            browse.Click += (s, e) => Browse();

            Grid.SetColumn(_path, 0);
            Grid.SetColumn(browse, 1);
            grid.Children.Add(_path);
            grid.Children.Add(browse);
            return grid;
        }

        CheckBox Check(string key, string label, bool initial)
        {
            var box = new CheckBox { Content = label, IsChecked = initial, Margin = new Thickness(0, 3, 0, 3) };
            _checks[key] = box;
            return box;
        }

        UIElement LimitRow()
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0) };
            panel.Children.Add(new TextBlock
            {
                Text = "Ограничить числом элементов (0 — без лимита):",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            });
            _maxElements.Text = _options.MaxElements.ToString();
            panel.Children.Add(_maxElements);
            return panel;
        }

        static UIElement Note()
        {
            var gap = Environment.NewLine + Environment.NewLine;

            return new TextBlock
            {
                Text = "Нативно создаётся только то, что имеет однозначную семантику в IFC. " +
                       "Сложная геометрия, лестницы, ограждения и витражи приходят как DirectShape — " +
                       "это ожидаемо, а не сбой." + gap +
                       "Перенос к нулю нужен для моделей, выгруженных в координатах площадки: дальше " +
                       "16 км от нуля Revit перестаёт закрашивать грани и показывает одни рёбра. " +
                       "Величина сдвига записывается в отчёт." + gap +
                       "Перед запуском сохраните проект.",
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.75,
                Margin = new Thickness(0, 14, 0, 4)
            };
        }

        UIElement Buttons()
        {
            var row = new Grid { Margin = new Thickness(0, 16, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // licence
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // author
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });                    // buttons

            var licence = new TextBlock
            {
                Text = "GNU GPLv3",
                VerticalAlignment = VerticalAlignment.Center,
                Opacity = 0.7,
                Margin = new Thickness(0, 0, 14, 0)
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };

            var ok = new Button { Content = "Конвертировать", Width = 140, Padding = new Thickness(6), IsDefault = true };
            var cancel = new Button { Content = "Отмена", Width = 90, Padding = new Thickness(6), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };

            ok.Click += (s, e) => Accept();
            cancel.Click += (s, e) => DialogResult = false;

            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);

            var author = Link("By baidurovlabs.ru", "https://baidurovlabs.ru");

            Grid.SetColumn(licence, 0);
            Grid.SetColumn(author, 1);
            Grid.SetColumn(buttons, 3);

            row.Children.Add(licence);
            row.Children.Add(author);
            row.Children.Add(buttons);
            return row;
        }

        static UIElement Link(string text, string url)
        {
            var hyperlink = new Hyperlink(new Run(text)) { NavigateUri = new Uri(url) };

            hyperlink.RequestNavigate += (s, e) =>
            {
                // UseShellExecute is required on .NET 8; without it Process.Start refuses a URL.
                try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                catch { }
                e.Handled = true;
            };

            var block = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
            block.Inlines.Add(hyperlink);
            return block;
        }

        void Browse()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "IFC (*.ifc;*.ifcxml;*.ifczip)|*.ifc;*.ifcxml;*.ifczip|Все файлы (*.*)|*.*",
                Title = "Выберите IFC"
            };

            if (dialog.ShowDialog() == true) _path.Text = dialog.FileName;
        }

        void Accept()
        {
            if (string.IsNullOrWhiteSpace(_path.Text) || !File.Exists(_path.Text))
            {
                MessageBox.Show(this, "Укажите существующий файл IFC.", "IFC2RVT",
                                MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _options.IfcFilePath = _path.Text;
            _options.ConvertWalls = IsChecked("walls");
            _options.ConvertSlabs = IsChecked("slabs");
            _options.ConvertColumns = IsChecked("columns");
            _options.ConvertBeams = IsChecked("beams");
            _options.ConvertOpenings = IsChecked("openings");
            _options.ConvertSpaces = IsChecked("spaces");
            _options.ConvertCeilings = IsChecked("ceilings");
            _options.ConvertGrids = IsChecked("grids");
            _options.ConvertOpeningVoids = IsChecked("voids");
            _options.ReuseSharedGeometry = IsChecked("reuse");
            _options.FallbackToDirectShape = IsChecked("fallback");
            _options.TransferProperties = IsChecked("properties");
            _options.IncludeTypeProperties = IsChecked("typeProps");
            _options.UseRevitNameHeuristic = IsChecked("names");
            _options.CreateMissingTypes = IsChecked("createTypes");
            _options.MoveToOrigin = IsChecked("moveToOrigin");

            _options.MaxElements = int.TryParse(_maxElements.Text, out var max) && max > 0 ? max : 0;

            Result = _options;
            DialogResult = true;
        }

        bool IsChecked(string key) => _checks.TryGetValue(key, out var box) && box.IsChecked == true;
    }
}
