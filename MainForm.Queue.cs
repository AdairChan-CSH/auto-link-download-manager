using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Threading;
using System.Windows.Forms;

namespace LinkDownloader
{
    public partial class MainForm
    {
        private void TimerTick(object sender, EventArgs e)
        {
            if (_shutdownStarted || _allowClose) return;
            try
            {
                RefreshQueue();
            }
            catch
            {
            }
        }

        private void RefreshQueue()
        {
            if (_queueGrid == null) return;

            List<DownloadTask> tasks = _manager.Tasks;

            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (DownloadTask task in tasks) ids.Add(task.Id);

            for (int i = _queueGrid.Rows.Count - 1; i >= 0; i--)
            {
                DownloadTask rowTask = _queueGrid.Rows[i].Tag as DownloadTask;
                if (rowTask == null || !ids.Contains(rowTask.Id))
                {
                    _queueGrid.Rows.RemoveAt(i);
                }
            }

            Dictionary<string, int> rowById = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (DataGridViewRow row in _queueGrid.Rows)
            {
                DownloadTask task = row.Tag as DownloadTask;
                if (task != null) rowById[task.Id] = row.Index;
            }

            long now = Stopwatch.GetTimestamp();
            long totalBytes = 0;
            long doneBytes = 0;
            double speedSum = 0;
            int active = 0;
            int queued = 0;
            int paused = 0;
            int completed = 0;
            int errors = 0;

            foreach (DownloadTask task in tasks)
            {
                RowStat stat;
                if (!_rowStats.TryGetValue(task.Id, out stat))
                {
                    stat = new RowStat();
                    stat.LastBytes = GetReceived(task);
                    stat.LastTimestamp = now;
                    _rowStats[task.Id] = stat;
                }

                long received = GetReceived(task);
                bool downloading = task.State == TaskState.Downloading;

                if (downloading)
                {
                    if (!stat.WasDownloading)
                    {
                        stat.WasDownloading = true;
                        stat.LastBytes = received;
                        stat.LastTimestamp = now;
                        stat.Speed = 0;
                    }
                    else
                    {
                        double seconds = (now - stat.LastTimestamp) / (double)Stopwatch.Frequency;
                        if (seconds >= 0.25)
                        {
                            double instant = Math.Max(0, received - stat.LastBytes) / seconds;
                            stat.Speed = stat.Speed <= 0 ? instant : stat.Speed * 0.65 + instant * 0.35;
                            if (stat.Speed < 1) stat.Speed = 0;
                            stat.LastBytes = received;
                            stat.LastTimestamp = now;
                        }
                    }
                }
                else
                {
                    stat.WasDownloading = false;
                    stat.Speed = 0;
                    stat.LastBytes = received;
                    stat.LastTimestamp = now;
                }

                int index;
                if (!rowById.TryGetValue(task.Id, out index))
                {
                    index = _queueGrid.Rows.Add();
                    DataGridViewRow added = _queueGrid.Rows[index];
                    added.Tag = task;
                    added.Cells[QueueUrlColumn].ToolTipText = task.Url;
                    added.Cells[QueueFileColumn].ToolTipText = task.FinalPath;
                }

                DataGridViewRow gridRow = _queueGrid.Rows[index];
                gridRow.Tag = task;

                string sizeText = task.LengthKnown ? HttpUtil.FormatBytes(task.TotalBytes) : "?";
                string speedText = stat.Speed > 0 ? HttpUtil.FormatSpeed(stat.Speed) : "";
                string etaText = "";
                if (stat.Speed > 0 && task.LengthKnown && task.TotalBytes > received)
                {
                    etaText = HttpUtil.FormatEta((task.TotalBytes - received) / stat.Speed);
                }

                SetCell(gridRow, QueueFileColumn, task.FileName);
                SetCell(gridRow, QueueSizeColumn, sizeText);
                SetCell(gridRow, QueueSpeedColumn, speedText);
                SetCell(gridRow, QueueEtaColumn, etaText);
                SetCell(gridRow, QueueStatusColumn, StatusText(task));
                SetCell(gridRow, QueueUrlColumn, task.Url);

                gridRow.Cells[QueueStatusColumn].Style.ForeColor = StatusColor(task.State);
                gridRow.Cells[QueueStatusColumn].ToolTipText =
                    task.State == TaskState.Error ? task.LastError : task.Url;
                gridRow.Cells[QueueFileColumn].ToolTipText = task.FinalPath;

                _queueGrid.InvalidateCell(QueueProgressColumn, index);

                switch (task.State)
                {
                    case TaskState.Downloading:
                    case TaskState.Probing:
                        active++;
                        break;
                    case TaskState.Queued:
                        queued++;
                        break;
                    case TaskState.Paused:
                        paused++;
                        break;
                    case TaskState.Completed:
                        completed++;
                        break;
                    case TaskState.Error:
                        errors++;
                        break;
                }

                if (task.LengthKnown && task.TotalBytes > 0)
                {
                    totalBytes += task.TotalBytes;
                    doneBytes += Math.Min(received, task.TotalBytes);
                }

                if (downloading) speedSum += stat.Speed;
            }

            List<string> deadStats = new List<string>();
            foreach (string key in _rowStats.Keys)
            {
                if (!ids.Contains(key)) deadStats.Add(key);
            }
            foreach (string key in deadStats) _rowStats.Remove(key);

            _overall.Maximum = totalBytes > 0 ? totalBytes : 1;
            _overall.Value = doneBytes;
            _overall.BarColor = errors > 0 && active == 0 ? Theme.Amber : Theme.Green;

            _queueLeft.Text = string.Format("{0} active  /  {1} queued  /  {2} paused  /  {3} done",
                active, queued, paused, completed);

            if (speedSum > 0 && totalBytes > doneBytes)
            {
                _queueRight.Text = HttpUtil.FormatSpeed(speedSum) + "  /  " +
                    HttpUtil.FormatEta((totalBytes - doneBytes) / speedSum) + " left";
            }
            else if (errors > 0 && active == 0)
            {
                _queueRight.Text = errors + " error(s)";
            }
            else if (completed > 0 && active == 0 && queued == 0)
            {
                _queueRight.Text = "All finished";
            }
            else
            {
                _queueRight.Text = "Idle";
            }
        }

