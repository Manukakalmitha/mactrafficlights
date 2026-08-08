using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MacTrafficLights
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            NativeMethods.MakeProcessDpiAware();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new OverlayManagerApplicationContext());
        }
    }

    internal struct WindowIdentity
    {
        public uint ProcessId;
        public string ProcessName;
        public string ClassName;
    }

    internal struct ManagedWindowCandidate
    {
        public IntPtr Window;
        public Rectangle Frame;
        public uint Dpi;
        public string ProcessName;
        public string ClassName;
        public bool Active;
        public bool OverlayAllowed;
        public bool MicroMaskMode;
        public string PickMode;
    }

    internal sealed class OverlayManagerApplicationContext : ApplicationContext
    {
        private const string Version = "v4.0";
        private const int DefaultDotDiameter = 13;
        private const int MinDotDiameter = 10;
        private const int MaxDotDiameter = 22;
        private const int ManagerPollIntervalMs = 90;
        private const int MaxOverlayWindows = 16;

        private readonly Timer _timer;
        private readonly NotifyIcon _tray;
        private readonly ContextMenuStrip _trayMenu;
        private readonly Dictionary<IntPtr, WindowOverlayForm> _overlays;
        private readonly Dictionary<IntPtr, WindowIdentity> _identityCache;
        private readonly HashSet<string> _excludedProcesses;
        private readonly HashSet<string> _microMaskProcesses;
        private readonly uint _ownProcessId;

        private bool _enabled;
        private bool _showSymbolsAlways;
        private int _offsetX;
        private int _offsetY;
        private int _dotDiameter = DefaultDotDiameter;
        private bool _refreshing;
        private int _lastVisibleOverlayCount;
        private string _lastStatus = "starting";

        public OverlayManagerApplicationContext()
        {
            _ownProcessId = (uint)Process.GetCurrentProcess().Id;
            _overlays = new Dictionary<IntPtr, WindowOverlayForm>();
            _identityCache = new Dictionary<IntPtr, WindowIdentity>();
            _excludedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _microMaskProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            SettingsStore.Load(out _enabled, out _showSymbolsAlways, _excludedProcesses, _microMaskProcesses, out _offsetX, out _offsetY, out _dotDiameter);

            _trayMenu = BuildTrayMenu();
            _tray = new NotifyIcon();
            _tray.Text = "Mac Traffic Lights " + Version;
            _tray.Icon = CreateTrafficLightIcon();
            _tray.Visible = true;
            _tray.ContextMenuStrip = _trayMenu;
            _tray.DoubleClick += delegate { ToggleEnabled(); };

            _timer = new Timer();
            _timer.Interval = ManagerPollIntervalMs;
            _timer.Tick += delegate { RefreshOverlays(); };
            _timer.Start();

            RefreshOverlays();
        }

        protected override void ExitThreadCore()
        {
            _timer.Stop();
            _timer.Dispose();

            DisposeAllOverlays();

            _tray.Visible = false;
            _tray.Dispose();
            _trayMenu.Dispose();

            base.ExitThreadCore();
        }

        private ContextMenuStrip BuildTrayMenu()
        {
            ContextMenuStrip menu = new ContextMenuStrip();

            ToolStripMenuItem enabledItem = new ToolStripMenuItem("Enabled");
            enabledItem.Checked = _enabled;
            enabledItem.CheckOnClick = true;
            enabledItem.Click += delegate
            {
                _enabled = enabledItem.Checked;
                SaveSettings();
                if (_enabled) RefreshOverlays();
                else DisposeAllOverlays();
            };
            menu.Items.Add(enabledItem);

            ToolStripMenuItem startupItem = new ToolStripMenuItem("Launch with Windows");
            startupItem.Checked = StartupManager.IsEnabled();
            startupItem.CheckOnClick = true;
            startupItem.Click += delegate
            {
                if (startupItem.Checked) StartupManager.Enable();
                else StartupManager.Disable();
                startupItem.Checked = StartupManager.IsEnabled();
            };
            menu.Items.Add(startupItem);

            ToolStripMenuItem symbolsItem = new ToolStripMenuItem("Always show symbols");
            symbolsItem.Checked = _showSymbolsAlways;
            symbolsItem.CheckOnClick = true;
            symbolsItem.Click += delegate
            {
                _showSymbolsAlways = symbolsItem.Checked;
                SaveSettings();
                InvalidateAllOverlays();
            };
            menu.Items.Add(symbolsItem);

            ToolStripMenuItem sizeMenu = new ToolStripMenuItem("Button size");
            AddButtonSizeItem(sizeMenu, "Tiny", 11);
            AddButtonSizeItem(sizeMenu, "Small", 13);
            AddButtonSizeItem(sizeMenu, "Medium", 15);
            AddButtonSizeItem(sizeMenu, "Large", 18);
            sizeMenu.DropDownOpening += delegate { RefreshButtonSizeMenu(sizeMenu); };
            RefreshButtonSizeMenu(sizeMenu);
            menu.Items.Add(sizeMenu);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem rescan = new ToolStripMenuItem("Re-scan windows / force topmost");
            rescan.Click += delegate { RefreshOverlays(); };
            menu.Items.Add(rescan);

            ToolStripMenuItem diagnostics = new ToolStripMenuItem("Renderer status");
            diagnostics.Click += delegate { ShowRendererStatus(); };
            menu.Items.Add(diagnostics);

            ToolStripMenuItem microMask = new ToolStripMenuItem("Toggle micro-mask for current app");
            microMask.Click += delegate { ToggleMicroMaskCurrentApp(); };
            menu.Items.Add(microMask);

            ToolStripMenuItem alignment = new ToolStripMenuItem("Alignment");
            AddAlignmentItem(alignment, "Move left 2 px", -2, 0);
            AddAlignmentItem(alignment, "Move right 2 px", 2, 0);
            AddAlignmentItem(alignment, "Move up 2 px", 0, -2);
            AddAlignmentItem(alignment, "Move down 2 px", 0, 2);
            alignment.DropDownItems.Add(new ToolStripSeparator());
            ToolStripMenuItem resetAlignment = new ToolStripMenuItem("Reset alignment");
            resetAlignment.Click += delegate
            {
                _offsetX = 0;
                _offsetY = 0;
                SaveSettings();
                RefreshOverlays();
            };
            alignment.DropDownItems.Add(resetAlignment);
            menu.Items.Add(alignment);

            ToolStripMenuItem exclude = new ToolStripMenuItem("Exclude current app");
            exclude.Click += delegate { ExcludeCurrentApp(); };
            menu.Items.Add(exclude);

            ToolStripMenuItem clearExclusions = new ToolStripMenuItem("Clear app exclusions");
            clearExclusions.Click += delegate
            {
                _excludedProcesses.Clear();
                SaveSettings();
                RefreshOverlays();
            };
            menu.Items.Add(clearExclusions);

            ToolStripMenuItem clearMicroMasks = new ToolStripMenuItem("Clear custom micro-mask apps");
            clearMicroMasks.Click += delegate
            {
                _microMaskProcesses.Clear();
                SaveSettings();
                RefreshOverlays();
            };
            menu.Items.Add(clearMicroMasks);

            menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem about = new ToolStripMenuItem("About Mac Traffic Lights " + Version);
            about.Click += delegate
            {
                MessageBox.Show(
                    "Mac Traffic Lights " + Version + "\n\nMulti-window overlay manager: every visible normal window can get its own macOS-style traffic-light controls.\n\nActive windows use brighter dots. Inactive windows use dimmed dots.\n\nNo Windows system files are patched, and no DLL is injected into other apps.",
                    "Mac Traffic Lights " + Version,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            };
            menu.Items.Add(about);

            ToolStripMenuItem exit = new ToolStripMenuItem("Exit");
            exit.Click += delegate { ExitThread(); };
            menu.Items.Add(exit);

            return menu;
        }

        private void AddAlignmentItem(ToolStripMenuItem parent, string text, int dx, int dy)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text);
            item.Click += delegate
            {
                _offsetX += dx;
                _offsetY += dy;
                SaveSettings();
                RefreshOverlays();
            };
            parent.DropDownItems.Add(item);
        }

        private void AddButtonSizeItem(ToolStripMenuItem parent, string text, int diameter)
        {
            ToolStripMenuItem item = new ToolStripMenuItem(text + " (" + diameter + " px)");
            item.Tag = diameter;
            item.Click += delegate
            {
                _dotDiameter = ClampDotDiameter(diameter);
                SaveSettings();
                RefreshOverlays();
            };
            parent.DropDownItems.Add(item);
        }

        private void RefreshButtonSizeMenu(ToolStripMenuItem parent)
        {
            foreach (ToolStripItem rawItem in parent.DropDownItems)
            {
                ToolStripMenuItem item = rawItem as ToolStripMenuItem;
                if (item == null || !(item.Tag is int)) continue;
                item.Checked = ((int)item.Tag) == _dotDiameter;
            }
        }

        private void ToggleEnabled()
        {
            _enabled = !_enabled;
            if (_trayMenu.Items.Count > 0)
            {
                ToolStripMenuItem item = _trayMenu.Items[0] as ToolStripMenuItem;
                if (item != null) item.Checked = _enabled;
            }

            SaveSettings();
            if (_enabled) RefreshOverlays();
            else DisposeAllOverlays();
        }

        private void ToggleMicroMaskCurrentApp()
        {
            WindowIdentity identity;
            if (!TryGetForegroundIdentity(out identity)) return;

            if (IsBuiltInMicroMaskProcess(identity.ProcessName))
            {
                MessageBox.Show(
                    identity.ProcessName + " already uses custom-titlebar micro-mask mode automatically.",
                    "Mac Traffic Lights " + Version,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            if (_microMaskProcesses.Contains(identity.ProcessName))
            {
                _microMaskProcesses.Remove(identity.ProcessName);
            }
            else
            {
                _microMaskProcesses.Add(identity.ProcessName);
            }

            SaveSettings();
            RefreshOverlays();
        }

        private void ExcludeCurrentApp()
        {
            WindowIdentity identity;
            if (!TryGetForegroundIdentity(out identity)) return;
            if (string.IsNullOrEmpty(identity.ProcessName)) return;

            _excludedProcesses.Add(identity.ProcessName);
            SaveSettings();
            RefreshOverlays();
        }

        private void ShowRendererStatus()
        {
            MessageBox.Show(
                "Mac Traffic Lights " + Version + "\n\n" +
                "Mode: multi-window overlay manager\n" +
                "Visible overlays: " + _lastVisibleOverlayCount + "\n" +
                "Tracked overlay objects: " + _overlays.Count + "\n" +
                "Polling: " + ManagerPollIntervalMs + " ms manager scan\n" +
                "Button size: " + _dotDiameter + " px\n" +
                "Alignment offset: " + _offsetX + ", " + _offsetY + "\n" +
                "Custom micro-mask apps: " + _microMaskProcesses.Count + "\n" +
                "Excluded apps: " + _excludedProcesses.Count + "\n" +
                "Last scan: " + _lastStatus,
                "Mac Traffic Lights " + Version + " - Renderer Status",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        }

        private void RefreshOverlays()
        {
            if (_refreshing) return;
            _refreshing = true;

            try
            {
                if (!_enabled)
                {
                    DisposeAllOverlays();
                    _lastVisibleOverlayCount = 0;
                    _lastStatus = "disabled";
                    return;
                }

                int now = Environment.TickCount;
                IntPtr foreground = NormalizeRootWindow(NativeMethods.GetForegroundWindow());
                List<ManagedWindowCandidate> candidates = BuildOrderedCandidates(foreground);
                HashSet<IntPtr> visibleTargets = new HashSet<IntPtr>();
                List<Rectangle> higherFrames = new List<Rectangle>();
                int visibleCount = 0;

                for (int i = 0; i < candidates.Count; i++)
                {
                    ManagedWindowCandidate candidate = candidates[i];
                    Rectangle overlayBounds = CalculateOverlayBounds(candidate);
                    bool covered = IsOverlayCoveredByHigherWindow(overlayBounds, higherFrames);

                    if (candidate.OverlayAllowed &&
                        !covered &&
                        visibleCount < MaxOverlayWindows &&
                        overlayBounds.Width > 0 &&
                        overlayBounds.Height > 0)
                    {
                        WindowOverlayForm overlay;
                        if (!_overlays.TryGetValue(candidate.Window, out overlay) || overlay.IsDisposed)
                        {
                            overlay = new WindowOverlayForm(_trayMenu);
                            _overlays[candidate.Window] = overlay;
                        }

                        overlay.UpdateOverlay(candidate, overlayBounds, _dotDiameter, _showSymbolsAlways, now);
                        visibleTargets.Add(candidate.Window);
                        visibleCount++;
                    }

                    higherFrames.Add(candidate.Frame);
                }

                DisposeMissingOverlays(visibleTargets);
                _lastVisibleOverlayCount = visibleCount;
                _lastStatus = "OK, candidates " + candidates.Count;
            }
            catch (Exception ex)
            {
                _lastStatus = ex.GetType().Name;
            }
            finally
            {
                _refreshing = false;
            }
        }

        private List<ManagedWindowCandidate> BuildOrderedCandidates(IntPtr foreground)
        {
            List<ManagedWindowCandidate> candidates = new List<ManagedWindowCandidate>();
            HashSet<IntPtr> seen = new HashSet<IntPtr>();

            NativeMethods.EnumWindows(delegate(IntPtr rawHwnd, IntPtr lParam)
            {
                IntPtr hwnd = NormalizeRootWindow(rawHwnd);
                if (hwnd == IntPtr.Zero || seen.Contains(hwnd)) return true;
                seen.Add(hwnd);

                ManagedWindowCandidate candidate;
                if (TryBuildCandidate(hwnd, foreground, out candidate))
                {
                    candidates.Add(candidate);
                }

                return true;
            }, IntPtr.Zero);

            return candidates;
        }

        private bool TryBuildCandidate(IntPtr hwnd, IntPtr foreground, out ManagedWindowCandidate candidate)
        {
            candidate = new ManagedWindowCandidate();

            WindowIdentity identity;
            if (!TryGetWindowIdentity(hwnd, out identity)) return false;
            if (identity.ProcessId == _ownProcessId) return false;
            if (!IsSuitableTopLevelWindow(hwnd, identity.ClassName)) return false;

            NativeMethods.RECT frameRect;
            if (!NativeMethods.TryGetExtendedFrameBounds(hwnd, out frameRect)) return false;

            uint dpi = NativeMethods.GetDpiForWindowSafe(hwnd);
            Rectangle frame = ToRectangle(frameRect);
            if (!IsRenderableTargetFrame(frame, dpi)) return false;

            candidate.Window = hwnd;
            candidate.Frame = frame;
            candidate.Dpi = dpi;
            candidate.ProcessName = identity.ProcessName;
            candidate.ClassName = identity.ClassName;
            candidate.Active = hwnd == foreground;
            candidate.OverlayAllowed =
                string.IsNullOrEmpty(identity.ProcessName) ||
                !_excludedProcesses.Contains(identity.ProcessName);
            candidate.MicroMaskMode =
                IsBuiltInMicroMaskProcess(identity.ProcessName) ||
                (!string.IsNullOrEmpty(identity.ProcessName) && _microMaskProcesses.Contains(identity.ProcessName));
            candidate.PickMode = candidate.Active ? "foreground" : "z-order";
            return true;
        }

        private bool TryGetForegroundIdentity(out WindowIdentity identity)
        {
            identity = new WindowIdentity();
            IntPtr foreground = NormalizeRootWindow(NativeMethods.GetForegroundWindow());
            if (foreground == IntPtr.Zero) return false;
            return TryGetWindowIdentity(foreground, out identity);
        }

        private bool TryGetWindowIdentity(IntPtr hwnd, out WindowIdentity identity)
        {
            identity = new WindowIdentity();

            uint processId;
            NativeMethods.GetWindowThreadProcessId(hwnd, out processId);
            if (processId == 0) return false;

            WindowIdentity cached;
            if (_identityCache.TryGetValue(hwnd, out cached) && cached.ProcessId == processId)
            {
                identity = cached;
                return true;
            }

            identity.ProcessId = processId;
            identity.ProcessName = string.Empty;
            identity.ClassName = NativeMethods.GetClassNameSafe(hwnd);

            if (processId != _ownProcessId)
            {
                try
                {
                    using (Process process = Process.GetProcessById((int)processId))
                    {
                        identity.ProcessName = process.ProcessName;
                    }
                }
                catch
                {
                    identity.ProcessName = string.Empty;
                }
            }

            _identityCache[hwnd] = identity;
            return true;
        }

        private bool IsSuitableTopLevelWindow(IntPtr hwnd, string className)
        {
            if (!NativeMethods.IsWindow(hwnd) ||
                !NativeMethods.IsWindowVisible(hwnd) ||
                NativeMethods.IsIconic(hwnd))
            {
                return false;
            }

            if (NativeMethods.IsWindowCloaked(hwnd)) return false;

            long style = NativeMethods.GetWindowStyle(hwnd);
            if ((style & NativeMethods.WS_CHILD) != 0) return false;

            long exStyle = NativeMethods.GetWindowExStyle(hwnd);
            bool toolWindow = (exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0;
            bool appWindow = (exStyle & NativeMethods.WS_EX_APPWINDOW) != 0;
            if (toolWindow && !appWindow) return false;

            if (className == "Progman" ||
                className == "WorkerW" ||
                className == "Shell_TrayWnd" ||
                className == "Shell_SecondaryTrayWnd")
            {
                return false;
            }

            return true;
        }

        private static bool IsRenderableTargetFrame(Rectangle frame, uint dpi)
        {
            float scale = GetScale(dpi);
            if (frame.Width < Scale(260, scale)) return false;
            if (frame.Height < Scale(140, scale)) return false;
            return true;
        }

        private Rectangle CalculateOverlayBounds(ManagedWindowCandidate target)
        {
            float scale = GetScale(target.Dpi);
            bool chromiumFrame = target.ClassName.StartsWith("Chrome_WidgetWin_", StringComparison.OrdinalIgnoreCase);

            int metricWidth = NativeMethods.GetSystemMetricForDpiSafe(NativeMethods.SM_CXSIZE, target.Dpi);
            int metricHeight = NativeMethods.GetSystemMetricForDpiSafe(NativeMethods.SM_CYSIZE, target.Dpi);
            int buttonWidth = Math.Max(metricWidth, Scale(chromiumFrame ? 54 : 46, scale));
            int buttonHeight = Math.Max(metricHeight, Scale(chromiumFrame ? 40 : 32, scale));

            int width = Math.Max(Scale(120, scale), buttonWidth * 3);
            int height = Math.Max(Scale(30, scale), buttonHeight);
            int left = target.Frame.Right - width + _offsetX;
            int top = target.Frame.Top + _offsetY;

            Rectangle virtualScreen = SystemInformation.VirtualScreen;
            if (left + width > virtualScreen.Right) left = virtualScreen.Right - width;
            if (left < virtualScreen.Left) left = virtualScreen.Left;
            if (top < virtualScreen.Top) top = virtualScreen.Top;
            if (top + height > virtualScreen.Bottom) top = virtualScreen.Bottom - height;

            return new Rectangle(left, top, width, height);
        }

        private static bool IsOverlayCoveredByHigherWindow(Rectangle overlayBounds, List<Rectangle> higherFrames)
        {
            if (overlayBounds.Width <= 0 || overlayBounds.Height <= 0) return true;

            for (int i = 0; i < higherFrames.Count; i++)
            {
                Rectangle intersection = Rectangle.Intersect(overlayBounds, higherFrames[i]);
                if (intersection.Width >= 8 && intersection.Height >= 8)
                {
                    return true;
                }
            }

            return false;
        }

        private void DisposeMissingOverlays(HashSet<IntPtr> visibleTargets)
        {
            List<IntPtr> stale = new List<IntPtr>();

            foreach (KeyValuePair<IntPtr, WindowOverlayForm> pair in _overlays)
            {
                if (!visibleTargets.Contains(pair.Key))
                {
                    stale.Add(pair.Key);
                }
            }

            for (int i = 0; i < stale.Count; i++)
            {
                WindowOverlayForm overlay;
                if (_overlays.TryGetValue(stale[i], out overlay))
                {
                    overlay.Close();
                    overlay.Dispose();
                }

                _overlays.Remove(stale[i]);
                _identityCache.Remove(stale[i]);
            }
        }

        private void DisposeAllOverlays()
        {
            foreach (WindowOverlayForm overlay in _overlays.Values)
            {
                overlay.Close();
                overlay.Dispose();
            }

            _overlays.Clear();
            _identityCache.Clear();
        }

        private void InvalidateAllOverlays()
        {
            foreach (WindowOverlayForm overlay in _overlays.Values)
            {
                if (!overlay.IsDisposed) overlay.Invalidate();
            }
        }

        private void SaveSettings()
        {
            SettingsStore.Save(_enabled, _showSymbolsAlways, _excludedProcesses, _microMaskProcesses, _offsetX, _offsetY, _dotDiameter);
        }

        private static bool IsBuiltInMicroMaskProcess(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return false;

            return
                processName.Equals("Spotify", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("Discord", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("DiscordCanary", StringComparison.OrdinalIgnoreCase) ||
                processName.Equals("DiscordPTB", StringComparison.OrdinalIgnoreCase);
        }

        private static IntPtr NormalizeRootWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return IntPtr.Zero;
            IntPtr root = NativeMethods.GetAncestor(hwnd, NativeMethods.GA_ROOT);
            return root == IntPtr.Zero ? hwnd : root;
        }

        private static Rectangle ToRectangle(NativeMethods.RECT rect)
        {
            return Rectangle.FromLTRB(rect.Left, rect.Top, rect.Right, rect.Bottom);
        }

        private static float GetScale(uint dpi)
        {
            return Math.Max(1.0f, dpi / 96.0f);
        }

        private static int Scale(int value, float scale)
        {
            return Math.Max(1, (int)Math.Round(value * scale));
        }

        private static int ClampDotDiameter(int diameter)
        {
            return Math.Max(MinDotDiameter, Math.Min(MaxDotDiameter, diameter));
        }

        private static Icon CreateTrafficLightIcon()
        {
            using (Bitmap bitmap = new Bitmap(32, 32))
            using (Graphics graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Transparent);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Color[] colors = new Color[]
                {
                    Color.FromArgb(255, 95, 87),
                    Color.FromArgb(255, 189, 46),
                    Color.FromArgb(40, 201, 64)
                };

                for (int i = 0; i < 3; i++)
                {
                    using (Brush brush = new SolidBrush(colors[i]))
                    {
                        graphics.FillEllipse(brush, 4 + (i * 9), 12, 7, 7);
                    }
                }

                IntPtr iconHandle = bitmap.GetHicon();
                try
                {
                    using (Icon temporary = Icon.FromHandle(iconHandle))
                    {
                        return (Icon)temporary.Clone();
                    }
                }
                finally
                {
                    NativeMethods.DestroyIcon(iconHandle);
                }
            }
        }
    }

    internal sealed class WindowOverlayForm : Form
    {
        private const int CaptionColorSampleIntervalMs = 1500;
        private const int DragMaskHoldIntervalMs = 160;
        private const int TopmostKeepAliveIntervalMs = 500;
        private const int DefaultDotDiameter = 13;

        private readonly ContextMenuStrip _menu;

        private IntPtr _targetWindow = IntPtr.Zero;
        private Rectangle _lastOverlayBounds = Rectangle.Empty;
        private Rectangle _lastTargetFrame = Rectangle.Empty;
        private uint _targetDpi = 96;
        private string _processName = string.Empty;
        private string _className = string.Empty;
        private string _pickMode = string.Empty;
        private bool _active;
        private bool _allowVisible;
        private bool _microMaskMode;
        private bool _dragMaskMode;
        private bool _showSymbolsAlways;
        private int _dotDiameter = DefaultDotDiameter;
        private int _hoverButton = -1;
        private int _lastColorSampleTick = int.MinValue;
        private int _lastMovementTick = int.MinValue;
        private int _lastTopmostTick = int.MinValue;
        private bool _forceColorSample = true;
        private Color _captionColor = Color.FromArgb(32, 32, 32);

        private struct ColorAccumulator
        {
            public int Count;
            public int R;
            public int G;
            public int B;
        }

        public WindowOverlayForm(ContextMenuStrip menu)
        {
            _menu = menu;

            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "Mac Traffic Lights Overlay";
            TopMost = true;
            DoubleBuffered = true;
            BackColor = _captionColor;
            ClientSize = new Size(140, 40);
        }

        protected override bool ShowWithoutActivation
        {
            get { return true; }
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(_allowVisible && value);
        }

        public void UpdateOverlay(ManagedWindowCandidate candidate, Rectangle overlayBounds, int dotDiameter, bool showSymbolsAlways, int now)
        {
            bool targetChanged = candidate.Window != _targetWindow;
            bool boundsChanged = overlayBounds != _lastOverlayBounds;
            bool dpiChanged = candidate.Dpi != _targetDpi;
            bool activeChanged = candidate.Active != _active;
            bool microMaskChanged = candidate.MicroMaskMode != _microMaskMode;
            bool sizeChanged = dotDiameter != _dotDiameter;
            bool symbolsChanged = showSymbolsAlways != _showSymbolsAlways;

            bool movingTarget =
                !targetChanged &&
                !_lastOverlayBounds.IsEmpty &&
                HasMeaningfulMovement(_lastOverlayBounds, overlayBounds);
            if (movingTarget)
            {
                _lastMovementTick = now;
            }

            bool dragMaskMode =
                movingTarget ||
                (_lastMovementTick != int.MinValue && !HasElapsed(now, _lastMovementTick, DragMaskHoldIntervalMs));
            bool dragMaskChanged = dragMaskMode != _dragMaskMode;

            if (targetChanged)
            {
                _forceColorSample = true;
                _lastMovementTick = int.MinValue;
            }

            _targetWindow = candidate.Window;
            _targetDpi = candidate.Dpi;
            _lastTargetFrame = candidate.Frame;
            _processName = candidate.ProcessName;
            _className = candidate.ClassName;
            _pickMode = candidate.PickMode;
            _active = candidate.Active;
            _microMaskMode = candidate.MicroMaskMode;
            _dragMaskMode = dragMaskMode;
            _showSymbolsAlways = showSymbolsAlways;
            _dotDiameter = Math.Max(10, Math.Min(22, dotDiameter));

            bool shouldSampleColor = !_dragMaskMode && (
                _forceColorSample ||
                targetChanged ||
                dpiChanged ||
                !Visible ||
                HasElapsed(now, _lastColorSampleTick, CaptionColorSampleIntervalMs));
            bool colorChanged = false;

            if (shouldSampleColor)
            {
                Color sampled = SampleCaptionColor(candidate.Frame, overlayBounds);
                colorChanged = sampled.ToArgb() != _captionColor.ToArgb();
                _lastColorSampleTick = now;
                _forceColorSample = false;

                if (colorChanged)
                {
                    _captionColor = sampled;
                    BackColor = _captionColor;
                }
            }

            if (ClientSize.Width != overlayBounds.Width || ClientSize.Height != overlayBounds.Height)
            {
                ClientSize = new Size(overlayBounds.Width, overlayBounds.Height);
                boundsChanged = true;
            }

            if (microMaskChanged || dragMaskChanged || dpiChanged || boundsChanged || sizeChanged)
            {
                ApplyWindowRegion();
            }

            _allowVisible = true;
            if (!Visible)
            {
                Location = overlayBounds.Location;
                Show();
                NativeMethods.ShowWindow(Handle, NativeMethods.SW_SHOWNOACTIVATE);
                boundsChanged = true;
            }

            bool needsTopmostUpdate =
                boundsChanged ||
                targetChanged ||
                dpiChanged ||
                activeChanged ||
                microMaskChanged ||
                dragMaskChanged ||
                HasElapsed(now, _lastTopmostTick, TopmostKeepAliveIntervalMs);

            if (needsTopmostUpdate)
            {
                NativeMethods.SetWindowPos(
                    Handle,
                    NativeMethods.HWND_TOPMOST,
                    overlayBounds.Left,
                    overlayBounds.Top,
                    overlayBounds.Width,
                    overlayBounds.Height,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

                _lastTopmostTick = now;
            }

            _lastOverlayBounds = overlayBounds;

            if (boundsChanged ||
                colorChanged ||
                dpiChanged ||
                activeChanged ||
                microMaskChanged ||
                dragMaskChanged ||
                sizeChanged ||
                symbolsChanged)
            {
                Invalidate();
            }
        }

        private void ApplyWindowRegion()
        {
            Region previous = Region;

            if (!_microMaskMode || _dragMaskMode || ClientSize.Width <= 0 || ClientSize.Height <= 0)
            {
                Region = null;
                if (previous != null) previous.Dispose();
                return;
            }

            float scale = GetScale(_targetDpi);
            int diameter = Scale(_dotDiameter, scale);
            int dotPadding = Scale(1, scale);
            int centerY = ClientSize.Height / 2;
            int[] centers = GetButtonCenters();

            using (GraphicsPath path = new GraphicsPath())
            {
                Rectangle[] masks = GetNativeGlyphMaskBounds();
                for (int i = 0; i < masks.Length; i++)
                {
                    AddRoundedRectangle(path, masks[i], Scale(7, scale));
                }

                for (int i = 0; i < centers.Length; i++)
                {
                    int left = centers[i] - (diameter / 2) - dotPadding;
                    int top = centerY - (diameter / 2) - dotPadding;
                    int size = diameter + (dotPadding * 2);
                    path.AddEllipse(left, top, size, size);
                }

                Region = new Region(path);
            }

            if (previous != null) previous.Dispose();
        }

        private Rectangle[] GetNativeGlyphMaskBounds()
        {
            float scale = GetScale(_targetDpi);
            int[] centers = GetButtonCenters();
            int maskWidth = Math.Max(Scale(21, scale), Scale(_dotDiameter + 8, scale));
            int maskHeight = Math.Max(Scale(23, scale), Scale(_dotDiameter + 10, scale));
            int centerY = (ClientSize.Height / 2) + Scale(1, scale);

            Rectangle[] masks = new Rectangle[centers.Length];
            for (int i = 0; i < centers.Length; i++)
            {
                masks[i] = new Rectangle(
                    centers[i] - (maskWidth / 2),
                    centerY - (maskHeight / 2),
                    maskWidth,
                    maskHeight);
            }

            return masks;
        }

        private static void AddRoundedRectangle(GraphicsPath path, Rectangle rectangle, int radius)
        {
            if (rectangle.Width <= 0 || rectangle.Height <= 0) return;
            radius = Math.Max(1, Math.Min(radius, Math.Min(rectangle.Width, rectangle.Height) / 2));
            int diameter = radius * 2;

            path.StartFigure();
            path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
            path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            e.Graphics.Clear(_captionColor);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            e.Graphics.Clear(_captionColor);

            if (_dragMaskMode)
            {
                return;
            }

            float scale = GetScale(_targetDpi);
            int diameter = Scale(_dotDiameter, scale);
            int centerY = ClientSize.Height / 2;
            int[] centers = GetButtonCenters();

            DrawTrafficLight(e.Graphics, 0, centers[0], centerY, diameter, scale);
            DrawTrafficLight(e.Graphics, 1, centers[1], centerY, diameter, scale);
            DrawTrafficLight(e.Graphics, 2, centers[2], centerY, diameter, scale);
        }

        private void DrawTrafficLight(Graphics graphics, int index, int centerX, int centerY, int diameter, float scale)
        {
            Color[] activeFills = new Color[]
            {
                Color.FromArgb(255, 95, 87),
                Color.FromArgb(255, 189, 46),
                Color.FromArgb(40, 201, 64)
            };

            Color[] activeBorders = new Color[]
            {
                Color.FromArgb(225, 72, 66),
                Color.FromArgb(221, 158, 34),
                Color.FromArgb(30, 172, 49)
            };

            Color fillColor = _active ? activeFills[index] : Blend(activeFills[index], _captionColor, 0.45);
            Color borderColor = _active ? activeBorders[index] : Blend(activeBorders[index], _captionColor, 0.55);

            int left = centerX - (diameter / 2);
            int top = centerY - (diameter / 2);
            Rectangle bounds = new Rectangle(left, top, diameter, diameter);

            using (Brush fill = new SolidBrush(fillColor))
            using (Pen border = new Pen(borderColor, Math.Max(1.0f, scale)))
            {
                graphics.FillEllipse(fill, bounds);
                graphics.DrawEllipse(border, bounds);
            }

            if (_active && (_showSymbolsAlways || _hoverButton >= 0))
            {
                DrawMacSymbol(graphics, index, centerX, centerY, diameter, scale);
            }
        }

        private void DrawMacSymbol(Graphics graphics, int index, int centerX, int centerY, int diameter, float scale)
        {
            int radius = Math.Max(4, diameter / 2);
            int half = Math.Max(2, radius - Scale(5, scale));
            Color glyph = Color.FromArgb(150, 45, 42, 35);

            using (Pen pen = new Pen(glyph, Math.Max(1.0f, 1.05f * scale)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;

                if (index == 0)
                {
                    graphics.DrawLine(pen, centerX - half, centerY - half, centerX + half, centerY + half);
                    graphics.DrawLine(pen, centerX + half, centerY - half, centerX - half, centerY + half);
                }
                else if (index == 1)
                {
                    graphics.DrawLine(pen, centerX - half, centerY, centerX + half, centerY);
                }
                else
                {
                    int arrow = Math.Max(2, half);
                    graphics.DrawLine(pen, centerX - 1, centerY + 1, centerX + arrow, centerY - arrow);
                    graphics.DrawLine(pen, centerX + arrow, centerY - arrow, centerX + arrow, centerY - 1);
                    graphics.DrawLine(pen, centerX + arrow, centerY - arrow, centerX + 1, centerY - arrow);

                    graphics.DrawLine(pen, centerX + 1, centerY - 1, centerX - arrow, centerY + arrow);
                    graphics.DrawLine(pen, centerX - arrow, centerY + arrow, centerX - arrow, centerY + 1);
                    graphics.DrawLine(pen, centerX - arrow, centerY + arrow, centerX - 1, centerY + arrow);
                }
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (_dragMaskMode)
            {
                if (_hoverButton != -1)
                {
                    _hoverButton = -1;
                    Invalidate();
                }

                base.OnMouseMove(e);
                return;
            }

            int button = GetButtonSlot(e.X);
            if (_hoverButton != button)
            {
                _hoverButton = button;
                Invalidate();
            }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (_hoverButton != -1)
            {
                _hoverButton = -1;
                Invalidate();
            }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (!_dragMaskMode) ActivateMacButton(GetButtonSlot(e.X));
                return;
            }

            if (e.Button == MouseButtons.Right)
            {
                _menu.Show(this, e.Location);
                return;
            }

            base.OnMouseDown(e);
        }

        private void ActivateMacButton(int button)
        {
            IntPtr target = _targetWindow;
            if (target == IntPtr.Zero || !NativeMethods.IsWindow(target)) return;

            if (button == 0)
            {
                NativeMethods.PostMessage(target, NativeMethods.WM_SYSCOMMAND, new IntPtr(NativeMethods.SC_CLOSE), IntPtr.Zero);
            }
            else if (button == 1)
            {
                NativeMethods.PostMessage(target, NativeMethods.WM_SYSCOMMAND, new IntPtr(NativeMethods.SC_MINIMIZE), IntPtr.Zero);
            }
            else
            {
                int command = NativeMethods.IsZoomed(target) ? NativeMethods.SC_RESTORE : NativeMethods.SC_MAXIMIZE;
                NativeMethods.PostMessage(target, NativeMethods.WM_SYSCOMMAND, new IntPtr(command), IntPtr.Zero);
            }
        }

        private int[] GetButtonCenters()
        {
            float scale = GetScale(_targetDpi);
            int metricWidth = NativeMethods.GetSystemMetricForDpiSafe(NativeMethods.SM_CXSIZE, _targetDpi);
            int minimumLeft = Scale(_dotDiameter + 6, scale);

            if (metricWidth > 0 && ClientSize.Width >= metricWidth * 3)
            {
                int closeCenter = ClientSize.Width - (metricWidth / 2);
                int maximizeCenter = closeCenter - metricWidth;
                int minimizeCenter = maximizeCenter - metricWidth;

                if (minimizeCenter >= minimumLeft && closeCenter < ClientSize.Width)
                {
                    return new int[]
                    {
                        minimizeCenter,
                        maximizeCenter,
                        closeCenter
                    };
                }
            }

            return new int[]
            {
                ClientSize.Width / 6,
                ClientSize.Width / 2,
                (ClientSize.Width * 5) / 6
            };
        }

        private int GetButtonSlot(int x)
        {
            if (ClientSize.Width <= 0) return 0;
            int[] centers = GetButtonCenters();
            int bestIndex = 0;
            int bestDistance = Math.Abs(x - centers[0]);

            for (int i = 1; i < centers.Length; i++)
            {
                int distance = Math.Abs(x - centers[i]);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private Color SampleCaptionColor(Rectangle frame, Rectangle overlay)
        {
            try
            {
                List<Color> samples = new List<Color>();
                int step = Math.Max(18, overlay.Width / 5);
                int minimumX = frame.Left + 8;
                int maximumSamplesPerRow = 8;
                int[] sampleRows = new int[]
                {
                    Clamp(overlay.Top + (overlay.Height / 2), frame.Top + 4, frame.Bottom - 4),
                    Clamp(overlay.Top + Math.Max(5, overlay.Height / 4), frame.Top + 4, frame.Bottom - 4)
                };

                int scanned = 0;
                for (int x = overlay.Left - step; x >= minimumX && scanned < maximumSamplesPerRow; x -= step)
                {
                    int sampleX = Clamp(x, frame.Left + 8, frame.Right - 8);

                    for (int yIndex = 0; yIndex < sampleRows.Length; yIndex++)
                    {
                        Color sample;
                        if (TryAverageScreenColor(sampleX, sampleRows[yIndex], 5, out sample))
                        {
                            samples.Add(sample);
                        }
                    }

                    scanned++;
                }

                return ChooseRepresentativeCaptionColor(samples);
            }
            catch
            {
                return _captionColor;
            }
        }

        private bool TryAverageScreenColor(int screenX, int screenY, int sampleSize, out Color color)
        {
            color = _captionColor;

            try
            {
                Rectangle virtualScreen = SystemInformation.VirtualScreen;
                if (sampleSize < 1) sampleSize = 1;
                if (sampleSize > virtualScreen.Width || sampleSize > virtualScreen.Height) return false;

                int half = sampleSize / 2;
                int left = Clamp(screenX - half, virtualScreen.Left, virtualScreen.Right - sampleSize);
                int top = Clamp(screenY - half, virtualScreen.Top, virtualScreen.Bottom - sampleSize);

                using (Bitmap bitmap = new Bitmap(sampleSize, sampleSize))
                using (Graphics graphics = Graphics.FromImage(bitmap))
                {
                    graphics.CopyFromScreen(left, top, 0, 0, new Size(sampleSize, sampleSize));

                    int r = 0;
                    int g = 0;
                    int b = 0;
                    int count = 0;

                    for (int y = 0; y < bitmap.Height; y++)
                    {
                        for (int x = 0; x < bitmap.Width; x++)
                        {
                            Color pixel = bitmap.GetPixel(x, y);
                            r += pixel.R;
                            g += pixel.G;
                            b += pixel.B;
                            count++;
                        }
                    }

                    if (count <= 0) return false;
                    color = Color.FromArgb(r / count, g / count, b / count);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        private Color ChooseRepresentativeCaptionColor(List<Color> samples)
        {
            if (samples == null || samples.Count == 0) return _captionColor;

            Dictionary<int, ColorAccumulator> buckets = new Dictionary<int, ColorAccumulator>();
            for (int i = 0; i < samples.Count; i++)
            {
                Color sample = samples[i];
                int key =
                    ((sample.R / 16) << 16) |
                    ((sample.G / 16) << 8) |
                    (sample.B / 16);

                ColorAccumulator accumulator;
                if (!buckets.TryGetValue(key, out accumulator))
                {
                    accumulator = new ColorAccumulator();
                }

                accumulator.Count++;
                accumulator.R += sample.R;
                accumulator.G += sample.G;
                accumulator.B += sample.B;
                buckets[key] = accumulator;
            }

            ColorAccumulator best = new ColorAccumulator();
            int bestDistance = int.MaxValue;
            foreach (ColorAccumulator candidate in buckets.Values)
            {
                Color average = Color.FromArgb(
                    candidate.R / candidate.Count,
                    candidate.G / candidate.Count,
                    candidate.B / candidate.Count);
                int distance = ColorDistanceSquared(average, _captionColor);

                if (candidate.Count > best.Count ||
                    (candidate.Count == best.Count && distance < bestDistance))
                {
                    best = candidate;
                    bestDistance = distance;
                }
            }

            if (best.Count >= 2)
            {
                return Color.FromArgb(best.R / best.Count, best.G / best.Count, best.B / best.Count);
            }

            return MedianColor(samples);
        }

        private static Color MedianColor(List<Color> samples)
        {
            int[] reds = new int[samples.Count];
            int[] greens = new int[samples.Count];
            int[] blues = new int[samples.Count];

            for (int i = 0; i < samples.Count; i++)
            {
                reds[i] = samples[i].R;
                greens[i] = samples[i].G;
                blues[i] = samples[i].B;
            }

            Array.Sort(reds);
            Array.Sort(greens);
            Array.Sort(blues);

            int middle = samples.Count / 2;
            return Color.FromArgb(reds[middle], greens[middle], blues[middle]);
        }

        private static int ColorDistanceSquared(Color a, Color b)
        {
            int dr = a.R - b.R;
            int dg = a.G - b.G;
            int db = a.B - b.B;
            return (dr * dr) + (dg * dg) + (db * db);
        }

        private static Color Blend(Color foreground, Color background, double foregroundAmount)
        {
            foregroundAmount = Math.Max(0.0, Math.Min(1.0, foregroundAmount));
            double backgroundAmount = 1.0 - foregroundAmount;

            return Color.FromArgb(
                Clamp((int)Math.Round((foreground.R * foregroundAmount) + (background.R * backgroundAmount)), 0, 255),
                Clamp((int)Math.Round((foreground.G * foregroundAmount) + (background.G * backgroundAmount)), 0, 255),
                Clamp((int)Math.Round((foreground.B * foregroundAmount) + (background.B * backgroundAmount)), 0, 255));
        }

        private static bool HasMeaningfulMovement(Rectangle oldBounds, Rectangle newBounds)
        {
            if (oldBounds.IsEmpty || newBounds.IsEmpty) return false;
            int dx = Math.Abs(oldBounds.Left - newBounds.Left);
            int dy = Math.Abs(oldBounds.Top - newBounds.Top);
            return dx >= 2 || dy >= 2;
        }

        private static bool HasElapsed(int now, int then, int milliseconds)
        {
            return then == int.MinValue || unchecked(now - then) >= milliseconds;
        }

        private static float GetScale(uint dpi)
        {
            return Math.Max(1.0f, dpi / 96.0f);
        }

        private static int Scale(int value, float scale)
        {
            return Math.Max(1, (int)Math.Round(value * scale));
        }

        private static int Clamp(int value, int minimum, int maximum)
        {
            if (maximum < minimum) return minimum;
            if (value < minimum) return minimum;
            if (value > maximum) return maximum;
            return value;
        }
    }

    internal static class SettingsStore
    {
        private static readonly string SettingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "MacTrafficLightsV4");

        private static readonly string SettingsPath = Path.Combine(SettingsFolder, "settings.ini");

        public static void Load(
            out bool enabled,
            out bool showSymbolsAlways,
            HashSet<string> exclusions,
            HashSet<string> microMaskProcesses,
            out int offsetX,
            out int offsetY,
            out int dotDiameter)
        {
            enabled = true;
            showSymbolsAlways = false;
            offsetX = 0;
            offsetY = 0;
            dotDiameter = 13;

            try
            {
                if (!File.Exists(SettingsPath)) return;

                string[] lines = File.ReadAllLines(SettingsPath);
                foreach (string raw in lines)
                {
                    string line = raw.Trim();
                    int split = line.IndexOf('=');
                    if (split <= 0) continue;

                    string key = line.Substring(0, split).Trim();
                    string value = line.Substring(split + 1).Trim();
                    bool parsedBool;
                    int parsedInt;

                    if (key.Equals("Enabled", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out parsedBool))
                    {
                        enabled = parsedBool;
                    }
                    else if (key.Equals("ShowSymbolsAlways", StringComparison.OrdinalIgnoreCase) && bool.TryParse(value, out parsedBool))
                    {
                        showSymbolsAlways = parsedBool;
                    }
                    else if (key.Equals("OffsetX", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out parsedInt))
                    {
                        offsetX = Math.Max(-200, Math.Min(200, parsedInt));
                    }
                    else if (key.Equals("OffsetY", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out parsedInt))
                    {
                        offsetY = Math.Max(-200, Math.Min(200, parsedInt));
                    }
                    else if (key.Equals("DotDiameter", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out parsedInt))
                    {
                        dotDiameter = Math.Max(10, Math.Min(22, parsedInt));
                    }
                    else if (key.Equals("ExcludedProcesses", StringComparison.OrdinalIgnoreCase))
                    {
                        AddNames(exclusions, value);
                    }
                    else if (key.Equals("MicroMaskProcesses", StringComparison.OrdinalIgnoreCase) ||
                             key.Equals("DotOnlyProcesses", StringComparison.OrdinalIgnoreCase))
                    {
                        AddNames(microMaskProcesses, value);
                    }
                }
            }
            catch
            {
            }
        }

        public static void Save(bool enabled, bool showSymbolsAlways, HashSet<string> exclusions, HashSet<string> microMaskProcesses, int offsetX, int offsetY, int dotDiameter)
        {
            try
            {
                Directory.CreateDirectory(SettingsFolder);

                string[] excludedNames = ToSortedArray(exclusions);
                string[] microMaskNames = ToSortedArray(microMaskProcesses);

                File.WriteAllLines(SettingsPath, new string[]
                {
                    "Enabled=" + enabled,
                    "ShowSymbolsAlways=" + showSymbolsAlways,
                    "OffsetX=" + offsetX,
                    "OffsetY=" + offsetY,
                    "DotDiameter=" + Math.Max(10, Math.Min(22, dotDiameter)),
                    "ExcludedProcesses=" + string.Join(";", excludedNames),
                    "MicroMaskProcesses=" + string.Join(";", microMaskNames)
                });
            }
            catch
            {
            }
        }

        private static void AddNames(HashSet<string> target, string value)
        {
            string[] names = value.Split(new char[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < names.Length; i++)
            {
                string name = names[i].Trim();
                if (!string.IsNullOrEmpty(name)) target.Add(name);
            }
        }

        private static string[] ToSortedArray(HashSet<string> names)
        {
            string[] values = new string[names.Count];
            names.CopyTo(values);
            Array.Sort(values, StringComparer.OrdinalIgnoreCase);
            return values;
        }
    }

    internal static class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "MacTrafficLights";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    object raw = key.GetValue(ValueName);
                    if (raw == null) return false;

                    string current = Convert.ToString(raw).Trim();
                    string executable = Application.ExecutablePath;
                    string quotedExecutable = "\"" + executable + "\"";
                    return string.Equals(current, executable, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(current, quotedExecutable, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch
            {
                return false;
            }
        }

        public static void Enable()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key != null)
                    {
                        key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                    }
                }
            }
            catch
            {
                MessageBox.Show("Windows would not let Mac Traffic Lights add itself to startup.", "Mac Traffic Lights");
            }
        }

        public static void Disable()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key != null) key.DeleteValue(ValueName, false);
                }
            }
            catch
            {
            }
        }
    }

    internal static class NativeMethods
    {
        public const int WS_EX_TOOLWINDOW = 0x00000080;
        public const int WS_EX_APPWINDOW = 0x00040000;
        public const int WS_EX_NOACTIVATE = 0x08000000;
        public const long WS_CHILD = 0x40000000L;
        public const int GWL_STYLE = -16;
        public const int GWL_EXSTYLE = -20;
        public const uint GA_ROOT = 2;
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        public const int DWMWA_CLOAKED = 14;
        public const int WM_SYSCOMMAND = 0x0112;
        public const int SC_MINIMIZE = 0xF020;
        public const int SC_MAXIMIZE = 0xF030;
        public const int SC_CLOSE = 0xF060;
        public const int SC_RESTORE = 0xF120;
        public const int SW_SHOWNOACTIVATE = 4;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public const int SM_CXSIZE = 30;
        public const int SM_CYSIZE = 31;

        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        public delegate bool EnumWindowsDelegate(IntPtr hwnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsDelegate callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hwnd);

        [DllImport("user32.dll")]
        public static extern bool IsZoomed(IntPtr hwnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hwnd, int command);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr hwnd, StringBuilder className, int maxCount);

        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        private static extern int GetWindowLong32(IntPtr hwnd, int index);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hwnd, int index);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out RECT value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetricsForDpi(int index, uint dpi);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int index);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", EntryPoint = "SetProcessDpiAwarenessContext")]
        private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr icon);

        public static void MakeProcessDpiAware()
        {
            try
            {
                SetProcessDpiAwarenessContext(new IntPtr(-4));
            }
            catch (EntryPointNotFoundException)
            {
                try { SetProcessDPIAware(); }
                catch { }
            }
        }

        public static uint GetDpiForWindowSafe(IntPtr hwnd)
        {
            try
            {
                uint dpi = GetDpiForWindow(hwnd);
                return dpi == 0 ? 96u : dpi;
            }
            catch (EntryPointNotFoundException)
            {
                return 96;
            }
        }

        public static int GetSystemMetricForDpiSafe(int index, uint dpi)
        {
            try
            {
                return GetSystemMetricsForDpi(index, dpi);
            }
            catch (EntryPointNotFoundException)
            {
                int value = GetSystemMetrics(index);
                return (int)Math.Round(value * (dpi / 96.0));
            }
        }

        public static bool TryGetExtendedFrameBounds(IntPtr hwnd, out RECT rect)
        {
            try
            {
                int result = DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out rect, Marshal.SizeOf(typeof(RECT)));
                if (result == 0 && rect.Right > rect.Left && rect.Bottom > rect.Top) return true;
            }
            catch
            {
            }

            return GetWindowRect(hwnd, out rect);
        }

        public static bool IsWindowCloaked(IntPtr hwnd)
        {
            try
            {
                int value;
                int result = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out value, Marshal.SizeOf(typeof(int)));
                return result == 0 && value != 0;
            }
            catch
            {
                return false;
            }
        }

        public static long GetWindowStyle(IntPtr hwnd)
        {
            return GetWindowLongSafe(hwnd, GWL_STYLE);
        }

        public static long GetWindowExStyle(IntPtr hwnd)
        {
            return GetWindowLongSafe(hwnd, GWL_EXSTYLE);
        }

        private static long GetWindowLongSafe(IntPtr hwnd, int index)
        {
            if (IntPtr.Size == 8) return GetWindowLongPtr64(hwnd, index).ToInt64();
            return GetWindowLong32(hwnd, index);
        }

        public static string GetClassNameSafe(IntPtr hwnd)
        {
            StringBuilder name = new StringBuilder(256);
            GetClassName(hwnd, name, name.Capacity);
            return name.ToString();
        }
    }
}
