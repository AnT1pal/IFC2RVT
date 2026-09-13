using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace IFC2RVT.Setup
{
    /// <summary>
    /// The installer window: a plain wizard, written by hand rather than generated, so the whole
    /// thing stays one self-contained executable with no toolchain to install before a release
    /// can be built.
    ///
    /// Everything is laid out with docking and auto-size, never with pixel coordinates. Hard
    /// positions look fine on the machine they were written on and fall apart at 125% display
    /// scaling, where the fonts grow but the coordinates do not - buttons slide off the bottom
    /// edge and text is clipped mid-sentence.
    /// </summary>
    internal sealed class WizardForm : Form
    {
        static readonly Color Ink = Color.FromArgb(30, 36, 48);
        static readonly Color Accent = Color.FromArgb(232, 163, 61);
        static readonly Color Muted = Color.FromArgb(110, 118, 132);
        static readonly Color Body = Color.FromArgb(62, 70, 84);

        readonly bool _uninstallMode;

        readonly Panel _content = new Panel { Dock = DockStyle.Fill, BackColor = Color.White, Padding = new Padding(22, 18, 22, 10) };
        readonly Button _back = new Button { Text = "Назад", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        readonly Button _next = new Button { Text = "Далее", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };
        readonly Button _cancel = new Button { Text = "Отмена", AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink };

        readonly CheckBox _accept = new CheckBox { Text = "Принимаю условия GNU GPLv3", AutoSize = true, Margin = new Padding(0, 10, 0, 4) };
        readonly Dictionary<string, CheckBox> _versionBoxes = new Dictionary<string, CheckBox>();
        readonly ProgressBar _progress = new ProgressBar { Dock = DockStyle.Top, Height = 16, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 28, Margin = new Padding(0, 0, 0, 10) };
        readonly ListBox _log = new ListBox { Dock = DockStyle.Fill, BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };

        int _page;
        bool _busy;
        bool _succeeded;
        string _failure;

        public WizardForm(bool uninstallMode)
        {
            _uninstallMode = uninstallMode;

            Text = Setup.Product + (uninstallMode ? " — удаление" : " — установка");
            Font = new Font("Segoe UI", 9F);

            // Font-based scaling makes the whole window grow with the display scale instead of
            // leaving the layout at 96 dpi while the text alone gets bigger.
            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = new SizeF(96F, 96F);

            ClientSize = new Size(620, 500);
            MinimumSize = new Size(600, 480);
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.White;

            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            // Added in reverse: docking stacks the most recently added control closest to the edge.
            Controls.Add(_content);
            Controls.Add(BuildFooter());
            Controls.Add(BuildHeader());

            ShowPage(0);
        }

        // ---- chrome ---------------------------------------------------------------------------

        Control BuildHeader()
        {
            var header = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Ink,
                ColumnCount = 1,
                Padding = new Padding(22, 12, 22, 12)
            };

            header.Controls.Add(new Label
            {
                Text = Setup.Product,
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 15F),
                AutoSize = true,
                Margin = new Padding(0)
            });

            header.Controls.Add(new Label
            {
                Text = "Конвертер IFC в нативные элементы Revit  ·  версия " + Setup.Version,
                ForeColor = Color.FromArgb(170, 178, 192),
                AutoSize = true,
                Margin = new Padding(2, 2, 0, 0)
            });

            var wrapper = new Panel { Dock = DockStyle.Top, AutoSize = true };
            var stripe = new Panel { Dock = DockStyle.Top, Height = 3, BackColor = Accent };

            wrapper.Controls.Add(stripe);
            wrapper.Controls.Add(header);
            return wrapper;
        }

        Control BuildFooter()
        {
            var footer = new Panel
            {
                Dock = DockStyle.Bottom,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                BackColor = Color.FromArgb(246, 247, 249),
                Padding = new Padding(0, 1, 0, 0)
            };

            var row = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 2,
                BackColor = Color.FromArgb(246, 247, 249),
                Padding = new Padding(18, 10, 14, 10)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            var licence = new LinkLabel
            {
                Text = "GNU GPLv3  ·  исходный код",
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                LinkColor = Muted,
                ActiveLinkColor = Accent,
                LinkBehavior = LinkBehavior.HoverUnderline,
                Margin = new Padding(0, 6, 0, 0)
            };
            licence.LinkClicked += (s, e) => Setup.OpenUrl(Setup.SourceUrl);

            var buttons = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlowDirection = FlowDirection.LeftToRight,
                Margin = new Padding(0),
                WrapContents = false
            };

            foreach (var b in new[] { _back, _next, _cancel })
            {
                b.FlatStyle = FlatStyle.System;
                b.MinimumSize = new Size(94, 28);
                b.Padding = new Padding(8, 3, 8, 3);
                b.Margin = new Padding(6, 0, 0, 0);
                buttons.Controls.Add(b);
            }

            _back.Click += (s, e) => ShowPage(_page - 1);
            _next.Click += (s, e) => Advance();
            _cancel.Click += (s, e) => Close();

            row.Controls.Add(licence, 0, 0);
            row.Controls.Add(buttons, 1, 0);

            footer.Controls.Add(row);
            footer.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(226, 229, 234) });
            return footer;
        }

        /// <summary>One column, rows sized to content, with a single row allowed to take the slack.</summary>
        static TableLayoutPanel Stack()
            => new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                AutoSize = false,
                BackColor = Color.White
            };

        // ---- pages ----------------------------------------------------------------------------

        void ShowPage(int page)
        {
            _page = Math.Max(0, page);

            _content.SuspendLayout();
            _content.Controls.Clear();

            if (_uninstallMode)
            {
                switch (_page)
                {
                    case 0: PageConfirmRemoval(); break;
                    case 1: PageProgress(); break;
                    default: PageFinish(); break;
                }
            }
            else
            {
                switch (_page)
                {
                    case 0: PageWelcome(); break;
                    case 1: PageVersions(); break;
                    case 2: PageProgress(); break;
                    default: PageFinish(); break;
                }
            }

            _content.ResumeLayout(true);
            UpdateButtons();
        }

        void PageWelcome()
        {
            var stack = Stack();
            stack.RowCount = 5;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // heading
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // intro
            stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); // licence
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // accept
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));   // note

            stack.Controls.Add(Heading("Установка IFC2RVT"), 0, 0);
            stack.Controls.Add(Paragraph(
                "Штатный импорт IFC создаёт DirectShape — геометрию без параметров. IFC2RVT читает "
              + "IFC напрямую и создаёт стены, перекрытия, колонны, двери, окна и помещения как "
              + "нативные элементы, перенося свойства в общие параметры."), 0, 1);

            var licence = new TextBox
            {
                Dock = DockStyle.Fill,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                BackColor = Color.FromArgb(250, 250, 251),
                BorderStyle = BorderStyle.FixedSingle,
                Text = "Загрузка текста лицензии...",
                Margin = new Padding(0, 6, 0, 0)
            };
            stack.Controls.Add(licence, 0, 2);

            // Unzipping the payload to read LICENSE must not block the window from appearing.
            Task.Run(() => Setup.ReadLicence()).ContinueWith(t =>
            {
                if (IsDisposed || licence.IsDisposed) return;
                licence.BeginInvoke(new Action(() =>
                {
                    licence.Text = t.Result.Replace("\r\n", "\n").Replace("\n", "\r\n");
                    // Setting Text leaves the caret at the end, which shows the middle of the
                    // licence; a licence box has to open at the first line.
                    licence.SelectionStart = 0;
                    licence.SelectionLength = 0;
                    licence.ScrollToCaret();
                }));
            });

            _accept.CheckedChanged -= OnAcceptChanged;
            _accept.CheckedChanged += OnAcceptChanged;
            stack.Controls.Add(_accept, 0, 3);

            stack.Controls.Add(Note(
                "Программа распространяется без каких-либо гарантий. Права администратора не нужны: "
              + "всё устанавливается в профиль текущего пользователя."), 0, 4);

            _content.Controls.Add(stack);
        }

        void OnAcceptChanged(object sender, EventArgs e) => UpdateButtons();

        void PageVersions()
        {
            var detected = Setup.DetectRevit();
            var installed = Setup.InstalledVersions();

            var stack = Stack();
            stack.RowCount = 4;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            stack.Controls.Add(Heading("Версии Revit"), 0, 0);
            stack.Controls.Add(Paragraph(detected.Count > 0
                ? "Отмечены версии, найденные на этом компьютере."
                : "Revit на этом компьютере не найден. Можно выбрать версию вручную — надстройка "
                  + "установится и заработает, когда Revit появится."), 0, 1);

            var list = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                AutoScroll = true,
                Margin = new Padding(6, 8, 0, 0)
            };

            _versionBoxes.Clear();
            foreach (var version in Setup.BuildFor.Keys.OrderBy(v => v))
            {
                var found = detected.Contains(version);
                var box = new CheckBox
                {
                    Text = "Revit " + version
                           + (found ? "   — найден" : string.Empty)
                           + (installed.Contains(version) ? "   ·  уже установлено" : string.Empty),
                    Checked = found,
                    AutoSize = true,
                    ForeColor = found ? Ink : Muted,
                    Margin = new Padding(0, 4, 0, 4)
                };
                box.CheckedChanged += (s, e) => UpdateButtons();

                _versionBoxes[version] = box;
                list.Controls.Add(box);
            }

            stack.Controls.Add(list, 0, 2);
            stack.Controls.Add(Note(
                "Две сборки закрывают весь диапазон: .NET Framework 4.8 для Revit 2022–2024 "
              + "и .NET 8 для Revit 2025–2026."), 0, 3);

            _content.Controls.Add(stack);
        }

        void PageConfirmRemoval()
        {
            var installed = Setup.InstalledVersions();

            var stack = Stack();
            stack.RowCount = 3;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            stack.Controls.Add(Heading("Удаление IFC2RVT"), 0, 0);
            stack.Controls.Add(Paragraph(installed.Count > 0
                ? "Надстройка будет удалена для версий: " + string.Join(", ", installed) + "."
                : "Установленных версий не найдено."), 0, 1);

            stack.Controls.Add(new Label
            {
                Text = "Что останется нетронутым:\r\n\r\n"
                     + "     ·  элементы, уже созданные в ваших проектах\r\n"
                     + "     ·  общие параметры и файл общих параметров\r\n"
                     + "     ·  отчёты о конвертации\r\n\r\n"
                     + "Удаляются только файлы самой надстройки.",
                ForeColor = Ink,
                Dock = DockStyle.Fill,
                Margin = new Padding(6, 12, 0, 0)
            }, 0, 2);

            _content.Controls.Add(stack);
        }

        void PageProgress()
        {
            var stack = Stack();
            stack.RowCount = 3;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            stack.Controls.Add(Heading(_uninstallMode ? "Удаление" : "Установка"), 0, 0);

            _progress.Style = ProgressBarStyle.Marquee;
            _progress.Dock = DockStyle.Fill;
            stack.Controls.Add(_progress, 0, 1);

            _log.Items.Clear();
            stack.Controls.Add(_log, 0, 2);

            _content.Controls.Add(stack);
            StartWork();
        }

        void PageFinish()
        {
            var stack = Stack();
            stack.RowCount = 3;
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            stack.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            stack.Controls.Add(Heading(_succeeded
                ? (_uninstallMode ? "Удаление завершено" : "Установка завершена")
                : "Не удалось завершить"), 0, 0);

            if (!_succeeded)
            {
                stack.Controls.Add(Paragraph(_failure ?? "Неизвестная ошибка."), 0, 1);
                _content.Controls.Add(stack);
                return;
            }

            stack.Controls.Add(Paragraph(_uninstallMode
                ? "Надстройка удалена. Элементы, созданные в проектах, остались на месте."
                : "Перезапустите Revit — надстройка появится на вкладке IFC2RVT."), 0, 1);

            if (_uninstallMode)
            {
                _content.Controls.Add(stack);
                return;
            }

            var details = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Margin = new Padding(6, 10, 0, 0)
            };

            details.Controls.Add(new Label
            {
                Text = "Вкладка  IFC2RVT  →  Конвертация  →  «IFC → нативные»\r\n\r\n"
                     + "Первый прогон делайте на копии проекта: в диалоге есть ограничение\r\n"
                     + "по числу элементов — поставьте 50, чтобы посмотреть результат\r\n"
                     + "до полного прохода.",
                ForeColor = Ink,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 14)
            });

            var link = new LinkLabel
            {
                Text = "Документация и исходный код на GitHub",
                AutoSize = true,
                LinkColor = Color.FromArgb(180, 120, 30),
                ActiveLinkColor = Accent
            };
            link.LinkClicked += (s, e) => Setup.OpenUrl(Setup.SourceUrl);
            details.Controls.Add(link);

            stack.Controls.Add(details, 0, 2);
            _content.Controls.Add(stack);
        }

        // ---- work -----------------------------------------------------------------------------

        void StartWork()
        {
            _busy = true;
            _succeeded = false;
            _failure = null;
            UpdateButtons();

            var versions = _uninstallMode
                ? null
                : _versionBoxes.Where(kv => kv.Value.Checked).Select(kv => kv.Key).ToList();

            var context = SynchronizationContext.Current;
            void Report(string line) => context?.Post(_ =>
            {
                if (_log.IsDisposed) return;
                _log.Items.Add(line);
                _log.TopIndex = _log.Items.Count - 1;
            }, null);

            Task.Run(() =>
            {
                try
                {
                    var count = _uninstallMode
                        ? Setup.Uninstall(null, Report)
                        : Setup.Install(versions, Report);

                    Report(_uninstallMode
                        ? (count == 0 ? "Установленных версий не найдено." : $"Готово: удалено для {count} версий.")
                        : $"Готово: установлено для {count} версий.");

                    _succeeded = true;
                }
                catch (Exception ex)
                {
                    _failure = ex.Message;
                    Report("Ошибка: " + ex.Message);
                }
            }).ContinueWith(_ => context?.Post(__ =>
            {
                if (IsDisposed) return;
                _busy = false;
                _progress.Style = ProgressBarStyle.Continuous;
                _progress.Value = _progress.Maximum;
                UpdateButtons();
            }, null));
        }

        // ---- helpers --------------------------------------------------------------------------

        static Label Heading(string text) => new Label
        {
            Text = text,
            Font = new Font("Segoe UI Semibold", 13F),
            ForeColor = Ink,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8)
        };

        Label Paragraph(string text) => new Label
        {
            Text = text,
            ForeColor = Body,
            AutoSize = true,
            MaximumSize = new Size(_content.ClientSize.Width - 50, 0),
            Margin = new Padding(0, 0, 0, 4)
        };

        Label Note(string text) => new Label
        {
            Text = text,
            ForeColor = Muted,
            AutoSize = true,
            MaximumSize = new Size(_content.ClientSize.Width - 50, 0),
            Margin = new Padding(0, 10, 0, 0)
        };

        void UpdateButtons()
        {
            var progressPage = _uninstallMode ? 1 : 2;
            var lastPage = progressPage + 1;

            _back.Visible = _page > 0 && _page < progressPage;
            _back.Enabled = !_busy;

            _cancel.Enabled = !_busy;
            _cancel.Text = _page >= lastPage ? "Закрыть" : "Отмена";

            if (_page >= lastPage)
            {
                _next.Visible = false;
                return;
            }

            _next.Visible = true;
            _next.Text = _page == progressPage
                ? "Далее"
                : (_page == progressPage - 1 ? (_uninstallMode ? "Удалить" : "Установить") : "Далее");

            if (_page == 0 && !_uninstallMode) _next.Enabled = _accept.Checked;
            else if (_page == 1 && !_uninstallMode) _next.Enabled = _versionBoxes.Values.Any(b => b.Checked);
            else _next.Enabled = !_busy;
        }

        void Advance()
        {
            var progressPage = _uninstallMode ? 1 : 2;
            if (_page == progressPage && _busy) return;
            ShowPage(_page + 1);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy) { e.Cancel = true; return; }
            base.OnFormClosing(e);
        }
    }
}
