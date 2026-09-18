using System;
using System.IO;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Threading;
using System.Threading.Tasks;
using System.Diagnostics;
using System.Net.NetworkInformation;

#region WinINet FTP Engine
public class WinInetFtpClient : IDisposable
{
    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr InternetOpen(string lpszAgent, uint dwAccessType, string? lpszProxyName, string? lpszProxyBypass, uint dwFlags);

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr InternetConnect(IntPtr hInternet, string lpszServerName, short nServerPort, string lpszUsername, string lpszPassword, uint dwService, uint dwFlags, IntPtr dwContext);

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FtpOpenFile(IntPtr hConnect, string lpszFileName, uint dwAccess, uint dwFlags, IntPtr dwContext);

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetReadFile(IntPtr hFile, byte[] lpBuffer, int dwNumberOfBytesToRead, out int lpdwNumberOfBytesRead);

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool FtpPutFile(IntPtr hConnect, string lpszLocalFile, string lpszNewFile, uint dwFlags, IntPtr dwContext);

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern IntPtr FtpFindFirstFile(IntPtr hConnect, string? lpszSearchFile, ref WIN32_FIND_DATA lpFindFileData, uint dwFlags, IntPtr dwContext);

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool InternetFindNextFile(IntPtr hFind, ref WIN32_FIND_DATA lpFindFileData);

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool FtpGetCurrentDirectory(IntPtr hConnect, System.Text.StringBuilder lpszCurrentDirectory, ref int lpdwCurrentDirectory);

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool FtpSetCurrentDirectory(IntPtr hConnect, string lpszDirectory);

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool FtpDeleteFile(IntPtr hConnect, string lpszFileName);

    [DllImport("wininet.dll", SetLastError = true, CharSet = CharSet.Auto)]
    private static extern bool FtpCreateDirectory(IntPtr hConnect, string lpszDirectory);

