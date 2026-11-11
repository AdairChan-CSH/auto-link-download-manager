using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace LinkDownloader
{
    public partial class MainForm : Form
    {
        private const int LinkCheckColumn = 0;
        private const int LinkNameColumn = 1;
        private const int LinkHostColumn = 2;
        private const int LinkTypeColumn = 3;
        private const int LinkUrlColumn = 4;

        private const int QueueFileColumn = 0;
        private const int QueueSizeColumn = 1;
        private const int QueueProgressColumn = 2;
        private const int QueueSpeedColumn = 3;
        private const int QueueEtaColumn = 4;
        private const int QueueStatusColumn = 5;
        private const int QueueUrlColumn = 6;

        private readonly AppSettings _settings;
        private readonly DownloadManager _manager;
        private readonly List<LinkItem> _links = new List<LinkItem>();
        private readonly HashSet<string> _checked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, RowStat> _rowStats = new Dictionary<string, RowStat>();

        private TextBox _urlBox;
        private TextBox _pasteBox;
        private TextBox _baseBox;
        private TextBox _filterBox;
        private TextBox _outBox;
        private ComboBox _typeBox;
        private CheckBox _sameHostBox;
        private CheckBox _resumeBox;
        private CheckBox _startNowBox;
        private FlatButton _fetchButton;
        private DarkGrid _linkGrid;
        private DarkGrid _queueGrid;
        private Label _linkCount;
        private Label _statusLabel;
        private Label _queueLeft;
        private Label _queueRight;
        private ProgressBarEx _overall;
        private NumericUpDown _parallelBox;
        private NumericUpDown _segmentsBox;
        private SplitContainer _split;
        private System.Windows.Forms.Timer _timer;
        private ContextMenuStrip _queueMenu;
        private ToolStripMenuItem _menuPause;
        private ToolStripMenuItem _menuResume;
        private ToolStripMenuItem _menuSaveAs;
        private ToolStripMenuItem _menuOpen;
        private ToolStripMenuItem _menuFolder;
        private ToolStripMenuItem _menuCopyUrl;
        private ToolStripMenuItem _menuRemove;

        private string _pageReferer = "";
        private bool _syncingLinks;
        private bool _allowClose;
        private bool _shutdownStarted;
        private bool _shown;

        public MainForm()
            : this(AppSettings.Load())
        {
        }

        public MainForm(AppSettings settings)
        {
            _settings = settings;
            _settings.Normalize();
            _manager = new DownloadManager(_settings);

            BuildUi();
            WireEvents();

            _manager.Log += ManagerLog;

            _outBox.Text = _settings.OutputFolder;
            _parallelBox.Value = _settings.ParallelFiles;
            _segmentsBox.Value = _settings.SegmentsPerFile;
            _resumeBox.Checked = _settings.AutoResume;
            _startNowBox.Checked = true;

            int loaded = _manager.LoadExisting();
            if (loaded > 0)
            {
                SetStatus(loaded + " unfinished or completed download(s) loaded from disk.");
            }
            else
            {
                SetStatus("Ready. Paste links or fetch a page to begin.");
            }

            _timer = new System.Windows.Forms.Timer();
            _timer.Interval = 500;
            _timer.Tick += TimerTick;
            _timer.Start();

            ApplyLinkFilter();
            UpdateLinkCount();
            RefreshQueue();
        }

        // ------------------------------------------------------------ UI build

        private void BuildUi()
        {
            SuspendLayout();

            Text = "All Links Downloader";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1240, 820);
            MinimumSize = new Size(1080, 700);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f);
            AutoScaleMode = AutoScaleMode.Font;
            DoubleBuffered = true;
            KeyPreview = true;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.ColumnCount = 1;
            root.RowCount = 3;
            root.BackColor = Theme.Bg;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            root.Padding = new Padding(12, 6, 12, 0);

            root.Controls.Add(BuildHeader(), 0, 0);

            _split = new SplitContainer();
            _split.Dock = DockStyle.Fill;
            _split.Orientation = Orientation.Vertical;
            _split.SplitterWidth = 8;
            _split.BackColor = Theme.Bg;
            _split.Panel1.BackColor = Theme.Bg;
            _split.Panel2.BackColor = Theme.Bg;
            _split.Panel1.Padding = new Padding(0, 0, 5, 0);
            _split.Panel2.Padding = new Padding(5, 0, 0, 0);
            _split.Panel1.Controls.Add(BuildLinksPanel());
            _split.Panel2.Controls.Add(BuildQueuePanel());
            root.Controls.Add(_split, 0, 1);

            root.Controls.Add(BuildStatusBar(), 0, 2);
            Controls.Add(root);

            ResumeLayout(true);
        }

        private Control BuildHeader()
        {
            Panel header = new Panel();
            header.Dock = DockStyle.Fill;
            header.BackColor = Theme.Bg;
            header.Paint += delegate (object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Theme.Border))
                {
                    e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
                }
            };

            Label title = Theme.Label("All Links Downloader", 17f, true, Theme.Text);
            title.Location = new Point(0, 2);
            header.Controls.Add(title);

            Label subtitle = Theme.Label(
                "Collect every link on a page, then download it in parallel segments. Pause, close, and continue later.",
                9f, false, Theme.Muted);
            subtitle.Location = new Point(2, 37);
            header.Controls.Add(subtitle);

            Label badge = Theme.Label("SEGMENTED  /  RESUMABLE  /  SAVE AS", 8.25f, true, Theme.Accent);
            badge.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            badge.AutoSize = false;
            badge.Width = 300;
            badge.Height = 20;
            badge.TextAlign = ContentAlignment.MiddleRight;
            badge.Location = new Point(header.Width - 302, 8);
            header.Resize += delegate
            {
                badge.Left = header.Width - badge.Width - 2;
            };
            header.Controls.Add(badge);

            return header;
        }

        private Control BuildLinksPanel()
        {
            TableLayoutPanel left = new TableLayoutPanel();
            left.Dock = DockStyle.Fill;
            left.BackColor = Theme.Bg;
            left.ColumnCount = 1;
            left.RowCount = 9;
            left.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 40));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            left.RowStyles.Add(new RowStyle(SizeType.Percent, 60));
            left.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
            left.Padding = new Padding(0, 0, 0, 4);

            Label header = Theme.Label("1   COLLECT LINKS", 10.5f, true, Theme.Accent);
            header.Margin = new Padding(0, 4, 0, 0);
            left.Controls.Add(header, 0, 0);

            TableLayoutPanel sourceRow = MakeRow(100, -96);
            sourceRow.Margin = new Padding(0, 1, 0, 1);
            _urlBox = MakeInput();
            _urlBox.Dock = DockStyle.Fill;
            _urlBox.Margin = new Padding(0);
            _fetchButton = new FlatButton("Fetch page", false);
            _fetchButton.Dock = DockStyle.Fill;
            _fetchButton.Margin = new Padding(6, 0, 0, 0);
            sourceRow.Controls.Add(_urlBox, 0, 0);
            sourceRow.Controls.Add(_fetchButton, 1, 0);
            left.Controls.Add(sourceRow, 0, 1);

            TableLayoutPanel pasteArea = MakeStack(18, 100);
            pasteArea.Margin = new Padding(0, 2, 0, 2);
            Label pasteHint = Theme.Label("Paste HTML, page source, or a plain list of URLs", 8.25f, false, Theme.Muted);
            pasteHint.Dock = DockStyle.Fill;
            pasteHint.TextAlign = ContentAlignment.MiddleLeft;
            pasteHint.Margin = new Padding(0);
            _pasteBox = new TextBox();
            _pasteBox.Dock = DockStyle.Fill;
            _pasteBox.Multiline = true;
            _pasteBox.AcceptsReturn = true;
            _pasteBox.AcceptsTab = true;
            _pasteBox.WordWrap = false;
            _pasteBox.ScrollBars = ScrollBars.Both;
            _pasteBox.Font = new Font("Consolas", 8.75f);
            _pasteBox.BackColor = Theme.Field;
            _pasteBox.ForeColor = Theme.Text;
            _pasteBox.BorderStyle = BorderStyle.FixedSingle;
            _pasteBox.Margin = new Padding(0);
            pasteArea.Controls.Add(pasteHint, 0, 0);
            pasteArea.Controls.Add(_pasteBox, 0, 1);
            left.Controls.Add(pasteArea, 0, 2);

            TableLayoutPanel extractRow = MakeRow(100, -138);
            extractRow.Margin = new Padding(0, 3, 0, 3);
            FlowLayoutPanel sourceButtons = new FlowLayoutPanel();
            sourceButtons.Dock = DockStyle.Fill;
            sourceButtons.WrapContents = false;
            sourceButtons.BackColor = Color.Transparent;
            sourceButtons.Margin = new Padding(0);
            sourceButtons.Padding = new Padding(0);
            FlatButton openFile = MakeButton("Open file...", 92);
            FlatButton paste = MakeButton("Paste", 62);
            FlatButton clear = MakeButton("Clear", 58);
            sourceButtons.Controls.Add(openFile);
            sourceButtons.Controls.Add(paste);
            sourceButtons.Controls.Add(clear);
            FlatButton extract = new FlatButton("Extract links", true);
            extract.Dock = DockStyle.Fill;
            extract.Margin = new Padding(6, 3, 0, 3);
            extractRow.Controls.Add(sourceButtons, 0, 0);
            extractRow.Controls.Add(extract, 1, 0);
            left.Controls.Add(extractRow, 0, 3);

            TableLayoutPanel baseRow = MakeRow(-58, 100);
            baseRow.Margin = new Padding(0, 2, 0, 2);
            Label baseLabel = Theme.Label("Base URL", 8.5f, false, Theme.Muted);
            baseLabel.Dock = DockStyle.Fill;
            baseLabel.TextAlign = ContentAlignment.MiddleLeft;
            baseLabel.Margin = new Padding(0);
            _baseBox = MakeInput();
            _baseBox.Dock = DockStyle.Fill;
            _baseBox.Margin = new Padding(0);
            baseRow.Controls.Add(baseLabel, 0, 0);
            baseRow.Controls.Add(_baseBox, 1, 0);
            left.Controls.Add(baseRow, 0, 4);

            TableLayoutPanel filterRow = MakeRow(100, -110, -104);
            filterRow.Margin = new Padding(0, 2, 0, 2);
            _filterBox = MakeInput();
            _filterBox.Dock = DockStyle.Fill;
            _filterBox.Margin = new Padding(0);
            _typeBox = new ComboBox();
            _typeBox.Dock = DockStyle.Fill;
            _typeBox.DropDownStyle = ComboBoxStyle.DropDownList;
            _typeBox.Margin = new Padding(6, 0, 0, 0);
            Theme.StyleCombo(_typeBox);
            _sameHostBox = new CheckBox();
            _sameHostBox.Text = "Same host";
            _sameHostBox.Dock = DockStyle.Fill;
            _sameHostBox.ForeColor = Theme.Muted;
            _sameHostBox.BackColor = Color.Transparent;
            _sameHostBox.FlatStyle = FlatStyle.Flat;
            _sameHostBox.Font = new Font("Segoe UI", 8.5f);
            _sameHostBox.Margin = new Padding(8, 0, 0, 0);
            _typeBox.Items.AddRange(new object[] { "All", "Video", "Audio", "Image", "Archive", "Document", "Program", "Web", "Other" });
            _typeBox.SelectedIndex = 0;
            filterRow.Controls.Add(_filterBox, 0, 0);
            filterRow.Controls.Add(_typeBox, 1, 0);
            filterRow.Controls.Add(_sameHostBox, 2, 0);
            left.Controls.Add(filterRow, 0, 5);

            TableLayoutPanel listTools = MakeRow(100, -160);
            listTools.Margin = new Padding(0, 1, 0, 1);
            FlowLayoutPanel listButtons = new FlowLayoutPanel();
            listButtons.Dock = DockStyle.Fill;
            listButtons.WrapContents = false;
            listButtons.BackColor = Color.Transparent;
            listButtons.Margin = new Padding(0);
            listButtons.Padding = new Padding(0);
            FlatButton all = MakeButton("All", 52);
            FlatButton none = MakeButton("None", 58);
            FlatButton clearList = MakeButton("Clear list", 76);
            all.Height = 25;
            none.Height = 25;
            clearList.Height = 25;
            listButtons.Controls.Add(all);
            listButtons.Controls.Add(none);
            listButtons.Controls.Add(clearList);
            _linkCount = Theme.Label("", 8.5f, false, Theme.Muted);
            _linkCount.Dock = DockStyle.Fill;
            _linkCount.TextAlign = ContentAlignment.MiddleRight;
            _linkCount.AutoSize = false;
            listTools.Controls.Add(listButtons, 0, 0);
            listTools.Controls.Add(_linkCount, 1, 0);
            left.Controls.Add(listTools, 0, 6);

            _linkGrid = new DarkGrid();
            Theme.StyleGrid(_linkGrid);
            _linkGrid.ReadOnly = false;
            _linkGrid.Margin = new Padding(0, 2, 0, 2);

            DataGridViewCheckBoxColumn checkColumn = new DataGridViewCheckBoxColumn();
            checkColumn.HeaderText = "";
            checkColumn.FillWeight = 4;
            checkColumn.MinimumWidth = 28;
            checkColumn.SortMode = DataGridViewColumnSortMode.NotSortable;
            checkColumn.Resizable = DataGridViewTriState.False;

            DataGridViewTextBoxColumn nameColumn = MakeTextColumn("File", 36, 120);
            nameColumn.ReadOnly = true;
            DataGridViewTextBoxColumn hostColumn = MakeTextColumn("Host", 18, 90);
            hostColumn.ReadOnly = true;
            DataGridViewTextBoxColumn typeColumn = MakeTextColumn("Type", 9, 52);
            typeColumn.ReadOnly = true;
            DataGridViewTextBoxColumn urlColumn = MakeTextColumn("URL", 33, 120);
            urlColumn.ReadOnly = true;

            _linkGrid.Columns.Add(checkColumn);
            _linkGrid.Columns.Add(nameColumn);
            _linkGrid.Columns.Add(hostColumn);
            _linkGrid.Columns.Add(typeColumn);
            _linkGrid.Columns.Add(urlColumn);
            left.Controls.Add(_linkGrid, 0, 7);

            TableLayoutPanel addRow = MakeRow(100, -186);
            addRow.Margin = new Padding(0, 4, 0, 0);
            _startNowBox = new CheckBox();
            _startNowBox.Text = "Start immediately";
            _startNowBox.Dock = DockStyle.Fill;
            _startNowBox.ForeColor = Theme.Muted;
            _startNowBox.BackColor = Color.Transparent;
            _startNowBox.FlatStyle = FlatStyle.Flat;
            _startNowBox.Font = new Font("Segoe UI", 8.5f);
            _startNowBox.Margin = new Padding(0);
            FlatButton addToQueue = new FlatButton("Add checked to queue", true);
            addToQueue.Dock = DockStyle.Fill;
            addToQueue.Margin = new Padding(6, 2, 0, 2);
            addRow.Controls.Add(_startNowBox, 0, 0);
            addRow.Controls.Add(addToQueue, 1, 0);
            left.Controls.Add(addRow, 0, 8);

            // Keep references for the event binder.
            openFile.Name = "openFileButton";
            paste.Name = "pasteButton";
            clear.Name = "clearButton";
            extract.Name = "extractButton";
            all.Name = "selectAllButton";
            none.Name = "selectNoneButton";
            clearList.Name = "clearListButton";
            addToQueue.Name = "addToQueueButton";

            _linkGrid.CellValueChanged += LinkGridCellValueChanged;
            _linkGrid.CurrentCellDirtyStateChanged += LinkGridDirtyStateChanged;
            _linkGrid.CellDoubleClick += LinkGridCellDoubleClick;

            openFile.Click += OpenSourceFileClick;
            paste.Click += PasteClick;
            clear.Click += ClearInputClick;
            extract.Click += ExtractClick;
            all.Click += SelectAllLinksClick;
            none.Click += SelectNoneLinksClick;
            clearList.Click += ClearListClick;
            addToQueue.Click += AddToQueueClick;
            _fetchButton.Click += FetchClick;

            return left;
        }

        private Control BuildQueuePanel()
        {
            TableLayoutPanel right = new TableLayoutPanel();
            right.Dock = DockStyle.Fill;
            right.BackColor = Theme.Bg;
            right.ColumnCount = 1;
            right.RowCount = 6;
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            right.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
            right.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
            right.Padding = new Padding(0, 0, 0, 4);

            TableLayoutPanel queueHeader = MakeRow(100, -276);
            Label queueTitle = Theme.Label("2   DOWNLOAD QUEUE", 10.5f, true, Theme.Accent);
            queueTitle.TextAlign = ContentAlignment.MiddleLeft;
            queueTitle.Dock = DockStyle.Fill;
            queueTitle.AutoSize = false;
            queueTitle.Margin = new Padding(0);
            FlowLayoutPanel headerButtons = new FlowLayoutPanel();
            headerButtons.Dock = DockStyle.Fill;
            headerButtons.WrapContents = false;
            headerButtons.FlowDirection = FlowDirection.RightToLeft;
            headerButtons.BackColor = Color.Transparent;
            headerButtons.Margin = new Padding(0);
            headerButtons.Padding = new Padding(0);
            FlatButton resumeAll = MakeButton("Resume all", 88);
            FlatButton pauseAll = MakeButton("Pause all", 82);
            FlatButton startAll = new FlatButton("Start all", true);
            startAll.Width = 78;
            startAll.Height = 25;
            startAll.Margin = new Padding(4, 0, 0, 0);
            resumeAll.Height = 25;
            pauseAll.Height = 25;
            headerButtons.Controls.Add(resumeAll);
            headerButtons.Controls.Add(pauseAll);
            headerButtons.Controls.Add(startAll);
            queueHeader.Controls.Add(queueTitle, 0, 0);
            queueHeader.Controls.Add(headerButtons, 1, 0);
            right.Controls.Add(queueHeader, 0, 0);

            TableLayoutPanel outputRow = MakeRow(-50, 100, -82, -62);
            outputRow.Margin = new Padding(0, 1, 0, 1);
            Label saveLabel = Theme.Label("Save to", 8.5f, false, Theme.Muted);
            saveLabel.Dock = DockStyle.Fill;
            saveLabel.TextAlign = ContentAlignment.MiddleLeft;
            saveLabel.AutoSize = false;
            saveLabel.Margin = new Padding(0);
            _outBox = MakeInput();
            _outBox.Dock = DockStyle.Fill;
            _outBox.Margin = new Padding(0);
            FlatButton browse = MakeButton("Browse...", 78);
            browse.Dock = DockStyle.Fill;
            browse.Margin = new Padding(6, 1, 0, 1);
            FlatButton openFolder = MakeButton("Open", 58);
            openFolder.Dock = DockStyle.Fill;
            openFolder.Margin = new Padding(6, 1, 0, 1);
            outputRow.Controls.Add(saveLabel, 0, 0);
            outputRow.Controls.Add(_outBox, 1, 0);
            outputRow.Controls.Add(browse, 2, 0);
            outputRow.Controls.Add(openFolder, 3, 0);
            right.Controls.Add(outputRow, 0, 1);

            FlowLayoutPanel options = new FlowLayoutPanel();
            options.Dock = DockStyle.Fill;
            options.WrapContents = false;
            options.BackColor = Color.Transparent;
            options.Margin = new Padding(0);
            options.Padding = new Padding(0);
            options.Height = 32;

            Label filesLabel = Theme.Label("Files at once", 8.5f, false, Theme.Muted);
            filesLabel.Margin = new Padding(0, 8, 5, 0);
            _parallelBox = new NumericUpDown();
            _parallelBox.Minimum = 1;
            _parallelBox.Maximum = 6;
            _parallelBox.Width = 48;
            _parallelBox.Height = 24;
            _parallelBox.Margin = new Padding(0, 4, 10, 0);
            Theme.StyleNumeric(_parallelBox);

            Label segLabel = Theme.Label("segments", 8.5f, false, Theme.Muted);
            segLabel.Margin = new Padding(0, 8, 5, 0);
            _segmentsBox = new NumericUpDown();
            _segmentsBox.Minimum = 1;
            _segmentsBox.Maximum = 8;
            _segmentsBox.Width = 48;
            _segmentsBox.Height = 24;
            _segmentsBox.Margin = new Padding(0, 4, 12, 0);
            Theme.StyleNumeric(_segmentsBox);

            _resumeBox = new CheckBox();
            _resumeBox.Text = "Resume on launch";
            _resumeBox.AutoSize = true;
            _resumeBox.ForeColor = Theme.Muted;
            _resumeBox.BackColor = Color.Transparent;
            _resumeBox.FlatStyle = FlatStyle.Flat;
            _resumeBox.Font = new Font("Segoe UI", 8.5f);
            _resumeBox.Margin = new Padding(0, 7, 12, 0);

            FlatButton headers = MakeButton("Headers...", 82);
            headers.Height = 26;
            headers.Margin = new Padding(0, 3, 0, 0);

            options.Controls.Add(filesLabel);
            options.Controls.Add(_parallelBox);
            options.Controls.Add(segLabel);
            options.Controls.Add(_segmentsBox);
            options.Controls.Add(_resumeBox);
            options.Controls.Add(headers);
            right.Controls.Add(options, 0, 2);

            _queueGrid = new DarkGrid();
            Theme.StyleGrid(_queueGrid);
            _queueGrid.Margin = new Padding(0, 2, 0, 2);
            _queueGrid.ReadOnly = true;

            DataGridViewTextBoxColumn fileColumn = MakeTextColumn("File", 30, 150);
            DataGridViewTextBoxColumn sizeColumn = MakeTextColumn("Size", 11, 68);
            DataGridViewTextBoxColumn progressColumn = MakeTextColumn("Progress", 16, 108);
            DataGridViewTextBoxColumn speedColumn = MakeTextColumn("Speed", 11, 72);
            DataGridViewTextBoxColumn etaColumn = MakeTextColumn("ETA", 9, 56);
            DataGridViewTextBoxColumn statusColumn = MakeTextColumn("Status", 15, 96);
            DataGridViewTextBoxColumn urlColumn = MakeTextColumn("URL", 30, 110);

            _queueGrid.Columns.Add(fileColumn);
            _queueGrid.Columns.Add(sizeColumn);
            _queueGrid.Columns.Add(progressColumn);
            _queueGrid.Columns.Add(speedColumn);
            _queueGrid.Columns.Add(etaColumn);
            _queueGrid.Columns.Add(statusColumn);
            _queueGrid.Columns.Add(urlColumn);
            _queueGrid.CellPainting += QueueGridCellPainting;
            _queueGrid.CellDoubleClick += QueueGridCellDoubleClick;
            _queueGrid.MouseDown += QueueGridMouseDown;
            right.Controls.Add(_queueGrid, 0, 3);

            FlowLayoutPanel actions = new FlowLayoutPanel();
            actions.Dock = DockStyle.Fill;
            actions.WrapContents = false;
            actions.BackColor = Color.Transparent;
            actions.Margin = new Padding(0);
            actions.Padding = new Padding(0);
            FlatButton pause = MakeButton("Pause", 64);
            FlatButton resume = MakeButton("Resume", 72);
            FlatButton saveAs = MakeButton("Save As...", 84);
            FlatButton saveDone = MakeButton("Save done...", 92);
            FlatButton remove = MakeButton("Remove", 72);
            FlatButton clearDone = MakeButton("Clear done", 84);
            pause.Height = 26;
            resume.Height = 26;
            saveAs.Height = 26;
            saveDone.Height = 26;
            remove.Height = 26;
            clearDone.Height = 26;
            actions.Controls.Add(pause);
            actions.Controls.Add(resume);
            actions.Controls.Add(saveAs);
            actions.Controls.Add(saveDone);
            actions.Controls.Add(remove);
            actions.Controls.Add(clearDone);
            right.Controls.Add(actions, 0, 4);

            TableLayoutPanel progressArea = MakeStack(16, 100);
            progressArea.Margin = new Padding(0, 2, 0, 0);
            _overall = new ProgressBarEx();
            _overall.Dock = DockStyle.Top;
            _overall.BarColor = Theme.Green;
            _overall.Margin = new Padding(0, 2, 0, 4);
            TableLayoutPanel progressLabels = MakeRow(100, -320);
            _queueLeft = Theme.Label("", 8.5f, false, Theme.Muted);
            _queueLeft.AutoSize = false;
            _queueLeft.Dock = DockStyle.Fill;
            _queueLeft.TextAlign = ContentAlignment.MiddleLeft;
            _queueLeft.Margin = new Padding(0);
            _queueRight = Theme.Label("", 8.5f, false, Theme.Muted);
            _queueRight.AutoSize = false;
            _queueRight.Dock = DockStyle.Fill;
            _queueRight.TextAlign = ContentAlignment.MiddleRight;
            _queueRight.Margin = new Padding(0);
            progressLabels.Controls.Add(_queueLeft, 0, 0);
            progressLabels.Controls.Add(_queueRight, 1, 0);
            progressArea.Controls.Add(_overall, 0, 0);
            progressArea.Controls.Add(progressLabels, 0, 1);
            right.Controls.Add(progressArea, 0, 5);

            // Build the context menu.
            _queueMenu = new ContextMenuStrip();
            _queueMenu.BackColor = Theme.PanelAlt;
            _queueMenu.ForeColor = Theme.Text;
            _menuPause = new ToolStripMenuItem("Pause");
            _menuResume = new ToolStripMenuItem("Resume / Retry");
            _menuSaveAs = new ToolStripMenuItem("Save As...");
            _menuOpen = new ToolStripMenuItem("Open file");
            _menuFolder = new ToolStripMenuItem("Show in folder");
            _menuCopyUrl = new ToolStripMenuItem("Copy URL");
            _menuRemove = new ToolStripMenuItem("Remove from queue");
            _queueMenu.Items.Add(_menuPause);
            _queueMenu.Items.Add(_menuResume);
            _queueMenu.Items.Add(new ToolStripSeparator());
            _queueMenu.Items.Add(_menuSaveAs);
            _queueMenu.Items.Add(_menuOpen);
            _queueMenu.Items.Add(_menuFolder);
            _queueMenu.Items.Add(_menuCopyUrl);
            _queueMenu.Items.Add(new ToolStripSeparator());
            _queueMenu.Items.Add(_menuRemove);
            _queueMenu.Opening += QueueMenuOpening;
            _queueGrid.ContextMenuStrip = _queueMenu;

            // Name buttons for the event binder.
            browse.Name = "browseButton";
            openFolder.Name = "openFolderButton";
            headers.Name = "headersButton";
            startAll.Name = "startAllButton";
            pauseAll.Name = "pauseAllButton";
            resumeAll.Name = "resumeAllButton";
            pause.Name = "pauseButton";
            resume.Name = "resumeButton";
            saveAs.Name = "saveAsButton";
            saveDone.Name = "saveDoneButton";
            remove.Name = "removeButton";
            clearDone.Name = "clearDoneButton";

            browse.Click += BrowseClick;
            openFolder.Click += OpenFolderClick;
            headers.Click += HeadersClick;
            startAll.Click += StartAllClick;
            pauseAll.Click += PauseAllClick;
            resumeAll.Click += ResumeAllClick;
            pause.Click += PauseClick;
            resume.Click += ResumeClick;
            saveAs.Click += SaveAsClick;
            saveDone.Click += SaveDoneClick;
            remove.Click += RemoveClick;
            clearDone.Click += ClearDoneClick;

            _menuPause.Click += PauseClick;
            _menuResume.Click += ResumeClick;
            _menuSaveAs.Click += SaveAsClick;
            _menuOpen.Click += OpenDownloadedFileClick;
            _menuFolder.Click += ShowInFolderClick;
            _menuCopyUrl.Click += CopyUrlClick;
            _menuRemove.Click += RemoveClick;

            return right;
        }

        private Control BuildStatusBar()
        {
            Panel bar = new Panel();
            bar.Dock = DockStyle.Fill;
            bar.BackColor = Theme.Bg;
            bar.Paint += delegate (object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(Theme.Border))
                {
                    e.Graphics.DrawLine(pen, 0, 0, bar.Width, 0);
                }
            };

            _statusLabel = Theme.Label("Ready.", 8.5f, false, Theme.Muted);
            _statusLabel.AutoSize = false;
            _statusLabel.Dock = DockStyle.Fill;
            _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
            _statusLabel.Padding = new Padding(2, 0, 0, 0);
            bar.Controls.Add(_statusLabel);

            Label version = Theme.Label("v1.1  ·  .NET Framework 4.8", 8.25f, false, Theme.Muted);
            version.AutoSize = false;
            version.Dock = DockStyle.Right;
            version.Width = 220;
            version.TextAlign = ContentAlignment.MiddleRight;
            bar.Controls.Add(version);

            return bar;
        }

        private static FlatButton MakeButton(string text, int width)
        {
            FlatButton button = new FlatButton(text, false);
            button.Width = width;
            button.Height = 28;
            button.Margin = new Padding(0, 0, 6, 0);
            return button;
        }

        private static DataGridViewTextBoxColumn MakeTextColumn(string header, int weight, int minWidth)
        {
            DataGridViewTextBoxColumn column = new DataGridViewTextBoxColumn();
            column.HeaderText = header;
            column.FillWeight = weight;
            column.MinimumWidth = minWidth;
            column.SortMode = DataGridViewColumnSortMode.NotSortable;
            column.ReadOnly = true;
            return column;
        }

        private static TextBox MakeInput()
        {
            TextBox box = new TextBox();
            Theme.StyleTextBox(box);
            box.Height = 24;
            return box;
        }

        /// <summary>One-row table. Positive widths are percentages, negative are absolute pixels.</summary>
        private static TableLayoutPanel MakeRow(params int[] widths)
        {
            TableLayoutPanel table = new TableLayoutPanel();
            table.Dock = DockStyle.Fill;
            table.BackColor = Color.Transparent;
            table.ColumnCount = widths.Length;
            table.RowCount = 1;
            table.Margin = new Padding(0);
            table.Padding = new Padding(0);
            for (int i = 0; i < widths.Length; i++)
            {
                int w = widths[i];
                table.ColumnStyles.Add(new ColumnStyle(
                    w < 0 ? SizeType.Absolute : SizeType.Percent,
                    w < 0 ? -w : w));
            }
            table.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            return table;
        }

        /// <summary>One-column table. Positive heights are percentages, negative are absolute pixels.</summary>
        private static TableLayoutPanel MakeStack(params int[] heights)
        {
            TableLayoutPanel table = new TableLayoutPanel();
            table.Dock = DockStyle.Fill;
            table.BackColor = Color.Transparent;
            table.ColumnCount = 1;
            table.RowCount = heights.Length;
            table.Margin = new Padding(0);
            table.Padding = new Padding(0);
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            for (int i = 0; i < heights.Length; i++)
            {
                int h = heights[i];
                table.RowStyles.Add(new RowStyle(
                    h < 0 ? SizeType.Absolute : SizeType.Percent,
                    h < 0 ? -h : h));
            }
            return table;
        }

        // ------------------------------------------------------------ lifetime

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (!_shown)
            {
                _shown = true;
                try
                {
                    // Min sizes can only be applied once the split container has a real width.
                    _split.Panel1MinSize = 400;
                    _split.Panel2MinSize = 500;
                    _split.SplitterDistance = Math.Max(_split.Panel1MinSize,
                        Math.Min(470, _split.Width - _split.Panel2MinSize - _split.SplitterWidth));
                }
                catch
                {
                }

                if (_settings.AutoResume)
                {
                    List<DownloadTask> tasks = _manager.Tasks;
                    foreach (DownloadTask t in tasks)
                    {
                        if (t.State == TaskState.Paused && t.SawDownloading)
                        {
                            _manager.Start(t);
                        }
                    }
                }
            }
        }

        private bool _closingHandled;

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_allowClose)
            {
                base.OnFormClosing(e);
                return;
            }

            e.Cancel = true;
            if (_closingHandled)
            {
                return;
            }
            _closingHandled = true;
            _shutdownStarted = true;

            if (_timer != null) _timer.Stop();
            SetStatus("Saving download state...");
            ShutdownThenClose();
        }

        private async void ShutdownThenClose()
        {
            try
            {
                await _manager.ShutdownAsync();
            }
            catch
            {
            }
            try
            {
                _settings.Normalize();
                _settings.Save();
            }
            catch
            {
            }
            try
            {
                _manager.Dispose();
            }
            catch
            {
            }
            _allowClose = true;
            Close();
        }

        // --------------------------------------------------------------- events

        private void WireEvents()
        {
            _filterBox.TextChanged += delegate { ApplyLinkFilter(); };
            _typeBox.SelectedIndexChanged += delegate { ApplyLinkFilter(); };
            _sameHostBox.CheckedChanged += delegate { ApplyLinkFilter(); };

            _parallelBox.ValueChanged += delegate
            {
                _settings.ParallelFiles = (int)_parallelBox.Value;
                try { _settings.Save(); } catch { }
                _manager.NotifySettingsChanged();
                SetStatus("Parallel file limit: " + _settings.ParallelFiles);
            };
            _segmentsBox.ValueChanged += delegate
            {
                _settings.SegmentsPerFile = (int)_segmentsBox.Value;
                try { _settings.Save(); } catch { }
                SetStatus("Segments for new downloads: " + _settings.SegmentsPerFile);
            };
            _resumeBox.CheckedChanged += delegate
            {
                _settings.AutoResume = _resumeBox.Checked;
                try { _settings.Save(); } catch { }
            };

            _outBox.Leave += delegate
            {
                string folder = _outBox.Text.Trim();
                if (folder.Length == 0) return;
                SetOutputFolder(folder, false);
            };

            _urlBox.KeyDown += delegate (object sender, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    FetchPage();
                }
            };
        }

        private sealed class RowStat
        {
            public long LastBytes;
            public long LastTimestamp;
            public double Speed;
            public bool WasDownloading;
        }
    }
}
