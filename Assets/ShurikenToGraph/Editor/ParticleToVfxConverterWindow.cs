using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.VFX;

public sealed class ParticleToVfxConverterWindow : EditorWindow
{
    enum WindowMode
    {
        Single,
        Batch,
        Validate
    }

    enum BatchListFilter
    {
        All,
        Selected,
        Success,
        Failed,
        Pending,
        Restored
    }

    WindowMode _mode = WindowMode.Single;

    GameObject _sourceObject;
    string _outputFolder = ParticleToVfxConversionService.DefaultOutputFolder;
    bool _createPrefab = true;
    Vector2 _reportScroll;

    string _batchSearchFolder = "Assets";
    readonly List<ParticleToVfxBatchItem> _batchItems = new();
    Vector2 _batchListScroll;
    string _batchStatusMessage;
    bool _batchSkipExisting = true;
    BatchListFilter _batchListFilter = BatchListFilter.All;
    bool _batchConverting;
    int _batchProgressCurrent;
    int _batchProgressTotal;
    string _batchProgressName;
    List<ParticleToVfxBatchItem> _batchActiveTargets;
    int _batchActiveIndex;
    bool _batchCancelRequested;

    float _visualCaptureTime = ParticleToVfxVisualBatchRunner.DefaultCaptureTimeSeconds;
    Vector2 _validationScroll;
    ParticleToVfxVisualValidationManifestFile _validationManifest;

    readonly List<string> _lastReport = new();
    ParticleToVfxConversionResult _lastSingleResult;

    const string LogPrefix = "[ShurikenToGraph] ";

    enum BatchStatusSeverity
    {
        Info,
        Warning,
        Error
    }

    void SetBatchStatus(string message, BatchStatusSeverity severity = BatchStatusSeverity.Info)
    {
        _batchStatusMessage = message;
        if (string.IsNullOrEmpty(message))
            return;

        switch (severity)
        {
            case BatchStatusSeverity.Warning:
                Debug.LogWarning(LogPrefix + message);
                break;
            case BatchStatusSeverity.Error:
                Debug.LogError(LogPrefix + message);
                break;
            default:
                Debug.Log(LogPrefix + message);
                break;
        }
    }

    [MenuItem("Tools/ShurikenToGraph/Particle System To VFX Graph")]
    public static void ShowWindow()
    {
        var window = GetWindow<ParticleToVfxConverterWindow>("Particle → VFX");
        window.minSize = new Vector2(520f, 420f);
        window.Show();
    }

    [MenuItem("GameObject/ShurikenToGraph/Convert Particle System To VFX Graph", false, 20)]
    static void ConvertFromSelection()
    {
        var window = GetWindow<ParticleToVfxConverterWindow>("Particle → VFX");
        window._sourceObject = Selection.activeGameObject;
        window._mode = WindowMode.Single;
        window.Show();
    }

    [MenuItem("GameObject/ShurikenToGraph/Convert Particle System To VFX Graph", true)]
    static bool ConvertFromSelectionValidate()
    {
        return Selection.activeGameObject != null
               && Selection.activeGameObject.GetComponentInChildren<ParticleSystem>(true) != null;
    }

    void OnEnable()
    {
        _outputFolder = ParticleToVfxAssetPaths.NormalizeOutputFolder(_outputFolder);
        _batchSearchFolder = ParticleToVfxAssetPaths.MigrateLegacyAssetPath(_batchSearchFolder);
        RecoverStuckBatchState();

        if (ParticleToVfxVisualBatchRunner.IsRunning)
        {
            ParticleToVfxVisualBatchRunner.RegisterCallbacks(
                message =>
                {
                    SetBatchStatus(message);
                    Repaint();
                },
                () =>
                {
                    ReloadValidationManifest();
                    Repaint();
                });
        }
    }

    void OnGUI()
    {
        EditorGUILayout.LabelField("Shuriken Particle System → VFX Graph", EditorStyles.boldLabel);
        _mode = (WindowMode)GUILayout.Toolbar((int)_mode, new[] { "Single", "Batch", "Validate" });

        EditorGUILayout.Space(6f);

        _outputFolder = EditorGUILayout.TextField("Output Folder", _outputFolder);
        _outputFolder = ParticleToVfxAssetPaths.NormalizeOutputFolder(_outputFolder);
        _createPrefab = EditorGUILayout.Toggle("Create Prefab With VisualEffect", _createPrefab);
        if (_mode == WindowMode.Batch)
            _batchSkipExisting = EditorGUILayout.Toggle("Skip Already Converted", _batchSkipExisting);

        EditorGUILayout.Space(8f);

        if (_mode == WindowMode.Single)
            DrawSingleModeGui();
        else if (_mode == WindowMode.Batch)
            DrawBatchModeGui();
        else
            DrawValidateModeGui();
    }

