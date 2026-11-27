using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Vst3;
using NAudio.Wave;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace WellenEditor;

public partial class MainWindow : Window
{
    private AudioDocument? document;
    private readonly List<AudioDocument> openDocuments = new();
    private int nextUntitledNumber = 1;
    private bool isRefreshingFileList;
    private bool isActivatingDocument;
    private IWavePlayer? player;
    private readonly AudioPlaybackLifetime hardwareLifetime = new();
    private IDisposable? playbackProvider;
    private MeteringWaveProvider? playbackMeter;
    private AudioDocument? playbackDocument;
    private AudioDocument? multitrackPlaybackDocument;
    private MultitrackWaveProvider? multitrackMixer;
    private PreparedMultitrackPlayback? blockPlayback;
    private CancellationTokenSource? blockPreparationCancellation;
    private int blockBufferBlocks = 8;
    private Task<bool> blockStopPending = Task.FromResult(true);
    private PreparedMultitrackPlayback? retiringBlock;
    private Task unpublishedPreparationCleanup = Task.CompletedTask;
    private IWavePlayer? previewPlayer;
    private bool isPlaying;
    // Render-Priorität verhindert, dass Cursor und Meter bei einer großen
    // Wellenform erst nach weniger wichtigen UI-Aufgaben aktualisiert werden.
    private readonly DispatcherTimer playbackTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch playbackClock = new();
    private long playbackStartFrame;
    private long playbackFrameCount;
    private long lastMeterFrame;
    // Puffergröße des WasapiOut: Anzeige und Cursor werden um diese Latenz
    // gegen das tatsächlich hörbare Signal zurückgerechnet.
    private const int OutputLatencyMs = 50;
    private bool loopSelection;
    private bool loopPlaybackEnabled;
    private long loopStartFrame;
    private long loopFrameCount;
    private readonly RecordingEngine recorder = new();
    private AudioDeviceMonitor? hardwareMonitor;
    private bool recordingStopPending;
    private bool closeAfterRecording;
    private AudioDocument? recordingTargetDocument;
    private int recordingTargetFrame;
    private IReadOnlyList<Vst3EffectDescriptor> vstEffects = Array.Empty<Vst3EffectDescriptor>();
    private bool isScanningVst;
    private readonly MultitrackProject multitrack = new();
    private MultitrackTrack? selectedTrack;
    private AudioDocument? dragSourceDocument;
    private Point dragStartPoint;
    private bool isDocumentDragging;
    private bool preparingMultitrack;
    private int playbackPreparationVersion;

    private enum SavePromptResult { Save, Discard, Cancel }

    private sealed class DocumentTabItem
    {
        public DocumentTabItem(AudioDocument audioDocument, bool isActive)
        {
            Document = audioDocument;
            IsActive = isActive;
        }

        public AudioDocument Document { get; }
        public bool IsActive { get; }
        public string DisplayName => Document.DisplayName + (Document.IsModified ? " *" : "");
    }

