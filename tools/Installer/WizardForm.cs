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
    /// The installer window: a plain four-step wizard, written by hand rather than generated, so
    /// the whole thing stays one self-contained executable with no toolchain to install before a
    /// release can be built.
    /// </summary>
    internal sealed class WizardForm : Form
    {
        static readonly Color Ink = Color.FromArgb(30, 36, 48);
        static readonly Color Accent = Color.FromArgb(232, 163, 61);
        static readonly Color Muted = Color.FromArgb(110, 118, 132);
        static readonly Color Surface = Color.White;

        readonly bool _uninstallMode;

        readonly Panel _content = new Panel { Dock = DockStyle.Fill, BackColor = Surface, Padding = new Padding(28, 24, 28, 12) };
        readonly Button _back = new Button { Text = "Назад", Width = 96, Height = 30 };
        readonly Button _next = new Button { Text = "Далее", Width = 120, Height = 30 };
        readonly Button _cancel = new Button { Text = "Отмена", Width = 96, Height = 30 };

        readonly CheckBox _accept = new CheckBox { Text = "Принимаю условия GNU GPLv3", AutoSize = true };
        readonly Dictionary<string, CheckBox> _versionBoxes = new Dictionary<string, CheckBox>();
        readonly ProgressBar _progress = new ProgressBar { Height = 18, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 28 };
        readonly ListBox _log = new ListBox { BorderStyle = BorderStyle.FixedSingle, IntegralHeight = false };

        int _page;
        bool _busy;
        bool _succeeded;
        string _failure;

        public WizardForm(bool uninstallMode)
        {
            _uninstallMode = uninstallMode;

            Text = Setup.Product + (uninstallMode ? " — удаление" : " — установка");
            ClientSize = new Size(580, 430);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Surface;
            Font = new Font("Segoe UI", 9F);

            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            Controls.Add(_content);
            Controls.Add(BuildHeader());
            Controls.Add(BuildFooter());

            ShowPage(0);
        }

        // ---- chrome ---------------------------------------------------------------------------

        Control BuildHeader()
        {
            var header = new Panel { Dock = DockStyle.Top, Height = 74, BackColor = Ink };

            var title = new Label
            {
                Text = Setup.Product,
                ForeColor = Color.White,
                Font = new Font("Segoe UI Semibold", 15F),
                AutoSize = true,
                Location = new Point(26, 14),
                BackColor = Color.Transparent
            };

            var subtitle = new Label
            {
                Text = "Конвертер IFC в нативные элементы Revit  ·  версия " + Setup.Version,
                ForeColor = Color.FromArgb(170, 178, 192),
                AutoSize = true,
                Location = new Point(28, 44),
                BackColor = Color.Transparent
            };

            var stripe = new Panel { Dock = DockStyle.Bottom, Height = 3, BackColor = Accent };

            header.Controls.Add(title);
            header.Controls.Add(subtitle);
            header.Controls.Add(stripe);
            return header;
        }

        Control BuildFooter()
        {
            var footer = new Panel { Dock = DockStyle.Bottom, Height = 52, BackColor = Color.FromArgb(246, 247, 249) };
            var line = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = Color.FromArgb(226, 229, 234) };

            _cancel.Location = new Point(ClientSize.Width - 120, 11);
            _next.Location = new Point(ClientSize.Width - 248, 11);
            _back.Location = new Point(ClientSize.Width - 350, 11);

            foreach (var b in new[] { _back, _next, _cancel })
            {
                b.FlatStyle = FlatStyle.System;
                b.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
                footer.Controls.Add(b);
            }

            var licence = new LinkLabel
            {
                Text = "GNU GPLv3 · исходный код",
                AutoSize = true,
                Location = new Point(22, 18),
                LinkColor = Muted,
                ActiveLinkColor = Accent,
                LinkBehavior = LinkBehavior.HoverUnderline
            };
            licence.LinkClicked += (s, e) => Setup.OpenUrl(Setup.SourceUrl);
            footer.Controls.Add(licence);

            _back.Click += (s, e) => ShowPage(_page - 1);
            _next.Click += (s, e) => Advance();
            _cancel.Click += (s, e) => Close();

            footer.Controls.Add(line);
            return footer;
        }

        // ---- pages ----------------------------------------------------------------------------

        void ShowPage(int page)
        {
            _page = Math.Max(0, page);
            _content.Controls.Clear();
            _content.SuspendLayout();

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

            _content.ResumeLayout();
            UpdateButtons();
        }

        void PageWelcome()
        {
            Heading("Установка IFC2RVT");

            Body("Штатный импорт IFC в Revit создаёт DirectShape — геометрию без параметров, " +
                 "которую нельзя редактировать и нельзя посчитать в спецификациях.\r\n\r\n" +
                 "IFC2RVT читает IFC напрямую и создаёт стены, перекрытия, колонны, двери, окна " +
                 "и помещения как нативные элементы, перенося свойства в общие параметры.", 78, 88);

            var licence = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Location = new Point(28, 176),
                Size = new Size(ClientSize.Width - 56, 92),
                BackColor = Color.FromArgb(250, 250, 251),
                BorderStyle = BorderStyle.FixedSingle,
                Text = "Загрузка текста лицензии..."
            };
            _content.Controls.Add(licence);

            // Reading the licence unzips the payload; keep that off the UI thread.
            Task.Run(() => Setup.ReadLicence()).ContinueWith(t =>
            {
                if (IsDisposed || licence.IsDisposed) return;
                licence.Invoke(new Action(() => licence.Text = t.Result.Replace("\n", "\r\n")));
            });

            _accept.Location = new Point(28, 278);
            _accept.CheckedChanged += (s, e) => UpdateButtons();
            _content.Controls.Add(_accept);

            var note = new Label
            {
                Text = "Программа распространяется без каких-либо гарантий. Права администратора не нужны:\r\n" +
                       "всё устанавливается в профиль текущего пользователя.",
                ForeColor = Muted,
                AutoSize = false,
                Location = new Point(28, 300),
                Size = new Size(ClientSize.Width - 56, 34)
            };
            _content.Controls.Add(note);
        }

        void PageVersions()
        {
            Heading("Версии Revit");

            var detected = Setup.DetectRevit();
            var installed = Setup.InstalledVersions();

            Body(detected.Count > 0
                    ? "Отмечены версии, найденные на этом компьютере."
                    : "Revit на этом компьютере не найден. Можно выбрать версию вручную — "
                      + "надстройка установится и заработает, когда Revit появится.",
                 78, 36);

            var y = 122;
            foreach (var version in Setup.BuildFor.Keys.OrderBy(v => v))
            {
                var isInstalled = installed.Contains(version);
                var box = new CheckBox
                {
                    Text = "Revit " + version
                           + (detected.Contains(version) ? "   — найден" : string.Empty)
                           + (isInstalled ? "   · уже установлено" : string.Empty),
                    Checked = detected.Contains(version),
                    AutoSize = true,
                    Location = new Point(34, y),
                    ForeColor = detected.Contains(version) ? Ink : Muted
                };
                box.CheckedChanged += (s, e) => UpdateButtons();

                _versionBoxes[version] = box;
                _content.Controls.Add(box);
                y += 28;
            }

            var note = new Label
            {
                Text = "Две сборки закрывают весь диапазон: .NET Framework 4.8 для Revit 2022–2024\r\n"
                     + "и .NET 8 для Revit 2025–2026.",
                ForeColor = Muted,
                AutoSize = false,
                Location = new Point(28, y + 12),
                Size = new Size(ClientSize.Width - 56, 40)
            };
            _content.Controls.Add(note);
        }

        void PageConfirmRemoval()
        {
            Heading("Удаление IFC2RVT");

            var installed = Setup.InstalledVersions();

            Body(installed.Count > 0
                    ? "Надстройка будет удалена для версий: " + string.Join(", ", installed) + "."
                    : "Установленных версий не найдено.",
                 78, 40);

            var note = new Label
            {
                Text = "Что останется нетронутым:\r\n\r\n"
                     + "   ·  элементы, уже созданные в ваших проектах\r\n"
                     + "   ·  общие параметры и файл общих параметров\r\n"
                     + "   ·  отчёты о конвертации\r\n\r\n"
                     + "Удаляются только файлы самой надстройки.",
                ForeColor = Ink,
                AutoSize = false,
                Location = new Point(34, 132),
                Size = new Size(ClientSize.Width - 68, 150)
            };
            _content.Controls.Add(note);
        }

        void PageProgress()
        {
            Heading(_uninstallMode ? "Удаление" : "Установка");

            _progress.Location = new Point(28, 86);
            _progress.Width = ClientSize.Width - 56;
            _content.Controls.Add(_progress);

            _log.Location = new Point(28, 118);
            _log.Size = new Size(ClientSize.Width - 56, 200);
            _content.Controls.Add(_log);

            StartWork();
        }

        void PageFinish()
        {
            Heading(_succeeded
                ? (_uninstallMode ? "Удаление завершено" : "Установка завершена")
                : "Не удалось завершить");

            if (_succeeded)
            {
                Body(_uninstallMode
                        ? "Надстройка удалена. Элементы, созданные в проектах, остались на месте."
                        : "Перезапустите Revit. Надстройка появится на вкладке IFC2RVT.",
                     78, 44);

                if (!_uninstallMode)
                {
                    var hint = new Label
                    {
                        Text = "Вкладка  IFC2RVT  →  Конвертация  →  «IFC → нативные»\r\n\r\n"
                             + "Первый прогон делайте на копии проекта: в диалоге есть ограничение\r\n"
                             + "по числу элементов — поставьте 50, чтобы посмотреть результат\r\n"
                             + "до полного прохода.",
                        ForeColor = Ink,
                        AutoSize = false,
                        Location = new Point(34, 136),
                        Size = new Size(ClientSize.Width - 68, 110)
                    };
                    _content.Controls.Add(hint);

                    var link = new LinkLabel
                    {
                        Text = "Документация и исходный код на GitHub",
                        AutoSize = true,
                        Location = new Point(34, 252),
                        LinkColor = Color.FromArgb(180, 120, 30),
                        ActiveLinkColor = Accent
                    };
                    link.LinkClicked += (s, e) => Setup.OpenUrl(Setup.SourceUrl);
                    _content.Controls.Add(link);
                }
            }
            else
            {
                Body(_failure ?? "Неизвестная ошибка.", 78, 120);
            }
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

        void Heading(string text)
        {
            _content.Controls.Add(new Label
            {
                Text = text,
                Font = new Font("Segoe UI Semibold", 13F),
                ForeColor = Ink,
                AutoSize = true,
                Location = new Point(26, 24)
            });
        }

        void Body(string text, int top, int height)
        {
            _content.Controls.Add(new Label
            {
                Text = text,
                ForeColor = Color.FromArgb(62, 70, 84),
                AutoSize = false,
                Location = new Point(28, top),
                Size = new Size(ClientSize.Width - 56, height)
            });
        }

        void UpdateButtons()
        {
            var lastPage = _uninstallMode ? 2 : 3;
            var progressPage = _uninstallMode ? 1 : 2;

            _back.Visible = _page > 0 && _page < progressPage;
            _back.Enabled = !_busy;

            _cancel.Enabled = !_busy;
            _cancel.Text = _page >= lastPage ? "Закрыть" : "Отмена";
            _cancel.Visible = true;

            if (_page >= lastPage)
            {
                _next.Visible = false;
                return;
            }

            _next.Visible = true;
            _next.Text = _page == progressPage ? "Далее" : (_uninstallMode ? "Удалить" : "Далее");

            if (_page == 0 && !_uninstallMode) _next.Enabled = _accept.Checked;
            else if (_page == 1 && !_uninstallMode) _next.Enabled = _versionBoxes.Values.Any(b => b.Checked);
            else if (_page == progressPage) _next.Enabled = !_busy;
            else _next.Enabled = !_busy;
        }

        void Advance()
        {
            var progressPage = _uninstallMode ? 1 : 2;

            // Leaving the progress page early would abandon a running copy mid-flight.
            if (_page == progressPage && _busy) return;

            ShowPage(_page + 1);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_busy)
            {
                e.Cancel = true;
                return;
            }
            base.OnFormClosing(e);
        }
    }
}