    void DrawSingleModeGui()
    {
        EditorGUILayout.HelpBox(
            "Convert one scene object or prefab with ParticleSystem components. Multi-emitter effects become subgraphs composed in a parent graph.",
            MessageType.Info);

        _sourceObject = (GameObject)EditorGUILayout.ObjectField("Source", _sourceObject, typeof(GameObject), true);

        using (new EditorGUI.DisabledScope(_sourceObject == null))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Convert To VFX Graph", GUILayout.Height(30f)))
                    ConvertSingle();

                using (new EditorGUI.DisabledScope(!CanCompareSingle()))
                {
                    if (GUILayout.Button(
                            new GUIContent("Compare", "Opens an additive temp scene with Shuriken (left) and VFX (right). Enter Play Mode to preview."),
                            GUILayout.Width(72f),
                            GUILayout.Height(30f)))
                        CompareSingle();
                }
            }
        }

        DrawReportSection();
    }

    void DrawBatchModeGui()
    {
        if (_batchConverting)
            DrawBatchProgress();

        if (!_batchConverting)
        {
            EditorGUILayout.HelpBox(
                "Scan a project folder for prefabs with ParticleSystem components, convert them in batch, then open a temporary side-by-side comparison scene per row. Progress shows as N of Total. Outputs and a manifest in the output folder let you resume after a crash — rescan or click Refresh Existing.",
                MessageType.Info);
        }

        DrawBatchFolderPicker();

        EditorGUILayout.Space(4f);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Scan Folder", GUILayout.Height(24f)))
                ScanBatchFolder();

            using (new EditorGUI.DisabledScope(_batchItems.Count == 0))
            {
                if (GUILayout.Button("Select All", GUILayout.Width(90f)))
                    SetAllBatchSelection(true);
                if (GUILayout.Button("Select None", GUILayout.Width(90f)))
                    SetAllBatchSelection(false);
                if (GUILayout.Button("Refresh Existing", GUILayout.Width(110f)))
                    RefreshExistingConversions();
                if (GUILayout.Button("Rebuild Manifest", GUILayout.Width(120f)))
                    RebuildBatchManifest();
            }
        }

        if (!string.IsNullOrEmpty(_batchStatusMessage) && !_batchConverting)
            EditorGUILayout.HelpBox(_batchStatusMessage, MessageType.None);

        DrawBatchSummary();
        DrawBatchListFilter();
        DrawBatchList();

        var selectedCount = _batchItems.Count(item => item.Selected);
        if (_batchConverting)
            DrawBatchProgress();
        else
        {
            using (new EditorGUI.DisabledScope(selectedCount == 0))
            {
                if (GUILayout.Button($"Convert Selected ({selectedCount})", GUILayout.Height(30f)))
                    BeginBatchConversion();
            }
        }
    }

    void DrawOperationProgress(string title, string statusLine, int current, int total, Action onCancel, string cancelLabel)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.LabelField(title, EditorStyles.boldLabel);

        var countLabel = total > 0 ? $"Item {current} of {total}" : "Working...";
        EditorGUILayout.LabelField(countLabel, EditorStyles.miniBoldLabel);

        if (!string.IsNullOrEmpty(statusLine))
            EditorGUILayout.LabelField(statusLine, EditorStyles.wordWrappedLabel);

        var fraction = total > 0 ? Mathf.Clamp01((float)current / total) : 0f;
        var rect = GUILayoutUtility.GetRect(24f, 24f, GUILayout.ExpandWidth(true));
        EditorGUI.ProgressBar(
            rect,
            fraction,
            total > 0 ? $"{Mathf.RoundToInt(fraction * 100f)}%" : string.Empty);

        if (onCancel != null && GUILayout.Button(cancelLabel, GUILayout.Height(32f)))
            onCancel();

        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(6f);
    }

    void DrawBatchProgress()
    {
        var statusLine = string.IsNullOrEmpty(_batchProgressName)
            ? "Preparing next conversion..."
            : _batchProgressName;
        DrawOperationProgress(
            "Batch Particle → VFX",
            statusLine,
            _batchProgressCurrent,
            _batchProgressTotal,
            RequestBatchCancel,
            "Cancel Batch");
    }

    void DrawVisualValidationProgress()
    {
        var message = SessionState.GetString(
            ParticleToVfxVisualBatchSession.StatusKey,
            "Running visual validation...");
        var index = SessionState.GetInt(ParticleToVfxVisualBatchSession.IndexKey, 0);
        var total = SessionState.GetInt(ParticleToVfxVisualBatchSession.TotalKey, 1);
        DrawOperationProgress(
            "Visual Validation",
            message,
            index,
            total,
            ParticleToVfxVisualBatchRunner.Cancel,
            "Cancel Validation");
    }

    void DrawBatchFolderPicker()
    {
        var folderObject = AssetDatabase.LoadAssetAtPath<DefaultAsset>(_batchSearchFolder);
        EditorGUILayout.BeginHorizontal();
        var picked = (DefaultAsset)EditorGUILayout.ObjectField("Search Folder", folderObject, typeof(DefaultAsset), false);
        if (GUILayout.Button("Browse", GUILayout.Width(72f)))
        {
            var chosen = ParticleToVfxConverterBatch.PickFolderPanel(_batchSearchFolder);
            if (!string.IsNullOrEmpty(chosen))
            {
                _batchSearchFolder = chosen;
                ScanBatchFolder();
            }
        }

        EditorGUILayout.EndHorizontal();

        if (picked != null)
        {
            var path = AssetDatabase.GetAssetPath(picked);
            if (!string.IsNullOrEmpty(path) && AssetDatabase.IsValidFolder(path))
                _batchSearchFolder = path;
        }

        EditorGUILayout.LabelField("Folder path", _batchSearchFolder, EditorStyles.miniLabel);
    }

    void DrawBatchSummary()
    {
        if (_batchItems.Count == 0)
            return;

        var selected = _batchItems.Count(item => item.Selected);
        var success = _batchItems.Count(item => item.Status == ParticleToVfxBatchItemStatus.Success);
        var failed = _batchItems.Count(item => item.Status == ParticleToVfxBatchItemStatus.Failed);
        var pending = _batchItems.Count(item => item.Status == ParticleToVfxBatchItemStatus.Pending);
        var restored = _batchItems.Count(item => item.Status == ParticleToVfxBatchItemStatus.AlreadyConverted);

        EditorGUILayout.LabelField(
            $"Found {_batchItems.Count} effect(s) · Selected {selected} · Success {success} · Failed {failed} · Pending {pending} · Restored {restored}",
            EditorStyles.miniBoldLabel);
    }

    void DrawBatchListFilter()
    {
        if (_batchItems.Count == 0)
            return;

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.PrefixLabel("Show");
        _batchListFilter = (BatchListFilter)EditorGUILayout.EnumPopup(_batchListFilter);

        var filteredCount = GetFilteredBatchItems().Count();
        var suffix = _batchListFilter == BatchListFilter.All
            ? string.Empty
            : $" · Showing {filteredCount} of {_batchItems.Count}";
        EditorGUILayout.LabelField(suffix, EditorStyles.miniLabel, GUILayout.Width(160f));
        EditorGUILayout.EndHorizontal();
    }

    IEnumerable<ParticleToVfxBatchItem> GetFilteredBatchItems()
    {
        switch (_batchListFilter)
        {
            case BatchListFilter.Selected:
                return _batchItems.Where(item => item.Selected);
            case BatchListFilter.Success:
                return _batchItems.Where(item => item.Status == ParticleToVfxBatchItemStatus.Success);
            case BatchListFilter.Failed:
                return _batchItems.Where(item => item.Status == ParticleToVfxBatchItemStatus.Failed);
            case BatchListFilter.Pending:
                return _batchItems.Where(item => item.Status == ParticleToVfxBatchItemStatus.Pending);
            case BatchListFilter.Restored:
                return _batchItems.Where(item => item.Status == ParticleToVfxBatchItemStatus.AlreadyConverted);
            default:
                return _batchItems;
        }
    }

    void DrawBatchList()
    {
        _batchListScroll = EditorGUILayout.BeginScrollView(_batchListScroll, GUILayout.MinHeight(220f));
        if (_batchItems.Count == 0)
        {
            EditorGUILayout.LabelField("No particle prefabs found. Choose a folder and click Scan Folder.", EditorStyles.wordWrappedLabel);
        }
        else
        {
            var filteredItems = GetFilteredBatchItems().ToList();
            if (filteredItems.Count == 0)
            {
                EditorGUILayout.LabelField(
                    $"No items match the '{_batchListFilter}' filter.",
                    EditorStyles.wordWrappedLabel);
            }
            else
            {
                foreach (var item in filteredItems)
                    DrawBatchItemRow(item);
            }
        }

        EditorGUILayout.EndScrollView();
    }

    void DrawBatchItemRow(ParticleToVfxBatchItem item)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);

        EditorGUILayout.BeginHorizontal();
        item.Selected = EditorGUILayout.Toggle(item.Selected, GUILayout.Width(18f));
        EditorGUILayout.LabelField(GetBatchStatusLabel(item), GUILayout.Width(72f));
        EditorGUILayout.LabelField(item.DisplayName, EditorStyles.boldLabel, GUILayout.MinWidth(120f));
        EditorGUILayout.LabelField($"{item.SystemCount} system(s)", GUILayout.Width(80f));
        GUILayout.FlexibleSpace();

        using (new EditorGUI.DisabledScope(!CanCompareItem(item)))
        {
            if (GUILayout.Button(new GUIContent("Compare", "Opens an additive temp scene with Shuriken (left) and VFX (right). Enter Play Mode to preview."), GUILayout.Width(72f)))
                ParticleToVfxComparisonSceneBuilder.OpenSideBySideComparison(item);
        }

        if (GUILayout.Button(item.ShowReport ? "Hide" : "Report", GUILayout.Width(56f)))
            item.ShowReport = !item.ShowReport;

        if (!string.IsNullOrEmpty(item.PrefabAssetPath) && GUILayout.Button("Ping", GUILayout.Width(44f)))
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(item.PrefabAssetPath));

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField(item.PrefabAssetPath, EditorStyles.miniLabel);

        if (item.Result != null)
            DrawBatchItemResultSummary(item);

        if (item.ShowReport)
            DrawBatchItemReport(item);

        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(2f);
    }

    static void DrawBatchItemResultSummary(ParticleToVfxBatchItem item)
    {
        var result = item.Result;
        if (!result.Success)
        {
            EditorGUILayout.HelpBox(result.ErrorMessage ?? "Conversion failed.", MessageType.Error);
            return;
        }

        if (item.Status == ParticleToVfxBatchItemStatus.AlreadyConverted)
            EditorGUILayout.HelpBox("Recovered existing conversion output (manifest or name match).", MessageType.Info);

        EditorGUILayout.LabelField($"VFX: {result.VfxAssetPath}", EditorStyles.miniLabel);
        if (!string.IsNullOrEmpty(result.PrefabPath))
            EditorGUILayout.LabelField($"Prefab: {result.PrefabPath}", EditorStyles.miniLabel);
        if (!string.IsNullOrEmpty(result.ReportPath))
            EditorGUILayout.LabelField($"Report: {result.ReportPath}", EditorStyles.miniLabel);
    }

    void DrawBatchItemReport(ParticleToVfxBatchItem item)
    {
        if (item.Result?.ReportLines == null || item.Result.ReportLines.Count == 0)
        {
            EditorGUILayout.LabelField("No report available.", EditorStyles.wordWrappedLabel);
            return;
        }

        EditorGUILayout.BeginVertical(EditorStyles.textArea);
        foreach (var line in item.Result.ReportLines)
            EditorGUILayout.LabelField(line, EditorStyles.wordWrappedMiniLabel);
        EditorGUILayout.EndVertical();
    }

    static bool CanCompareItem(ParticleToVfxBatchItem item)
    {
        return CanCompareResult(item?.Result);
    }

    static bool CanCompareResult(ParticleToVfxConversionResult result)
    {
        return result != null
               && result.Success
               && (!string.IsNullOrEmpty(result.VfxAssetPath)
                   || result.VfxAsset != null
                   || !string.IsNullOrEmpty(result.PrefabPath)
                   || result.VfxPrefab != null);
    }

    bool CanCompareSingle()
    {
        return _sourceObject != null && CanCompareResult(_lastSingleResult);
    }

    static string GetBatchStatusLabel(ParticleToVfxBatchItem item)
    {
        return item.Status switch
        {
            ParticleToVfxBatchItemStatus.Converting => "Working",
            ParticleToVfxBatchItemStatus.Success => "OK",
            ParticleToVfxBatchItemStatus.AlreadyConverted => item.Result?.RecoveredFromManifest == true ? "OK (log)" : "OK (disk)",
            ParticleToVfxBatchItemStatus.Failed => "Failed",
            ParticleToVfxBatchItemStatus.Skipped => "Skipped",
            _ => "Pending"
        };
    }

    void DrawReportSection()
    {
        if (_lastReport.Count == 0)
            return;

        EditorGUILayout.Space(8f);
        EditorGUILayout.LabelField("Conversion Report", EditorStyles.boldLabel);
        _reportScroll = EditorGUILayout.BeginScrollView(_reportScroll, GUILayout.MinHeight(120f));
        foreach (var line in _lastReport)
            EditorGUILayout.LabelField(line, EditorStyles.wordWrappedLabel);
        EditorGUILayout.EndScrollView();
    }

    void ConvertSingle()
    {
        _lastReport.Clear();
        _lastSingleResult = null;

        var result = ParticleToVfxConversionService.ConvertSource(_sourceObject, _outputFolder, _createPrefab);
        _lastSingleResult = result;
        _lastReport.AddRange(result.ReportLines);

        if (!result.Success)
        {
            if (!string.IsNullOrEmpty(result.ErrorMessage))
                _lastReport.Add($"Error: {result.ErrorMessage}");
            return;
        }

        if (result.VfxAsset != null)
            EditorGUIUtility.PingObject(result.VfxAsset);
        if (result.VfxPrefab != null)
            Selection.activeObject = result.VfxPrefab;

        Debug.Log($"Particle → VFX conversion complete for '{result.SourceName}'. Asset: {result.VfxAssetPath}");
    }

    void CompareSingle()
    {
        if (!CanCompareSingle())
            return;

        ParticleToVfxComparisonSceneBuilder.OpenSideBySideComparison(_lastSingleResult, _sourceObject);
    }

    void ScanBatchFolder()
    {
        _outputFolder = ParticleToVfxAssetPaths.NormalizeOutputFolder(_outputFolder);
        _batchSearchFolder = ParticleToVfxAssetPaths.MigrateLegacyAssetPath(_batchSearchFolder);
        _batchSearchFolder = ParticleToVfxConverterBatch.NormalizeFolderAssetPath(_batchSearchFolder);
        if (string.IsNullOrEmpty(_batchSearchFolder))
        {
            SetBatchStatus("Choose a valid folder under Assets.", BatchStatusSeverity.Warning);
            _batchItems.Clear();
            return;
        }

        _batchItems.Clear();
        _batchItems.AddRange(ParticleToVfxConverterBatch.ScanFolder(_batchSearchFolder));
        ParticleToVfxBatchRecovery.RestoreExistingConversions(_batchItems, _outputFolder);

        var restored = _batchItems.Count(item => item.Status == ParticleToVfxBatchItemStatus.AlreadyConverted);
        if (_batchItems.Count == 0)
        {
            SetBatchStatus(
                $"No prefabs with ParticleSystem found under '{_batchSearchFolder}'.",
                BatchStatusSeverity.Warning);
        }
        else if (restored > 0)
        {
            SetBatchStatus(
                $"Found {_batchItems.Count} prefab(s); {restored} already converted in '{_outputFolder}' (matched by name/manifest).");
        }
        else
        {
            SetBatchStatus(
                $"Found {_batchItems.Count} prefab(s) with ParticleSystem under '{_batchSearchFolder}'.");
        }
    }

    void SetAllBatchSelection(bool selected)
    {
        // Only affect the rows currently visible under the active filter so Select All /
        // Select None operate on what the user can actually see. Materialize first because
        // some filters (e.g. Selected) query the same field we are about to mutate.
        foreach (var item in GetFilteredBatchItems().ToList())
            item.Selected = selected;
    }

    void RefreshExistingConversions()
    {
        if (_batchItems.Count == 0)
            return;

        ParticleToVfxBatchRecovery.RestoreExistingConversions(_batchItems, _outputFolder);
        var restored = _batchItems.Count(item => item.Status == ParticleToVfxBatchItemStatus.AlreadyConverted);
        SetBatchStatus(
            restored > 0
                ? $"Refreshed: {restored} item(s) matched existing outputs in '{_outputFolder}'."
                : $"No existing conversions found in '{_outputFolder}' for the current list.",
            restored > 0 ? BatchStatusSeverity.Info : BatchStatusSeverity.Warning);
        Repaint();
    }

    void RebuildBatchManifest()
    {
        if (_batchItems.Count == 0)
        {
            SetBatchStatus("Scan a folder first.", BatchStatusSeverity.Warning);
            return;
        }

        ParticleToVfxBatchRecovery.RebuildManifestFromDisk(_batchItems, _outputFolder);
        ParticleToVfxBatchRecovery.RestoreExistingConversions(_batchItems, _outputFolder);
        var restored = _batchItems.Count(item => item.Status == ParticleToVfxBatchItemStatus.AlreadyConverted);
        SetBatchStatus($"Rebuilt manifest from disk: {restored} item(s) matched in '{_outputFolder}'.");
        Repaint();
    }

    int _batchCooldownFrames;

    void BeginBatchConversion()
    {
        RecoverStuckBatchState();
        _outputFolder = ParticleToVfxAssetPaths.NormalizeOutputFolder(_outputFolder);

        if (!ParticleToVfxAssetPaths.SeedTemplateExists())
        {
            SetBatchStatus(
                $"Seed template missing at '{ParticleToVfxAssetPaths.SeedTemplateAssetPath}'. Reimport the ShurikenToGraph/Templates folder.",
                BatchStatusSeverity.Error);
            return;
        }

        if (!ParticleToVfxAssetPaths.EnsureAssetFolderExists(_outputFolder))
        {
            SetBatchStatus(
                $"Could not create or find output folder '{_outputFolder}'.",
                BatchStatusSeverity.Error);
            return;
        }

        _batchActiveTargets = _batchItems.Where(item => item.Selected).ToList();
        if (_batchActiveTargets.Count == 0)
        {
            SetBatchStatus(
                "No items selected. Check rows in the batch list or use Select All.",
                BatchStatusSeverity.Warning);
            return;
        }

        var pendingCount = _batchActiveTargets.Count(item =>
            !_batchSkipExisting || !ParticleToVfxBatchRecovery.IsAlreadyConverted(item, _outputFolder));
        if (pendingCount == 0)
        {
            SetBatchStatus(
                $"All {_batchActiveTargets.Count} selected item(s) already exist in '{_outputFolder}'. " +
                "Uncheck 'Skip Already Converted' to force reconversion.",
                BatchStatusSeverity.Warning);
            return;
        }

        _batchActiveIndex = 0;
        _batchCooldownFrames = 0;
        _batchCancelRequested = false;
        _batchConverting = true;
        _batchProgressCurrent = 0;
        _batchProgressTotal = _batchActiveTargets.Count;
        _batchProgressName = string.Empty;
        SetBatchStatus($"Converting {pendingCount} of {_batchActiveTargets.Count} selected item(s)...");
        ParticleToVfxConversionContext.BeginBatch();
        EditorApplication.update += ProcessBatchConversionStep;
        Focus();
    }

    void ProcessBatchConversionStep()
    {
        if (!_batchConverting || _batchActiveTargets == null)
        {
            EditorApplication.update -= ProcessBatchConversionStep;
            return;
        }

        if (_batchCancelRequested)
        {
            FinishBatchConversion(cancelled: true);
            return;
        }

        if (_batchActiveIndex >= _batchActiveTargets.Count)
        {
            FinishBatchConversion();
            return;
        }

        if (_batchCooldownFrames > 0)
        {
            _batchCooldownFrames--;
            return;
        }

        var item = _batchActiveTargets[_batchActiveIndex];
        _batchProgressCurrent = _batchActiveIndex + 1;
        _batchProgressTotal = _batchActiveTargets.Count;
        _batchProgressName = item.DisplayName;
        Repaint();
        Focus();

        try
        {
            if (_batchSkipExisting && ParticleToVfxBatchRecovery.IsAlreadyConverted(item, _outputFolder))
            {
                if (item.Result == null || !item.Result.Success)
                    ParticleToVfxBatchRecovery.TryRestoreItem(item, _outputFolder);

                item.Status = ParticleToVfxBatchItemStatus.AlreadyConverted;
                item.Selected = false;
            }
            else
            {
                item.Status = ParticleToVfxBatchItemStatus.Converting;
                item.ShowReport = false;
                item.Result = ParticleToVfxConversionService.ConvertPrefabAsset(
                    item.PrefabAssetPath,
                    _outputFolder,
                    _createPrefab);

                item.Status = item.Result.Success
                    ? ParticleToVfxBatchItemStatus.Success
                    : ParticleToVfxBatchItemStatus.Failed;

                if (item.Result.Success)
                {
                    ParticleToVfxBatchRecovery.RecordSuccessfulConversion(item, _outputFolder);
                    ParticleToVfxConversionContext.OnBatchItemCompleted();
                }
                else
                {
                    Debug.LogWarning(
                        $"{LogPrefix} Failed '{item.DisplayName}': {item.Result.ErrorMessage ?? "Conversion failed."}");
                }
            }
        }
        catch (Exception ex)
        {
            item.Status = ParticleToVfxBatchItemStatus.Failed;
            item.Result ??= new ParticleToVfxConversionResult();
            item.Result.Success = false;
            item.Result.ErrorMessage = ex.Message;
            Debug.LogException(ex);
        }

        _batchActiveIndex++;
        _batchCooldownFrames = ParticleToVfxConversionContext.BatchCooldownFrames;
        if (_batchCancelRequested || _batchActiveIndex >= _batchActiveTargets.Count)
            FinishBatchConversion(cancelled: _batchCancelRequested);
    }

    void FinishBatchConversion(bool cancelled = false)
    {
        var targets = _batchActiveTargets;
        var processedCount = _batchActiveIndex;
        var successCount = targets?.Count(item =>
            item.Status == ParticleToVfxBatchItemStatus.Success
            || item.Status == ParticleToVfxBatchItemStatus.AlreadyConverted) ?? 0;
        var failedCount = targets?.Count(item => item.Status == ParticleToVfxBatchItemStatus.Failed) ?? 0;
        var skippedCount = targets?.Count(item => item.Status == ParticleToVfxBatchItemStatus.AlreadyConverted) ?? 0;
        var convertedCount = targets?.Count(item => item.Status == ParticleToVfxBatchItemStatus.Success) ?? 0;

        StopBatchConversion(clearProgressBar: true);

        if (cancelled)
        {
            var remaining = targets != null ? Mathf.Max(0, targets.Count - processedCount) : 0;
            SetBatchStatus(
                $"Batch cancelled: {convertedCount} converted, {skippedCount} skipped (existing), {failedCount} failed before cancel. " +
                $"{remaining} item(s) not processed.",
                BatchStatusSeverity.Warning);
        }
        else
        {
            var summary =
                $"Batch complete: {convertedCount} converted, {skippedCount} skipped (existing), {failedCount} failed, {successCount} total OK.";
            SetBatchStatus(summary, failedCount > 0 ? BatchStatusSeverity.Warning : BatchStatusSeverity.Info);
        }

        Repaint();
    }

    void RequestBatchCancel()
    {
        if (!_batchConverting)
            return;

        _batchCancelRequested = true;
        SetBatchStatus("Cancelling batch conversion...", BatchStatusSeverity.Warning);
    }

    void DrawValidateModeGui()
    {
        if (ParticleToVfxVisualBatchRunner.IsRunning)
            DrawVisualValidationProgress();

        if (!ParticleToVfxVisualBatchRunner.IsRunning)
        {
            EditorGUILayout.HelpBox(
                "Runs side-by-side visual captures in Play Mode for one representative per spell family (color variants are grouped). Outputs PNGs and VisualValidationManifest.json under the output folder.",
                MessageType.Info);
        }

        DrawBatchFolderPicker();

        EditorGUILayout.Space(4f);
        using (new EditorGUILayout.HorizontalScope())
        {
            if (GUILayout.Button("Scan Folder", GUILayout.Height(24f)))
                ScanBatchFolder();

            using (new EditorGUI.DisabledScope(_batchItems.Count == 0))
            {
                if (GUILayout.Button("Refresh Existing", GUILayout.Width(110f)))
                    RefreshExistingConversions();
            }
        }

        _visualCaptureTime = EditorGUILayout.Slider("Capture Time (s)", _visualCaptureTime, 0.5f, 5f);

        var convertedCount = _batchItems.Count(ParticleToVfxSpellFamilyKey.CanValidateItem);
        var representativeCount = ParticleToVfxSpellFamilyKey.SelectFamilyRepresentatives(_batchItems).Count;
        EditorGUILayout.LabelField(
            $"Converted {convertedCount} effect(s) · {representativeCount} family representative(s) will be validated",
            EditorStyles.miniBoldLabel);

        if (!string.IsNullOrEmpty(_batchStatusMessage) && !ParticleToVfxVisualBatchRunner.IsRunning)
            EditorGUILayout.HelpBox(_batchStatusMessage, MessageType.None);

        if (ParticleToVfxVisualBatchRunner.IsRunning)
        {
            DrawVisualValidationProgress();
        }
        else
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(representativeCount == 0))
                {
                    if (GUILayout.Button($"Run Visual Batch ({representativeCount})", GUILayout.Height(30f)))
                        BeginVisualBatchValidation();
                }

                if (GUILayout.Button("Reload Results", GUILayout.Width(110f), GUILayout.Height(30f)))
                    ReloadValidationManifest();
            }
        }

        DrawValidationResults();
    }

    void BeginVisualBatchValidation()
    {
        if (_batchItems.Count == 0)
        {
            SetBatchStatus("Scan a folder first.", BatchStatusSeverity.Warning);
            return;
        }

        ParticleToVfxBatchRecovery.RestoreExistingConversions(_batchItems, _outputFolder);
        ParticleToVfxVisualBatchRunner.Begin(
            _batchItems,
            _outputFolder,
            _visualCaptureTime,
            message =>
            {
                SetBatchStatus(message);
                Repaint();
            },
            () =>
            {
                ReloadValidationManifest();
                Repaint();
            });
        Focus();
    }

    void ReloadValidationManifest()
    {
        _validationManifest = ParticleToVfxVisualValidationManifest.Load(_outputFolder);
    }

    void DrawValidationResults()
    {
        if (_validationManifest == null)
            ReloadValidationManifest();

        if (_validationManifest == null || _validationManifest.Entries == null || _validationManifest.Entries.Count == 0)
        {
            EditorGUILayout.Space(6f);
            EditorGUILayout.LabelField("No visual validation results yet.", EditorStyles.wordWrappedLabel);
            return;
        }

        EditorGUILayout.Space(6f);
        EditorGUILayout.LabelField(
            $"Results: {_validationManifest.PassedCount} passed · {_validationManifest.ReviewCount} review · {_validationManifest.FailedCount} failed · {_validationManifest.ErrorCount} errors",
            EditorStyles.boldLabel);

        _validationScroll = EditorGUILayout.BeginScrollView(_validationScroll, GUILayout.MinHeight(220f));
        foreach (var entry in _validationManifest.Entries.OrderBy(entry => entry.FamilyKey, StringComparer.OrdinalIgnoreCase))
            DrawValidationEntryRow(entry);
        EditorGUILayout.EndScrollView();
    }

    void DrawValidationEntryRow(ParticleToVfxVisualValidationEntry entry)
    {
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(entry.Status ?? "Pending", GUILayout.Width(56f));
        EditorGUILayout.LabelField(entry.FamilyKey, EditorStyles.boldLabel);
        GUILayout.FlexibleSpace();
        EditorGUILayout.LabelField($"diff {entry.DiffScore:0.00}", GUILayout.Width(72f));
        if (GUILayout.Button("Ping", GUILayout.Width(44f)) && !string.IsNullOrEmpty(entry.CompareImagePath))
            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(entry.CompareImagePath));
        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField(
            $"Rep: {entry.DisplayName} · variants {entry.VariantCount} · shuriken {entry.ShurikenAliveCount} · vfx {entry.VfxAliveCount}",
            EditorStyles.miniLabel);

        if (!string.IsNullOrEmpty(entry.Notes))
            EditorGUILayout.LabelField(entry.Notes, EditorStyles.wordWrappedMiniLabel);

        EditorGUILayout.EndVertical();
        EditorGUILayout.Space(2f);
    }

    void OnDisable()
    {
        if (EditorApplication.isPlayingOrWillChangePlaymode)
            return;

        StopBatchConversion(clearProgressBar: true);
        if (ParticleToVfxVisualBatchRunner.IsRunning)
            ParticleToVfxVisualBatchRunner.Cancel();
    }

    void RecoverStuckBatchState()
    {
        if (!_batchConverting)
            return;

        StopBatchConversion(clearProgressBar: false);
        SetBatchStatus(
            "Recovered interrupted batch conversion. Click Convert Selected to continue.",
            BatchStatusSeverity.Warning);
    }

    void StopBatchConversion(bool clearProgressBar)
    {
        EditorApplication.update -= ProcessBatchConversionStep;
        if (clearProgressBar)
            EditorUtility.ClearProgressBar();

        if (_batchConverting)
            ParticleToVfxConversionContext.EndBatch();

        _batchConverting = false;
        _batchActiveTargets = null;
        _batchActiveIndex = 0;
        _batchCooldownFrames = 0;
        _batchCancelRequested = false;
    }
}
