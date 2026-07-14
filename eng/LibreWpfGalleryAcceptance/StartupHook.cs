using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.ProGPU;
using System.Windows.Media.ProGPU.Platform;
using System.Windows.Threading;
using ProGPU.Wpf.Interop;

public static class StartupHook
{
    public static void Initialize()
    {
        GalleryAcceptanceLog.Initialize();
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            GalleryAcceptanceLog.RecordFailure("AppDomain unhandled exception", args.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            GalleryAcceptanceLog.RecordFailure("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        new Thread(WaitForApplication)
        {
            IsBackground = true,
            Name = "LibreWPF Gallery acceptance bootstrap"
        }.Start();
    }

    private static void WaitForApplication()
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (Application.Current is { } application)
            {
                application.Dispatcher.BeginInvoke(
                    DispatcherPriority.ApplicationIdle,
                    new Action(() => new GalleryAcceptanceScenario(application).Start()));
                return;
            }

            Thread.Sleep(25);
        }

        GalleryAcceptanceLog.RecordFailure("Application.Current did not become available within 30 seconds.");
        Environment.Exit(1);
    }
}

internal sealed class GalleryAcceptanceScenario
{
    private const int ExpectedNavigationTargetCount = 52;
    private const double RequestedWindowLeft = -240;
    private const double RequestedWindowTop = 120;
    private static readonly TimeSpan ScenarioTimeout = TimeSpan.FromSeconds(75);

    private readonly Application _application;
    private readonly DispatcherTimer _timer;
    private readonly DateTime _deadline;
    private readonly List<NavigationTarget> _targets = new();
    private Window? _window;
    private TreeView? _navigationTree;
    private Frame? _frame;
    private ComboBox? _openComboBox;
    private ContextMenu? _contextMenu;
    private MenuItem? _nestedMenu;
    private MenuItem? _nestedCommand;
    private TextBox? _selectedTextBox;
    private int _targetIndex;
    private int _navigatedCount;
    private int _stateTicks;
    private bool _inputExercised;
    private bool _comboBoxExercised;
    private bool _contextMenuExercised;
    private bool _textSelectionInputExercised;
    private bool _textSelectionExercised;
    private bool _nestedCommandInvoked;
    private bool _nestedCommandMouseDown;
    private bool _nestedCommandMouseUp;
    private Point _nestedCommandOwnerPoint;
    private ScenarioState _state;

    public GalleryAcceptanceScenario(Application application)
    {
        _application = application;
        _deadline = DateTime.UtcNow + ScenarioTimeout;
        _timer = new DispatcherTimer(DispatcherPriority.ApplicationIdle, application.Dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(125)
        };
        _timer.Tick += OnTick;
    }

    public void Start()
    {
        _application.DispatcherUnhandledException += OnDispatcherUnhandledException;
        GalleryAcceptanceLog.Write("Starting unchanged WPFGallery acceptance scenario.");
        _timer.Start();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs args)
    {
        args.Handled = true;
        Fail("Dispatcher unhandled exception", args.Exception);
    }

    private void OnTick(object? sender, EventArgs args)
    {
        try
        {
            if (DateTime.UtcNow >= _deadline)
            {
                Fail($"Scenario exceeded {ScenarioTimeout.TotalSeconds:0} seconds in state {_state}.");
                return;
            }

            _stateTicks++;
            switch (_state)
            {
                case ScenarioState.WaitingForWindow:
                    WaitForWindow();
                    break;
                case ScenarioState.WaitingForMovedFrame:
                    WaitForMovedFrame();
                    break;
                case ScenarioState.DiscoveringNavigation:
                    DiscoverNavigation();
                    break;
                case ScenarioState.Navigating:
                    NavigateNext();
                    break;
                case ScenarioState.WaitingForNavigation:
                    VerifyNavigation();
                    break;
                case ScenarioState.WaitingForTextSelectionRender:
                    VerifyTextSelectionRender();
                    break;
                case ScenarioState.OpeningComboBox:
                    OpenComboBox();
                    break;
                case ScenarioState.WaitingForComboBoxOpen:
                    VerifyComboBoxOpen();
                    break;
                case ScenarioState.WaitingForComboBoxClose:
                    VerifyComboBoxClose();
                    break;
                case ScenarioState.OpeningContextMenu:
                    OpenContextMenu();
                    break;
                case ScenarioState.WaitingForContextMenuOpen:
                    VerifyContextMenuOpen();
                    break;
                case ScenarioState.WaitingForNestedMenuOpen:
                    VerifyNestedMenuOpen();
                    break;
                case ScenarioState.PressingNestedCommand:
                    PressNestedCommand();
                    break;
                case ScenarioState.ReleasingNestedCommand:
                    ReleaseNestedCommand();
                    break;
                case ScenarioState.WaitingForNestedCommand:
                    VerifyNestedCommand();
                    break;
                case ScenarioState.Finishing:
                    Finish();
                    break;
            }
        }
        catch (Exception exception)
        {
            Fail("Acceptance scenario failed", exception);
        }
    }

