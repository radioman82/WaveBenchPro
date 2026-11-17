using Microsoft.Win32;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace WellenEditor;

public partial class MainWindow : Window
{
    private AudioDocument? document;
    private IWavePlayer? player;
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

    public MainWindow()
    {
        InitializeComponent();
        AllowDrop = true;
        DragOver += (_, e) => e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        Drop += (_, e) => { if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0) OpenAudio(files[0]); };
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        Closing += (_, _) => { StopPlayback(); recorder.Dispose(); };
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
        try
        {
            StopPlayback(); document = AudioDocument.Load(fileName); Waveform.SetAudio(document.Samples, document.Channels, document.SampleRate);
            FileNameText.Text = Path.GetFileName(fileName); FileList.Items.Clear(); FileList.Items.Add(new ListBoxItem { Content = Path.GetFileName(fileName) });
            FormatText.Text = $"{document.SampleRate:N0} Hz · {ChannelText(document.Channels)} · 32-bit float";
            DurationText.Text = $"Länge: {FormatTime(document.Duration)}"; ViewRangeText.Text = $"0:00.000 – {FormatTime(document.Duration)}";
            HintText.Content = "Bereit. Mit der Maus einen Bereich markieren."; UpdateSelectionText();
        }
        catch (Exception exception) { MessageBox.Show(this, $"Die Datei konnte nicht geöffnet werden.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (document is null) return;
        var dialog = new SaveFileDialog { Filter = "WAV 16-Bit PCM|*.wav|WAV 24-Bit PCM|*.wav|WAV 32-Bit Float|*.wav|MP3 192 kBit/s|*.mp3|MP3 320 kBit/s|*.mp3", FilterIndex = 2, FileName = $"{Path.GetFileNameWithoutExtension(document.SourcePath)}_bearbeitet.wav" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (dialog.FilterIndex >= 4)
            {
                var bitRate = dialog.FilterIndex == 5 ? 320 : 192;
                document.SaveAsMp3(dialog.FileName, bitRate);
                HintText.Content = $"Gespeichert: {Path.GetFileName(dialog.FileName)} (MP3 {bitRate} kBit/s)";
            }
            else
            {
                var bits = dialog.FilterIndex switch { 1 => 16, 3 => 32, _ => 24 };
                document.SaveAsWave(dialog.FileName, bits);
                HintText.Content = $"Gespeichert: {Path.GetFileName(dialog.FileName)} ({bits}-Bit)";
            }
        }
        catch (Exception exception) { MessageBox.Show(this, $"Die Datei konnte nicht gespeichert werden.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error); }
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
                    document = AudioDocument.FromSamples(recorded.Samples, recorded.Channels, recorded.SampleRate);
                    Waveform.SetAudio(document.Samples, document.Channels, document.SampleRate);
                    FileNameText.Text = "Aufnahme";
                    FileList.Items.Clear();
                    FileList.Items.Add(new ListBoxItem { Content = "Aufnahme" });
                    HintText.Content = "Aufnahme übernommen.";
                }
                else
                {
                    var insertAt = Waveform.CursorSample;
                    StopPlayback();
                    document.Insert(insertAt, recorded);
                    RefreshAfterEdit(insertAt + recorded.Samples.Length / recorded.Channels);
                    HintText.Content = $"Aufnahme an Position {insertAt:N0} eingefügt.";
                }

                FormatText.Text = $"{document.SampleRate:N0} Hz · {ChannelText(document.Channels)} · 32-bit float";
                DurationText.Text = $"Länge: {FormatTime(document.Duration)}";
                ViewRangeText.Text = $"0:00.000 – {FormatTime(document.Duration)}";
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

    private void ScanVst_Click(object sender, RoutedEventArgs e) => ScanVstEffects();

    private async void ScanVstEffects()
    {
        if (isScanningVst) return;
        isScanningVst = true;
        HintText.Content = "VST3-Plugins werden gescannt …";
        try
        {
            var effects = await Task.Run(() => Vst3EffectCatalog.Scan());
            vstEffects = effects;
            VstEffectsList.ItemsSource = effects;
            HintText.Content = effects.Count == 0
                ? "Keine VST3-Effekte gefunden."
                : $"{effects.Count} VST3-Effekte gefunden. Doppelklick wendet sie an.";
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
        StopPlayback();
        HintText.Content = $"VST3-Effekt '{descriptor.Name}' wird gerendert …";
        try
        {
            var rendered = Vst3OfflineRenderer.Render(descriptor, document.CreateSampleProvider(start, frames), document.Channels, frames);
            document.ReplaceFrames(start, rendered, frames);
            RefreshAfterEdit(start);
            HintText.Content = $"VST3-Effekt '{descriptor.Name}' angewendet.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Der VST3-Effekt konnte nicht angewendet werden.\n\n{exception.Message}", "WaveBench Pro", MessageBoxButton.OK, MessageBoxImage.Error);
            HintText.Content = "VST3-Effekt fehlgeschlagen.";
        }
    }

    private void RefreshAfterEdit(int? cursor = null)
    {
        if (document is null) return;
        Waveform.UpdateAudio(document.Samples, document.Channels, document.SampleRate); if (cursor.HasValue) Waveform.SetCursor(cursor.Value);
        DurationText.Text = $"Länge: {FormatTime(document.Duration)}"; ViewRangeText.Text = $"0:00.000 – {FormatTime(document.Duration)}"; UpdateSelectionText();
        UpdateUndoRedoState();
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
    private void FileList_SelectionChanged(object sender, SelectionChangedEventArgs e) { }

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
