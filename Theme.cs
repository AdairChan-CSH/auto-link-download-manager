using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace LinkDownloader
{
    internal static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(14, 16, 20);
        public static readonly Color Panel = Color.FromArgb(21, 24, 30);
        public static readonly Color PanelAlt = Color.FromArgb(24, 28, 35);
        public static readonly Color Field = Color.FromArgb(12, 14, 18);
        public static readonly Color Border = Color.FromArgb(42, 48, 59);
        public static readonly Color Text = Color.FromArgb(230, 233, 238);
        public static readonly Color Muted = Color.FromArgb(139, 148, 163);
        public static readonly Color Accent = Color.FromArgb(88, 166, 255);
        public static readonly Color AccentDark = Color.FromArgb(31, 111, 201);
        public static readonly Color Green = Color.FromArgb(63, 185, 80);
        public static readonly Color Red = Color.FromArgb(248, 81, 73);
        public static readonly Color Amber = Color.FromArgb(210, 153, 34);
        public static readonly Color Selected = Color.FromArgb(35, 48, 67);
        public static readonly Color Track = Color.FromArgb(36, 41, 51);

        public static Label Label(string text, float size, bool bold, Color color)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.BackColor = Color.Transparent;
            label.ForeColor = color;
            label.Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular);
            label.Margin = new Padding(0);
            return label;
        }

        public static void StyleGrid(DarkGrid grid)
        {
            grid.BackgroundColor = Panel;
            grid.BorderStyle = BorderStyle.None;
            grid.GridColor = Border;
            grid.EnableHeadersVisualStyles = false;
            grid.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.None;
            grid.ColumnHeadersHeight = 30;
            grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = PanelAlt,
                ForeColor = Muted,
                SelectionBackColor = PanelAlt,
                SelectionForeColor = Muted,
                Font = new Font("Segoe UI", 8.25f, FontStyle.Bold),
                Alignment = DataGridViewContentAlignment.MiddleLeft,
                Padding = new Padding(7, 0, 0, 0),
                WrapMode = DataGridViewTriState.False
            };
            grid.DefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = Panel,
                ForeColor = Text,
                SelectionBackColor = Selected,
                SelectionForeColor = Text,
                Font = new Font("Segoe UI", 9f),
                Padding = new Padding(7, 0, 0, 0),
                WrapMode = DataGridViewTriState.False
            };
            grid.AlternatingRowsDefaultCellStyle = new DataGridViewCellStyle
            {
                BackColor = PanelAlt,
                ForeColor = Text,
                SelectionBackColor = Selected,
                SelectionForeColor = Text,
                Font = new Font("Segoe UI", 9f),
                Padding = new Padding(7, 0, 0, 0),
                WrapMode = DataGridViewTriState.False
            };
            grid.RowHeadersVisible = false;
            grid.AllowUserToAddRows = false;
            grid.AllowUserToDeleteRows = false;
            grid.AllowUserToResizeRows = false;
            grid.AllowUserToOrderColumns = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            grid.MultiSelect = true;
            grid.RowTemplate.Height = 28;
            grid.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
            grid.ShowCellToolTips = true;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            grid.Dock = DockStyle.Fill;
        }

        public static GraphicsPath RoundedPath(Rectangle rect, int radius)
        {
            GraphicsPath path = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }
            int d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
            if (d <= 1)
            {
                path.AddRectangle(rect);
                return path;
            }
            path.AddArc(rect.X, rect.Y, d, d, 180, 90);
            path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
            path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
            path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRounded(Graphics g, Brush brush, Rectangle rect, int radius)
        {
            using (GraphicsPath path = RoundedPath(rect, radius))
            {
                g.FillPath(brush, path);
            }
        }

        public static void StyleTextBox(TextBox box)
        {
            box.BackColor = Field;
            box.ForeColor = Text;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.Font = new Font("Segoe UI", 9f);
        }

        public static void StyleNumeric(NumericUpDown box)
        {
            box.BackColor = Field;
            box.ForeColor = Text;
            box.BorderStyle = BorderStyle.FixedSingle;
            box.Font = new Font("Segoe UI", 9f);
        }

        public static void StyleCombo(ComboBox box)
        {
            box.BackColor = Field;
            box.ForeColor = Text;
            box.FlatStyle = FlatStyle.Flat;
            box.Font = new Font("Segoe UI", 9f);
        }
    }

    internal class FlatButton : Button
    {
        public bool Primary { get; set; }

        public FlatButton() : this("", false)
        {
        }

        public FlatButton(string text, bool primary)
        {
            Text = text;
            Primary = primary;
            AutoSize = false;
            Height = 28;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 1;
            UseVisualStyleBackColor = false;
            Cursor = Cursors.Hand;
            Font = new Font("Segoe UI", 8.75f);
            Padding = new Padding(4, 0, 4, 0);
            ApplyColors(false);
        }

        private void ApplyColors(bool hover)
        {
            if (Primary)
            {
                BackColor = hover ? Color.FromArgb(112, 180, 255) : Theme.Accent;
                ForeColor = Color.FromArgb(8, 17, 31);
                FlatAppearance.BorderColor = Theme.Accent;
                FlatAppearance.MouseOverBackColor = Color.FromArgb(112, 180, 255);
                FlatAppearance.MouseDownBackColor = Theme.AccentDark;
            }
            else
            {
                BackColor = hover ? Color.FromArgb(35, 41, 51) : Theme.PanelAlt;
                ForeColor = Theme.Text;
                FlatAppearance.BorderColor = Theme.Border;
                FlatAppearance.MouseOverBackColor = Color.FromArgb(35, 41, 51);
                FlatAppearance.MouseDownBackColor = Color.FromArgb(45, 53, 66);
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            ApplyColors(true);
            base.OnMouseEnter(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            ApplyColors(false);
            base.OnMouseLeave(e);
        }
    }

    internal class DarkGrid : DataGridView
    {
        public DarkGrid()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }
    }

    internal class ProgressBarEx : Control
    {
        private double _value;
        private double _maximum = 1.0;

        public Color BarColor { get; set; }

        public ProgressBarEx()
        {
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            BarColor = Theme.Accent;
            Height = 14;
        }

        public double Maximum
        {
            get { return _maximum; }
            set
            {
                _maximum = value <= 0 ? 1 : value;
                Invalidate();
            }
        }

        public double Value
        {
            get { return _value; }
            set
            {
                _value = value < 0 ? 0 : value;
                Invalidate();
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Bg);

            Rectangle track = new Rectangle(0, Math.Max(0, (Height - 12) / 2), Math.Max(1, Width - 1), 12);
            using (SolidBrush brush = new SolidBrush(Theme.Track))
            {
                Theme.FillRounded(g, brush, track, 6);
            }

            double pct = _maximum > 0 ? _value / _maximum : 0;
            if (pct < 0) pct = 0;
            if (pct > 1) pct = 1;
            int width = (int)Math.Round((track.Width - 1) * pct);
            if (width > 0)
            {
                Rectangle fill = new Rectangle(track.X, track.Y, Math.Max(2, width), track.Height);
                using (SolidBrush brush = new SolidBrush(BarColor))
                {
                    Theme.FillRounded(g, brush, fill, 6);
                }
            }
        }
    }
}
