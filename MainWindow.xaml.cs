using Microsoft.Win32;
using NAudio.CoreAudioApi;
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
    private IWavePlayer? previewPlayer;
    private bool isPlaying;
    private readonly DispatcherTimer playbackTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly Stopwatch playbackClock = new();
    private int playbackStartFrame;
    private int playbackFrameCount;
    private int lastMeterFrame;
    // Puffergröße des WasapiOut: Anzeige und Cursor werden um diese Latenz
    // gegen das tatsächlich hörbare Signal zurückgerechnet.
    private const int OutputLatencyMs = 100;
    private bool loopSelection;
    private int loopStartFrame;
    private int loopFrameCount;
    private readonly RecordingEngine recorder = new();
    private IReadOnlyList<Vst3EffectDescriptor> vstEffects = Array.Empty<Vst3EffectDescriptor>();
    private bool isScanningVst;

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
        AllowDrop = true;
        DragOver += (_, e) => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) OpenAudio(files[0]); };
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Closing += (_, e) =>
        {
            if (!ConfirmCloseAllDocuments())
            {
                e.Cancel = true;
                return;
            }
            StopPlayback();
            recorder.Dispose();
        };
        playbackTimer.Tick += PlaybackTimer_Tick;
        Loaded += (_, _) =>
        {
            var startupFile = Environment.GetCommandLineArgs().Skip(1).FirstOrDefault(File.Exists);
            if (startupFile is not null) OpenAudio(startupFile);
            ScanVstEffects();
        };
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "Audiodateien|*.wav;*.mp3;*.m4a;*.aac;*.wma;*.flac|Alle Dateien|*.*" };
        if (dialog.ShowDialog(this) == true) OpenAudio(dialog.FileName);
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
        Waveform.SetAudio(documentToActivate.Samples, documentToActivate.Channels, documentToActivate.SampleRate);
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
        if (openDocuments.Count == 0)
        {
            document = null;
            Waveform.SetAudio(Array.Empty<float>(), 1, 44100);
            RefreshDocumentTabs();
            StartText.Content = "Start: —";
            EndText.Content = "Ende: —";
            SelectionText.Content = "Auswahl: —";
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
                document.SaveAsWave(document.SourcePath, 32);
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
        var dialog = new SaveFileDialog { Filter = "WAV 16-Bit PCM|*.wav|WAV 24-Bit PCM|*.wav|WAV 32-Bit Float|*.wav|MP3 192 kBit/s|*.mp3|MP3 320 kBit/s|*.mp3", FilterIndex = 2, FileName = $"{Path.GetFileNameWithoutExtension(document.DisplayName)}_bearbeitet.wav" };
        if (dialog.ShowDialog(this) != true) return false;
        try
        {
            if (dialog.FilterIndex >= 4)
            {
                var bitRate = dialog.FilterIndex == 5 ? 320 : 192;
                document.SaveAsMp3(dialog.FileName, bitRate);
            }
            else
            {
                var bits = dialog.FilterIndex switch { 1 => 16, 3 => 32, _ => 24 };
                document.SaveAsWave(dialog.FileName, bits);
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

    private void ExportCurrent()
    {
        if (document is null)
        {
            HintText.Content = "Keine Datei geladen.";
            return;
        }
        var selection = Waveform.GetSelection();
        var dialog = new ExportDialog(this, document, selection);
        if (dialog.ShowDialog() != true) return;
        try
        {
            StopPlayback();
            if (dialog.IsMp3)
            {
                if (dialog.ExportSelection)
                    document.SaveRangeAsMp3(dialog.TargetPath, selection!.Value.StartSample, selection.Value.Length, dialog.BitRate);
                else
                    document.SaveAsMp3(dialog.TargetPath, dialog.BitRate);
            }
            else
            {
                if (dialog.ExportSelection)
                    document.SaveRangeAsWave(dialog.TargetPath, selection!.Value.StartSample, selection.Value.Length, dialog.BitsPerSample);
                else
                    document.SaveAsWave(dialog.TargetPath, dialog.BitsPerSample);
            }
            HintText.Content = $"Exportiert: {Path.GetFileName(dialog.TargetPath)}";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Der Export ist fehlgeschlagen.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error);
            HintText.Content = "Export fehlgeschlagen.";
        }
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        if (document is null) return;
        if (isPlaying) { StopPlayback(); return; }
        var selection = Waveform.GetSelection(); var start = selection?.StartSample ?? Waveform.CursorSample; var count = selection?.Length ?? document.FrameCount - start;
        if (count <= 0) return;
        loopSelection = selection.HasValue;
        loopStartFrame = start;
        loopFrameCount = count;
        try
        {
            StartPlayback(start, count);
            HintText.Content = selection.HasValue ? "Schleifenwiedergabe der Auswahl." : "Wiedergabe ab Cursorposition.";
        }
        catch (Exception exception) { StopPlayback(); MessageBox.Show(this, $"Die Wiedergabe konnte nicht gestartet werden.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private static IWavePlayer CreatePlayer()
    {
        using var enumerator = new MMDeviceEnumerator();
        var output = string.IsNullOrWhiteSpace(AudioHardwareSettings.OutputDeviceId)
            ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
            : enumerator.GetDevice(AudioHardwareSettings.OutputDeviceId);
        return new WasapiOut(output, AudioClientShareMode.Shared, false, OutputLatencyMs);
    }

    private void Player_PlaybackStopped(object? sender, StoppedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (e.Exception is not null)
            {
                StopPlayback();
                HintText.Content = $"Wiedergabefehler: {e.Exception.Message}";
            }
            else if (loopSelection && isPlaying && document is not null)
            {
                StartPlayback(loopStartFrame, loopFrameCount);
                HintText.Content = "Schleifenwiedergabe der Auswahl.";
            }
            else
            {
                StopPlayback();
                HintText.Content = "Wiedergabe beendet.";
            }
        });
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => StopPlayback();
    private void StopPlayback()
    {
        StopPreview();
        loopSelection = false;
        isPlaying = false;
        playbackClock.Stop(); playbackTimer.Stop();
        DisposePlayer();
        if (!recorder.IsRecording) ResetLevel();
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (recorder.IsRecording)
            {
                var finished = recorder.Finish();
                recorder.LevelsChanged -= Recorder_LevelChanged;
                RecordButton.Content = "●";
                RecordButton.ToolTip = "Aufnahme starten/stoppen (Strg+R)";
                ResetLevel();
                if (finished is not { } recorded || recorded.Samples.Length == 0)
                {
                    HintText.Content = "Aufnahme leer – keine Audiodaten übernommen.";
                    return;
                }

                if (document is null)
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
                    var insertAt = Waveform.CursorSample;
                    StopPlayback();
                    document.Insert(insertAt, recorded);
                    RefreshAfterEdit(insertAt + recorded.Samples.Length / recorded.Channels);
                    RefreshFileList();
                    HintText.Content = $"Aufnahme an Position {insertAt:N0} eingefügt.";
                }

                RefreshDocumentInfo();
                UpdateSelectionText();
                UpdateUndoRedoState();
                return;
            }

            StopPlayback();
            recorder.LevelsChanged += Recorder_LevelChanged;
            recorder.Start(AudioHardwareSettings.InputDeviceId);
            RecordButton.Content = "■";
            RecordButton.ToolTip = "Aufnahme stoppen (Strg+R)";
            HintText.Content = $"Aufnahme läuft ({recorder.CaptureFormat?.SampleRate:N0} Hz, {recorder.CaptureFormat?.Channels} Kanäle)…";
        }
        catch (Exception exception)
        {
            recorder.Cancel();
            recorder.LevelsChanged -= Recorder_LevelChanged;
            RecordButton.Content = "●";
            ResetLevel();
            MessageBox.Show(this, $"Die Aufnahme konnte nicht gestartet werden.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Recorder_LevelChanged(object? sender, float[] peaks) => Dispatcher.BeginInvoke(new Action(() => SetLevels(peaks)));

    private void SetLevels(float[] peaks) => StereoMeter.SetLevels(peaks);

    private void ResetLevel() => StereoMeter.Reset();

    private void MeterToggle_Click(object sender, RoutedEventArgs e) =>
        MeterPanel.Visibility = MeterMenuItem.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

    private void StartPlayback(int start, int count)
    {
        DisposePlayer();
        playbackStartFrame = start;
        playbackFrameCount = count;
        lastMeterFrame = start;
        Waveform.SetCursor(start);
        player = CreatePlayer();
        player.Init(document!.CreateWaveProvider(start, count));
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
        if (oldPlayer is null) return;
        oldPlayer.PlaybackStopped -= Player_PlaybackStopped; oldPlayer.Stop(); oldPlayer.Dispose();
    }

    private void StopPreview()
    {
        var oldPlayer = previewPlayer;
        previewPlayer = null;
        if (oldPlayer is null) return;
        oldPlayer.Stop();
        oldPlayer.Dispose();
    }

    private void PlaybackTimer_Tick(object? sender, EventArgs e)
    {
        if (document is null || !isPlaying) return;
        // Die hörbare Position liegt um die Ausgangspuffer-Latenz hinter der Wanduhr.
        var elapsed = Math.Max(0d, playbackClock.Elapsed.TotalSeconds - OutputLatencyMs / 1000d);
        var playedFrames = (int)(elapsed * document.SampleRate);
        var frame = Math.Min(playbackStartFrame + playedFrames, playbackStartFrame + playbackFrameCount);
        Waveform.SetCursor(frame);

        // Peaks aus dem Intervall seit dem letzten Tick: lückenlos, ohne
        // Vorausschau auf noch nicht gehörte Samples.
        var channels = document.Channels;
        var from = Math.Clamp(lastMeterFrame * channels, 0, document.Samples.Length);
        var to = Math.Clamp(Math.Max(frame, lastMeterFrame) * channels, from, document.Samples.Length);
        var peaks = new float[channels];
        for (var index = from; index < to; index++)
        {
            var channel = (index - from) % channels;
            peaks[channel] = Math.Max(peaks[channel], Math.Abs(document.Samples[index]));
        }
        lastMeterFrame = Math.Max(lastMeterFrame, frame);
        SetLevels(peaks);
        HintText.Content = $"▶ Wiedergabe: {FormatTime(TimeSpan.FromSeconds(frame / (double)document.SampleRate))}";
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
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Waveform.ZoomIn();
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Waveform.ZoomOut();
    private void ZoomVerticalIn_Click(object sender, RoutedEventArgs e) => Waveform.ZoomVerticalIn();
    private void ZoomVerticalOut_Click(object sender, RoutedEventArgs e) => Waveform.ZoomVerticalOut();
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

    private async void ScanVstEffects()
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
                HintText.Content = effects.Count == 0
                    ? "Keine VST3-Effekte gefunden."
                    : $"{effects.Count} VST3-Effekte gefunden. Doppelklick wendet sie an.";
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
        var selection = Waveform.GetSelection();
        var start = selection?.StartSample ?? 0;
        var frames = selection?.Length ?? document.FrameCount;
        if (frames <= 0) return;

        var dialog = new VstPluginDialog(this, descriptor);
        dialog.PreviewRequested += state => PreviewVstEffect(descriptor, state);
        try
        {
            if (dialog.ShowDialog() != true) return;
            StopPlayback();
            var state = dialog.ResultState;
            var rendered = Vst3OfflineRenderer.Render(descriptor, document.CreateSampleProvider(start, frames), document.Channels, frames, state);
            document.ReplaceFrames(start, rendered, frames);
            RefreshAfterEdit(start);
            HintText.Content = $"VST3-Effekt '{descriptor.Name}' angewendet.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Der VST3-Effekt konnte nicht angewendet werden.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error);
            HintText.Content = "VST3-Effekt fehlgeschlagen.";
        }
        finally { StopPreview(); }
    }

    private async void PreviewVstEffect(Vst3EffectDescriptor descriptor, byte[]? state)
    {
        if (document is null) return;
        StopPreview();
        try
        {
            var selection = Waveform.GetSelection();
            var start = selection?.StartSample ?? 0;
            var frames = selection?.Length ?? document.FrameCount;
            if (frames <= 0) return;
            var rendered = await Task.Run(() => Vst3OfflineRenderer.Render(descriptor, document.CreateSampleProvider(start, frames), document.Channels, frames, state));
            await Dispatcher.InvokeAsync(() =>
            {
                previewPlayer = CreatePlayer();
                previewPlayer.Init(new ArrayWaveProvider(rendered, 0, rendered.Length, WaveFormat.CreateIeeeFloatWaveFormat(document.SampleRate, document.Channels)));
                previewPlayer.Play();
                HintText.Content = $"Vorschau: {descriptor.DisplayName}";
            });
        }
        catch (Exception exception)
        {
            StopPreview();
            MessageBox.Show(this, $"Die VST3-Vorschau konnte nicht gestartet werden.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void RefreshAfterEdit(int? cursor = null)
    {
        if (document is null) return;
        Waveform.UpdateAudio(document.Samples, document.Channels, document.SampleRate); if (cursor.HasValue) Waveform.SetCursor(cursor.Value);
        DurationText.Text = $"Länge: {FormatTime(document.Duration)}"; ViewRangeText.Text = $"0:00.000 – {FormatTime(document.Duration)}"; UpdateSelectionText();
        UpdateUndoRedoState();
        RefreshDocumentTabs();
    }

    private void Waveform_SelectionChanged(object? sender, EventArgs e) => UpdateSelectionText();
    private void UpdateSelectionText()
    {
        if (document is null || Waveform.GetSelection() is not Selection selection) { StartText.Content = "Start: —"; EndText.Content = "Ende: —"; SelectionText.Content = "Auswahl: —"; SampleText.Content = $"Samples: {Waveform.CursorSample:N0} · keine Auswahl"; return; }
        StartText.Content = $"Start: {FormatTime(TimeSpan.FromSeconds(selection.StartSample / (double)document.SampleRate))}";
        EndText.Content = $"Ende: {FormatTime(TimeSpan.FromSeconds(selection.EndSample / (double)document.SampleRate))}";
        SelectionText.Content = $"Auswahl: {FormatTime(TimeSpan.FromSeconds(selection.Length / (double)document.SampleRate))}";
        SampleText.Content = $"Samples: {selection.StartSample:N0} – {selection.EndSample:N0} ({selection.Length:N0})";
    }

    private void UpdateUndoRedoState()
    {
        if (document is null) { HintText.Content = "Keine Datei geladen."; return; }
        HintText.Content = document.CanUndo ? "Rückgängig möglich." : "Keine Rückgängig-Schritte.";
    }

    private void AudioHardware_Click(object sender, RoutedEventArgs e) { var dialog = new AudioHardwareWindow { Owner = this }; if (dialog.ShowDialog() == true) HintText.Content = "Audio-Hardwarezuordnung übernommen."; }
    private void About_Click(object sender, RoutedEventArgs e) => MessageBox.Show(this, "WaveBench Pro\nKlassische Einzelspuren-Bearbeitung für Windows 11", "Info", MessageBoxButton.OK, MessageBoxImage.Information);
    private void Exit_Click(object sender, RoutedEventArgs e) => Close();
    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isRefreshingFileList || isActivatingDocument) return;
        var index = FileList.SelectedIndex;
        if (index < 0 || index >= openDocuments.Count) return;
        var selected = openDocuments[index];
        if (!ReferenceEquals(selected, document)) ActivateDocument(selected);
    }

    private void MainWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
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