    private void WaitForWindow()
    {
        _window = _application.MainWindow;
        if (_window == null || !_window.IsVisible)
        {
            return;
        }

        _window.UpdateLayout();
        _navigationTree = FindDescendant<TreeView>(
            _window,
            tree => string.Equals(
                System.Windows.Automation.AutomationProperties.GetName(tree),
                "Navigation Pane",
                StringComparison.Ordinal));
        _frame = FindDescendant<Frame>(_window);
        if (_navigationTree == null || _frame == null ||
            !ProGpuWpfDiagnostics.TryGetWindowHost(_window, out var host) || host == null)
        {
            return;
        }

        _window.WindowStartupLocation = WindowStartupLocation.Manual;
        _window.Left = RequestedWindowLeft;
        _window.Top = RequestedWindowTop;
        _window.UpdateLayout();
        ProGpuWpfDiagnostics.TryRequestRender(_window);
        SetState(ScenarioState.WaitingForMovedFrame);
        GalleryAcceptanceLog.Write(
            $"Main window, navigation tree, frame, and ProGPU host are available; requested window origin ({RequestedWindowLeft:0.##},{RequestedWindowTop:0.##}).");
    }

    private void WaitForMovedFrame()
    {
        if (_window == null ||
            !ProGpuWpfDiagnostics.TryGetWindowHost(_window, out var host) ||
            host == null || !host.HasPresentedFrame)
        {
            ProGpuWpfDiagnostics.TryRequestRender(_window);
            return;
        }

        var screenOrigin = _window.PointToScreen(new Point(0, 0));
        Require(IsFinite(screenOrigin.X) && IsFinite(screenOrigin.Y),
            $"Window reported a non-finite screen origin {screenOrigin}.");
        Require(Math.Abs(screenOrigin.X) > 1 || Math.Abs(screenOrigin.Y) > 1,
            $"Window did not move away from the desktop origin: {screenOrigin}.");
        GalleryAcceptanceLog.Write(
            $"Moved window origin is screen ({screenOrigin.X:0.##},{screenOrigin.Y:0.##}); logical Left/Top=({_window.Left:0.##},{_window.Top:0.##}).");
        AssertRenderState("moved first frame");
        SetState(ScenarioState.DiscoveringNavigation);
    }

    private void DiscoverNavigation()
    {
        ArgumentNullException.ThrowIfNull(_navigationTree);
        _navigationTree.UpdateLayout();
        _targets.Clear();

        for (var rootIndex = 0; rootIndex < _navigationTree.Items.Count; rootIndex++)
        {
            if (_navigationTree.ItemContainerGenerator.ContainerFromIndex(rootIndex) is not TreeViewItem root)
            {
                return;
            }

            root.IsExpanded = true;
            root.UpdateLayout();
            _targets.Add(new NavigationTarget(root, null));

            for (var childIndex = 0; childIndex < root.Items.Count; childIndex++)
            {
                if (root.ItemContainerGenerator.ContainerFromIndex(childIndex) is not TreeViewItem child)
                {
                    return;
                }

                _targets.Add(new NavigationTarget(child, root));
            }
        }

        Require(_targets.Count == ExpectedNavigationTargetCount,
            $"Expected {ExpectedNavigationTargetCount} Gallery navigation targets, found {_targets.Count}.");
        GalleryAcceptanceLog.Write($"Discovered {_targets.Count} navigation targets.");
        SetState(ScenarioState.Navigating);
    }