    public MainWindow()
    {
        InitializeComponent();
        recorder.RecordingFaulted += (_, _) => Dispatcher.BeginInvoke(new Action(() =>
        {
            AudioHardwareSettings.RequireReselection(AudioDirection.Input);
            if (recorder.IsRecording && !recordingStopPending) Record_Click(this, new RoutedEventArgs());
        }));
        try
        {
            hardwareMonitor = new AudioDeviceMonitor();
            hardwareMonitor.Changed += change => Dispatcher.BeginInvoke(new Action(() => HardwareChanged(change)));
        }
        catch (Exception e) { HardwareStatusText.Text = "Geräteüberwachung nicht verfügbar: " + e.Message; }
        Closed += (_, _) => hardwareMonitor?.Dispose();
        PreviewMouseMove += FileList_PreviewMouseMove;
        PreviewMouseLeftButtonUp += DocumentDrag_MouseUp;
        Sequencer.TrackSelected += track => selectedTrack = track;
        Sequencer.EffectsRequested += track =>
        {
            var restartBlock = blockPlayback is not null;
            var restartCursor = Sequencer.PositionSeconds;
            if (restartBlock) StopPlayback();
            var dialog = new TrackEffectsWindow(this, track,
                () => { multitrackMixer?.UpdateTrackEffects(track); Sequencer.SetProject(multitrack); },
                () => { if (!isPlaying) PlayMultitrack_Click(this, new RoutedEventArgs()); },
                StopPlayback, vstEffects,
                livePlugin =>
                {
                    if (livePlugin is null) StopPreview();
                    else StartLiveVstPreview(livePlugin, MultitrackWaveProvider.RenderTrackDry(track, track.ProjectSampleRate), track.ProjectSampleRate);
                });
            dialog.ShowDialog();
            Sequencer.SetProject(multitrack);
            if (restartBlock) _ = PlayBlockMultitrackAsync((long)(restartCursor * multitrack.SampleRate));
        };
        Sequencer.PlaybackRangeChanged += () =>
        {
            HintText.Content = Sequencer.GetPlaybackRange() is null ? "Mehrspur-Auswahl aufgehoben." : "Mehrspur-Auswahl gesetzt – der markierte Bereich wird wiederholt.";
            UpdateMultitrackPlaybackRange();
            UpdateSelectionText();
        };
        Sequencer.SeekRequested += async frame =>
        {
            if (blockPlayback is not { } active) { multitrackMixer?.Seek(frame); return; }
            try { await active.SeekAsync(frame); }
            catch (Exception exception) { BlockPlaybackFailed(active, exception); }
        };
        Sequencer.RemoveTrackRequested += track =>
        {
            StopPlayback();
            multitrack.Remove(track);
            selectedTrack = multitrack.Tracks.FirstOrDefault();
            RefreshMultitrack();
        };
        Sequencer.ProjectChanged += () =>
        {
            RestartBlockPlayback();
            BpmText.Text = multitrack.Bpm.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
            MultitrackInfo.Text = $"{multitrack.Tracks.Count} Spuren · {multitrack.Bpm:0.##} BPM";
            UpdateSelectionText();
            HintText.Content = "Clip im Beat-Raster platziert. Projekt abspielen startet alle Spuren.";
        };
        AllowDrop = true;
        DragOver += (_, e) => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) OpenAudio(files[0]); };
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Closing += async (_, e) =>
        {
            if (closeAfterRecording) return;
            if (hardwareLifetime.HasPending)
            {
                e.Cancel = true;
                MessageBox.Show(this, "Ein Audiogerät ist noch nicht sicher freigegeben. Bitte Stopp erneut versuchen; aktive Ressourcen werden nicht parallel freigegeben.", "Audio-Hardware", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (recordingStopPending || preparingMultitrack)
            {
                e.Cancel = true;
                return;
            }
            if (recorder.IsFinalizationPending)
            {
                e.Cancel = true;
                MessageBox.Show(this, "Die Aufnahme wird noch abgeschlossen. Bitte das Programm geöffnet lassen, bis der Datenträger wieder reagiert.", "Aufnahme", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!ConfirmCloseAllDocuments())
            {
                e.Cancel = true;
                return;
            }
            StopPlayback();
            if (recorder.IsRecording)
            {
                e.Cancel = true;
                recordingStopPending = true;
                try { await Task.Run(recorder.Cancel); }
                catch (Exception exception)
                {
                    MessageBox.Show(this, exception.Message, "Aufnahme", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                finally { recordingStopPending = false; }
                if (!DisposeRecorderForClose()) return;
                closeAfterRecording = true;
                Close();
                return;
            }
            if (!DisposeRecorderForClose() || hardwareLifetime.HasPending) e.Cancel = true;
        };
        playbackTimer.Tick += PlaybackTimer_Tick;
        Loaded += (_, _) =>
        {
            var startupFile = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists);
            if (startupFile is not null) OpenAudio(startupFile);
            // Scan-Dialog nur noch auf Bedarf über den Menüpunkt/„VST3 scannen"-Knopf öffnen.
            // Gespeicherte VST3-Plugins sofort in die Liste laden (kein Neuscan nötig).
            var cached = Vst3EffectCatalog.LoadCache();
            if (cached.Count > 0)
            {
                vstEffects = cached;
                ReplaceItemsSource(VstEffectsList, cached);
                HintText.Content = $"{cached.Count} gespeicherte VST3-Effekte geladen. Doppelklick wendet ein Plugin an.";
            }
        };
    }

    private bool DisposeRecorderForClose()
    {
        try
        {
            recorder.Dispose();
            return true;
        }
        catch (Exception error)
        {
            // A confirmed stop permits normal application exit with the native driver
            // still owned by the process. An unconfirmed stop must keep the window live.
            var canClose = recorder.CanCloseWithRetainedCapture && !recorder.IsFinalizationPending;
            var message = canClose
                ? "Der Treiber ist gestoppt. Seine Ressourcen bleiben bis zum Programmende gebunden."
                : "Die Aufnahme ist noch nicht sicher beendet. Bitte das Programm geöffnet lassen.";
            MessageBox.Show(this, $"{message}\n\n{error.Message}", "Aufnahme", MessageBoxButton.OK, MessageBoxImage.Warning);
            return canClose;
        }
    }

    private void SequencerHorizontalIn_Click(object sender, RoutedEventArgs e) => Sequencer.ZoomHorizontal(1.25);
    private void SequencerHorizontalOut_Click(object sender, RoutedEventArgs e) => Sequencer.ZoomHorizontal(1 / 1.25);
    private void SequencerVerticalIn_Click(object sender, RoutedEventArgs e) => Sequencer.ZoomVertical(1.25);
    private void SequencerVerticalOut_Click(object sender, RoutedEventArgs e) => Sequencer.ZoomVertical(1 / 1.25);

    private void Open_Click(object sender, RoutedEventArgs e)
    {
#if BETA_PROFILE
        var dialog = new OpenFileDialog { Filter = "WAV-Audiodateien|*.wav" };
#else
        var dialog = new OpenFileDialog { Filter = "Audiodateien|*.wav;*.flac;*.mp3;*.ogg;*.opus;*.m4a;*.aac;*.wma|Alle Dateien|*.*" };
#endif
        if (dialog.ShowDialog(this) == true) OpenAudio(dialog.FileName);
    }

    private void EditView_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        EditorPanel.Visibility = Visibility.Visible;
        MultitrackPanel.Visibility = Visibility.Collapsed;
        EditViewButton.Style = (Style)FindResource("ViewTabActiveButton");
        MultitrackViewButton.Style = (Style)FindResource("TabSelectButton");
        UpdateSelectionText();
    }

    private void MultitrackView_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        EditorPanel.Visibility = Visibility.Collapsed;
        MultitrackPanel.Visibility = Visibility.Visible;
        EditViewButton.Style = (Style)FindResource("TabSelectButton");
        MultitrackViewButton.Style = (Style)FindResource("ViewTabActiveButton");
        RefreshMultitrack();
    }

    private void FileList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        var index = item is null ? -1 : FileList.ItemContainerGenerator.IndexFromContainer(item);
        dragSourceDocument = index >= 0 && index < openDocuments.Count ? openDocuments[index] : null;
        dragStartPoint = e.GetPosition(FileList);
        if (dragSourceDocument is not null && MultitrackPanel.Visibility == Visibility.Visible)
        {
            FileList.SelectedIndex = index;
            isDocumentDragging = false;
            System.Windows.Input.Mouse.Capture(this, System.Windows.Input.CaptureMode.SubTree);
            e.Handled = true;
        }
    }

    private void FileList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (dragSourceDocument is null || e.LeftButton != System.Windows.Input.MouseButtonState.Pressed) return;
        var distance = e.GetPosition(FileList) - dragStartPoint;
        if (Math.Abs(distance.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(distance.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        if (MultitrackPanel.Visibility != Visibility.Visible) return;
        isDocumentDragging = true;
        Cursor = System.Windows.Input.Cursors.Hand;
        HintText.Content = $"'{dragSourceDocument.DisplayName}' rechts ablegen – neue Spur oder weiterer Clip.";
    }

    private void DocumentDrag_MouseUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var source = dragSourceDocument;
        var dragging = isDocumentDragging;
        dragSourceDocument = null;
        isDocumentDragging = false;
        Cursor = null;
        if (System.Windows.Input.Mouse.Captured == this) System.Windows.Input.Mouse.Capture(null);
        if (!dragging || source is null) return;
        var point = e.GetPosition(Sequencer);
        if (point.X >= 0 && point.Y >= 0 && point.X < Sequencer.ActualWidth && point.Y < Sequencer.ActualHeight)
        {
            Sequencer.PlaceDocument(source, point);
            e.Handled = true;
        }
    }

    private void AddTrack_Click(object sender, RoutedEventArgs e)
    {
        selectedTrack = multitrack.AddEmptyTrack();
        RefreshMultitrack();
        HintText.Content = "Neue leere Spur angelegt. Benennen und dann eine Datei links auf die Spur ziehen.";
    }

    private void ApplyBpm_Click(object sender, RoutedEventArgs e)
    {
        if (!double.TryParse(BpmText.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out var bpm))
        { HintText.Content = "Bitte BPM als Zahl eingeben, z. B. 120."; return; }
        try
        {
            if (Math.Abs(multitrack.Bpm - bpm) < 0.0001) return;
            StopPlayback();
            multitrack.SetBpm(bpm);
            BpmText.Text = multitrack.Bpm.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);
            RefreshMultitrack();
            HintText.Content = $"Raster auf {multitrack.Bpm:0.##} BPM gesetzt. Clips rasten beim Ablegen auf volle Beats ein.";
        }
        catch (ArgumentOutOfRangeException) { HintText.Content = "BPM muss zwischen 30 und 300 liegen."; }
    }

    private void Bpm_LostFocus(object sender, RoutedEventArgs e) => ApplyBpm_Click(sender, e);

    private void TrackLane_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent("WaveBench.AudioDocument") ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void TrackLane_Drop(object sender, DragEventArgs e)
    {
        if (sender is not Canvas lane || lane.DataContext is not MultitrackTrack track || e.Data.GetData("WaveBench.AudioDocument") is not AudioDocument source) return;
        const double pixelsPerBeat = 80;
        var beat = Math.Round(Math.Max(0, e.GetPosition(lane).X / pixelsPerBeat));
        multitrack.PutClipOnBeat(track, source, beat);
        selectedTrack = track;
        RefreshMultitrack();
        HintText.Content = $"'{source.DisplayName}' auf '{track.Name}' ab Beat {beat + 1:0} abgelegt.";
        e.Handled = true;
    }

    private void RemoveTrack_Click(object sender, RoutedEventArgs e)
    {
        if (selectedTrack is null) { HintText.Content = "Bitte zuerst eine Spur anklicken."; return; }
        multitrack.Remove(selectedTrack);
        selectedTrack = multitrack.Tracks.LastOrDefault();
        RefreshMultitrack();
    }

    private void TrackMute_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: MultitrackTrack track }) { selectedTrack = track; RefreshMultitrack(); } }
    private void TrackSolo_Click(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { DataContext: MultitrackTrack track }) { selectedTrack = track; RefreshMultitrack(); } }
    private void TrackGain_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (sender is FrameworkElement { DataContext: MultitrackTrack track }) { track.Gain = e.NewValue; selectedTrack = track; } }
    private void TrackPan_Changed(object sender, RoutedPropertyChangedEventArgs<double> e) { if (sender is FrameworkElement { DataContext: MultitrackTrack track }) { track.Pan = e.NewValue; selectedTrack = track; } }
    private void Track_Selected(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: MultitrackTrack track })
        {
            selectedTrack = track;
            HintText.Content = $"Spur ausgewählt: {track.Name}";
        }
    }

    private async void Mixdown_Click(object sender, RoutedEventArgs e)
    {
        if (preparingMultitrack) return;
        StopPlayback();
        var workspace = Content as UIElement;
        if (workspace is not null) workspace.IsEnabled = false;
        HintText.Content = "Mehrspur-Mixdown wird gerendert…";
        try
        {
            var samples = await Task.Run(() => multitrack.RenderMixdown());
            var mix = AudioDocument.FromSamples(samples, 2, multitrack.SampleRate, "Mixdown.wav");
            openDocuments.Add(mix);
            ActivateDocument(mix);
            EditView_Click(sender, e);
            HintText.Content = "Mixdown erstellt – die Originalspuren bleiben unverändert.";
        }
        catch (Exception exception)
        {
            var cleanup = exception.Data["MixdownCleanupError"] as string;
            MessageBox.Show(this, $"Mixdown fehlgeschlagen.\n\n{exception.Message}{(cleanup is null ? "" : $"\n\n{cleanup}")}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { if (workspace is not null) workspace.IsEnabled = true; }
    }

    private async void StreamingMixdown_Click(object sender, RoutedEventArgs e)
    {
        if (preparingMultitrack) return;
        var dialog = new SaveFileDialog { Filter = "WAV-Audio (*.wav)|*.wav", FileName = "Mixdown.wav", AddExtension = true };
        if (dialog.ShowDialog(this) != true) return;
        StopPlayback();
        using var cancellation = new CancellationTokenSource();
        var bar = new ProgressBar { Minimum = 0, Maximum = 100, Height = 18, Margin = new Thickness(16) };
        var cancel = new Button { Content = "Abbrechen", Width = 110, Margin = new Thickness(16) };
        var panel = new StackPanel();
        panel.Children.Add(bar);
        panel.Children.Add(cancel);
        var progressWindow = new Window { Title = "Mixdown erstellen", Owner = this, Width = 360, Height = 150, WindowStartupLocation = WindowStartupLocation.CenterOwner, ResizeMode = ResizeMode.NoResize, Content = panel };
        cancel.Click += (_, _) => { cancel.IsEnabled = false; cancellation.Cancel(); };
        var finished = false;
        progressWindow.Closing += (_, args) => { if (!finished) { args.Cancel = true; cancellation.Cancel(); cancel.IsEnabled = false; } };
        var workspace = Content as UIElement;
        if (workspace is not null) workspace.IsEnabled = false;
        preparingMultitrack = true;
        progressWindow.Show();
        try
        {
            var progress = new Progress<double>(value => bar.Value = value * 100);
            await Task.Run(() => BlockMixdownRenderer.WriteWave(multitrack, dialog.FileName, cancellation.Token, progress));
            HintText.Content = "Mixdown als WAV gespeichert.";
        }
        catch (OperationCanceledException exception)
        {
            HintText.Content = "Mixdown abgebrochen.";
            if (exception.Data["MixdownCleanupError"] is string cleanup)
                MessageBox.Show(this, cleanup, "Mixdown", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception exception)
        {
            var cleanup = exception.Data["MixdownCleanupError"] as string;
            MessageBox.Show(this, $"Mixdown fehlgeschlagen.\n\n{exception.Message}{(cleanup is null ? "" : $"\n\n{cleanup}")}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            finished = true;
            progressWindow.Close();
            preparingMultitrack = false;
            if (workspace is not null) workspace.IsEnabled = true;
        }
    }

    private async void PlayMultitrack_Click(object sender, RoutedEventArgs e)
    {
        if (preparingMultitrack) return;
        if (isPlaying) { StopPlayback(); return; }
        if (BlockPlaybackMenuItem.IsChecked) { await PlayBlockMultitrackAsync(); return; }
        try
        {
            preparingMultitrack = true;
            var workspace = Content as UIElement;
            if (workspace is not null) workspace.IsEnabled = false;
            Sequencer.IsEnabled = false;
            FileList.IsEnabled = false;
            BpmText.IsEnabled = false;
            var version = ++playbackPreparationVersion;
            HintText.Content = "Mehrspur-Wiedergabe wird vorbereitet…";
            var provider = await Task.Run(() => new MultitrackWaveProvider(multitrack));
            if (version != playbackPreparationVersion) return;
            var range = Sequencer.GetPlaybackRange();
            var start = range?.Start ?? 0;
            if (start >= provider.FrameCount) throw new InvalidOperationException("Die Auswahl liegt hinter dem letzten Sample. Bitte einen Bereich mit Audio markieren.");
            var count = Math.Min(range?.Count ?? provider.FrameCount, provider.FrameCount - start);
            provider.ConfigurePlayback(start, count, range.HasValue || loopPlaybackEnabled);
            multitrackMixer = provider;
            multitrackPlaybackDocument = AudioDocument.FromSamples(new float[2], 2, multitrack.SampleRate);
            loopSelection = false; // Multitrack mixer wraps internally and accepts live range updates.
            StartPlayback(start, count, multitrackPlaybackDocument, provider);
            var status = range.HasValue || loopPlaybackEnabled ? "Mehrspur-Schleife läuft." : "Mehrspurprojekt wird gemeinsam wiedergegeben.";
            HintText.Content = provider.PreparationWarnings.Count == 0
                ? status
                : $"{status} Hinweis: {string.Join(" | ", provider.PreparationWarnings)}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Mehrspur-Wiedergabe konnte nicht gestartet werden.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            preparingMultitrack = false;
            Sequencer.IsEnabled = true; FileList.IsEnabled = true; BpmText.IsEnabled = true;
            if (Content is UIElement workspace) workspace.IsEnabled = true;
        }
    }

    private void BlockPlaybackToggle_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        HintText.Content = BlockPlaybackMenuItem.IsChecked
            ? "Experimentelle Mehrspur-Wiedergabe aktiviert. Spuren werden vor dem Start auf Disk vorbereitet."
            : "Bisherige Mehrspur-Wiedergabe aktiviert.";
    }
    private void BlockBuffer_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string text } && int.TryParse(text, out var blocks))
        {
            StopPlayback(); blockBufferBlocks = blocks;
            HintText.Content = $"Mehrspur-Wiedergabepuffer: {blocks} Blöcke zu je 1024 Frames.";
        }
    }
    private async void RestartBlockPlayback()
    {
        if (blockPlayback is null || preparingMultitrack) return;
        var cursor = (long)(Sequencer.PositionSeconds * multitrack.SampleRate);
        StopPlayback();
        await PlayBlockMultitrackAsync(cursor);
    }
    private void BlockPlaybackFailed(PreparedMultitrackPlayback source, Exception exception)
    {
        if (!ReferenceEquals(blockPlayback, source)) return;
        StopPlayback();
        HintText.Content = $"Renderfehler: {exception.GetBaseException().Message}. Bisherige Engine unter Optionen verfügbar.";
    }
    private async Task PlayBlockMultitrackAsync(long? cursor = null)
    {
        if (preparingMultitrack) return;
        preparingMultitrack = true;
        var version = ++playbackPreparationVersion;
        using var cancellation = new CancellationTokenSource();
        blockPreparationCancellation = cancellation;
        PreparedMultitrackSession? session = null;
        PreparedMultitrackPlayback? prepared = null;
        var cancel = new Button { Content = "Abbrechen", Margin = new Thickness(12), MinWidth = 100 };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Mehrspur-Audio und Effekte werden vorbereitet…", Margin = new Thickness(12) });
        panel.Children.Add(cancel);
        var progress = new Window { Owner = this, Title = "Wiedergabe vorbereiten", Content = panel,
            SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        var finished = false;
        Exception? preparationError = null;
        cancel.Click += (_, _) => { cancellation.Cancel(); cancel.IsEnabled = false; };
        progress.Closing += (_, args) => { if (!finished) { args.Cancel = true; cancellation.Cancel(); cancel.IsEnabled = false; } };
        try
        {
            if (!await blockStopPending && retiringBlock is { } previous &&
                !await previous.StopAsync(TimeSpan.FromSeconds(2)))
                throw new TimeoutException("Die vorherige Wiedergabe ist noch nicht beendet. Bitte den blockierten Geräte- oder Dateizugriff prüfen.");
            retiringBlock = null;
            if (await Task.WhenAny(unpublishedPreparationCleanup, Task.Delay(TimeSpan.FromSeconds(2))) != unpublishedPreparationCleanup)
                throw new TimeoutException("Die vorherige Audiovorbereitung hält noch Ressourcen.");
            await unpublishedPreparationCleanup;
            var snapshot = multitrack.Snapshot();
            var controls = multitrack.Tracks.ToArray();
            if (Content is UIElement workspace) workspace.IsEnabled = false;
            progress.Show();
            session = await Task.Run(() => new PreparedMultitrackSession(snapshot, controls, cancellation.Token));
            cancellation.Token.ThrowIfCancellationRequested();
            if (version != playbackPreparationVersion) return;
            prepared = new PreparedMultitrackPlayback(session, bufferBlocks: blockBufferBlocks);
            session = null;
            var range = Sequencer.GetPlaybackRange();
            var start = range?.Start ?? Math.Clamp(cursor ?? (long)(Sequencer.PositionSeconds * multitrack.SampleRate), 0, prepared.FrameCount - 1);
            var count = Math.Min(range?.Count ?? prepared.FrameCount - start, prepared.FrameCount - start);
            var configuring = prepared.ConfigurePlaybackAsync(start, count, range.HasValue || loopPlaybackEnabled, cancellation.Token);
            try { await configuring.WaitAsync(cancellation.Token); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                // The transport still owns an opening/blocked reader. Retire it
                // below, without making the Cancel button wait for native I/O.
                _ = ObservePlaybackCleanupAsync(configuring);
                throw;
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (version != playbackPreparationVersion) return;
            blockPlayback = prepared;
            multitrackPlaybackDocument = AudioDocument.FromSamples(new float[2], 2, multitrack.SampleRate);
            loopSelection = false;
            StartPlayback(start, count, multitrackPlaybackDocument, prepared);
            prepared = null;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { HintText.Content = "Audiovorbereitung abgebrochen."; }
        catch (Exception exception)
        {
            preparationError = exception;
            try { StopPlayback(); } catch (Exception cleanup) { exception.Data["PlaybackStopError"] = cleanup; }
        }
        finally
        {
            preparationError = PlaybackPreparationCleanup.Run(preparationError,
                () => RetireUnpublishedPlayback(prepared, session),
                () =>
                {
                    if (ReferenceEquals(blockPreparationCancellation, cancellation)) blockPreparationCancellation = null;
                    preparingMultitrack = false;
                },
                () => { if (Content is UIElement workspace) workspace.IsEnabled = true; },
                () => { finished = true; progress.Close(); });
            if (preparationError is not null)
                MessageBox.Show(this, $"Experimentelle Wiedergabe konnte nicht vorbereitet werden.\n\n{preparationError.Message}\n{preparationError.Data["PlaybackCleanupError"]}\n\nDie bisherige Engine kann unter Optionen gewählt werden.",
                    "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RetireUnpublishedPlayback(PreparedMultitrackPlayback? prepared, PreparedMultitrackSession? session)
    {
        if (prepared is not null)
        {
            prepared.Dispose();
            retiringBlock = prepared;
            blockStopPending = prepared.StopAsync(TimeSpan.FromSeconds(2));
            _ = ObservePlaybackCleanupAsync(blockStopPending);
        }
        if (session is not null)
        {
            unpublishedPreparationCleanup = Task.Run(async () =>
            {
                session.Dispose();
                await session.Completion.ConfigureAwait(false);
            });
            _ = ObservePlaybackCleanupAsync(unpublishedPreparationCleanup);
        }
    }
    private async Task ObservePlaybackCleanupAsync(Task completion)
    {
        try { await completion; }
        catch (OperationCanceledException) { }
        catch (Exception error) { HintText.Content = $"Fehler bei der Audiofreigabe: {error.GetBaseException().Message}"; }
    }

    private async void UpdateMultitrackPlaybackRange()
    {
        if (blockPlayback is { } active)
        {
            var selected = Sequencer.GetPlaybackRange();
            var selectedStart = selected?.Start ?? 0;
            var selectedCount = Math.Min(selected?.Count ?? active.FrameCount, active.FrameCount - selectedStart);
            try { await active.ConfigurePlaybackAsync(selectedStart, selectedCount, selected.HasValue || loopPlaybackEnabled); }
            catch (Exception exception) { BlockPlaybackFailed(active, exception); }
            return;
        }
        if (multitrackMixer is not { } mixer) return;
        var range = Sequencer.GetPlaybackRange();
        var start = range?.Start ?? 0;
        var count = range?.Count ?? mixer.FrameCount;
        mixer.ConfigurePlayback(start, count, range.HasValue || loopPlaybackEnabled);
    }

    private void RefreshMultitrack()
    {
        Sequencer.SetProject(multitrack);
        MultitrackInfo.Text = multitrack.Tracks.Count == 0
            ? "Noch keine Spuren"
            : $"{multitrack.Tracks.Count} Spur(en) · {multitrack.Bpm:0.##} BPM · {multitrack.SampleRate:N0} Hz";
        UpdateSelectionText();
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match) return match;
            current = VisualTreeHelper.GetParent(current);
        }
        return null;
    }

    private void OpenAudio(string fileName)
    {
        var fullPath = Path.GetFullPath(fileName);
        var existing = openDocuments.FirstOrDefault(d => !string.IsNullOrWhiteSpace(d.SourcePath) &&
            string.Equals(Path.GetFullPath(d.SourcePath), fullPath, StringComparison.OrdinalIgnoreCase));
        if (existing is not null)
        {
            ActivateDocument(existing);
            HintText.Content = $"Dokument '{existing.DisplayName}' ist bereits geöffnet.";
            return;
        }
        try
        {
            StopPlayback();
            var loaded = AudioDocument.Load(fullPath);
            openDocuments.Add(loaded);
            ActivateDocument(loaded);
            HintText.Content = $"Audio geladen: {loaded.DisplayName}. Mit der Maus einen Bereich markieren.";
        }
        catch (Exception exception) { MessageBox.Show(this, $"Die Datei konnte nicht geöffnet werden.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void ActivateDocument(AudioDocument documentToActivate)
    {
        if (!openDocuments.Contains(documentToActivate)) openDocuments.Add(documentToActivate);
        isActivatingDocument = true;
        StopPlayback();
        document = documentToActivate;
        documentToActivate.PeakCacheReady -= Document_PeakCacheReady;
        documentToActivate.PeakCacheReady += Document_PeakCacheReady;
        documentToActivate.SpectrogramReady -= Document_SpectrogramReady;
        documentToActivate.SpectrogramReady += Document_SpectrogramReady;
        if (documentToActivate.IsStreaming)
            Waveform.SetStreamingPreview(documentToActivate.WaveformSamples, documentToActivate.Channels, documentToActivate.SampleRate, documentToActivate.FrameCount);
        else
            Waveform.SetAudio(documentToActivate.Samples, documentToActivate.Channels, documentToActivate.SampleRate);
        Waveform.SetPeakCache(documentToActivate.PeakCache);
        Waveform.SetSpectrogram(documentToActivate.Spectrogram);
        if (SpectrogramMenuItem.IsChecked) documentToActivate.RequestSpectrogram();
        RefreshDocumentTabs();
        RefreshDocumentInfo();
        RefreshFileList();
        UpdateSelectionText();
        UpdateUndoRedoState();
        isActivatingDocument = false;
    }

    private void RefreshDocumentInfo()
    {
        if (document is null)
        {
            DurationText.Text = "Länge: 0:00.000";
            FormatText.Text = "Rate: — · Kanäle: —";
            ViewRangeText.Text = "0:00.000 – 0:00.000";
            return;
        }
        DurationText.Text = $"Länge: {FormatTime(document.Duration)}";
        FormatText.Text = $"{document.SampleRate:N0} Hz · {ChannelText(document.Channels)} · 32-bit float";
        ViewRangeText.Text = $"0:00.000 – {FormatTime(document.Duration)}";
    }

    private static void ReplaceItemsSource(System.Windows.Controls.ListBox list, System.Collections.IEnumerable source)
    {
        list.ItemsSource = null;
        list.Items.Clear();
        list.ItemsSource = source;
    }

    private static void ReplaceItemsSource(System.Windows.Controls.ItemsControl itemsControl, System.Collections.IEnumerable source)
    {
        itemsControl.ItemsSource = null;
        itemsControl.Items.Clear();
        itemsControl.ItemsSource = source;
    }

    private void RefreshDocumentTabs()
    {
        ReplaceItemsSource(DocumentTabs, openDocuments.Select(d => new DocumentTabItem(d, ReferenceEquals(d, document))).ToList());
    }

    private void RefreshFileList()
    {
        isRefreshingFileList = true;
        var displayItems = openDocuments.Select(d => d.DisplayName + (d.IsModified ? " *" : "")).ToList();
        ReplaceItemsSource(FileList, displayItems);
        var currentIndex = document is null ? -1 : openDocuments.IndexOf(document);
        if (currentIndex >= 0 && currentIndex < displayItems.Count) FileList.SelectedIndex = currentIndex;
        RefreshDocumentTabs();
        isRefreshingFileList = false;
    }

    private void NewFromSelection_Click(object sender, RoutedEventArgs e) => NewFromSelection();

    private void NewFromSelection()
    {
        if (document is null || Waveform.GetSelection() is not Selection selection || selection.Length <= 0)
        {
            MessageBox.Show(this, "Bitte zuerst einen Bereich in der Wellenform markieren.", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        StopPlayback();
        var extracted = document.Extract(selection);
        var newDocument = AudioDocument.FromSamples(extracted.Samples, extracted.Channels, extracted.SampleRate);
        newDocument.DisplayName = $"Unbenannt {nextUntitledNumber++}";
        newDocument.MarkModified();
        openDocuments.Add(newDocument);
        ActivateDocument(newDocument);
        loopPlaybackEnabled = true;
        LoopButton.IsChecked = true;
        HintText.Content = "Neue Datei aus Auswahl – Endlosschleife ist aktiv.";
        HintText.Content = $"{newDocument.DisplayName} aus Auswahl erstellt: {selection.Length:N0} Samples.";
    }

    private void CloseDocument_Click(object sender, RoutedEventArgs e) => CloseDocument();

    private void DocumentTab_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DocumentTabItem item) ActivateDocument(item.Document);
    }

    private void DocumentTabClose_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is DocumentTabItem item) RequestCloseDocument(item.Document);
    }

    private bool CloseDocument()
    {
        if (document is null) return true;
        return RequestCloseDocument(document);
    }

    private SavePromptResult ShowSavePrompt(string displayName)
    {
        var dialog = new Window
        {
            Title = "WaveBench Pro",
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 360,
            ShowInTaskbar = false,
            Background = (Brush)FindResource("Surface"),
            Foreground = (Brush)FindResource("Text"),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13
        };
        var result = SavePromptResult.Cancel;
        var root = new StackPanel { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = $"„{displayName}“ schließen?\n\nWas möchten Sie tun?",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var saveButton = new Button { Content = "Speichern", IsDefault = true, MinWidth = 100, FontSize = 13, Margin = new Thickness(0, 0, 8, 0) };
        var discardButton = new Button
        {
            Content = "Schließen",
            MinWidth = 100,
            FontSize = 13,
            Margin = new Thickness(0, 0, 8, 0),
            ToolTip = "Ohne speichern schließen"
        };
        var cancelButton = new Button { Content = "Abbrechen", IsCancel = true, MinWidth = 100, FontSize = 13 };
        saveButton.Click += (_, _) => { result = SavePromptResult.Save; dialog.DialogResult = true; };
        discardButton.Click += (_, _) => { result = SavePromptResult.Discard; dialog.DialogResult = true; };
        cancelButton.Click += (_, _) => { result = SavePromptResult.Cancel; dialog.DialogResult = false; };
        buttons.Children.Add(saveButton);
        buttons.Children.Add(discardButton);
        buttons.Children.Add(cancelButton);
        root.Children.Add(buttons);
        dialog.Content = root;
        dialog.ShowDialog();
        return result;
    }

    private bool ConfirmCloseAllDocuments()
    {
        foreach (var candidate in openDocuments.ToList())
        {
            var prompt = ShowSavePrompt(candidate.DisplayName);
            if (prompt == SavePromptResult.Cancel) return false;
            if (prompt == SavePromptResult.Save)
            {
                ActivateDocument(candidate);
                if (!SaveCurrent()) return false;
            }
        }
        return true;
    }

    private bool RequestCloseDocument(AudioDocument documentToClose)
    {
        if (recorder.IsRecording && ReferenceEquals(documentToClose, recordingTargetDocument))
        {
            HintText.Content = "Das Aufnahme-Zieldokument kann erst nach dem Stoppen der Aufnahme geschlossen werden.";
            return false;
        }
        var prompt = ShowSavePrompt(documentToClose.DisplayName);
        if (prompt == SavePromptResult.Cancel) return false;
        if (prompt == SavePromptResult.Save)
        {
            ActivateDocument(documentToClose);
            if (!SaveCurrent()) return false;
        }
        var index = openDocuments.IndexOf(documentToClose);
        if (index < 0) return true;
        StopPlayback();
        openDocuments.RemoveAt(index);
        documentToClose.Dispose();
        if (openDocuments.Count == 0)
        {
            document = null;
            Waveform.SetAudio(Array.Empty<float>(), 1, 44100);
            RefreshDocumentTabs();
            StartText.Content = "Start: —";
            EndText.Content = "Ende: —";
            SelectionText.Content = "Dauer: —";
            SampleText.Content = "Samples: —";
            RefreshDocumentInfo();
            RefreshFileList();
            UpdateUndoRedoState();
        }
        else
        {
            var nextIndex = Math.Clamp(index, 0, openDocuments.Count - 1);
            ActivateDocument(openDocuments[nextIndex]);
        }
        return true;
    }

    private void Document_PeakCacheReady(object? sender, EventArgs e)
    {
        if (sender is not AudioDocument source) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ReferenceEquals(document, source)) Waveform.SetPeakCache(source.PeakCache);
        }));
    }

    private void Document_SpectrogramReady(object? sender, EventArgs e)
    {
        if (sender is not AudioDocument source) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ReferenceEquals(document, source)) Waveform.SetSpectrogram(source.Spectrogram);
        }));
    }

    private void Save_Click(object sender, RoutedEventArgs e) => SaveCurrent();

    private bool SaveCurrent()
    {
        if (document is null)
        {
            HintText.Content = "Keine Datei geladen.";
            return false;
        }
        var extension = Path.GetExtension(document.SourcePath);
        if (!string.IsNullOrWhiteSpace(document.SourcePath) &&
            string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                document.SaveInSourceWaveFormat(document.SourcePath);
                document.MarkSaved();
                RefreshFileList();
                HintText.Content = $"Datei gespeichert: {document.SourcePath}";
                return true;
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, $"Speichern fehlgeschlagen: {exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error);
                return false;
            }
        }
        return SaveAsCurrent();
    }

    private void SaveAs_Click(object sender, RoutedEventArgs e) => SaveAsCurrent();

    private bool SaveAsCurrent()
    {
        if (document is null)
        {
            HintText.Content = "Keine Datei geladen.";
            return false;
        }
#if BETA_PROFILE
        var dialog = new SaveFileDialog { Filter = "WAV 16-Bit PCM|*.wav|WAV 24-Bit PCM|*.wav|WAV 32-Bit Float|*.wav", FilterIndex = 2, FileName = $"{Path.GetFileNameWithoutExtension(document.DisplayName)}_bearbeitet.wav" };
#else
        var dialog = new SaveFileDialog { Filter = "WAV 16-Bit PCM|*.wav|WAV 24-Bit PCM|*.wav|WAV 32-Bit Float|*.wav|MP3 192 kBit/s|*.mp3|MP3 320 kBit/s|*.mp3|FLAC|*.flac|OGG Vorbis|*.ogg|Opus|*.opus", FilterIndex = 2, FileName = $"{Path.GetFileNameWithoutExtension(document.DisplayName)}_bearbeitet.wav" };
#endif
        if (dialog.ShowDialog(this) != true) return false;
        try
        {
            switch (dialog.FilterIndex)
            {
                case 4: document.SaveAsMp3(dialog.FileName, 192); break;
                case 5: document.SaveAsMp3(dialog.FileName, 320); break;
                case 6: document.SaveAsFlac(dialog.FileName); break;
                case 7: document.SaveAsOgg(dialog.FileName); break;
                case 8: document.SaveAsOpus(dialog.FileName); break;
                default:
                    var bits = dialog.FilterIndex switch { 1 => 16, 3 => 32, _ => 24 };
                    document.SaveAsWave(dialog.FileName, bits);
                    break;
            }
            document.UpdateSourcePath(dialog.FileName);
            document.MarkSaved();
            RefreshFileList();
            HintText.Content = $"Gespeichert: {Path.GetFileName(dialog.FileName)}";
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Die Datei konnte nicht gespeichert werden.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e) => ExportCurrent();

    private async void ExportCurrent()
    {
        if (document is null)
        {
            HintText.Content = "Keine Datei geladen.";
            return;
        }
        var exportDocument = document;
        var selection = Waveform.GetSelection();
        var dialog = new ExportDialog(this, exportDocument, selection);
        if (dialog.ShowDialog() != true) return;
        // Snapshot WPF control values on their dispatcher before rendering off-thread.
        var format = dialog.Format;
        var exportSelection = dialog.ExportSelection;
        var targetPath = dialog.TargetPath;
        var bitRate = dialog.BitRate;
        var vbrQuality = dialog.VbrQuality;
        var bitsPerSample = dialog.BitsPerSample;
        StopPlayback();
        var workspace = Content as UIElement;
        if (workspace is not null) workspace.IsEnabled = false;
        HintText.Content = "Export wird gerendert…";
        try
        {
            await Task.Run(() =>
            {
                switch (format)
                {
                    case ExportFormat.Mp3:
                        if (exportSelection) exportDocument.SaveRangeAsMp3(targetPath, selection!.Value.StartSample, selection.Value.Length, bitRate);
                        else exportDocument.SaveAsMp3(targetPath, bitRate);
                        break;
                    case ExportFormat.Flac:
                        if (exportSelection) exportDocument.SaveRangeAsFlac(targetPath, selection!.Value.StartSample, selection.Value.Length);
                        else exportDocument.SaveAsFlac(targetPath);
                        break;
                    case ExportFormat.Ogg:
                        if (exportSelection) exportDocument.SaveRangeAsOgg(targetPath, selection!.Value.StartSample, selection.Value.Length, vbrQuality);
                        else exportDocument.SaveAsOgg(targetPath, vbrQuality);
                        break;
                    case ExportFormat.Opus:
                        if (exportSelection) exportDocument.SaveRangeAsOpus(targetPath, selection!.Value.StartSample, selection.Value.Length, vbrQuality);
                        else exportDocument.SaveAsOpus(targetPath, vbrQuality);
                        break;
                    default:
                        if (exportSelection) exportDocument.SaveRangeAsWave(targetPath, selection!.Value.StartSample, selection.Value.Length, bitsPerSample);
                        else exportDocument.SaveAsWave(targetPath, bitsPerSample);
                        break;
                }
            });
            HintText.Content = $"Exportiert: {Path.GetFileName(targetPath)}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Der Export ist fehlgeschlagen.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error);
            HintText.Content = "Export fehlgeschlagen.";
        }
        finally { if (workspace is not null) workspace.IsEnabled = true; }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (MultitrackPanel.Visibility == Visibility.Visible) { PlayMultitrack_Click(sender, e); return; }
        if (document is null) return;
        if (isPlaying) { StopPlayback(); return; }
        var selection = Waveform.GetSelection(); var start = selection?.StartSample ?? Waveform.CursorSample; var count = selection?.Length ?? document.FrameCount - start;
        if (count <= 0) return;
        loopSelection = selection.HasValue || loopPlaybackEnabled;
        loopStartFrame = start;
        loopFrameCount = count;
        try
        {
            StartPlayback(start, count);
            HintText.Content = loopSelection
                ? (selection.HasValue ? "Schleifenwiedergabe der Auswahl." : "Endlosschleife aktiv – Stopp beendet die Wiedergabe.")
                : "Wiedergabe ab Cursorposition.";
        }
        catch (Exception exception) { StopPlayback(); MessageBox.Show(this, $"Die Wiedergabe konnte nicht gestartet werden.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private IWavePlayer CreatePlayer()
    {
        hardwareLifetime.EnsureReady();
        if (AudioHardwareSettings.OutputReselectionRequired)
            throw new InvalidOperationException("Das Ausgabegerät muss unter Optionen → Audio-Hardware erneut ausgewählt werden.");
        return AudioHardwareBackend.Default.CreatePlayback(AudioHardwareSettings.Output);
    }

    private void Player_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (!ReferenceEquals(sender, player)) return;
            if (blockPlayback is { WorkerError: { } renderError } failed)
            { BlockPlaybackFailed(failed, renderError); return; }
            if (e.Exception is not null)
            {
                AudioHardwareSettings.RequireReselection(AudioDirection.Output);
                StopPlayback();
                HintText.Content = $"Wiedergabefehler: {e.Exception.Message}";
                HardwareStatusText.Text = $"Audiogerät gestoppt: {e.Exception.Message}. Bitte unter Audio-Hardware erneut auswählen.";
            }
            else
            {
                // Der Anzeige-Cursor läuft bewusst um die WASAPI-Pufferlatenz hinter
                // der Stoppuhr. PlaybackStopped trifft daher ein, bevor der letzte
                // Timer-Tick die Endposition erreichen kann. Am natürlichen Ende
                // wird die Position explizit vervollständigt.
                var finalFrame = playbackStartFrame + playbackFrameCount;
                if (ReferenceEquals(playbackDocument, document))
                    Waveform.SetCursor((int)Math.Clamp(finalFrame, 0, document?.FrameCount ?? 0), notifySelectionChanged: false);
                StopPlayback();
                HintText.Content = "Wiedergabe beendet.";
            }
        });
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();
    private void LoopToggle_Click(object sender, RoutedEventArgs e)
    {
        loopPlaybackEnabled = LoopButton.IsChecked == true;
        UpdateMultitrackPlaybackRange();
        HintText.Content = loopPlaybackEnabled ? "Endlosschleife aktiviert."
            : MultitrackPanel.Visibility == Visibility.Visible && Sequencer.GetPlaybackRange().HasValue
                ? "Zusätzliche Endlosschleife deaktiviert. Die markierte Auswahl wird weiter wiederholt."
                : "Endlosschleife deaktiviert.";
    }
    private void StopPlayback()
    {
        var retryError = hardwareLifetime.Retry();
        if (retryError is not null) HardwareStatusText.Text = "Audiofreigabe: " + retryError.GetBaseException().Message;
        var retiringBlockPlayback = blockPlayback;
        playbackPreparationVersion++;
        blockPreparationCancellation?.Cancel();
        StopPreview();
        loopSelection = false;
        isPlaying = false;
        playbackClock.Stop(); playbackTimer.Stop();
        DisposePlayer();
        playbackDocument = null;
        multitrackPlaybackDocument = null;
        multitrackMixer = null;
        blockPlayback?.Dispose();
        if (retiringBlockPlayback is not null)
        {
            retiringBlock = retiringBlockPlayback;
            blockStopPending = retiringBlockPlayback.StopAsync(TimeSpan.FromSeconds(2));
            _ = ObservePlaybackCleanupAsync(blockStopPending);
        }
        blockPlayback = null;
        Sequencer.PositionSeconds = 0;
        if (!recorder.IsRecording) ResetLevel();
        HintText.Content = hardwareLifetime.HasPending ? "Audiogerät wird noch nicht sicher freigegeben. Neustart gesperrt." : "Wiedergabe gestoppt.";
    }

    private async void Record_Click(object sender, RoutedEventArgs e)
    {
        if (recordingStopPending) return;
        try
        {
            if (recorder.IsRecording)
            {
                recordingStopPending = true;
                RecordButton.IsEnabled = false;
                var finished = await Task.Run(recorder.Finish);
                recordingStopPending = false;
                RecordButton.IsEnabled = true;
                recorder.LevelsChanged -= Recorder_LevelChanged;
                RecordButton.Content = "●";
                RecordButton.ToolTip = "Aufnahme starten/stoppen (Strg+R)";
                ResetLevel();
                if (finished is not { } recorded || recorded.Samples.Length == 0)
                {
                    HintText.Content = "Aufnahme leer – keine Audiodaten übernommen.";
                    return;
                }

                var targetDocument = recordingTargetDocument;
                var insertAt = recordingTargetFrame;
                recordingTargetDocument = null;
                if (targetDocument is null)
                {
                    var recordedDocument = AudioDocument.FromSamples(recorded.Samples, recorded.Channels, recorded.SampleRate);
                    recordedDocument.DisplayName = $"Aufnahme {nextUntitledNumber++}";
                    recordedDocument.MarkModified();
                    openDocuments.Add(recordedDocument);
                    ActivateDocument(recordedDocument);
                    HintText.Content = "Aufnahme übernommen.";
                }
                else
                {
                    StopPlayback();
                    if (!openDocuments.Contains(targetDocument))
                        throw new InvalidOperationException("Das Zieldokument wurde während der Aufnahme geschlossen. Die Aufnahme wurde nicht eingefügt.");
                    ActivateDocument(targetDocument);
                    var oldFrameCount = targetDocument.FrameCount;
                    targetDocument.Insert(insertAt, recorded);
                    var insertedFrames = targetDocument.FrameCount - oldFrameCount;
                    RefreshAfterEdit((int)Math.Clamp((long)insertAt + insertedFrames, 0, targetDocument.FrameCount));
                    RefreshFileList();
                    HintText.Content = $"Aufnahme an Position {insertAt:N0} eingefügt.";
                }

                RefreshDocumentInfo();
                UpdateSelectionText();
                UpdateUndoRedoState();
                return;
            }

            StopPlayback();
            if (AudioHardwareSettings.Input.DeviceId is null || AudioHardwareSettings.InputReselectionRequired)
            {
                var hardware = new AudioHardwareWindow { Owner = this };
                if (hardware.ShowDialog() != true || AudioHardwareSettings.Input.DeviceId is null) return;
            }
            recordingTargetDocument = document;
            recordingTargetFrame = document is null ? 0 : Waveform.CursorSample;
            recorder.LevelsChanged += Recorder_LevelChanged;
            recorder.Start(AudioHardwareSettings.InputDeviceId);
            HardwareStatusText.Text = recorder.HardwareStatus?.ToString() ?? "Aufnahme aktiv";
            RecordButton.Content = "■";
            RecordButton.ToolTip = "Aufnahme stoppen (Strg+R)";
            HintText.Content = $"Aufnahme läuft ({recorder.CaptureFormat?.SampleRate:N0} Hz, {recorder.CaptureFormat?.Channels} Kanäle)"
                + (AudioHardwareSettings.MonitorInput ? " · ASIO-Monitoring aktiv" : "…");
        }
        catch (Exception exception)
        {
            var wasStoppingRecording = recordingStopPending;
            recordingStopPending = false;
            RecordButton.IsEnabled = true;
            try { recorder.Cancel(); } catch (Exception cleanup) { exception.Data["RecordingCleanup"] = cleanup.Message; }
            recordingTargetDocument = null;
            recorder.LevelsChanged -= Recorder_LevelChanged;
            RecordButton.Content = "●";
            ResetLevel();
            var operation = wasStoppingRecording ? "Die Aufnahme konnte nicht abgeschlossen werden." : "Die Aufnahme konnte nicht gestartet werden.";
            MessageBox.Show(this, $"{operation}\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Recorder_LevelChanged(object? sender, float[] peaks) => Dispatcher.BeginInvoke(new Action(() => SetLevels(peaks)));

    private void SetLevels(IReadOnlyList<float> peaks, IReadOnlyList<float>? rms = null, bool clipped = false) => StereoMeter.SetLevels(peaks, rms, clipped);

    private void ResetLevel() => StereoMeter.Reset();

    private void MeterToggle_Click(object sender, RoutedEventArgs e) =>
        MeterPanel.Visibility = MeterMenuItem.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void SpectrogramToggle_Click(object sender, RoutedEventArgs e)
    {
        var enabled = SpectrogramMenuItem.IsChecked == true;
        Waveform.SetSpectrogramMode(enabled);
        if (enabled && document is not null)
        {
            document.RequestSpectrogram();
            HintText.Content = "Spektrogramm wird berechnet…";
        }
    }

    private void StartPlayback(long start, long count, AudioDocument? sourceDocument = null, IWaveProvider? preparedProvider = null)
    {
        var targetDocument = sourceDocument ?? document ?? throw new InvalidOperationException("Keine Audiodatei geöffnet.");
        DisposePlayer();
        playbackDocument = targetDocument;
        playbackStartFrame = start;
        playbackFrameCount = count;
        lastMeterFrame = start;
        if (ReferenceEquals(targetDocument, document))
            Waveform.SetCursor((int)Math.Clamp(start, 0, targetDocument.FrameCount), notifySelectionChanged: false);
        player = CreatePlayer();
        var sourceProvider = preparedProvider ?? targetDocument.CreateWaveProvider((int)Math.Clamp(start, 0, targetDocument.FrameCount), (int)Math.Clamp(count, 0, targetDocument.FrameCount - Math.Clamp(start, 0, targetDocument.FrameCount)));
        var provider = loopSelection && sourceProvider is IRewindableWaveProvider rewindable
            ? new LoopingWaveProvider(rewindable)
            : sourceProvider;
        var meteredProvider = new MeteringWaveProvider(provider);
        playbackMeter = meteredProvider;
        playbackProvider = meteredProvider;
        player.Init(meteredProvider);
        HardwareStatusText.Text = (player as IAudioPlayback)?.Status.ToString() ?? "Wiedergabe aktiv";
        player.PlaybackStopped += Player_PlaybackStopped;
        playbackClock.Restart();
        player.Play();
        isPlaying = true;
        playbackTimer.Start();
    }

    private void DisposePlayer()
    {
        var oldPlayer = player;
        player = null;
        var oldProvider = playbackProvider;
        playbackProvider = null;
        playbackMeter = null;
        if (oldPlayer is not null)
        {
            oldPlayer.PlaybackStopped -= Player_PlaybackStopped;
            if (hardwareLifetime.Retire(oldPlayer, oldProvider) is { } error)
            {
                AudioHardwareSettings.RequireReselection(AudioDirection.Output);
                HardwareStatusText.Text = "Fehler bei der Audiofreigabe: " + error.GetBaseException().Message;
            }
        }
        else oldProvider?.Dispose();
    }

    private void StopPreview()
    {
        var oldPlayer = previewPlayer;
        previewPlayer = null;
        if (oldPlayer is null) return;
        if (hardwareLifetime.Retire(oldPlayer, null) is { } error)
        {
            AudioHardwareSettings.RequireReselection(AudioDirection.Output);
            HardwareStatusText.Text = "Vorschau noch nicht sicher freigegeben: " + error.GetBaseException().Message;
            if (hardwareLifetime.HasPending) throw new InvalidOperationException(HardwareStatusText.Text, error);
        }
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (playbackDocument is null || !isPlaying) return;
        // Die hörbare Position liegt um die Ausgangspuffer-Latenz hinter der Wanduhr.
        var hardwareLatency = player is IAudioPlayback hardware ? hardware.AverageLatency.TotalMilliseconds : OutputLatencyMs;
        var elapsed = Math.Max(0d, playbackClock.Elapsed.TotalSeconds - hardwareLatency / 1000d);
        var playedFrames = (long)(elapsed * playbackDocument.SampleRate);
        if (player is IAudioPlayback positioned)
        {
            try
            {
                playedFrames = positioned.PositionFrames;
                if (positioned.SourceError is { } sourceError) throw sourceError;
                HardwareStatusText.Text = positioned.Status + $" · Vorlauf-Unterläufe: {positioned.ReadAheadUnderruns}";
            }
            catch (Exception error)
            {
                AudioHardwareSettings.RequireReselection(AudioDirection.Output);
                StopPlayback(); HardwareStatusText.Text = "Audiogerät gestoppt: " + error.Message; return;
            }
        }
        if (blockPlayback is { WorkerError: { } workerError } failed)
        { BlockPlaybackFailed(failed, workerError); return; }
        var frame = blockPlayback is { } block
            ? block.AudibleFrame((int)(block.WaveFormat.SampleRate * hardwareLatency / 1000))
            : multitrackMixer is { } mixer
            ? mixer.AudibleFrame((int)(mixer.WaveFormat.SampleRate * hardwareLatency / 1000))
            : loopSelection && playbackFrameCount > 0
            ? playbackStartFrame + playedFrames % playbackFrameCount
            : Math.Min(playbackStartFrame + playedFrames, playbackStartFrame + playbackFrameCount);
        if (ReferenceEquals(playbackDocument, multitrackPlaybackDocument)) Sequencer.PositionSeconds = frame / (double)playbackDocument.SampleRate;
        if (ReferenceEquals(playbackDocument, document))
            Waveform.SetCursor((int)Math.Clamp(frame, 0, playbackDocument.FrameCount), notifySelectionChanged: false);

        // Diese Werte entstehen im WASAPI-Read, nicht aus der UI-Uhr.
        SetLevels(playbackMeter?.Peaks ?? Array.Empty<float>(), playbackMeter?.Rms, playbackMeter?.Clipped == true);
        HintText.Content = $"▶ Wiedergabe: {FormatTime(TimeSpan.FromSeconds(frame / (double)playbackDocument.SampleRate))}"
            + (blockPlayback is { } buffered ? $" · Puffer {blockBufferBlocks} × 1024 · Unterläufe: {buffered.UnderrunCount}" : "");
    }

    private void Cut_Click(object sender, RoutedEventArgs e) { CopySelection(); DeleteSelection(); }
    private void Copy_Click(object sender, RoutedEventArgs e) => CopySelection();
    private void CopySelection()
    {
        if (document is null || Waveform.GetSelection() is not Selection selection) return;
        AudioClipboard.Set(document.Extract(selection)); HintText.Content = "Auswahl kopiert.";
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelection();
    private void DeleteSelection()
    {
        if (document is null || Waveform.GetSelection() is not Selection selection) return;
        StopPlayback(); document.Delete(selection); RefreshAfterEdit(selection.StartSample);
    }

    private void Paste_Click(object sender, RoutedEventArgs e)
    {
        if (document is null || AudioClipboard.Current is not AudioSelectionData audio) return;
        try { StopPlayback(); var point = Waveform.CursorSample; document.Insert(point, audio); RefreshAfterEdit(point + audio.Samples.Length / audio.Channels); }
        catch (Exception exception) { MessageBox.Show(this, $"Einfügen fehlgeschlagen: {exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Information); }
    }

    private void Undo_Click(object sender, RoutedEventArgs e) { if (document?.CanUndo == true) { StopPlayback(); document.Undo(); RefreshAfterEdit(); } }
    private void Redo_Click(object sender, RoutedEventArgs e) { if (document?.CanRedo == true) { StopPlayback(); document.Redo(); RefreshAfterEdit(); } }
    private void SelectAll_Click(object sender, RoutedEventArgs e) => Waveform.SelectAll();
    private void PreciseSelection_Click(object sender, RoutedEventArgs e)
    {
        if (document is null) return;
        var dialog = new SelectionDialog(this, document.SampleRate, document.FrameCount, Waveform.GetSelection());
        if (dialog.ShowDialog() != true) return;
        Waveform.SetSelection(dialog.Result.StartSample, dialog.Result.EndSample);
        HintText.Content = "Auswahl präzise gesetzt.";
    }
    private void ZoomIn_Click(object sender, RoutedEventArgs e) { if (MultitrackPanel.Visibility == Visibility.Visible) Sequencer.ZoomHorizontal(1.25); else Waveform.ZoomIn(); }
    private void ZoomOut_Click(object sender, RoutedEventArgs e) { if (MultitrackPanel.Visibility == Visibility.Visible) Sequencer.ZoomHorizontal(1 / 1.25); else Waveform.ZoomOut(); }
    private void ZoomVerticalIn_Click(object sender, RoutedEventArgs e) { if (MultitrackPanel.Visibility == Visibility.Visible) Sequencer.ZoomVertical(1.25); else Waveform.ZoomVerticalIn(); }
    private void ZoomVerticalOut_Click(object sender, RoutedEventArgs e) { if (MultitrackPanel.Visibility == Visibility.Visible) Sequencer.ZoomVertical(1 / 1.25); else Waveform.ZoomVerticalOut(); }
    private void ResetHorizontalZoom_Click(object sender, RoutedEventArgs e) { if (MultitrackPanel.Visibility == Visibility.Visible) Sequencer.ResetHorizontalZoom(); else Waveform.ResetHorizontalZoom(); }
    private void ResetVerticalZoom_Click(object sender, RoutedEventArgs e) { if (MultitrackPanel.Visibility == Visibility.Visible) Sequencer.ResetVerticalZoom(); else Waveform.ResetVerticalZoom(); }
    private void ShowAll_Click(object sender, RoutedEventArgs e) => Waveform.ShowAll();

    private void Normalize_Click(object sender, RoutedEventArgs e) { if (document is not null) ApplyEffect(document.Normalize, "Normalisierung abgeschlossen."); }
    private void FadeIn_Click(object sender, RoutedEventArgs e) { if (document is not null) ApplyEffect(document.FadeIn, "Einblenden angewendet."); }
    private void FadeOut_Click(object sender, RoutedEventArgs e) { if (document is not null) ApplyEffect(document.FadeOut, "Ausblenden angewendet."); }
    private void ApplyEffect(Action<Selection?>? effect, string message)
    {
        if (document is null || effect is null) return;
        StopPlayback(); effect(Waveform.GetSelection()); RefreshAfterEdit(); HintText.Content = message;
    }

    private void Gain_Click(object sender, RoutedEventArgs e) => OpenNativeEffect(NativeEffectKind.Gain);
    private void Equalizer_Click(object sender, RoutedEventArgs e) => OpenNativeEffect(NativeEffectKind.Equalizer);
    private void HighPass_Click(object sender, RoutedEventArgs e) => OpenNativeEffect(NativeEffectKind.HighPass);
    private void LowPass_Click(object sender, RoutedEventArgs e) => OpenNativeEffect(NativeEffectKind.LowPass);
    private void Compressor_Click(object sender, RoutedEventArgs e) => OpenNativeEffect(NativeEffectKind.Compressor);
    private void Limiter_Click(object sender, RoutedEventArgs e) => OpenNativeEffect(NativeEffectKind.Limiter);

    private void OpenNativeEffect(NativeEffectKind kind)
    {
        if (document is null)
        {
            MessageBox.Show(this, "Bitte zuerst eine Audiodatei öffnen.", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new EffectDialog(this, kind);
        dialog.PreviewRequested += settings => PreviewNativeEffect(kind, settings);
        try
        {
            if (dialog.ShowDialog() != true) return;
            StopPlayback();
            document.ApplyNativeEffect(Waveform.GetSelection(), kind, dialog.Settings);
            RefreshAfterEdit();
            HintText.Content = $"{NativeEffects.DisplayName(kind)} angewendet.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Der Effekt konnte nicht angewendet werden.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { StopPreview(); }
    }

    private void PreviewNativeEffect(NativeEffectKind kind, EffectSettings settings)
    {
        if (document is null) return;
        StopPreview();
        try
        {
            var selection = Waveform.GetSelection();
            var start = selection?.StartSample ?? 0;
            var frames = selection?.Length ?? document.FrameCount;
            if (frames <= 0) return;
            var block = new float[frames * document.Channels];
            Array.Copy(document.Samples, start * document.Channels, block, 0, block.Length);
            var processed = NativeEffects.Process(settings, block, document.Channels, document.SampleRate, kind);
            previewPlayer = CreatePlayer();
            previewPlayer.Init(new ArrayWaveProvider(processed, 0, processed.Length, WaveFormat.CreateIeeeFloatWaveFormat(document.SampleRate, document.Channels)));
            previewPlayer.Play();
            HintText.Content = $"Vorschau: {NativeEffects.DisplayName(kind)}";
        }
        catch (Exception exception)
        {
            StopPreview();
            MessageBox.Show(this, $"Die Vorschau konnte nicht gestartet werden.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ScanVst_Click(object sender, RoutedEventArgs e) => ScanVstEffects();

    private void ScanVstEffects()
    {
        if (isScanningVst) return;
        isScanningVst = true;
        HintText.Content = "VST3-Scan-Dialog wird geöffnet …";
        try
        {
            var dialog = new VstScanDialog(this);
            if (dialog.ShowDialog() != true) { isScanningVst = false; return; }
            if (dialog.Result is { } effects && effects.Count > 0)
            {
                vstEffects = effects;
                ReplaceItemsSource(VstEffectsList, effects);
                // Übernommene Plugins in die Liste aufnehmen (Cache wurde bereits im Dialog aktualisiert).
                var merged = Vst3EffectCatalog.LoadCache();
                if (merged.Count > effects.Count)
                {
                    vstEffects = merged;
                    ReplaceItemsSource(VstEffectsList, merged);
                }
                HintText.Content = $"{vstEffects.Count} VST3-Effekte verfügbar. Doppelklick auf ein Plugin wendet es auf die geöffnete Audiodatei an.";
            }
            else
            {
                HintText.Content = "Keine VST3-Effekte gefunden.";
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Der VST3-Scan ist fehlgeschlagen.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error);
            HintText.Content = "VST3-Scan fehlgeschlagen.";
        }
        finally { isScanningVst = false; }
    }

    private void VstEffectsList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (VstEffectsList.SelectedItem is not Vst3EffectDescriptor descriptor) return;
        ApplyVstEffect(descriptor);
    }

    private void ApplyVstEffect(Vst3EffectDescriptor descriptor)
    {
        if (document is null)
        {
            MessageBox.Show(this, "Bitte zuerst eine Audiodatei öffnen.", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // Pro Aufruf eine frische Instanz – nie dieselbe Vst3PluginView/HwndHost wiederverwenden.
        RunApplyVstEffect(descriptor);
    }

    private void RunApplyVstEffect(Vst3EffectDescriptor descriptor)
    {
        var selection = Waveform.GetSelection();
        var start = selection?.StartSample ?? 0;
        var frames = selection?.Length ?? document!.FrameCount;
        if (frames <= 0) return;

        VstPluginDialog dialog;
        try
        {
            dialog = new VstPluginDialog(this, descriptor, document!.SampleRate);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, BuildFullErrorText($"Das VST3-Plugin '{descriptor.Name}' konnte nicht geladen werden.", ex), "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error);
            HintText.Content = "VST3-Plugin konnte nicht geladen werden.";
            return;
        }
        if (dialog.LoadErrorText != null)
        {
            dialog.ShowDialog();
            HintText.Content = "VST3-Plugin konnte nicht geladen werden (Details siehe Dialog).";
            return;
        }
        dialog.LivePreviewRequested += livePlugin =>
        {
            if (livePlugin is null) StopPreview();   // Dialog schließt → Live-Vorschau stoppen, bevor das Plugin disposed wird
            else StartLiveVstPreview(livePlugin);
        };
        try
        {
            if (dialog.ShowDialog() != true) return;
            StopPlayback();
            StopPreview();
            var state = dialog.ResultState;
            var rendered = Vst3OfflineRenderer.Render(descriptor, document!.CreateSampleProvider(start, frames), document.SampleRate, document.Channels, frames, state);
            document.ReplaceFrames(start, rendered, frames);
            RefreshAfterEdit(start);
            HintText.Content = $"VST3-Effekt '{descriptor.Name}' angewendet.";
            // Nach OK direkt die angewendete Version abspielen, damit der Effekt sofort hörbar ist
            StartPlayback(start, frames);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, BuildFullErrorText("Der VST3-Effekt konnte nicht angewendet werden.", exception), "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error);
            HintText.Content = "VST3-Effekt fehlgeschlagen.";
        }
        finally { StopPreview(); }
    }

    private static string BuildFullErrorText(string prefix, Exception ex)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(prefix);
        sb.AppendLine();
        var current = ex;
        int depth = 0;
        while (current != null)
        {
            if (depth > 0) sb.AppendLine("--- Innere Exception ---");
            sb.AppendLine($"Typ:        {current.GetType().FullName}");
            sb.AppendLine($"Nachricht:  {current.Message}");
            sb.AppendLine($"Source:     {current.Source}");
            if (!string.IsNullOrWhiteSpace(current.StackTrace))
                sb.AppendLine($"StackTrace:\n{current.StackTrace}");
            if (current.Data?.Count > 0)
            {
                sb.AppendLine("Data:");
                foreach (System.Collections.DictionaryEntry entry in current.Data)
                    sb.AppendLine($"  {entry.Key}: {entry.Value}");
            }
            current = current.InnerException;
            depth++;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Live-Vorschau: spielt die Auswahl (bzw. den ganzen Clip) in Endlosschleife durch das
    /// <b>laufende</b> Plugin-Objekt des offenen Dialogs. Regler im Plugin-UI wirken sofort,
    /// weil NAudio die Parameter-Änderungen beim nächsten Process-Block anwendet.
    /// Wichtig: Das Plugin gehört dem Dialog – beim Schließen des Dialogs wird StopPreview()
    /// aufgerufen, BEVOR das Plugin disposed wird (siehe RunApplyVstEffect/finally + OnClosed-Reihenfolge).
    /// </summary>
    private void StartLiveVstPreview(Vst3Plugin? livePlugin)
    {
        if (document is null || livePlugin is null) return;
        try
        {
            var selection = Waveform.GetSelection();
            var start = selection?.StartSample ?? 0;
            var frames = selection?.Length ?? document.FrameCount;
            if (frames <= 0) return;

            // Quell-Samples in Stereo-Puffer kopieren (Renderer-Logik: Mono→Stereo-Duplizierung)
            var stereo = new float[frames * 2];
            var src = document.CreateSampleProvider(start, frames);
            var buf = new float[4096 * document.Channels];
            var written = 0;
            while (written < frames)
            {
                var want = Math.Min(frames - written, 4096);
                var read = src.Read(buf.AsSpan(0, want * document.Channels));
                if (read <= 0) break;
                var readFrames = read / document.Channels;
                for (var f = 0; f < readFrames; f++)
                {
                    var t = (written + f) * 2;
                    if (document.Channels == 1) { stereo[t] = buf[f]; stereo[t + 1] = buf[f]; }
                    else { stereo[t] = buf[f * 2]; stereo[t + 1] = buf[f * 2 + 1]; }
                }
                written += readFrames;
            }
            if (written <= 0) return;
            Array.Resize(ref stereo, written * 2);

            StartLiveVstPreview(livePlugin, stereo, document.SampleRate);
        }
        catch (Exception exception)
        {
            StopPreview();
            MessageBox.Show(this, $"Die Live-Vorschau konnte nicht gestartet werden.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void StartLiveVstPreview(Vst3Plugin livePlugin, float[] stereo, int sampleRate)
    {
        try
        {
            if (stereo.Length == 0) return;
            StopPlayback();
            StopPreview();
            var frames = stereo.Length / 2;
            var loop = new LoopingSampleProvider(stereo, 2, sampleRate);
            var effect = new Vst3EffectSampleProvider(loop, livePlugin)
            {
                RenderTail = false,
                CompensateLatency = true
            };
            previewPlayer = CreatePlayer();
            previewPlayer.Init(effect);
            previewPlayer.Play();
            HintText.Content = $"Live-Vorschau: {frames / (double)sampleRate:F1}s Schleife durch das Plugin – Regler wirken mit Audiovorlauf. Dialog schließen stoppt.";
        }
        catch (Exception exception)
        {
            StopPreview();
            MessageBox.Show(this, $"Die Live-Vorschau konnte nicht gestartet werden.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshAfterEdit(int? cursor = null)
    {
        if (document is null) return;
        Waveform.UpdateDocument(document); if (cursor.HasValue) Waveform.SetCursor(cursor.Value);
        DurationText.Text = $"Länge: {FormatTime(document.Duration)}"; ViewRangeText.Text = $"0:00.000 – {FormatTime(document.Duration)}"; UpdateSelectionText();
        UpdateUndoRedoState();
        RefreshDocumentTabs();
    }

    private void Waveform_SelectionChanged(object? sender, EventArgs e) => UpdateSelectionText();
    private void UpdateSelectionText()
    {
        if (MultitrackPanel.Visibility == Visibility.Visible)
        {
            var range = Sequencer.GetPlaybackRange();
            StartText.Content = range is { } r ? $"Start: {FormatTime(TimeSpan.FromSeconds(r.Start / (double)multitrack.SampleRate))}" : "Start: —";
            EndText.Content = range is { } end ? $"Ende: {FormatTime(TimeSpan.FromSeconds((end.Start + end.Count) / (double)multitrack.SampleRate))}" : "Ende: —";
            SelectionText.Content = range is { } length ? $"Dauer: {FormatTime(TimeSpan.FromSeconds(length.Count / (double)multitrack.SampleRate))}" : "Dauer: —";
            SampleText.Content = range is null ? "Kein Loop-Bereich markiert" : "Markierter Loop-Bereich";
            return;
        }
        if (document is null || Waveform.GetSelection() is not Selection selection) { StartText.Content = "Start: —"; EndText.Content = "Ende: —"; SelectionText.Content = "Dauer: —"; SampleText.Content = $"Samples: {Waveform.CursorSample:N0} · keine Auswahl"; return; }
        StartText.Content = $"Start: {FormatTime(TimeSpan.FromSeconds(selection.StartSample / (double)document.SampleRate))}";
        EndText.Content = $"Ende: {FormatTime(TimeSpan.FromSeconds(selection.EndSample / (double)document.SampleRate))}";
        SelectionText.Content = $"Dauer: {FormatTime(TimeSpan.FromSeconds(selection.Length / (double)document.SampleRate))}";
        SampleText.Content = $"Samples: {selection.StartSample:N0} – {selection.EndSample:N0} ({selection.Length:N0})";
    }

    private void UpdateUndoRedoState()
    {
        if (document is null) { HintText.Content = "Keine Datei geladen."; return; }
        HintText.Content = document.CanUndo ? "Rückgängig möglich." : "Keine Rückgängig-Schritte.";
    }

    private void AudioHardware_Click(object sender, RoutedEventArgs e)
    {
        if (recorder.IsRecording || recordingStopPending || preparingMultitrack)
        { HintText.Content = "Bitte zuerst Aufnahme bzw. Vorbereitung beenden."; return; }
        StopPlayback();
        if (hardwareLifetime.HasPending) return;
        var dialog = new AudioHardwareWindow { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            HintText.Content = "Audio-Hardwarezuordnung gespeichert.";
            HardwareStatusText.Text = $"Gewählt: {AudioHardwareSettings.Output.Backend} · tatsächliche Gerätewerte nach dem nächsten Start";
        }
    }
    private void HardwareChanged(AudioDeviceChange change)
    {
        if (recorder.IsRecording && recorder.HardwareStatus is { } input && AudioDeviceChangePolicy.RequiresStop(change, AudioHardwareSettings.Input, input, AudioDirection.Input))
        {
            AudioHardwareSettings.RequireReselection(AudioDirection.Input);
            recorder.DeviceLost(new IOException("Das Aufnahmegerät wurde entfernt oder sein Format geändert. Teilaufnahme sichern; Gerät erneut auswählen."));
        }
        var active = player as IAudioPlayback ?? previewPlayer as IAudioPlayback;
        if (active is not null && AudioDeviceChangePolicy.RequiresStop(change, AudioHardwareSettings.Output, active.Status, AudioDirection.Output))
        {
            AudioHardwareSettings.RequireReselection(AudioDirection.Output);
            StopPlayback();
            HardwareStatusText.Text = "Audiogerät oder Standardzuordnung geändert. Bitte unter Audio-Hardware erneut auswählen; kein automatischer Neustart.";
        }
    }
    private void About_Click(object sender, RoutedEventArgs e)
    {
        var version = typeof(MainWindow).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "unbekannt";
        MessageBox.Show(this, $"WaveBench Pro {version}\nAudio-Editor und Mehrspur-Sequenzer für Windows", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
    }
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isRefreshingFileList || isActivatingDocument || MultitrackPanel.Visibility == Visibility.Visible) return;
        var index = FileList.SelectedIndex;
        if (index < 0 || index >= openDocuments.Count) return;
        var selected = openDocuments[index];
        if (!ReferenceEquals(selected, document)) ActivateDocument(selected);
    }

    private void FileListRemove_Click(object sender, RoutedEventArgs e)
    {
        var index = FileList.SelectedIndex;
        if (index < 0 || index >= openDocuments.Count) return;
        RequestCloseDocument(openDocuments[index]);
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        var modifiers = System.Windows.Input.Keyboard.Modifiers;
        var key = e.Key;
        if (key == System.Windows.Input.Key.R && modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
        {
            Record_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (key == System.Windows.Input.Key.M && modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
        {
            MeterMenuItem.IsChecked = !MeterMenuItem.IsChecked;
            MeterToggle_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (key == System.Windows.Input.Key.O && modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            Open_Click(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        if (key == System.Windows.Input.Key.W && modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            CloseDocument();
            e.Handled = true;
            return;
        }
        if (key == System.Windows.Input.Key.S && modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)) SaveAsCurrent();
            else SaveCurrent();
            e.Handled = true;
            return;
        }
        if (key == System.Windows.Input.Key.E && modifiers == System.Windows.Input.ModifierKeys.Control)
        {
            ExportCurrent();
            e.Handled = true;
            return;
        }
        if (key == System.Windows.Input.Key.N && modifiers == (System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Shift))
        {
            NewFromSelection();
            e.Handled = true;
            return;
        }
        if (document is null) return;

        if ((key == System.Windows.Input.Key.Space || key == System.Windows.Input.Key.Tab)) { Play_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (key == System.Windows.Input.Key.Delete) DeleteSelection();
        else if (key == System.Windows.Input.Key.A && modifiers == System.Windows.Input.ModifierKeys.Control) Waveform.SelectAll();
        else if (key == System.Windows.Input.Key.Z && modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
        {
            if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)) Redo_Click(this, new RoutedEventArgs());
            else Undo_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
        else if (key == System.Windows.Input.Key.Y && modifiers == System.Windows.Input.ModifierKeys.Control) { Redo_Click(this, new RoutedEventArgs()); e.Handled = true; }
        else if (key == System.Windows.Input.Key.Left || key == System.Windows.Input.Key.Right)
        {
            var step = key == System.Windows.Input.Key.Right ? 1 : -1;
            if (modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)) step *= 100;
            Waveform.MoveCursor(step, modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift));
            e.Handled = true;
        }
        else if (key == System.Windows.Input.Key.Home) { Waveform.SetCursor(0); e.Handled = true; }
        else if (key == System.Windows.Input.Key.End) { Waveform.SetCursor(document.FrameCount); e.Handled = true; }
    }

    private static string FormatTime(TimeSpan duration) => duration.TotalHours >= 1 ? duration.ToString(@"h\:mm\:ss\.fff") : duration.ToString(@"m\:ss\.fff");
    private static string ChannelText(int channels) => channels == 1 ? "Mono" : channels == 2 ? "Stereo" : $"{channels} Kanäle";
}
