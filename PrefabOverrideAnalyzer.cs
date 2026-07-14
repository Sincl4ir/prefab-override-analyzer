using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Sirenix.OdinInspector;
using Sirenix.OdinInspector.Editor;
using UnityEditor;
using UnityEngine;

//Finds and removes no-op prefab overrides that bloat your prefab files without touching real overrides.
internal sealed class PrefabOverrideAnalyzer : OdinEditorWindow
{
    private const string MenuPath = "Tools/Prefab Override Analyzer";

    [SerializeField]
    [Required]
    [AssetsOnly]
    [AssetSelector(Paths = "Assets")]
    [LabelText("Target Prefab")]
    [PropertyOrder(0)]
    [InfoBox(
        "Assign a prefab, then Run Analysis (read-only) to see how many overrides are redundant. "
        + "Revert Redundant Overrides removes only the no-op overrides and saves the prefab.",
        InfoMessageType.None
    )]
    private GameObject _prefab;

    [SerializeField]
    [ReadOnly]
    [MultiLineProperty(16)]
    [LabelText("Result")]
    [PropertyOrder(30)]
    private string _result = "";

    [Button(ButtonSizes.Large)]
    [GUIColor(0.4f, 0.7f, 1f)]
    [PropertyOrder(10)]
    [DisableIf("@_prefab == null")]
    private void RunAnalysis()
    {
        if (_prefab == null)
        {
            return;
        }

        var path = AssetDatabase.GetAssetPath(_prefab);
        var root = PrefabUtility.LoadPrefabContents(path);

        try
        {
            var stats = Scan(root, null);
            _result = BuildReport(path, stats, false);
            Debug.Log(_result);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    [Button(ButtonSizes.Large)]
    [GUIColor(1f, 0.6f, 0.3f)]
    [PropertyOrder(11)]
    [DisableIf("@_prefab == null")]
    private void RevertRedundantOverrides()
    {
        if (_prefab == null)
        {
            return;
        }

        var path = AssetDatabase.GetAssetPath(_prefab);
        var sizeBefore = FileSizeKb(path);

        var root = PrefabUtility.LoadPrefabContents(path);

        try
        {
            // First pass: count what we'd remove so the confirmation is informative.
            var preview = Scan(root, null);

            if (preview.Redundant == 0)
            {
                _result = $"No redundant overrides found in {_prefab.name}. Nothing to remove.";
                Debug.Log(_result);

                return;
            }

            var proceed = EditorUtility.DisplayDialog(
                "Revert Redundant Overrides",
                $"Remove {preview.Redundant} redundant (no-op) overrides from '{_prefab.name}'?\n\n"
                + $"Real overrides ({preview.Real}) and unknown ({preview.Unknown}) are kept. "
                + "This modifies the prefab asset on disk.",
                "Remove",
                "Cancel"
            );

            if (!proceed)
            {
                _result = "Cancelled.";

                return;
            }

            if (!AssetDatabase.MakeEditable(path))
            {
                _result =
                    $"Could not make '{path}' editable. Aborted; no changes made.";
                Debug.LogError(_result);

                return;
            }

            // Second pass: actually strip the redundant modifications from each instance.
            var stats = Scan(root, StripRedundant);

            PrefabUtility.SaveAsPrefabAsset(root, path, out var success);

            if (!success)
            {
                _result = $"SaveAsPrefabAsset FAILED for '{path}'. No changes persisted.";
                Debug.LogError(_result);

                return;
            }

            AssetDatabase.Refresh();

            var sizeAfter = FileSizeKb(path);
            var report = BuildReport(path, stats, true);

            report +=
                $"\n  REMOVED {stats.Redundant} redundant overrides."
                + $"\n  File size: {sizeBefore}K -> {sizeAfter}K ({sizeBefore - sizeAfter}K saved).";
            _result = report;
            Debug.Log(_result);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(root);
        }
    }

    private sealed class Stats
    {
        public int Instances;
        public int Real;
        public int Redundant;
        public readonly Dictionary<string, int> RedundantByPath = new();
        public int Total;
        public int Unknown;
    }

    // Walks every outermost prefab instance under root, classifies its property modifications, and (optionally) applies
    // a transform that returns the modifications to keep. Returns aggregate stats.
    private static Stats Scan(GameObject root, System.Func<PropertyModification[], PropertyModification[]> transform)
    {
        var stats = new Stats();

        foreach (var t in root.GetComponentsInChildren<Transform>(true))
        {
            var go = t.gameObject;

            if (!PrefabUtility.IsOutermostPrefabInstanceRoot(go))
            {
                continue;
            }

            stats.Instances++;

            var mods = PrefabUtility.GetPropertyModifications(go);

            if (mods == null)
            {
                continue;
            }

            foreach (var pm in mods)
            {
                stats.Total++;

                switch (Classify(pm))
                {
                    case Verdict.Redundant:
                        stats.Redundant++;
                        stats.RedundantByPath.TryGetValue(pm.propertyPath, out var c);
                        stats.RedundantByPath[pm.propertyPath] = c + 1;

                        break;
                    case Verdict.Real:
                        stats.Real++;

                        break;
                    default:
                        stats.Unknown++;

                        break;
                }
            }

            if (transform != null)
            {
                PrefabUtility.SetPropertyModifications(go, transform(mods));
            }
        }

        return stats;
    }

    // Keeps everything that is not provably redundant (Real + Unknown are preserved).
    private static PropertyModification[] StripRedundant(PropertyModification[] mods)
    {
        return mods.Where(pm => Classify(pm) != Verdict.Redundant).ToArray();
    }

    private enum Verdict
    {
        Redundant,
        Real,
        Unknown
    }

    private static Verdict Classify(PropertyModification pm)
    {
        if (pm.target == null)
        {
            return Verdict.Unknown;
        }

        var so = new SerializedObject(pm.target);
        var prop = so.FindProperty(pm.propertyPath);

        if (prop == null)
        {
            return Verdict.Unknown;
        }

        switch (prop.propertyType)
        {
            case SerializedPropertyType.Float:
                return float.TryParse(pm.value, out var f) && Mathf.Approximately(prop.floatValue, f)
                    ? Verdict.Redundant
                    : Verdict.Real;
            case SerializedPropertyType.Integer:
                return long.TryParse(pm.value, out var i) && prop.longValue == i ? Verdict.Redundant : Verdict.Real;
            case SerializedPropertyType.Boolean:
                return pm.value == "1" == prop.boolValue ? Verdict.Redundant : Verdict.Real;
            case SerializedPropertyType.String:
                return prop.stringValue == pm.value ? Verdict.Redundant : Verdict.Real;
            case SerializedPropertyType.ObjectReference:
                return prop.objectReferenceValue == pm.objectReference ? Verdict.Redundant : Verdict.Real;
            default:
                return Verdict.Unknown;
        }
    }

    private static string BuildReport(string path, Stats stats, bool removed)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"{(removed ? "Removal" : "Analysis")} for {path}");
        sb.AppendLine($"  Prefab instances scanned: {stats.Instances}");
        sb.AppendLine($"  TOTAL overrides: {stats.Total}");
        sb.AppendLine($"  REDUNDANT (no-op): {stats.Redundant} ({Pct(stats.Redundant, stats.Total)}%)");
        sb.AppendLine($"  REAL (kept): {stats.Real} ({Pct(stats.Real, stats.Total)}%)");
        sb.AppendLine($"  UNKNOWN (kept): {stats.Unknown} ({Pct(stats.Unknown, stats.Total)}%)");
        sb.AppendLine("  --- top redundant propertyPaths ---");

        foreach (var kv in stats.RedundantByPath.OrderByDescending(k => k.Value).Take(12))
        {
            sb.AppendLine($"    {kv.Value,6}  {kv.Key}");
        }

        return sb.ToString();
    }

    private static long FileSizeKb(string assetPath)
    {
        var full = Path.GetFullPath(assetPath);

        return File.Exists(full) ? new FileInfo(full).Length / 1024 : 0;
    }

    private static int Pct(int n, int total)
    {
        return total == 0 ? 0 : Mathf.RoundToInt(n * 100f / total);
    }

    [MenuItem(MenuPath)]
    private static void Open()
    {
        GetWindow<PrefabOverrideAnalyzer>("Prefab Override Analyzer").Show();
    }
}