    [DllImport("wininet.dll", SetLastError = true)]
    private static extern bool InternetCloseHandle(IntPtr hInternet);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct WIN32_FIND_DATA
    {
        public uint dwFileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftCreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME ftLastWriteTime;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        public uint dwReserved0;
        public uint dwReserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string cFileName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)]
        public string cAlternateFileName;
    }

    private const uint INTERNET_OPEN_TYPE_DIRECT = 1;
    private const uint INTERNET_SERVICE_FTP = 1;
    private const uint INTERNET_FLAG_PASSIVE = 0x08000000;
    private const uint INTERNET_FLAG_RELOAD = 0x80000000;
    private const uint GENERIC_READ = 0x80000000;
    private const uint FTP_TRANSFER_TYPE_BINARY = 0x00000002;
    private const uint FILE_ATTRIBUTE_DIRECTORY = 0x00000010;

    private IntPtr _hInternet = IntPtr.Zero;
    private IntPtr _hConnect = IntPtr.Zero;

    public bool IsConnected => _hConnect != IntPtr.Zero;

    public bool Connect(string server, string user, string pass, int port = 21, bool passive = true)
    {
        Disconnect();
        _hInternet = InternetOpen("WinInetFtpExplorer", INTERNET_OPEN_TYPE_DIRECT, null, null, 0);
        if (_hInternet == IntPtr.Zero) return false;

        uint flags = passive ? INTERNET_FLAG_PASSIVE : 0;
        _hConnect = InternetConnect(_hInternet, server, (short)port, user, pass, INTERNET_SERVICE_FTP, flags, IntPtr.Zero);
        return _hConnect != IntPtr.Zero;
    }

    public string GetCurrentDirectory()
    {
        if (!IsConnected) return "/";
        var sb = new System.Text.StringBuilder(260);
        int len = sb.Capacity;
        return FtpGetCurrentDirectory(_hConnect, sb, ref len) ? sb.ToString() : "/";
    }

    public bool SetCurrentDirectory(string path)
    {
        if (!IsConnected) return false;
        return FtpSetCurrentDirectory(_hConnect, path);
    }

    public record FtpEntry(string Name, bool IsDirectory, long Size);

    public List<FtpEntry> ListDirectory()
    {
        var entries = new List<FtpEntry>();
        if (!IsConnected) return entries;

        var findData = new WIN32_FIND_DATA();
        IntPtr hFind = FtpFindFirstFile(_hConnect, null, ref findData, INTERNET_FLAG_RELOAD, IntPtr.Zero);

        if (hFind != IntPtr.Zero)
        {
            try
            {
                do
                {
                    bool isDir = (findData.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0;
                    long size = ((long)findData.nFileSizeHigh << 32) | findData.nFileSizeLow;
                    if (findData.cFileName != "." && findData.cFileName != "..")
                    {
                        entries.Add(new FtpEntry(findData.cFileName, isDir, size));
                    }
                } while (InternetFindNextFile(hFind, ref findData));
            }
            finally
            {
                InternetCloseHandle(hFind);
            }
        }
        return entries;
    }

    public bool DownloadFileStream(string remoteFile, string localFile, Action<int>? onBytesRead, CancellationToken token)
    {
        if (!IsConnected) return false;

        IntPtr hFile = FtpOpenFile(_hConnect, remoteFile, GENERIC_READ, FTP_TRANSFER_TYPE_BINARY | INTERNET_FLAG_RELOAD, IntPtr.Zero);
        if (hFile == IntPtr.Zero) return false;

        byte[] buffer = new byte[64 * 1024];
        bool success = false;

        try
        {
            using (var fs = new FileStream(localFile, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                while (!token.IsCancellationRequested)
                {
                    if (!InternetReadFile(hFile, buffer, buffer.Length, out int bytesRead))
                    {
                        break;
                    }

                    if (bytesRead == 0)
                    {
                        success = true;
                        break;
                    }

                    fs.Write(buffer, 0, bytesRead);
                    onBytesRead?.Invoke(bytesRead);
                }
            }
        }
        catch
        {
            success = false;
        }
        finally
        {
            InternetCloseHandle(hFile);
        }

        if (token.IsCancellationRequested || !success)
        {
            try { if (File.Exists(localFile)) File.Delete(localFile); } catch { }
            return false;
        }

        return true;
    }

    public bool UploadFile(string localFile, string remoteFile)
    {
        if (!IsConnected) return false;
        return FtpPutFile(_hConnect, localFile, remoteFile, FTP_TRANSFER_TYPE_BINARY, IntPtr.Zero);
    }

    public bool DeleteRemoteFile(string remoteFile)
    {
        if (!IsConnected) return false;
        return FtpDeleteFile(_hConnect, remoteFile);
    }

    public bool CreateRemoteDirectory(string dirName)
    {
        if (!IsConnected) return false;
        return FtpCreateDirectory(_hConnect, dirName);
    }

    public void Disconnect()
    {
        if (_hConnect != IntPtr.Zero) { InternetCloseHandle(_hConnect); _hConnect = IntPtr.Zero; }
        if (_hInternet != IntPtr.Zero) { InternetCloseHandle(_hInternet); _hInternet = IntPtr.Zero; }
    }

    public void Dispose() => Disconnect();
}
#endregion

#region Custom Rounded Button
public class RoundedButton : Button
{
    public int CornerRadius { get; set; } = 12;
    public Color NormalColor { get; set; } = Color.FromArgb(46, 125, 50);
    public Color HoverColor { get; set; } = Color.FromArgb(56, 142, 60);
    public Color ClickColor { get; set; } = Color.FromArgb(27, 94, 32);
    public Color DisabledColor { get; set; } = Color.FromArgb(120, 125, 120);

    private bool _isHovered = false;
    private bool _isPressed = false;

    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 9.5f, FontStyle.Bold);
        Cursor = Cursors.Hand;
        Size = new Size(110, 36);
        SetStyle(ControlStyles.Selectable, false);
    }

    protected override bool ShowFocusCues => false;
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _isHovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _isHovered = false; _isPressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); _isPressed = true; Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); _isPressed = false; Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var rect = new Rectangle(0, 0, Width, Height);

        Color fill;
        Color textCol = ForeColor;

        if (!Enabled)
        {
            fill = DisabledColor;
            textCol = Color.FromArgb(180, 180, 180);
        }
        else
        {
            fill = _isPressed ? ClickColor : (_isHovered ? HoverColor : NormalColor);
        }

        using (var path = GetRoundPath(rect, CornerRadius))
        using (var brush = new SolidBrush(fill))
        {
            e.Graphics.FillPath(brush, path);
        }

        TextRenderer.DrawText(e.Graphics, Text, Font, rect, textCol, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    private GraphicsPath GetRoundPath(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
#endregion

#region GUI Application
static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        try
        {
            Application.Run(new FtpExplorerForm());
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.ToString(), "Application Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}

public class FtpExplorerForm : Form
{
    private WinInetFtpClient _ftp = new WinInetFtpClient();
    private CancellationTokenSource? _cts;

    // Network Speed Tracker (Title Bar)
    private System.Windows.Forms.Timer _speedMonitorTimer = new();
    private long _lastReceivedBytes = 0;
    private Stopwatch _speedWatch = new();

    // Progress State (Bottom Strip)
    private long _totalBytesExpected = 0;
    private long _totalBytesDownloaded = 0;

    private readonly Color _darkHeader = Color.FromArgb(33, 37, 41);
    private readonly Color _surroundGray = Color.FromArgb(62, 68, 74);
    private readonly Color _greenPrimary = Color.FromArgb(46, 125, 50);
    private readonly Color _greenHover = Color.FromArgb(56, 142, 60);
    private readonly Color _redPrimary = Color.FromArgb(198, 40, 40);
    private readonly Color _redHover = Color.FromArgb(229, 57, 53);

    // Top Controls
    private TextBox txtServer = new() { Text = "cdimage.debian.org", Width = 150 };
    private TextBox txtUser = new() { Text = "anonymous", Width = 90 };
    private TextBox txtPass = new() { Text = "", UseSystemPasswordChar = true, Width = 90 };
    private TextBox txtPort = new() { Text = "21", Width = 40 };
    private CheckBox chkPassive = new() { Text = "Passive", Checked = true, ForeColor = Color.White, AutoSize = true, Margin = new Padding(6, 8, 4, 0) };
    private RoundedButton btnConnect = new() { Text = "Connect", Width = 110, Height = 34 };

    // Local Controls
    private ListView lvLocal = new();
    private TextBox txtLocalPath = new() { ReadOnly = true, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9.5f) };
    private RoundedButton btnLocalBack = new() { Text = "<", Width = 32, Height = 30, CornerRadius = 8 };
    private RoundedButton btnLocalForward = new() { Text = ">", Width = 32, Height = 30, CornerRadius = 8, Enabled = false };
    private RoundedButton btnLocalUp = new() { Text = "^", Width = 32, Height = 30, CornerRadius = 8 };
    private RoundedButton btnLocalRefresh = new() { Text = "R", Width = 32, Height = 30, CornerRadius = 8 };
    private RoundedButton btnLocalDesktop = new() { Text = "Desk", Width = 52, Height = 30, CornerRadius = 8 };
    private RoundedButton btnLocalNewFolder = new() { Text = "+Dir", Width = 52, Height = 30, CornerRadius = 8 };
    private Stack<string> _localBackStack = new();
    private Stack<string> _localForwardStack = new();
    private string _currentLocalDir;

    // Remote Controls
    private ListView lvRemote = new();
    private TextBox txtRemotePath = new() { ReadOnly = true, Dock = DockStyle.Fill, Font = new Font("Segoe UI", 9.5f) };
    private RoundedButton btnRemoteBack = new() { Text = "<", Width = 32, Height = 30, CornerRadius = 8, Enabled = false };
    private RoundedButton btnRemoteForward = new() { Text = ">", Width = 32, Height = 30, CornerRadius = 8, Enabled = false };
    private RoundedButton btnRemoteUp = new() { Text = "^", Width = 32, Height = 30, CornerRadius = 8, Enabled = false };
    private RoundedButton btnRemoteRefresh = new() { Text = "R", Width = 32, Height = 30, CornerRadius = 8, Enabled = false };
    private RoundedButton btnRemoteNewFolder = new() { Text = "+Dir", Width = 52, Height = 30, CornerRadius = 8, Enabled = false };
    private Stack<string> _remoteBackStack = new();
    private Stack<string> _remoteForwardStack = new();
    private string _currentRemoteDir = "/";

    // Center Action Controls
    private RoundedButton btnUpload = new() { Text = "Upload ->", Width = 120, Height = 42 };
    private RoundedButton btnDownload = new() { Text = "<- Download", Width = 120, Height = 42 };
    private RoundedButton btnStop = new() { Text = "Stop", Width = 120, Height = 40, Enabled = false };

    // Bottom Status Strip with Progress Bar & Percentage
    private StatusStrip statusStrip = new();
    private ToolStripStatusLabel lblStatus = new() { Text = "Ready", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private ToolStripStatusLabel lblPercent = new() { Text = "", AutoSize = true, Font = new Font("Segoe UI", 9.0f, FontStyle.Bold) };
    private ToolStripProgressBar bottomProgressBar = new() { Width = 220, Minimum = 0, Maximum = 100, Value = 0, Visible = false };

    public FtpExplorerForm()
    {
        string? desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop))
        {
            _currentLocalDir = desktop;
        }
        else
        {
            string fallback = @"C:\Users\" + Environment.UserName + @"\Desktop";
            _currentLocalDir = Directory.Exists(fallback) ? fallback : @"C:\";
        }

        Text = "WinINet FTP Explorer";
        Width = 1180;
        Height = 740;
        BackColor = _surroundGray;
        Font = new Font("Segoe UI", 9.5f);
        StartPosition = FormStartPosition.CenterScreen;

        InitializeComponents();
        SetupContextMenus();
        SetupNetworkSpeedTracker();
        NavigateLocal(_currentLocalDir, pushHistory: true);
    }

    private void SetupNetworkSpeedTracker()
    {
        _lastReceivedBytes = GetTotalSystemBytesReceived();
        _speedWatch.Restart();

        _speedMonitorTimer.Interval = 1000;
        _speedMonitorTimer.Tick += (s, e) =>
        {
            long currentBytes = GetTotalSystemBytesReceived();
            double seconds = _speedWatch.ElapsedMilliseconds / 1000.0;
            _speedWatch.Restart();

            long diffBytes = currentBytes - _lastReceivedBytes;
            _lastReceivedBytes = currentBytes;

            if (seconds > 0 && diffBytes >= 0)
            {
                double bytesPerSec = diffBytes / seconds;
                double mbps = (bytesPerSec * 8) / 1_000_000.0;
                double mbPerSec = bytesPerSec / (1024 * 1024);

                if (mbps >= 1.0)
                {
                    Text = $"WinINet FTP Explorer - {mbps:F0} Mbps ({mbPerSec:F1} MB/s)";
                }
                else
                {
                    Text = "WinINet FTP Explorer";
                }
            }
        };
        _speedMonitorTimer.Start();
    }

    private long GetTotalSystemBytesReceived()
    {
        long total = 0;
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                if (ni.OperationalStatus == OperationalStatus.Up &&
                    (ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                     ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet))
                {
                    total += ni.GetIPStatistics().BytesReceived;
                }
            }
        }
        catch { }
        return total;
    }

    private void InitializeComponents()
    {
        // 1. Top Header Bar
        var topBar = new Panel { Dock = DockStyle.Top, Height = 58, BackColor = _darkHeader, Padding = new Padding(12, 12, 12, 0) };
        var topLayout = new FlowLayoutPanel { Dock = DockStyle.Fill, BackColor = Color.Transparent };

        Label CreateHeaderLabel(string text) => new Label
        {
            Text = text,
            ForeColor = Color.FromArgb(235, 235, 235),
            AutoSize = true,
            Margin = new Padding(6, 6, 2, 0)
        };

        btnConnect.NormalColor = _greenPrimary;
        btnConnect.HoverColor = _greenHover;
        btnConnect.Click += async (s, e) => await ToggleConnectionAsync();

        topLayout.Controls.AddRange(new Control[] {
            CreateHeaderLabel("Host:"), txtServer,
            CreateHeaderLabel("User:"), txtUser,
            CreateHeaderLabel("Pass:"), txtPass,
            CreateHeaderLabel("Port:"), txtPort,
            chkPassive,
            btnConnect
        });
        topBar.Controls.Add(topLayout);

        // 2. Main 3-Column Layout
        var tableLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = _surroundGray,
            Padding = new Padding(12, 12, 12, 4)
        };

        tableLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46f));
        tableLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140f));
        tableLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46f));
        tableLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

        // Left Pane
        var pnlLeftCard = CreateCard();
        var localNavPanel = CreateNavPanel("Local", txtLocalPath, btnLocalBack, btnLocalForward, btnLocalUp, btnLocalRefresh, btnLocalDesktop, btnLocalNewFolder);
        SetupListView(lvLocal);
        lvLocal.DoubleClick += LvLocal_DoubleClick;
        lvLocal.KeyDown += LvLocal_KeyDown;

        btnLocalBack.Click += (s, e) => LocalGoBack();
        btnLocalForward.Click += (s, e) => LocalGoForward();
        btnLocalUp.Click += (s, e) => LocalGoUp();
        btnLocalRefresh.Click += (s, e) => NavigateLocal(_currentLocalDir, pushHistory: false);
        btnLocalDesktop.Click += (s, e) => NavigateLocal(_currentLocalDir, pushHistory: true);
        btnLocalNewFolder.Click += (s, e) => CreateLocalFolderPrompt();

        pnlLeftCard.Controls.Add(lvLocal);
        pnlLeftCard.Controls.Add(localNavPanel);
        tableLayout.Controls.Add(pnlLeftCard, 0, 0);

        // Center Column
        var centerActions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Color.Transparent,
            Padding = new Padding(10, 180, 0, 0)
        };

        btnStop.NormalColor = _redPrimary;
        btnStop.HoverColor = _redHover;
        btnStop.Click += (s, e) =>
        {
            _cts?.Cancel();
            SetStatus("Canceling transfer...");
        };

        btnUpload.Click += async (s, e) => await PerformUploadAsync();
        btnDownload.Click += async (s, e) => await PerformDownloadAsync();
        btnDownload.Margin = new Padding(0, 14, 0, 0);
        btnStop.Margin = new Padding(0, 14, 0, 0);

        centerActions.Controls.Add(btnUpload);
        centerActions.Controls.Add(btnDownload);
        centerActions.Controls.Add(btnStop);
        tableLayout.Controls.Add(centerActions, 1, 0);

        // Right Pane
        var pnlRightCard = CreateCard();
        var remoteNavPanel = CreateNavPanel("Remote", txtRemotePath, btnRemoteBack, btnRemoteForward, btnRemoteUp, btnRemoteRefresh, null, btnRemoteNewFolder);
        SetupListView(lvRemote);
        lvRemote.DoubleClick += LvRemote_DoubleClick;
        lvRemote.KeyDown += LvRemote_KeyDown;

        btnRemoteBack.Click += (s, e) => RemoteGoBack();
        btnRemoteForward.Click += (s, e) => RemoteGoForward();
        btnRemoteUp.Click += (s, e) => RemoteGoUp();
        btnRemoteRefresh.Click += (s, e) => NavigateRemote(_currentRemoteDir, pushHistory: false);
        btnRemoteNewFolder.Click += (s, e) => CreateRemoteFolderPrompt();

        pnlRightCard.Controls.Add(lvRemote);
        pnlRightCard.Controls.Add(remoteNavPanel);
        tableLayout.Controls.Add(pnlRightCard, 2, 0);

        // 3. Bottom Strip with Progress Bar
        statusStrip.BackColor = Color.FromArgb(235, 238, 235);
        statusStrip.Items.Add(lblStatus);
        statusStrip.Items.Add(lblPercent);
        statusStrip.Items.Add(bottomProgressBar);

        Controls.Add(tableLayout);
        Controls.Add(statusStrip);
        Controls.Add(topBar);
    }

    private Panel CreateCard()
    {
        return new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Color.White,
            Padding = new Padding(3),
            BorderStyle = BorderStyle.FixedSingle
        };
    }

    private Panel CreateNavPanel(string title, TextBox pathBox, RoundedButton btnBack, RoundedButton btnForward, RoundedButton btnUp, RoundedButton btnRefresh, RoundedButton? btnExtra, RoundedButton btnNewFolder)
    {
        var panel = new Panel { Dock = DockStyle.Top, Height = 42, BackColor = Color.FromArgb(248, 249, 248), Padding = new Padding(4) };

        var lbl = new Label
        {
            Text = title,
            ForeColor = _greenPrimary,
            Font = new Font("Segoe UI", 9.5f, FontStyle.Bold),
            AutoSize = true,
            Margin = new Padding(4, 8, 4, 0)
        };

        var flow = new FlowLayoutPanel { Dock = DockStyle.Left, AutoSize = true, WrapContents = false, BackColor = Color.Transparent };
        flow.Controls.Add(lbl);
        flow.Controls.Add(btnBack);
        flow.Controls.Add(btnForward);
        flow.Controls.Add(btnUp);
        flow.Controls.Add(btnRefresh);
        if (btnExtra != null) flow.Controls.Add(btnExtra);
        flow.Controls.Add(btnNewFolder);

        var pathContainer = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6, 6, 4, 4) };
        pathContainer.Controls.Add(pathBox);

        panel.Controls.Add(pathContainer);
        panel.Controls.Add(flow);
        return panel;
    }

    private void SetupListView(ListView lv)
    {
        lv.Dock = DockStyle.Fill;
        lv.View = View.Details;
        lv.FullRowSelect = true;
        lv.MultiSelect = true;
        lv.BorderStyle = BorderStyle.None;
        lv.BackColor = Color.White;

        lv.Columns.Add("Name", 220);
        lv.Columns.Add("Size", 100, HorizontalAlignment.Right);
        lv.Columns.Add("Type", 80);
    }

    private void SetupContextMenus()
    {
        var localMenu = new ContextMenuStrip();
        var mnuUpload = new ToolStripMenuItem("Upload Selected ->", null, async (s, e) => await PerformUploadAsync());
        var mnuNewLocalFolder = new ToolStripMenuItem("Create New Folder...", null, (s, e) => CreateLocalFolderPrompt());
        var mnuOpenLocal = new ToolStripMenuItem("Open in Windows Explorer", null, (s, e) => Process.Start("explorer.exe", _currentLocalDir));
        var mnuDeleteLocal = new ToolStripMenuItem("Delete Selected", null, (s, e) => DeleteLocalSelected());
        var mnuRefreshLocal = new ToolStripMenuItem("Refresh", null, (s, e) => NavigateLocal(_currentLocalDir, false));
        localMenu.Items.AddRange(new ToolStripItem[] { mnuUpload, new ToolStripSeparator(), mnuNewLocalFolder, mnuOpenLocal, mnuDeleteLocal, mnuRefreshLocal });
        lvLocal.ContextMenuStrip = localMenu;

        var remoteMenu = new ContextMenuStrip();
        var mnuDownload = new ToolStripMenuItem("<- Download Selected", null, async (s, e) => await PerformDownloadAsync());
        var mnuNewFolder = new ToolStripMenuItem("Create New Directory...", null, (s, e) => CreateRemoteFolderPrompt());
        var mnuDeleteRemote = new ToolStripMenuItem("Delete Remote File", null, (s, e) => DeleteRemoteSelected());
        var mnuRefreshRemote = new ToolStripMenuItem("Refresh", null, (s, e) => NavigateRemote(_currentRemoteDir, false));
        remoteMenu.Items.AddRange(new ToolStripItem[] { mnuDownload, new ToolStripSeparator(), mnuNewFolder, mnuDeleteRemote, mnuRefreshRemote });
        lvRemote.ContextMenuStrip = remoteMenu;
    }

    #region Local Operations
    private void NavigateLocal(string dir, bool pushHistory)
    {
        try
        {
            var di = new DirectoryInfo(dir);
            if (!di.Exists) return;

            if (pushHistory && !string.IsNullOrEmpty(_currentLocalDir) && _currentLocalDir != di.FullName)
            {
                _localBackStack.Push(_currentLocalDir);
                _localForwardStack.Clear();
            }

            _currentLocalDir = di.FullName;
            txtLocalPath.Text = _currentLocalDir;

            lvLocal.Items.Clear();
            foreach (var sub in di.GetDirectories())
            {
                var lvi = new ListViewItem(new[] { sub.Name, "", "Folder" }) { Tag = sub.FullName };
                lvi.ForeColor = Color.FromArgb(40, 40, 40);
                lvLocal.Items.Add(lvi);
            }

            foreach (var f in di.GetFiles())
            {
                string sizeStr = FormatFileSize(f.Length);
                var lvi = new ListViewItem(new[] { f.Name, sizeStr, "File" }) { Tag = f.FullName };
                lvLocal.Items.Add(lvi);
            }

            btnLocalBack.Enabled = _localBackStack.Count > 0;
            btnLocalForward.Enabled = _localForwardStack.Count > 0;
            btnLocalUp.Enabled = di.Parent != null;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Error opening folder: " + ex.Message);
        }
    }

    private void LocalGoBack()
    {
        if (_localBackStack.Count == 0) return;
        _localForwardStack.Push(_currentLocalDir);
        NavigateLocal(_localBackStack.Pop(), pushHistory: false);
    }

    private void LocalGoForward()
    {
        if (_localForwardStack.Count == 0) return;
        _localBackStack.Push(_currentLocalDir);
        NavigateLocal(_localForwardStack.Pop(), pushHistory: false);
    }

    private void LocalGoUp()
    {
        var di = new DirectoryInfo(_currentLocalDir);
        if (di.Parent != null) NavigateLocal(di.Parent.FullName, pushHistory: true);
    }

    private void CreateLocalFolderPrompt()
    {
        string folderName = ShowInputDialog("New Folder Name:", "Create Local Folder");
        if (string.IsNullOrWhiteSpace(folderName)) return;

        string fullPath = Path.Combine(_currentLocalDir, folderName);
        try
        {
            if (!Directory.Exists(fullPath))
            {
                Directory.CreateDirectory(fullPath);
                NavigateLocal(_currentLocalDir, false);
                SetStatus($"Folder created: {folderName}");
            }
            else
            {
                MessageBox.Show("A folder with this name already exists.", "Info");
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show("Failed to create folder: " + ex.Message, "Error");
        }
    }

    private void DeleteLocalSelected()
    {
        if (lvLocal.SelectedItems.Count == 0) return;

        string prompt = lvLocal.SelectedItems.Count == 1 
            ? $"Delete '{lvLocal.SelectedItems[0].Text}'?" 
            : $"Delete {lvLocal.SelectedItems.Count} selected items?";

        if (MessageBox.Show(prompt, "Confirm Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
        {
            foreach (ListViewItem item in lvLocal.SelectedItems)
            {
                if (item.Tag is not string path) continue;
                bool isDir = item.SubItems[2].Text == "Folder";

                try
                {
                    if (isDir) Directory.Delete(path, true);
                    else File.Delete(path);
                }
                catch { }
            }

            NavigateLocal(_currentLocalDir, false);
            SetStatus("Local deletion complete.");
        }
    }
    #endregion

    #region Remote Operations
    private void NavigateRemote(string path, bool pushHistory)
    {
        if (!_ftp.IsConnected) return;

        if (path != _currentRemoteDir)
        {
            if (!_ftp.SetCurrentDirectory(path))
            {
                MessageBox.Show("Failed to open remote directory: " + path, "FTP Error");
                return;
            }
        }

        string actualDir = _ftp.GetCurrentDirectory();
        if (pushHistory && !string.IsNullOrEmpty(_currentRemoteDir) && _currentRemoteDir != actualDir)
        {
            _remoteBackStack.Push(_currentRemoteDir);
            _remoteForwardStack.Clear();
        }

        _currentRemoteDir = actualDir;
        txtRemotePath.Text = _currentRemoteDir;

        lvRemote.Items.Clear();
        var entries = _ftp.ListDirectory();
        foreach (var entry in entries)
        {
            string sizeStr = entry.IsDirectory ? "" : FormatFileSize(entry.Size);
            string typeStr = entry.IsDirectory ? "Folder" : "File";
            var item = new ListViewItem(new[] { entry.Name, sizeStr, typeStr });
            item.Tag = entry.Size;
            lvRemote.Items.Add(item);
        }

        btnRemoteBack.Enabled = _remoteBackStack.Count > 0;
        btnRemoteForward.Enabled = _remoteForwardStack.Count > 0;
        btnRemoteUp.Enabled = _currentRemoteDir != "/" && !string.IsNullOrEmpty(_currentRemoteDir);
        btnRemoteRefresh.Enabled = true;
        btnRemoteNewFolder.Enabled = true;
    }

    private void RemoteGoBack()
    {
        if (_remoteBackStack.Count == 0) return;
        _remoteForwardStack.Push(_currentRemoteDir);
        NavigateRemote(_remoteBackStack.Pop(), pushHistory: false);
    }

    private void RemoteGoForward()
    {
        if (_remoteForwardStack.Count == 0) return;
        _remoteBackStack.Push(_currentRemoteDir);
        NavigateRemote(_remoteForwardStack.Pop(), pushHistory: false);
    }

    private void RemoteGoUp()
    {
        NavigateRemote("..", pushHistory: true);
    }

    private void DeleteRemoteSelected()
    {
        if (lvRemote.SelectedItems.Count == 0) return;

        string prompt = lvRemote.SelectedItems.Count == 1 
            ? $"Delete remote file '{lvRemote.SelectedItems[0].Text}'?" 
            : $"Delete {lvRemote.SelectedItems.Count} selected remote files?";

        if (MessageBox.Show(prompt, "Confirm Remote Delete", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
        {
            int deletedCount = 0;
            foreach (ListViewItem item in lvRemote.SelectedItems)
            {
                if (item.SubItems[2].Text == "File")
                {
                    if (_ftp.DeleteRemoteFile(item.Text)) deletedCount++;
                }
            }

            NavigateRemote(_currentRemoteDir, false);
            SetStatus($"Deleted {deletedCount} remote files.");
        }
    }

    private void CreateRemoteFolderPrompt()
    {
        string dirName = ShowInputDialog("New Directory Name:", "Create Remote Directory");
        if (!string.IsNullOrWhiteSpace(dirName))
        {
            if (_ftp.CreateRemoteDirectory(dirName))
            {
                NavigateRemote(_currentRemoteDir, false);
                SetStatus($"Directory created: {dirName}");
            }
            else
            {
                MessageBox.Show("Could not create remote directory. Check server permissions.", "Error");
            }
        }
    }
    #endregion

    #region Async Transfers
    private async Task ToggleConnectionAsync()
    {
        if (_ftp.IsConnected)
        {
            _cts?.Cancel();
            _ftp.Disconnect();
            btnConnect.Text = "Connect";
            btnConnect.NormalColor = _greenPrimary;
            btnConnect.Invalidate();

            lvRemote.Items.Clear();
            txtRemotePath.Text = "(Disconnected)";
            _remoteBackStack.Clear();
            _remoteForwardStack.Clear();
            btnRemoteBack.Enabled = false;
            btnRemoteForward.Enabled = false;
            btnRemoteUp.Enabled = false;
            btnRemoteRefresh.Enabled = false;
            btnRemoteNewFolder.Enabled = false;
            SetStatus("Disconnected from FTP server.");
            return;
        }

        int port = int.TryParse(txtPort.Text, out int p) ? p : 21;
        SetBusy(true, "Connecting to " + txtServer.Text + "...");

        bool ok = await Task.Run(() => _ftp.Connect(txtServer.Text, txtUser.Text, txtPass.Text, port, chkPassive.Checked));

        SetBusy(false);

        if (ok)
        {
            btnConnect.Text = "Disconnect";
            btnConnect.NormalColor = _redPrimary;
            btnConnect.Invalidate();

            _currentRemoteDir = _ftp.GetCurrentDirectory();
            NavigateRemote(_currentRemoteDir, pushHistory: false);
            SetStatus("Connected to " + txtServer.Text);
        }
        else
        {
            int err = Marshal.GetLastWin32Error();
            MessageBox.Show($"Connection failed (Error: {err}). Check host, credentials, or passive mode.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            SetStatus("Connection failed.");
        }
    }

    private void UpdateProgressChunk(int bytes)
    {
        Interlocked.Add(ref _totalBytesDownloaded, bytes);
        if (_totalBytesExpected > 0)
        {
            long downloaded = Interlocked.Read(ref _totalBytesDownloaded);
            int pct = (int)Math.Min(100, (downloaded * 100) / _totalBytesExpected);

            BeginInvoke(new Action(() =>
            {
                bottomProgressBar.Value = pct;
                lblPercent.Text = $"{pct}%";
            }));
        }
    }

    private bool DownloadRemoteDirectoryRecursive(string remoteFolderName, string localTargetDir, CancellationToken token)
    {
        if (token.IsCancellationRequested) return false;
        if (!_ftp.SetCurrentDirectory(remoteFolderName)) return false;

        Directory.CreateDirectory(localTargetDir);
        var entries = _ftp.ListDirectory();
        bool allOk = true;

        foreach (var entry in entries)
        {
            if (token.IsCancellationRequested)
            {
                allOk = false;
                break;
            }

            if (entry.IsDirectory)
            {
                string nextLocal = Path.Combine(localTargetDir, entry.Name);
                if (!DownloadRemoteDirectoryRecursive(entry.Name, nextLocal, token))
                {
                    allOk = false;
                }
            }
            else
            {
                string targetFile = Path.Combine(localTargetDir, entry.Name);
                SetStatus($"Downloading: {entry.Name}");
                if (!_ftp.DownloadFileStream(entry.Name, targetFile, UpdateProgressChunk, token))
                {
                    allOk = false;
                }
            }
        }

        _ftp.SetCurrentDirectory("..");
        return allOk;
    }

    private async Task PerformDownloadAsync()
    {
        if (lvRemote.SelectedItems.Count == 0)
        {
            MessageBox.Show("Please select remote files or folders to download.");
            return;
        }

        var targets = new List<(string Name, bool IsFolder, long Size)>();
        long totalExpected = 0;

        foreach (ListViewItem item in lvRemote.SelectedItems)
        {
            bool isDir = item.SubItems[2].Text == "Folder";
            long sz = (item.Tag is long s) ? s : 0;
            if (!isDir) totalExpected += sz;
            targets.Add((item.Text, isDir, sz));
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _totalBytesExpected = totalExpected;
        _totalBytesDownloaded = 0;

        bottomProgressBar.Value = 0;
        bottomProgressBar.Visible = true;
        lblPercent.Text = totalExpected > 0 ? "0%" : "";

        SetBusy(true, $"Downloading {targets.Count} item(s)...", canCancel: true);

        bool allCompleted = true;

        await Task.Run(() =>
        {
            string startRemoteDir = _ftp.GetCurrentDirectory();

            foreach (var target in targets)
            {
                if (token.IsCancellationRequested)
                {
                    allCompleted = false;
                    break;
                }

                if (target.IsFolder)
                {
                    string targetDir = Path.Combine(_currentLocalDir, target.Name);
                    bool folderOk = DownloadRemoteDirectoryRecursive(target.Name, targetDir, token);
                    _ftp.SetCurrentDirectory(startRemoteDir);

                    if (!folderOk) allCompleted = false;
                }
                else
                {
                    BeginInvoke(new Action(() => SetStatus($"Downloading: {target.Name}")));
                    string destFile = Path.Combine(_currentLocalDir, target.Name);
                    bool fileOk = _ftp.DownloadFileStream(target.Name, destFile, UpdateProgressChunk, token);
                    if (!fileOk) allCompleted = false;
                }
            }
        });

        bottomProgressBar.Visible = false;
        lblPercent.Text = "";

        SetBusy(false);
        NavigateLocal(_currentLocalDir, pushHistory: false);

        if (token.IsCancellationRequested)
        {
            SetStatus("Download canceled by user.");
        }
        else if (allCompleted)
        {
            SetStatus($"Finished downloading {targets.Count} item(s).");
        }
        else
        {
            MessageBox.Show("Downloads completed with errors.", "Notice", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            SetStatus("Download finished with warnings.");
        }
    }

    private async Task PerformUploadAsync()
    {
        if (lvLocal.SelectedItems.Count == 0)
        {
            MessageBox.Show("Please select local files to upload.");
            return;
        }

        var uploadTargets = new List<string>();
        foreach (ListViewItem item in lvLocal.SelectedItems)
        {
            if (item.SubItems[2].Text == "File" && item.Tag is string path && File.Exists(path))
            {
                uploadTargets.Add(path);
            }
        }

        if (uploadTargets.Count == 0)
        {
            MessageBox.Show("Only files can be uploaded at this time.");
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        SetBusy(true, $"Uploading {uploadTargets.Count} file(s)...", canCancel: true);

        int uploadedCount = 0;
        await Task.Run(() =>
        {
            foreach (var localPath in uploadTargets)
            {
                if (token.IsCancellationRequested) break;

                string remoteName = Path.GetFileName(localPath);
                BeginInvoke(new Action(() => SetStatus($"Uploading: {remoteName}")));

                if (!string.IsNullOrEmpty(remoteName))
                {
                    if (_ftp.UploadFile(localPath, remoteName))
                    {
                        uploadedCount++;
                    }
                }
            }
        });

        SetBusy(false);
        NavigateRemote(_currentRemoteDir, pushHistory: false);

        if (token.IsCancellationRequested)
        {
            SetStatus("Upload canceled by user.");
        }
        else
        {
            SetStatus($"Uploaded {uploadedCount} of {uploadTargets.Count} file(s).");
        }
    }
    #endregion

    #region Helpers & Handlers
    private void LvLocal_DoubleClick(object? sender, EventArgs e)
    {
        if (lvLocal.SelectedItems.Count == 1)
        {
            var item = lvLocal.SelectedItems[0];
            if (item.SubItems[2].Text == "Folder" && item.Tag != null)
                NavigateLocal((string)item.Tag, pushHistory: true);
        }
    }

    private async void LvLocal_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && lvLocal.SelectedItems.Count > 0)
        {
            if (lvLocal.SelectedItems.Count == 1 && lvLocal.SelectedItems[0].SubItems[2].Text == "Folder" && lvLocal.SelectedItems[0].Tag != null)
                NavigateLocal((string)lvLocal.SelectedItems[0].Tag, pushHistory: true);
            else
                await PerformUploadAsync();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Back)
        {
            LocalGoUp();
            e.Handled = true;
        }
    }

    private void LvRemote_DoubleClick(object? sender, EventArgs e)
    {
        if (lvRemote.SelectedItems.Count == 1)
        {
            var item = lvRemote.SelectedItems[0];
            if (item.SubItems[2].Text == "Folder")
                NavigateRemote(item.Text, pushHistory: true);
        }
    }

    private async void LvRemote_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter && lvRemote.SelectedItems.Count > 0)
        {
            if (lvRemote.SelectedItems.Count == 1 && lvRemote.SelectedItems[0].SubItems[2].Text == "Folder")
                NavigateRemote(lvRemote.SelectedItems[0].Text, pushHistory: true);
            else
                await PerformDownloadAsync();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Back)
        {
            RemoteGoUp();
            e.Handled = true;
        }
    }

    private void SetBusy(bool busy, string message = "", bool canCancel = false)
    {
        Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        btnUpload.Enabled = !busy;
        btnDownload.Enabled = !busy;
        btnStop.Enabled = busy && canCancel;
        if (!string.IsNullOrEmpty(message)) lblStatus.Text = message;
    }

    private void SetStatus(string text) => lblStatus.Text = text;

    private static string FormatFileSize(long bytes)
    {
        string[] sizes = { "B", "KB", "MB", "GB", "TB" };
        double len = bytes;
        int order = 0;
        while (len >= 1024 && order < sizes.Length - 1)
        {
            order++;
            len /= 1024;
        }
        return $"{len:0.#} {sizes[order]}";
    }

    private static string ShowInputDialog(string text, string caption)
    {
        Form prompt = new Form()
        {
            Width = 360,
            Height = 160,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            Text = caption,
            StartPosition = FormStartPosition.CenterScreen,
            MaximizeBox = false,
            MinimizeBox = false
        };
        Label textLabel = new Label() { Left = 20, Top = 15, Text = text, AutoSize = true };
        TextBox textBox = new TextBox() { Left = 20, Top = 40, Width = 300 };
        Button confirmation = new Button() { Text = "OK", Left = 220, Width = 100, Top = 75, DialogResult = DialogResult.OK };
        confirmation.Click += (sender, e) => prompt.Close();
        prompt.Controls.Add(textLabel);
        prompt.Controls.Add(textBox);
        prompt.Controls.Add(confirmation);
        prompt.AcceptButton = confirmation;

        return prompt.ShowDialog() == DialogResult.OK ? textBox.Text : "";
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _speedMonitorTimer.Stop();
        _cts?.Cancel();
        _ftp.Dispose();
        base.OnFormClosing(e);
    }
    #endregion
}
#endregion