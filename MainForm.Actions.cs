using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace LinkDownloader
{
    public partial class MainForm
    {
        // --------------------------------------------------------------- links

        private async void FetchClick(object sender, EventArgs e)
        {
            await FetchPageAsync();
        }

        private async void FetchPage()
        {
            await FetchPageAsync();
        }

        private async System.Threading.Tasks.Task FetchPageAsync()
        {
            string url = _urlBox.Text.Trim();
            if (url.Length == 0)
            {
                SetStatus("Enter a page URL first.");
                _urlBox.Focus();
                return;
            }

            _fetchButton.Enabled = false;
            _fetchButton.Text = "Fetching...";
            SetStatus("Fetching " + url + " ...");

            try
            {
                PageResult page = await _manager.FetchPageAsync(url);
                _pageReferer = page.BaseUrl;
                _urlBox.Text = page.BaseUrl;
                _baseBox.Text = page.BaseUrl;

                if (!page.IsText)
                {
                    string message = "That URL returns '" + page.ContentType +
                        "', so it is a file rather than a web page.\n\nAdd it to the download queue?";
                    DialogResult answer = MessageBox.Show(this, message, "Not a web page",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                    if (answer == DialogResult.Yes)
                    {
                        AddUrlToQueue(page.BaseUrl, null);
                    }
                    else
                    {
                        SetStatus("URL is not an HTML page.");
                    }
                    return;
                }

                if (page.Text.Length <= 2000000)
                {
                    _pasteBox.Text = page.Text;
                }

                int skipped;
                List<LinkItem> found = LinkExtractor.Extract(page.Text, page.BaseUrl, out skipped);
                int added = MergeLinks(found);
                SetStatus(string.Format("Fetched {0}: {1} link(s), {2} new.",
                    Shorten(page.BaseUrl, 64), found.Count, added));
            }
            catch (Exception ex)
            {
                SetStatus("Fetch failed: " + ex.Message);
                MessageBox.Show(this, ex.Message, "Could not fetch the page",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                _fetchButton.Enabled = true;
                _fetchButton.Text = "Fetch page";
            }
        }

        private void OpenSourceFileClick(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Open HTML or a link list";
                dialog.Filter = "Web pages and text|*.html;*.htm;*.txt;*.json;*.xml;*.m3u;*.m3u8|All files|*.*";
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    try
                    {
                        _pasteBox.Text = File.ReadAllText(dialog.FileName);
                        SetStatus("Loaded " + Path.GetFileName(dialog.FileName) +
                            ". Click Extract links.");
                    }
                    catch (Exception ex)
                    {
                        SetStatus("Could not read the file: " + ex.Message);
                    }
                }
            }
        }

        private void PasteClick(object sender, EventArgs e)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    _pasteBox.Text = Clipboard.GetText();
                    SetStatus("Pasted clipboard contents.");
                }
                else
                {
                    SetStatus("The clipboard does not contain text.");
                }
            }
            catch (Exception ex)
            {
                SetStatus("Paste failed: " + ex.Message);
            }
        }

        private void ClearInputClick(object sender, EventArgs e)
        {
            _pasteBox.Clear();
            SetStatus("Input cleared.");
        }

        private void ExtractClick(object sender, EventArgs e)
        {
            string text = _pasteBox.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                SetStatus("Paste HTML or a list of URLs first.");
                _pasteBox.Focus();
                return;
            }

            string baseText = _baseBox.Text.Trim();
            if (baseText.Length == 0) baseText = _pageReferer;

            int skipped;
            List<LinkItem> found = LinkExtractor.Extract(text, baseText, out skipped);
            int added = MergeLinks(found);
            SetStatus(string.Format("Found {0} link(s); {1} new; {2} skipped.", found.Count, added, skipped));
        }

        private int MergeLinks(List<LinkItem> found)
        {
            HashSet<string> existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (LinkItem item in _links)
            {
                existing.Add(item.Url);
            }

            int added = 0;
            foreach (LinkItem item in found)
            {
                if (!existing.Add(item.Url)) continue;
                _links.Add(item);
                _checked.Add(item.Url);
                added++;
            }

            ApplyLinkFilter();
            return added;
        }

        private void SelectAllLinksClick(object sender, EventArgs e)
        {
            SetVisibleLinksChecked(true);
        }

        private void SelectNoneLinksClick(object sender, EventArgs e)
        {
            SetVisibleLinksChecked(false);
        }

        private void SetVisibleLinksChecked(bool value)
        {
            _syncingLinks = true;
            try
            {
                foreach (DataGridViewRow row in _linkGrid.Rows)
                {
                    LinkItem item = row.Tag as LinkItem;
                    if (item == null) continue;
                    row.Cells[LinkCheckColumn].Value = value;
                    if (value) _checked.Add(item.Url);
                    else _checked.Remove(item.Url);
                }
            }
            finally
            {
                _syncingLinks = false;
            }
            UpdateLinkCount();
        }

        private void ClearListClick(object sender, EventArgs e)
        {
            if (_links.Count > 0)
            {
                DialogResult answer = MessageBox.Show(this,
                    "Clear the collected link list?\n\nDownloads already in the queue are not affected.",
                    "Clear link list", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
                if (answer != DialogResult.Yes) return;
            }
            _links.Clear();
            _checked.Clear();
            ApplyLinkFilter();
            SetStatus("Link list cleared.");
        }

        private void LinkGridCellValueChanged(object sender, DataGridViewCellEventArgs e)
        {
            if (_syncingLinks || e.RowIndex < 0 || e.ColumnIndex != LinkCheckColumn) return;
            LinkItem item = _linkGrid.Rows[e.RowIndex].Tag as LinkItem;
            if (item == null) return;
            object value = _linkGrid.Rows[e.RowIndex].Cells[LinkCheckColumn].Value;
            bool isChecked = value is bool && (bool)value;
            if (isChecked) _checked.Add(item.Url);
            else _checked.Remove(item.Url);
            UpdateLinkCount();
        }

        private void LinkGridDirtyStateChanged(object sender, EventArgs e)
        {
            if (_syncingLinks) return;
            if (_linkGrid.IsCurrentCellDirty &&
                _linkGrid.CurrentCell is DataGridViewCheckBoxCell)
            {
                _linkGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            }
        }

        private void LinkGridCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (_syncingLinks || e.RowIndex < 0) return;
            LinkItem item = _linkGrid.Rows[e.RowIndex].Tag as LinkItem;
            if (item == null) return;
            bool isChecked = !_checked.Contains(item.Url);
            if (isChecked) _checked.Add(item.Url);
            else _checked.Remove(item.Url);
            _syncingLinks = true;
            try
            {
                _linkGrid.Rows[e.RowIndex].Cells[LinkCheckColumn].Value = isChecked;
            }
            finally
            {
                _syncingLinks = false;
            }
            UpdateLinkCount();
        }

        private void ApplyLinkFilter()
        {
            if (_linkGrid == null) return;

            string query = _filterBox.Text.Trim();
            string type = _typeBox.SelectedItem == null ? "All" : _typeBox.SelectedItem.ToString();

            Uri baseUri = null;
            bool restrictHost = _sameHostBox.Checked && TryGetBaseUri(out baseUri);

            _syncingLinks = true;
            _linkGrid.SuspendLayout();
            try
            {
                _linkGrid.Rows.Clear();
                foreach (LinkItem item in _links)
                {
                    if (query.Length > 0 &&
                        item.Url.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 &&
                        item.FileName.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }
                    if (!string.Equals(type, "All", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(item.Category, type, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                    if (restrictHost && !string.Equals(item.Host, baseUri.Host, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    int index = _linkGrid.Rows.Add(_checked.Contains(item.Url), item.FileName,
                        item.Host, item.Category, item.Url);
                    DataGridViewRow row = _linkGrid.Rows[index];
                    row.Tag = item;
                    row.Cells[LinkUrlColumn].ToolTipText = item.Url;
                    row.Cells[LinkNameColumn].ToolTipText = item.FileName;
                }
            }
            finally
            {
                _linkGrid.ResumeLayout();
                _syncingLinks = false;
            }
            UpdateLinkCount();
        }

        private void UpdateLinkCount()
        {
            if (_linkCount == null || _linkGrid == null) return;
            _linkCount.Text = string.Format("{0} shown  /  {1} checked  /  {2} total",
                _linkGrid.Rows.Count, _checked.Count, _links.Count);
        }

        private bool TryGetBaseUri(out Uri uri)
        {
            uri = null;
            string baseText = _baseBox.Text.Trim();
            if (baseText.Length == 0) baseText = _pageReferer;
            return LinkExtractor.TryCreateBase(baseText, out uri);
        }

        private void AddToQueueClick(object sender, EventArgs e)
        {
            List<LinkItem> chosen = new List<LinkItem>();
            foreach (LinkItem item in _links)
            {
                if (_checked.Contains(item.Url)) chosen.Add(item);
            }

            if (chosen.Count == 0)
            {
                foreach (DataGridViewRow row in _linkGrid.SelectedRows)
                {
                    LinkItem item = row.Tag as LinkItem;
                    if (item != null) chosen.Add(item);
                }
            }

            if (chosen.Count == 0)
            {
                SetStatus("Check or select one or more links first.");
                return;
            }

            int added = 0;
            int duplicates = 0;
            int failed = 0;

            foreach (LinkItem item in chosen)
            {
                if (_manager.HasActiveUrl(item.Url))
                {
                    duplicates++;
                    continue;
                }
                try
                {
                    DownloadTask task = _manager.AddTask(item.Url, item.FileName, _pageReferer);
                    if (_startNowBox.Checked) _manager.Start(task);
                    _checked.Remove(item.Url);
                    added++;
                }
                catch
                {
                    failed++;
                }
            }

            ApplyLinkFilter();
            SetStatus(string.Format("Added {0} download(s). {1} already queued, {2} failed.",
                added, duplicates, failed));
        }

        private void AddUrlToQueue(string url, string name)
        {
            if (_manager.HasActiveUrl(url))
            {
                SetStatus("Already in the queue: " + url);
                return;
            }

            DownloadTask task = _manager.AddTask(url, name, _pageReferer);
            if (_startNowBox != null && _startNowBox.Checked) _manager.Start(task);
            SetStatus("Queued " + task.FileName);
        }

        // ------------------------------------------------------------ settings

        private void BrowseClick(object sender, EventArgs e)
        {
            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Choose where downloads are saved";
                dialog.ShowNewFolderButton = true;
                dialog.SelectedPath = _settings.OutputFolder;
                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    SetOutputFolder(dialog.SelectedPath, true);
                }
            }
        }

        private void SetOutputFolder(string folder, bool announce)
        {
            try
            {
                Directory.CreateDirectory(folder);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "Cannot use that folder:\n\n" + ex.Message,
                    "Save folder", MessageBoxButtons.OK, MessageBoxIcon.Error);
                _outBox.Text = _settings.OutputFolder;
                return;
            }

            _settings.OutputFolder = folder;
            _outBox.Text = folder;
            try { _settings.Save(); } catch { }
            if (announce) SetStatus("New downloads will be saved to " + folder);
        }

        private void OpenFolderClick(object sender, EventArgs e)
        {
            try
            {
                Directory.CreateDirectory(_settings.OutputFolder);
                System.Diagnostics.Process.Start("explorer.exe", "\"" + _settings.OutputFolder + "\"");
            }
            catch (Exception ex)
            {
                SetStatus("Could not open the folder: " + ex.Message);
            }
        }

        private void HeadersClick(object sender, EventArgs e)
        {
            using (Form dialog = new Form())
            {
                dialog.Text = "Request headers";
                dialog.ClientSize = new Size(560, 400);
                dialog.StartPosition = FormStartPosition.CenterParent;
                dialog.FormBorderStyle = FormBorderStyle.FixedDialog;
                dialog.MinimizeBox = false;
                dialog.MaximizeBox = false;
                dialog.ShowInTaskbar = false;
                dialog.BackColor = Theme.Bg;
                dialog.ForeColor = Theme.Text;
                dialog.Font = new Font("Segoe UI", 9f);

                TableLayoutPanel root = new TableLayoutPanel();
                root.Dock = DockStyle.Fill;
                root.ColumnCount = 1;
                root.RowCount = 4;
                root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
                root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
                root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
                root.Padding = new Padding(12);

                Label info = Theme.Label(
                    "One header per line, for example:\nAuthorization: Bearer abc123   or   Cookie: session=...",
                    8.5f, false, Theme.Muted);
                info.Dock = DockStyle.Fill;
                info.AutoSize = false;

                TextBox headersBox = new TextBox();
                headersBox.Dock = DockStyle.Fill;
                headersBox.Multiline = true;
                headersBox.ScrollBars = ScrollBars.Both;
                headersBox.WordWrap = false;
                headersBox.Font = new Font("Consolas", 9f);
                headersBox.BackColor = Theme.Field;
                headersBox.ForeColor = Theme.Text;
                headersBox.BorderStyle = BorderStyle.FixedSingle;
                headersBox.Text = _settings.ExtraHeaders;

                FlowLayoutPanel retryRow = new FlowLayoutPanel();
                retryRow.Dock = DockStyle.Fill;
                retryRow.WrapContents = false;
                retryRow.BackColor = Color.Transparent;
                Label retryLabel = Theme.Label("Retries per chunk", 8.5f, false, Theme.Muted);
                retryLabel.Margin = new Padding(0, 8, 8, 0);
                NumericUpDown retryBox = new NumericUpDown();
                retryBox.Minimum = 0;
                retryBox.Maximum = 10;
                retryBox.Value = _settings.Retries;
                retryBox.Width = 56;
                retryBox.Margin = new Padding(0, 4, 0, 0);
                Theme.StyleNumeric(retryBox);
                retryRow.Controls.Add(retryLabel);
                retryRow.Controls.Add(retryBox);

                FlowLayoutPanel buttons = new FlowLayoutPanel();
                buttons.Dock = DockStyle.Fill;
                buttons.FlowDirection = FlowDirection.RightToLeft;
                buttons.WrapContents = false;
                buttons.BackColor = Color.Transparent;
                FlatButton ok = new FlatButton("Save", true);
                ok.Width = 90;
                ok.Height = 28;
                ok.DialogResult = DialogResult.OK;
                FlatButton cancel = new FlatButton("Cancel", false);
                cancel.Width = 90;
                cancel.Height = 28;
                cancel.DialogResult = DialogResult.Cancel;
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);

                root.Controls.Add(info, 0, 0);
                root.Controls.Add(headersBox, 0, 1);
                root.Controls.Add(retryRow, 0, 2);
                root.Controls.Add(buttons, 0, 3);
                dialog.Controls.Add(root);
                dialog.AcceptButton = ok;
                dialog.CancelButton = cancel;

                if (dialog.ShowDialog(this) == DialogResult.OK)
                {
                    _settings.ExtraHeaders = headersBox.Text;
                    _settings.Retries = (int)retryBox.Value;
                    try { _settings.Save(); } catch { }
                    SetStatus("Request headers saved.");
                }
            }
        }

        // ------------------------------------------------------------- actions

        private void StartAllClick(object sender, EventArgs e)
        {
            _manager.StartAll();
            SetStatus("Starting the queue...");
        }

        private void PauseAllClick(object sender, EventArgs e)
        {
            _manager.PauseAll();
            SetStatus("Pausing all downloads...");
        }

        private void ResumeAllClick(object sender, EventArgs e)
        {
            _manager.ResumeAll();
            SetStatus("Resuming...");
        }

        private void PauseClick(object sender, EventArgs e)
        {
            List<DownloadTask> tasks = SelectedTasks();
            foreach (DownloadTask task in tasks)
            {
                if (task.State == TaskState.Downloading || task.State == TaskState.Probing ||
                    task.State == TaskState.Queued)
                {
                    _manager.Pause(task);
                }
            }
            if (tasks.Count == 0) SetStatus("Select a download first.");
        }

        private void ResumeClick(object sender, EventArgs e)
        {
            List<DownloadTask> tasks = SelectedTasks();
            foreach (DownloadTask task in tasks)
            {
                if (task.State == TaskState.Paused || task.State == TaskState.Error)
                {
                    _manager.Start(task);
                }
            }
            if (tasks.Count == 0) SetStatus("Select a download first.");
        }

        private void SaveAsClick(object sender, EventArgs e)
        {
            DownloadTask task = FirstSelectedWithState(TaskState.Completed);
            if (task == null)
            {
                SetStatus("Select a finished download to use Save As.");
                return;
            }

            using (SaveFileDialog dialog = new SaveFileDialog())
            {
                dialog.Title = "Save As";
                dialog.FileName = task.FileName;
                dialog.InitialDirectory = task.OutputDirectory;
                dialog.Filter = "All files|*.*";
                dialog.OverwritePrompt = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    _manager.SaveAs(task, dialog.FileName);
                    SetStatus("Saved a copy to " + dialog.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Save As failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void SaveDoneClick(object sender, EventArgs e)
        {
            List<DownloadTask> done = new List<DownloadTask>();
            foreach (DownloadTask task in _manager.Tasks)
            {
                if (task.State == TaskState.Completed) done.Add(task);
            }
            if (done.Count == 0)
            {
                SetStatus("There are no finished downloads yet.");
                return;
            }

            using (FolderBrowserDialog dialog = new FolderBrowserDialog())
            {
                dialog.Description = "Save all finished downloads to a folder";
                dialog.ShowNewFolderButton = true;
                dialog.SelectedPath = _settings.OutputFolder;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    int copied = _manager.CopyCompletedTo(dialog.SelectedPath);
                    SetStatus("Copied " + copied + " file(s) to " + dialog.SelectedPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "Save all failed",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void RemoveClick(object sender, EventArgs e)
        {
            List<DownloadTask> tasks = SelectedTasks();
            if (tasks.Count == 0)
            {
                SetStatus("Select one or more downloads first.");
                return;
            }

            string message = "Remove " + tasks.Count + " download(s) from the list?\n\n" +
                "Incomplete partial data will be deleted. Finished files are kept.";
            if (MessageBox.Show(this, message, "Remove downloads",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            foreach (DownloadTask task in tasks)
            {
                _manager.Remove(task);
            }
            SetStatus("Removed " + tasks.Count + " download(s).");
        }

        private void ClearDoneClick(object sender, EventArgs e)
        {
            int finished = 0;
            foreach (DownloadTask task in _manager.Tasks)
            {
                if (task.State == TaskState.Completed) finished++;
            }
            if (finished == 0)
            {
                SetStatus("There are no finished downloads to clear.");
                return;
            }

            string message = "Remove " + finished + " finished download(s) from the list?\n\n" +
                "The downloaded files stay on disk.";
            if (MessageBox.Show(this, message, "Clear finished",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            {
                return;
            }

            int count = _manager.ClearFinished();
            SetStatus("Cleared " + count + " finished download(s).");
        }

        private void OpenDownloadedFileClick(object sender, EventArgs e)
        {
            DownloadTask task = FirstSelectedWithState(TaskState.Completed);
            if (task == null)
            {
                SetStatus("Select a finished download first.");
                return;
            }
            OpenFile(task);
        }

        private void ShowInFolderClick(object sender, EventArgs e)
        {
            DownloadTask task = FirstSelectedWithState(TaskState.Completed);
            if (task == null) return;
            ShowInFolder(task);
        }

        private void CopyUrlClick(object sender, EventArgs e)
        {
            DownloadTask task = SelectedTasks().Count > 0 ? SelectedTasks()[0] : null;
            if (task == null) return;
            try
            {
                Clipboard.SetText(task.Url);
                SetStatus("URL copied.");
            }
            catch (Exception ex)
            {
                SetStatus("Could not copy the URL: " + ex.Message);
            }
        }

        private List<DownloadTask> SelectedTasks()
        {
            List<DownloadTask> result = new List<DownloadTask>();
            if (_queueGrid == null) return result;
            foreach (DataGridViewRow row in _queueGrid.SelectedRows)
            {
                DownloadTask task = row.Tag as DownloadTask;
                if (task != null && !result.Contains(task)) result.Add(task);
            }
            if (result.Count == 0 && _queueGrid.CurrentRow != null)
            {
                DownloadTask task = _queueGrid.CurrentRow.Tag as DownloadTask;
                if (task != null) result.Add(task);
            }
            return result;
        }

        private DownloadTask FirstSelectedWithState(TaskState state)
        {
            foreach (DownloadTask task in SelectedTasks())
            {
                if (task.State == state) return task;
            }
            return null;
        }

        private static void OpenFile(DownloadTask task)
        {
            try
            {
                if (!File.Exists(task.FinalPath))
                {
                    MessageBox.Show("The file is no longer at:\n\n" + task.FinalPath,
                        "Open file", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(task.FinalPath)
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Open file", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private static void ShowInFolder(DownloadTask task)
        {
            try
            {
                if (File.Exists(task.FinalPath))
                {
                    System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + task.FinalPath + "\"");
                }
                else
                {
                    System.Diagnostics.Process.Start("explorer.exe", "\"" + task.OutputDirectory + "\"");
                }
            }
            catch
            {
            }
        }

        private void QueueMenuOpening(object sender, System.ComponentModel.CancelEventArgs e)
        {
            List<DownloadTask> tasks = SelectedTasks();
            if (tasks.Count == 0)
            {
                e.Cancel = true;
                return;
            }

            bool anyRunning = false;
            bool anyPaused = false;
            bool anyDone = false;
            foreach (DownloadTask task in tasks)
            {
                if (task.State == TaskState.Downloading || task.State == TaskState.Queued ||
                    task.State == TaskState.Probing) anyRunning = true;
                if (task.State == TaskState.Paused || task.State == TaskState.Error) anyPaused = true;
                if (task.State == TaskState.Completed) anyDone = true;
            }

            _menuPause.Enabled = anyRunning;
            _menuResume.Enabled = anyPaused;
            _menuSaveAs.Enabled = anyDone;
            _menuOpen.Enabled = anyDone;
            _menuFolder.Enabled = anyDone;
            _menuCopyUrl.Enabled = true;
        }

        private void QueueGridMouseDown(object sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            DataGridView.HitTestInfo hit = _queueGrid.HitTest(e.X, e.Y);
            if (hit.RowIndex < 0) return;
            if (!_queueGrid.Rows[hit.RowIndex].Selected)
            {
                _queueGrid.ClearSelection();
                _queueGrid.Rows[hit.RowIndex].Selected = true;
                _queueGrid.CurrentCell = _queueGrid.Rows[hit.RowIndex].Cells[QueueFileColumn];
            }
        }

        private void QueueGridCellDoubleClick(object sender, DataGridViewCellEventArgs e)
        {
            if (e.RowIndex < 0) return;
            DownloadTask task = _queueGrid.Rows[e.RowIndex].Tag as DownloadTask;
            if (task == null) return;

            if (task.State == TaskState.Completed)
            {
                OpenFile(task);
            }
            else if (task.State == TaskState.Paused || task.State == TaskState.Error)
            {
                if (task.State == TaskState.Error && !string.IsNullOrEmpty(task.LastError))
                {
                    MessageBox.Show(this, task.LastError, "Download error",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                _manager.Start(task);
            }
            else
            {
                _manager.Pause(task);
            }
        }

        // -------------------------------------------------------------- status

        private void SetStatus(string message)
        {
            if (_statusLabel == null || _statusLabel.IsDisposed) return;
            _statusLabel.Text = message == null ? "" : message;
        }

        private void ManagerLog(string message)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action<string>(SetStatus), message);
            }
            catch
            {
            }
        }

        private static string Shorten(string text, int max)
        {
            if (string.IsNullOrEmpty(text) || text.Length <= max) return text;
            return text.Substring(0, max - 3) + "...";
        }
    }
}