    private void NavigateNext()
    {
        if (_targetIndex >= _targets.Count)
        {
            SetState(ScenarioState.OpeningContextMenu);
            return;
        }

        ArgumentNullException.ThrowIfNull(_navigationTree);
        var target = _targets[_targetIndex];
        if (target.Parent != null)
        {
            target.Parent.IsExpanded = true;
            target.Parent.UpdateLayout();
        }

        target.Container.IsSelected = true;
        _navigationTree.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
        {
            RoutedEvent = UIElement.PreviewMouseLeftButtonUpEvent,
            Source = _navigationTree
        });
        ProGpuWpfDiagnostics.TryRequestRender(_window);
        SetState(ScenarioState.WaitingForNavigation);
    }

    private void VerifyNavigation()
    {
        ArgumentNullException.ThrowIfNull(_frame);
        if (_frame.Content is not Page page || !page.IsLoaded)
        {
            return;
        }

        _window!.UpdateLayout();
        ExerciseSafeControls(page);
        AssertRenderState($"navigation target {_targetIndex + 1}");
        _navigatedCount++;
        GalleryAcceptanceLog.Write(
            $"Navigation {_navigatedCount}/{_targets.Count}: title='{page.Title ?? string.Empty}', content bounds={page.ActualWidth:0.##}x{page.ActualHeight:0.##}.");
        _targetIndex++;

        SetState(_selectedTextBox != null && !_textSelectionExercised
            ? ScenarioState.WaitingForTextSelectionRender
            : _openComboBox == null
                ? ScenarioState.Navigating
                : ScenarioState.OpeningComboBox);
    }

    private void ExerciseSafeControls(Page page)
    {
        if (!_inputExercised && _window != null &&
            ProGpuWpfDiagnostics.TryGetRenderSurfaceGeometry(_window, out var geometry))
        {
            var centerX = geometry.LogicalWidth / 2.0;
            var centerY = geometry.LogicalHeight / 2.0;
            Require(RaiseInput(new WpfInputEventArgs(WpfInputEventKind.MouseMove, x: centerX, y: centerY)),
                "ProGPU diagnostic mouse-move input was not accepted.");
            Require(RaiseInput(new WpfInputEventArgs(WpfInputEventKind.KeyDown, key: "Tab")),
                "ProGPU diagnostic key-down input was not accepted.");
            RaiseInput(new WpfInputEventArgs(WpfInputEventKind.KeyUp, key: "Tab"));
            _inputExercised = true;
        }

        if (FindDescendant<TextBox>(page, control => control.IsEnabled && !control.IsReadOnly && control.IsVisible) is { } textBox)
        {
            textBox.Text = "LibreWPF Gallery acceptance";
            textBox.CaretIndex = textBox.Text.Length;

            if (!_textSelectionInputExercised)
            {
                Require(textBox.Focus(), "Gallery TextBox did not accept keyboard focus.");
                Require(RaiseInput(new WpfInputEventArgs(
                        WpfInputEventKind.KeyDown,
                        key: "A",
                        modifiers: WpfInputModifiers.Control)),
                    "Ctrl+A key-down was not accepted by the portable WPF input route.");
                RaiseInput(new WpfInputEventArgs(
                    WpfInputEventKind.KeyUp,
                    key: "A",
                    modifiers: WpfInputModifiers.Control));
                Require(textBox.SelectionStart == 0 && textBox.SelectionLength == textBox.Text.Length,
                    $"Ctrl+A did not select the complete TextBox value; start={textBox.SelectionStart}, length={textBox.SelectionLength}, text={textBox.Text.Length}.");
                textBox.UpdateLayout();
                ProGpuWpfDiagnostics.TryRequestRender(_window);
                _selectedTextBox = textBox;
                _textSelectionInputExercised = true;
            }
        }

        if (FindDescendant<PasswordBox>(page, control => control.IsEnabled) is { } passwordBox)
        {
            passwordBox.Password = "LibreWPF";
        }

        if (FindDescendant<CheckBox>(page, control => control.IsEnabled) is { } checkBox)
        {
            checkBox.IsChecked = checkBox.IsChecked != true;
        }

        if (FindDescendant<Slider>(page, control => control.IsEnabled && control.Maximum > control.Minimum) is { } slider)
        {
            slider.Value = slider.Minimum + ((slider.Maximum - slider.Minimum) * 0.5);
        }

        if (FindDescendant<TabControl>(page, control => control.IsEnabled && control.Items.Count > 1) is { } tabs)
        {
            tabs.SelectedIndex = (tabs.SelectedIndex + 1) % tabs.Items.Count;
        }

        if (FindDescendant<ListBox>(page, control => control.IsEnabled && control.Items.Count > 0) is { } listBox)
        {
            listBox.SelectedIndex = 0;
        }

        if (FindDescendant<DataGrid>(page, control => control.IsEnabled && control.Items.Count > 0) is { } dataGrid)
        {
            dataGrid.SelectedIndex = 0;
        }

        if (FindDescendant<DatePicker>(page, control => control.IsEnabled) is { } datePicker)
        {
            datePicker.SelectedDate = new DateTime(2026, 7, 14);
        }

        if (FindDescendant<Expander>(page, control => control.IsEnabled) is { } expander)
        {
            expander.IsExpanded = !expander.IsExpanded;
        }

        if (!_comboBoxExercised && _openComboBox == null &&
            FindDescendant<ComboBox>(page, control => control.IsEnabled && control.Items.Count > 0) is { } comboBox)
        {
            comboBox.SelectedIndex = Math.Max(0, comboBox.SelectedIndex);
            _openComboBox = comboBox;
        }
    }

    private void VerifyTextSelectionRender()
    {
        ArgumentNullException.ThrowIfNull(_selectedTextBox);
        if (!FindSelectionRenderData(_selectedTextBox, out var selectionRenderData))
        {
            Require(_stateTicks < 12, "Selected TextBox did not publish typed caret/selection render data after a presented frame.");
            ProGpuWpfDiagnostics.TryRequestRender(_window);
            return;
        }

        GalleryAcceptanceLog.Write(
            $"TextBox Ctrl+A selected {_selectedTextBox.SelectionLength} characters and published {selectionRenderData.RenderDataBytes} bytes of typed selection render data; " +
            $"brush alpha={selectionRenderData.BrushAlpha}, geometry={selectionRenderData.GeometryBounds}, visual={selectionRenderData.VisualBounds}.");
        Require(selectionRenderData.BrushAlpha > 0,
            "Selected TextBox published a fully transparent selection brush.");
        Require(!selectionRenderData.GeometryBounds.IsEmpty,
            "Selected TextBox published empty selection geometry.");
        _textSelectionExercised = true;
        _selectedTextBox = null;
        SetState(_openComboBox == null
            ? ScenarioState.Navigating
            : ScenarioState.OpeningComboBox);
    }

    private void OpenComboBox()
    {
        ArgumentNullException.ThrowIfNull(_openComboBox);
        Require(_openComboBox.IsLoaded && _openComboBox.IsVisible,
            "Selected ComboBox is no longer loaded and visible.");
        _openComboBox.Focus();
        _openComboBox.IsDropDownOpen = true;
        _comboBoxExercised = true;
        ProGpuWpfDiagnostics.TryRequestRender(_window);
        SetState(ScenarioState.WaitingForComboBoxOpen);
    }

    private void VerifyComboBoxOpen()
    {
        ArgumentNullException.ThrowIfNull(_openComboBox);
        Require(_openComboBox.IsDropDownOpen, "ComboBox popup closed before its rendered acceptance turn.");
        AssertPopupRenderState("open ComboBox popup", minimumPopupCount: 1);
        _openComboBox.IsDropDownOpen = false;
        ProGpuWpfDiagnostics.TryRequestRender(_window);
        SetState(ScenarioState.WaitingForComboBoxClose);
    }

    private void VerifyComboBoxClose()
    {
        ArgumentNullException.ThrowIfNull(_openComboBox);
        Require(!_openComboBox.IsDropDownOpen, "ComboBox popup did not close.");
        _openComboBox = null;
        AssertRenderState("closed ComboBox popup");
        SetState(ScenarioState.Navigating);
    }

    private void OpenContextMenu()
    {
        ArgumentNullException.ThrowIfNull(_frame);
        ArgumentNullException.ThrowIfNull(_window);
        if (_frame.Content is not Page page || !page.IsLoaded)
        {
            return;
        }

        _nestedCommand = new MenuItem { Header = "Invoke acceptance command" };
        _nestedCommand.Click += (_, _) => _nestedCommandInvoked = true;
        _nestedCommand.PreviewMouseLeftButtonDown += (_, _) => _nestedCommandMouseDown = true;
        _nestedCommand.PreviewMouseLeftButtonUp += (_, _) => _nestedCommandMouseUp = true;
        _nestedMenu = new MenuItem { Header = "Nested acceptance menu" };
        _nestedMenu.Items.Add(_nestedCommand);
        _contextMenu = new ContextMenu { Placement = PlacementMode.MousePoint };
        _contextMenu.Items.Add(new MenuItem { Header = "Gallery acceptance context menu" });
        _contextMenu.Items.Add(_nestedMenu);
        page.ContextMenu = _contextMenu;
        page.UpdateLayout();

        var pagePoint = new Point(
            Math.Max(8, Math.Min(page.ActualWidth - 8, page.ActualWidth * 0.35)),
            Math.Max(8, Math.Min(page.ActualHeight - 8, page.ActualHeight * 0.35)));
        var ownerPoint = page.TranslatePoint(pagePoint, _window);
        Require(IsFinite(ownerPoint.X) && IsFinite(ownerPoint.Y),
            $"Context-menu target translated to a non-finite owner point {ownerPoint}.");
        Require(RaiseInput(new WpfInputEventArgs(WpfInputEventKind.MouseMove, x: ownerPoint.X, y: ownerPoint.Y)),
            "Context-menu target mouse move was not accepted.");
        Require(RaiseInput(new WpfInputEventArgs(
                WpfInputEventKind.MouseDown,
                x: ownerPoint.X,
                y: ownerPoint.Y,
                button: WpfMouseButton.Right)),
            "Context-menu right-button down was not accepted.");
        Require(RaiseInput(new WpfInputEventArgs(
                WpfInputEventKind.MouseUp,
                x: ownerPoint.X,
                y: ownerPoint.Y,
                button: WpfMouseButton.Right)),
            "Context-menu right-button up was not accepted.");
        ProGpuWpfDiagnostics.TryRequestRender(_window);
        SetState(ScenarioState.WaitingForContextMenuOpen);
    }

    private void VerifyContextMenuOpen()
    {
        ArgumentNullException.ThrowIfNull(_contextMenu);
        ArgumentNullException.ThrowIfNull(_nestedMenu);
        if (!_contextMenu.IsOpen)
        {
            Require(_stateTicks < 12, "Right-click input did not open the attached ContextMenu.");
            return;
        }

        _contextMenu.UpdateLayout();
        AssertPopupRenderState("open ContextMenu", minimumPopupCount: 1);
        var contextOrigin = _contextMenu.PointToScreen(new Point(0, 0));
        var ownerPoint = _window!.PointFromScreen(contextOrigin);
        Require(IsFinite(ownerPoint.X) && IsFinite(ownerPoint.Y),
            $"ContextMenu origin translated to a non-finite owner point {ownerPoint}.");
        Require(ownerPoint.X > -64 && ownerPoint.X < _window.ActualWidth + 64 &&
                ownerPoint.Y > -64 && ownerPoint.Y < _window.ActualHeight + 64,
            $"ContextMenu screen origin {contextOrigin} mapped outside moved owner bounds at {ownerPoint}.");
        GalleryAcceptanceLog.Write(
            $"ContextMenu opened at screen ({contextOrigin.X:0.##},{contextOrigin.Y:0.##}), owner ({ownerPoint.X:0.##},{ownerPoint.Y:0.##}).");

        _nestedMenu.IsSubmenuOpen = true;
        ProGpuWpfDiagnostics.TryRequestRender(_window);
        SetState(ScenarioState.WaitingForNestedMenuOpen);
    }

    private void VerifyNestedMenuOpen()
    {
        ArgumentNullException.ThrowIfNull(_nestedMenu);
        ArgumentNullException.ThrowIfNull(_nestedCommand);
        if (!_nestedMenu.IsSubmenuOpen || !_nestedCommand.IsVisible || _nestedCommand.ActualWidth <= 0)
        {
            Require(_stateTicks < 12, "Nested ContextMenu submenu did not become visible.");
            return;
        }

        AssertPopupRenderState("open nested ContextMenu submenu", minimumPopupCount: 2);
        var commandScreenPoint = _nestedCommand.PointToScreen(
            new Point(_nestedCommand.ActualWidth * 0.5, _nestedCommand.ActualHeight * 0.5));
        _nestedCommandOwnerPoint = _window!.PointFromScreen(commandScreenPoint);
        Require(IsFinite(_nestedCommandOwnerPoint.X) && IsFinite(_nestedCommandOwnerPoint.Y),
            $"Nested command translated to a non-finite owner point {_nestedCommandOwnerPoint}.");

        var owners = new object?[64];
        Require(ProGpuWpfDiagnostics.TryHitTestOwners(
                    _window,
                    _nestedCommandOwnerPoint.X,
                    _nestedCommandOwnerPoint.Y,
                    owners,
                    out var ownerCount) && ownerCount > 0,
            $"Nested command GPU hit test returned no owners at {_nestedCommandOwnerPoint}.");
        var commandOwnerIndex = -1;
        for (var index = 0; index < ownerCount; index++)
        {
            if (ReferenceEquals(owners[index], _nestedCommand) ||
                owners[index] is Visual visual && visual.IsDescendantOf(_nestedCommand))
            {
                commandOwnerIndex = index;
                break;
            }
        }
        if (commandOwnerIndex < 0)
        {
            if (_stateTicks < 12)
            {
                ProGpuWpfDiagnostics.TryRequestRender(_window);
                return;
            }

            var ownerSummary = string.Empty;
            for (var index = 0; index < ownerCount; index++)
            {
                var owner = owners[index];
                var description = owner switch
                {
                    MenuItem menuItem => $"MenuItem[{menuItem.Header}]",
                    FrameworkElement element => element.GetType().Name,
                    null => "<null>",
                    _ => owner.GetType().Name
                };
                ownerSummary += index == 0 ? description : $", {description}";
            }
            GalleryAcceptanceLog.Write($"Nested command owner miss: {ownerSummary}");
        }
        Require(commandOwnerIndex >= 0,
            $"Nested command was absent from {ownerCount} GPU hit-test owners at {_nestedCommandOwnerPoint}.");
        Require(RaiseInput(new WpfInputEventArgs(
                WpfInputEventKind.MouseMove,
                x: _nestedCommandOwnerPoint.X,
                y: _nestedCommandOwnerPoint.Y)),
            "Nested command mouse move was not accepted.");
        GalleryAcceptanceLog.Write(
            $"Nested command input at screen ({commandScreenPoint.X:0.##},{commandScreenPoint.Y:0.##}), owner ({_nestedCommandOwnerPoint.X:0.##},{_nestedCommandOwnerPoint.Y:0.##}), GPU owners={ownerCount}, command index={commandOwnerIndex}.");
        SetState(ScenarioState.PressingNestedCommand);
    }

    private void PressNestedCommand()
    {
        ArgumentNullException.ThrowIfNull(_nestedMenu);
        Require(_nestedMenu.IsSubmenuOpen, "Nested submenu closed before pointer press.");
        Require(RaiseInput(new WpfInputEventArgs(
                WpfInputEventKind.MouseDown,
                x: _nestedCommandOwnerPoint.X,
                y: _nestedCommandOwnerPoint.Y,
                button: WpfMouseButton.Left)),
            "Nested command left-button down was not accepted.");
        SetState(ScenarioState.ReleasingNestedCommand);
    }

    private void ReleaseNestedCommand()
    {
        Require(RaiseInput(new WpfInputEventArgs(
                WpfInputEventKind.MouseUp,
                x: _nestedCommandOwnerPoint.X,
                y: _nestedCommandOwnerPoint.Y,
                button: WpfMouseButton.Left)),
            "Nested command left-button up was not accepted.");
        SetState(ScenarioState.WaitingForNestedCommand);
    }

    private void VerifyNestedCommand()
    {
        Require(
            _nestedCommandInvoked,
            $"Nested ContextMenu command was not invoked through ProGPU input; routed down={_nestedCommandMouseDown}, up={_nestedCommandMouseUp}.");
        ArgumentNullException.ThrowIfNull(_contextMenu);
        _contextMenu.IsOpen = false;
        _contextMenuExercised = true;
        ProGpuWpfDiagnostics.TryRequestRender(_window);
        SetState(ScenarioState.Finishing);
    }

    private void AssertPopupRenderState(string stage, int minimumPopupCount)
    {
        AssertRenderState(stage);
        Require(ProGpuWpfDiagnostics.TryGetCompositionLayerSnapshot(_window, out var layers) &&
                layers.PopupLayerChildCount >= minimumPopupCount,
            $"{stage}: expected at least {minimumPopupCount} rendered popup roots, found {layers.PopupLayerChildCount}.");
    }

    private void AssertRenderState(string stage)
    {
        ArgumentNullException.ThrowIfNull(_window);
        Require(ProGpuWpfDiagnostics.TryGetWindowHost(_window, out var host) && host != null,
            $"{stage}: ProGPU window host is unavailable.");
        Require(host!.HasPresentedFrame, $"{stage}: no ProGPU frame has been presented.");

        Require(ProGpuWpfDiagnostics.TryGetRenderSurfaceGeometry(_window, out var geometry),
            $"{stage}: render-surface geometry is unavailable.");
        Require(geometry.LogicalWidth > 0 && geometry.LogicalHeight > 0 &&
                geometry.PixelWidth > 0 && geometry.PixelHeight > 0 &&
                geometry.ViewportWidth > 0 && geometry.ViewportHeight > 0,
            $"{stage}: invalid render-surface geometry {geometry}.");

        Require(ProGpuWpfDiagnostics.TryGetCompositionLayerSnapshot(_window, out var layers),
            $"{stage}: composition-layer snapshot is unavailable.");
        Require(layers.HasCompositionTarget && layers.SceneRootChildCount >= 3 &&
                layers.RetainedLayerIndex >= 0 && layers.FlatLayerIndex >= 0 &&
                layers.PopupLayerIndex >= 0 && layers.RetainedLayerChildCount > 0,
            $"{stage}: invalid composition layers {layers}.");

        Require(ProGpuWpfDiagnostics.TryGetGpuHitTestCacheSnapshot(_window, out var hitTest),
            $"{stage}: GPU hit-test cache snapshot is unavailable.");
        Require(hitTest.HasIndex && hitTest.OwnerCount > 0 && hitTest.PrimitiveCount > 0,
            $"{stage}: invalid GPU hit-test cache {hitTest}.");

        var owners = new object?[64];
        Require(ProGpuWpfDiagnostics.TryQueryHitTestBoundsOwners(
                    _window,
                    0,
                    0,
                    geometry.LogicalWidth,
                    geometry.LogicalHeight,
                    owners,
                    out var ownerCount) && ownerCount > 0,
            $"{stage}: full-window GPU hit-test query returned no owners.");
    }

    private void Finish()
    {
        Require(_navigatedCount == ExpectedNavigationTargetCount,
            $"Expected {ExpectedNavigationTargetCount} successful navigations, observed {_navigatedCount}.");
        Require(_inputExercised, "Typed ProGPU input was not exercised.");
        Require(_comboBoxExercised, "No ComboBox popup was exercised.");
        Require(_contextMenuExercised, "No ContextMenu was exercised.");
        Require(_textSelectionExercised, "No TextBox keyboard shortcut and selection rendering was exercised.");
        Require(_nestedCommandInvoked, "Nested ContextMenu command was not invoked.");
        AssertRenderState("final frame");
        GalleryAcceptanceLog.Write(
            $"PASS: {_navigatedCount} pages navigated; moved-window rendering, retained composition, GPU hit testing, typed input, TextBox selection, keyboard shortcuts, ComboBox, ContextMenu, and nested submenu validated.");
        StopAndShutdown(0);
    }

    private bool RaiseInput(WpfInputEventArgs input) =>
        ProGpuWpfDiagnostics.TryRaiseInput(_window, input);

    private void SetState(ScenarioState state)
    {
        _state = state;
        _stateTicks = 0;
    }

    private void Fail(string message, object? error = null)
    {
        GalleryAcceptanceLog.RecordFailure(message, error);
        StopAndShutdown(1);
    }

    private void StopAndShutdown(int exitCode)
    {
        _timer.Stop();
        _application.DispatcherUnhandledException -= OnDispatcherUnhandledException;
        Environment.ExitCode = exitCode;
        _application.Shutdown(exitCode);
    }

    private static bool IsFinite(double value) =>
        !double.IsNaN(value) && !double.IsInfinity(value);

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static T? FindDescendant<T>(DependencyObject root, Predicate<T>? predicate = null)
        where T : DependencyObject
    {
        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match && (predicate == null || predicate(match)))
            {
                return match;
            }

            if (FindDescendant(child, predicate) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private static bool FindSelectionRenderData(DependencyObject root, out SelectionRenderData selectionRenderData)
    {
        selectionRenderData = default;
        if (root is Adorner &&
            root is IPortableDrawingContentSource drawingContentSource &&
            drawingContentSource.TryGetPortableDrawingContent(out var content) &&
            content is IPortableRenderDataSource renderDataSource &&
            renderDataSource.TryGetPortableRenderDataSnapshot(out var snapshot) &&
            snapshot.RenderData.Length > 0)
        {
            var brushAlpha = (byte)0;
            var geometryBounds = Rect.Empty;
            foreach (var resource in snapshot.DependentResources)
            {
                if (resource is SolidColorBrush solidColorBrush)
                {
                    brushAlpha = Math.Max(brushAlpha, solidColorBrush.Color.A);
                }
                else if (resource is Geometry geometry && !geometry.Bounds.IsEmpty)
                {
                    geometryBounds.Union(geometry.Bounds);
                }
            }

            var visualBounds = Rect.Empty;
            if (root is IPortableVisualBoundsSource visualBoundsSource &&
                visualBoundsSource.TryGetPortableVisualBounds(out var portableVisualBounds))
            {
                visualBounds = new Rect(
                    portableVisualBounds.ContentBounds.X,
                    portableVisualBounds.ContentBounds.Y,
                    portableVisualBounds.ContentBounds.Width,
                    portableVisualBounds.ContentBounds.Height);
            }

            selectionRenderData = new SelectionRenderData(
                snapshot.RenderData.Length,
                brushAlpha,
                geometryBounds,
                visualBounds);
            return true;
        }

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < childCount; index++)
        {
            if (FindSelectionRenderData(VisualTreeHelper.GetChild(root, index), out selectionRenderData))
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct NavigationTarget(TreeViewItem Container, TreeViewItem? Parent);

    private readonly record struct SelectionRenderData(
        int RenderDataBytes,
        byte BrushAlpha,
        Rect GeometryBounds,
        Rect VisualBounds);

    private enum ScenarioState
    {
        WaitingForWindow,
        WaitingForMovedFrame,
        DiscoveringNavigation,
        Navigating,
        WaitingForNavigation,
        WaitingForTextSelectionRender,
        OpeningComboBox,
        WaitingForComboBoxOpen,
        WaitingForComboBoxClose,
        OpeningContextMenu,
        WaitingForContextMenuOpen,
        WaitingForNestedMenuOpen,
        PressingNestedCommand,
        ReleasingNestedCommand,
        WaitingForNestedCommand,
        Finishing
    }
}

internal static class GalleryAcceptanceLog
{
    private static readonly object Sync = new();
    private static string _path = "/tmp/librewpf-wpfgallery-acceptance.log";

    public static void Initialize()
    {
        _path = Environment.GetEnvironmentVariable("LIBREWPF_GALLERY_ACCEPTANCE_LOG") ?? _path;
        lock (Sync)
        {
            File.WriteAllText(
                _path,
                $"{DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)} START{Environment.NewLine}");
        }
    }

    public static void Write(string message)
    {
        lock (Sync)
        {
            File.AppendAllText(
                _path,
                $"{DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture)} {message}{Environment.NewLine}");
        }
    }

    public static void RecordFailure(string message, object? error = null)
    {
        Write($"FAIL: {message}{(error == null ? string.Empty : Environment.NewLine + error)}");
        Environment.ExitCode = 1;
    }
}