        private static long GetReceived(DownloadTask task)
        {
            return Interlocked.Read(ref task.Received);
        }

        private static void SetCell(DataGridViewRow row, int index, string text)
        {
            DataGridViewCell cell = row.Cells[index];
            if (!object.Equals(cell.Value, text))
            {
                cell.Value = text;
            }
        }

        private static string StatusText(DownloadTask task)
        {
            switch (task.State)
            {
                case TaskState.Queued: return "Queued";
                case TaskState.Probing: return "Checking...";
                case TaskState.Downloading:
                    if (task.AcceptRanges && task.Segments != null && task.Segments.Count > 1)
                    {
                        return "Downloading x" + task.Segments.Count;
                    }
                    return "Downloading";
                case TaskState.Paused: return "Paused";
                case TaskState.Completed: return "Saved";
                case TaskState.Error: return "Error";
                default: return task.State.ToString();
            }
        }

        private static Color StatusColor(TaskState state)
        {
            switch (state)
            {
                case TaskState.Completed: return Theme.Green;
                case TaskState.Error: return Theme.Red;
                case TaskState.Paused: return Theme.Amber;
                case TaskState.Downloading:
                case TaskState.Probing: return Theme.Accent;
                default: return Theme.Muted;
            }
        }

        private void QueueGridCellPainting(object sender, DataGridViewCellPaintingEventArgs e)
        {
            if (e.RowIndex < 0 || e.ColumnIndex != QueueProgressColumn) return;
            if (e.Graphics == null) return;

            DataGridViewRow row = _queueGrid.Rows[e.RowIndex];
            DownloadTask task = row.Tag as DownloadTask;
            if (task == null) return;

            e.Paint(e.CellBounds, DataGridViewPaintParts.Background |
                DataGridViewPaintParts.Border | DataGridViewPaintParts.SelectionBackground);

            long received = GetReceived(task);
            double percent = 0;
            if (task.State == TaskState.Completed)
            {
                percent = 1;
            }
            else if (task.LengthKnown && task.TotalBytes > 0)
            {
                percent = (double)received / task.TotalBytes;
            }
            if (percent < 0) percent = 0;
            if (percent > 1) percent = 1;

            Rectangle bar = new Rectangle(
                e.CellBounds.X + 5,
                e.CellBounds.Y + (e.CellBounds.Height - 11) / 2,
                Math.Max(1, e.CellBounds.Width - 10),
                11);

            using (SolidBrush track = new SolidBrush(Theme.Track))
            {
                Theme.FillRounded(e.Graphics, track, bar, 5);
            }

            int fillWidth = (int)Math.Round((bar.Width - 1) * percent);
            if (fillWidth > 0)
            {
                Rectangle fill = new Rectangle(bar.X, bar.Y, Math.Max(2, fillWidth), bar.Height);
                using (SolidBrush brush = new SolidBrush(StatusColor(task.State)))
                {
                    Theme.FillRounded(e.Graphics, brush, fill, 5);
                }
            }

            string text = string.Format("{0:0}%", percent * 100);
            TextRenderer.DrawText(e.Graphics, text, row.Cells[e.ColumnIndex].InheritedStyle.Font,
                e.CellBounds, Theme.Text,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                TextFormatFlags.NoPrefix);

            e.Handled = true;
        }
    }
}
