// COMMENTED OUT: UI Toolkit UXML conversion removed
#if false
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using UnityEditor;
using Newtonsoft.Json.Linq;
using TMPro;

namespace clibridge4unity
{
    public static class ConvertUxmlCommand
    {
        // ═══════════════════════════════════════════════════════════════
        // Entry point
        // ═══════════════════════════════════════════════════════════════

        [BridgeCommand("CONVERT_UXML",
            "Convert a uGUI prefab to UXML + USS template files",
            Category = "UI",
            Usage = "CONVERT_UXML Assets/Prefabs/MyPrefab.prefab [Assets/UI/Output/]",
            RequiresMainThread = false)]
        public static async Task<string> ConvertToUxml(string data)
        {
            try
            {
                string prefabPath = null;
                string outputDir = null;

                data = data?.Trim() ?? "";
                if (data.StartsWith("{"))
                {
                    var json = JObject.Parse(data);
                    prefabPath = json["prefab"]?.ToString();
                    outputDir = json["output"]?.ToString();
                }
                else
                {
                    // Simple argument parsing: CONVERT_UXML <prefab> [output_dir]
                    // Find the .prefab path (may contain spaces), then optional output dir after
                    int prefabEnd = data.IndexOf(".prefab", StringComparison.OrdinalIgnoreCase);
                    if (prefabEnd >= 0)
                    {
                        prefabEnd += ".prefab".Length;
                        prefabPath = data.Substring(0, prefabEnd).Trim();
                        if (prefabEnd < data.Length)
                            outputDir = data.Substring(prefabEnd).Trim();
                        if (string.IsNullOrEmpty(outputDir))
                            outputDir = null;
                    }
                    else
                    {
                        prefabPath = data;
                    }
                }

                if (string.IsNullOrEmpty(prefabPath))
                    return Response.Error("Usage: CONVERT_UXML <prefab path>");

                // Phase 1: Walk hierarchy on main thread
                var result = await CommandRegistry.RunOnMainThreadAsync(() =>
                    CollectPrefabData(prefabPath));

                if (result.Error != null)
                    return Response.Error(result.Error);

                // Phase 1b: Post-process — detect hover states, extract CSS variables
                DetectHoverStates(result);
                ExtractCssVariables(result);

                // Determine output paths
                if (string.IsNullOrEmpty(outputDir))
                    outputDir = Path.GetDirectoryName(prefabPath);

                string baseName = SanitizeCssClass(Path.GetFileNameWithoutExtension(prefabPath));
                string ussPath = Path.Combine(outputDir, baseName + ".uss").Replace('\\', '/');
                string uxmlPath = Path.Combine(outputDir, baseName + ".uxml").Replace('\\', '/');

                // Phase 2 & 3: Generate files
                var uss = GenerateUss(result, prefabPath);
                var uxml = GenerateUxml(result, baseName);

                // Write and import
                Directory.CreateDirectory(outputDir);
                File.WriteAllText(ussPath, uss);
                File.WriteAllText(uxmlPath, uxml);

                await CommandRegistry.RunOnMainThreadAsync(() =>
                {
                    AssetDatabase.ImportAsset(ussPath, ImportAssetOptions.ForceUpdate);
                    AssetDatabase.ImportAsset(uxmlPath, ImportAssetOptions.ForceUpdate);
                    return 0;
                });

                var sb = new StringBuilder();
                sb.AppendLine($"Converted: {prefabPath}");
                sb.AppendLine($"USS: {ussPath} ({result.Nodes.Count} rules)");
                sb.AppendLine($"UXML: {uxmlPath}");
                sb.AppendLine($"Elements: {result.Nodes.Count}");
                sb.AppendLine($"Hover rules: {result.HoverRules.Count}");
                if (result.CssVariables.Count > 0)
                    sb.AppendLine($"CSS variables: {result.CssVariables.Count}");
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex)
            {
                return Response.Exception(ex);
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Data structures
        // ═══════════════════════════════════════════════════════════════

        class ConversionResult
        {
            public string Error;
            public NodeInfo Root;
            public List<NodeInfo> Nodes = new List<NodeInfo>();
            public List<HoverRule> HoverRules = new List<HoverRule>();
            public Dictionary<string, string> CssVariables = new Dictionary<string, string>();
            public string PrefabName;
        }

        class NodeInfo
        {
            public string Name;
            public string HierarchyPath;        // e.g. "Background/Image"
            public string CssClass;
            public string Element = "VisualElement";
            public string LabelText;
            public List<string> ExtraClasses = new List<string>();
            public List<CssProp> Styles = new List<CssProp>();
            public List<CssProp> ChildStyles = new List<CssProp>(); // .parent > * rules
            public List<NodeInfo> Children = new List<NodeInfo>();
            public bool IsActive = true;
            public bool IsFocusable;
            public bool MaskHidesGraphic;       // Mask(showMaskGraphic=false)
            public bool IgnorePicking;          // raycastTarget=false or CanvasGroup.interactable=false

            // For state detection
            public float CanvasGroupAlpha = -1f;    // -1 = no CanvasGroup
            public bool HasAnimator;
            public bool HasSelectable;
            public SelectableData SelectableInfo;
            public int Depth;
            public NodeInfo Parent;

            // Filled image approximation
            public string FilledAxis;           // "width" or "height"
            public float FilledAmount;          // 0..1
        }

        class HoverRule
        {
            public string ParentSelector;       // .root-class:hover
            public string TargetSelector;       // .child-class (null = targets parent itself)
            public List<CssProp> Styles = new List<CssProp>();
            public string Comment;
        }

        class SelectableData
        {
            public string Type;                 // "Button", "Toggle", etc.
            public Color NormalColor;
            public Color HighlightedColor;
            public Color PressedColor;
            public Color DisabledColor;
            public float FadeDuration;
            public bool HasColorTint;
        }

        struct CssProp
        {
            public string Name;
            public string Value;
            public string Comment;
            public CssProp(string name, string value, string comment = null)
            { Name = name; Value = value; Comment = comment; }
        }

        // ═══════════════════════════════════════════════════════════════
        // Phase 1: Collect data on main thread
        // ═══════════════════════════════════════════════════════════════

        static ConversionResult CollectPrefabData(string prefabPath)
        {
            var result = new ConversionResult();
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (prefab == null)
            {
                result.Error = $"Prefab not found: {prefabPath}";
                return result;
            }

            result.PrefabName = prefab.name;

            var usedNames = new HashSet<string>();
            result.Root = WalkNode(prefab.transform, null, "", usedNames, result.Nodes, 0);
            return result;
        }

        static NodeInfo WalkNode(Transform node, NodeInfo parent, string parentPath,
            HashSet<string> usedNames, List<NodeInfo> allNodes, int depth)
        {
            var info = new NodeInfo
            {
                Name = node.name,
                HierarchyPath = string.IsNullOrEmpty(parentPath)
                    ? node.name : $"{parentPath}/{node.name}",
                CssClass = GetUniqueCssClass(node.name, usedNames),
                IsActive = node.gameObject.activeSelf,
                Depth = depth,
                Parent = parent
            };

            // Detect stateful components first (they affect other collectors)
            info.HasAnimator = node.GetComponent<Animator>() != null;

            var mask = node.GetComponent<Mask>();
            if (mask != null)
                info.MaskHidesGraphic = !mask.showMaskGraphic;

            // Inactive elements
            if (!info.IsActive)
                info.Styles.Add(new CssProp("display", "none", "GameObject was inactive"));

            // Collect styles from all components
            CollectRectTransformStyles(node, info);
            CollectMaskStyles(node, info);
            CollectImageStyles(node, info);
            CollectRawImageStyles(node, info);
            CollectTextStyles(node, info);
            CollectCanvasGroupStyles(node, info);
            CollectLayoutGroupStyles(node, info);
            CollectLayoutElementStyles(node, info);
            CollectContentSizeFitterStyles(node, info);
            CollectAspectRatioStyles(node, info);
            CollectSelectableStyles(node, info);
            CollectTransformStyles(node, info);
            CollectShadowOutlineStyles(node, info);

            // Hide state overlays that lack CanvasGroup (e.g., InputField's Highlight)
            // These are managed programmatically by custom components at runtime
            if (info.CanvasGroupAlpha < 0 && info.Parent != null)
            {
                string lower = node.name.ToLowerInvariant();
                bool isStateOverlay = lower == "highlight" || lower == "highlighted"
                    || lower == "selected" || lower == "pressed";
                if (isStateOverlay)
                {
                    // Check if any ancestor has a Selectable
                    var check = node.parent;
                    while (check != null)
                    {
                        if (check.GetComponent<Selectable>() != null)
                        {
                            info.Styles.Add(new CssProp("opacity", "0",
                                "State overlay — hidden by default"));
                            break;
                        }
                        check = check.parent;
                    }
                }
            }

            // Approximate filled images by overriding width/height
            if (info.FilledAxis != null)
            {
                var pct = $"{info.FilledAmount * 100f:F0}%";
                info.Styles.RemoveAll(s => s.Name == info.FilledAxis);
                // Remove conflicting absolute positioning on the fill axis
                if (info.FilledAxis == "width")
                    info.Styles.RemoveAll(s => s.Name == "right");
                else if (info.FilledAxis == "height")
                    info.Styles.RemoveAll(s => s.Name == "bottom");
                info.Styles.Add(new CssProp(info.FilledAxis, pct,
                    $"Filled image approximation (fillAmount={info.FilledAmount:F2})"));
            }

            allNodes.Add(info);

            // Recurse children
            for (int i = 0; i < node.childCount; i++)
            {
                var child = WalkNode(node.GetChild(i), info, info.HierarchyPath,
                    usedNames, allNodes, depth + 1);
                info.Children.Add(child);
            }

            return info;
        }

        // ═══════════════════════════════════════════════════════════════
        // Component extractors
        // ═══════════════════════════════════════════════════════════════

        static void CollectRectTransformStyles(Transform node, NodeInfo info)
        {
            var rt = node.GetComponent<RectTransform>();
            if (rt == null) return;

            var pivot = rt.pivot;
            var amin = rt.anchorMin;
            var amax = rt.anchorMax;
            var apos = rt.anchoredPosition;
            var sd = rt.sizeDelta;

            bool stretchX = !Mathf.Approximately(amin.x, amax.x);
            bool stretchY = !Mathf.Approximately(amin.y, amax.y);

            // Check if child of a layout group (flex child)
            // Elements with LayoutElement.ignoreLayout are NOT flex children — they use absolute positioning
            var layoutElement = node.GetComponent<LayoutElement>();
            bool ignoresLayout = layoutElement != null && layoutElement.ignoreLayout;
            bool isFlexChild = !ignoresLayout && node.parent != null &&
                (node.parent.GetComponent<HorizontalLayoutGroup>() != null ||
                 node.parent.GetComponent<VerticalLayoutGroup>() != null ||
                 node.parent.GetComponent<GridLayoutGroup>() != null);

            if (isFlexChild)
            {
                if (sd.x > 0) info.Styles.Add(new CssProp("width", FormatPx(sd.x)));
                if (sd.y > 0) info.Styles.Add(new CssProp("height", FormatPx(sd.y)));
                return;
            }

            // Root element: stretch to fill, but respect ContentSizeFitter per-axis
            if (info.Parent == null)
            {
                var csf = node.GetComponent<ContentSizeFitter>();
                bool fitW = csf != null && csf.horizontalFit != ContentSizeFitter.FitMode.Unconstrained;
                bool fitH = csf != null && csf.verticalFit != ContentSizeFitter.FitMode.Unconstrained;
                info.Styles.Add(new CssProp("width", fitW ? "auto" : "100%",
                    fitW ? $"ContentSizeFitter.{csf.horizontalFit}" : null));
                info.Styles.Add(new CssProp("height", fitH ? "auto" : "100%",
                    fitH ? $"ContentSizeFitter.{csf.verticalFit}" : null));
                return;
            }

            // ── Full stretch (both axes) ──
            if (stretchX && stretchY)
            {
                info.Styles.Add(new CssProp("position", "absolute"));
                float left = rt.offsetMin.x;
                float bottom = rt.offsetMin.y;
                float right = -rt.offsetMax.x;
                float top = -rt.offsetMax.y;
                info.Styles.Add(new CssProp("left", FormatPx(left)));
                info.Styles.Add(new CssProp("top", FormatPx(top)));
                info.Styles.Add(new CssProp("right", FormatPx(right)));
                info.Styles.Add(new CssProp("bottom", FormatPx(bottom)));
            }
            // ── Stretch X, fixed Y ──
            else if (stretchX && !stretchY)
            {
                info.Styles.Add(new CssProp("position", "absolute"));
                info.Styles.Add(new CssProp("left", FormatPx(rt.offsetMin.x)));
                info.Styles.Add(new CssProp("right", FormatPx(-rt.offsetMax.x)));
                info.Styles.Add(new CssProp("height", FormatPx(sd.y)));
                EmitFixedAxisPosition(info, amin.y, apos.y, sd.y, pivot.y, isVertical: true);
            }
            // ── Fixed X, stretch Y ──
            else if (!stretchX && stretchY)
            {
                info.Styles.Add(new CssProp("position", "absolute"));
                info.Styles.Add(new CssProp("top", FormatPx(-rt.offsetMax.y)));
                info.Styles.Add(new CssProp("bottom", FormatPx(rt.offsetMin.y)));
                info.Styles.Add(new CssProp("width", FormatPx(sd.x)));
                EmitFixedAxisPosition(info, amin.x, apos.x, sd.x, pivot.x, isVertical: false);
            }
            // ── Fixed size (both axes) ──
            else
            {
                info.Styles.Add(new CssProp("position", "absolute"));
                if (sd.x > 0) info.Styles.Add(new CssProp("width", FormatPx(sd.x)));
                if (sd.y > 0) info.Styles.Add(new CssProp("height", FormatPx(sd.y)));
                EmitFixedAxisPosition(info, amin.x, apos.x, sd.x, pivot.x, isVertical: false);
                EmitFixedAxisPosition(info, amin.y, apos.y, sd.y, pivot.y, isVertical: true);
            }
        }

        /// <summary>
        /// Emit USS position for a fixed-size axis, correctly accounting for pivot.
        /// In uGUI: anchoredPosition is from anchor point to pivot point.
        /// USS uses edge distances (left/right/top/bottom) from parent edges.
        /// </summary>
        static void EmitFixedAxisPosition(NodeInfo info, float anchor, float pos,
            float size, float pivot, bool isVertical)
        {
            if (isVertical)
            {
                // uGUI Y-up → USS Y-down
                if (anchor <= 0.01f)
                {
                    // Bottom-anchored: bottom = pos - size * pivot
                    float bottom = pos - size * pivot;
                    info.Styles.Add(new CssProp("bottom", FormatPx(bottom)));
                }
                else if (anchor >= 0.99f)
                {
                    // Top-anchored: top = -pos - size * (1 - pivot)
                    float top = -pos - size * (1f - pivot);
                    info.Styles.Add(new CssProp("top", FormatPx(top)));
                }
                else if (Mathf.Approximately(anchor, 0.5f))
                {
                    // Center-anchored vertically
                    float topOffset = -pos - size * (1f - pivot);
                    info.Styles.Add(new CssProp("top", "50%"));
                    info.Styles.Add(new CssProp("margin-top", FormatPx(topOffset),
                        "pivot-adjusted center offset"));
                }
                else
                {
                    // Arbitrary anchor
                    float pct = (1f - anchor) * 100f;
                    float topOffset = -pos - size * (1f - pivot);
                    info.Styles.Add(new CssProp("top", $"{pct:F1}%",
                        $"anchor.y={anchor:F2}"));
                    if (Mathf.Abs(topOffset) > 0.5f)
                        info.Styles.Add(new CssProp("margin-top", FormatPx(topOffset)));
                }
            }
            else
            {
                // Horizontal: uGUI X left-to-right matches USS
                if (anchor <= 0.01f)
                {
                    // Left-anchored
                    float left = pos - size * pivot;
                    info.Styles.Add(new CssProp("left", FormatPx(left)));
                }
                else if (anchor >= 0.99f)
                {
                    // Right-anchored
                    float right = -pos - size * (1f - pivot);
                    info.Styles.Add(new CssProp("right", FormatPx(right)));
                }
                else if (Mathf.Approximately(anchor, 0.5f))
                {
                    // Center-anchored horizontally
                    float leftOffset = pos - size * pivot;
                    info.Styles.Add(new CssProp("left", "50%"));
                    info.Styles.Add(new CssProp("margin-left", FormatPx(leftOffset),
                        "pivot-adjusted center offset"));
                }
                else
                {
                    // Arbitrary anchor
                    float pct = anchor * 100f;
                    float leftOffset = pos - size * pivot;
                    info.Styles.Add(new CssProp("left", $"{pct:F1}%",
                        $"anchor.x={anchor:F2}"));
                    if (Mathf.Abs(leftOffset) > 0.5f)
                        info.Styles.Add(new CssProp("margin-left", FormatPx(leftOffset)));
                }
            }
        }

        static void CollectImageStyles(Transform node, NodeInfo info)
        {
            var img = node.GetComponent<Image>();
            if (img == null) return;

            // Skip visual properties if Mask(showMaskGraphic=false)
            if (info.MaskHidesGraphic) return;

            if (img.sprite != null)
            {
                string path = AssetDatabase.GetAssetPath(img.sprite);
                if (!string.IsNullOrEmpty(path))
                {
                    info.Styles.Add(new CssProp("background-image", $"url(\"/{path}\")"));

                    switch (img.type)
                    {
                        case Image.Type.Simple:
                            info.Styles.Add(new CssProp("-unity-background-scale-mode",
                                img.preserveAspect ? "scale-to-fit" : "stretch-to-fill"));
                            break;

                        case Image.Type.Sliced:
                            info.Styles.Add(new CssProp("-unity-background-scale-mode",
                                "stretch-to-fill"));
                            var border = img.sprite.border; // (left, bottom, right, top)
                            if (border.sqrMagnitude > 0)
                            {
                                info.Styles.Add(new CssProp("-unity-slice-left",
                                    $"{(int)border.x}"));
                                info.Styles.Add(new CssProp("-unity-slice-bottom",
                                    $"{(int)border.y}"));
                                info.Styles.Add(new CssProp("-unity-slice-right",
                                    $"{(int)border.z}"));
                                info.Styles.Add(new CssProp("-unity-slice-top",
                                    $"{(int)border.w}"));
                            }
                            // pixelsPerUnitMultiplier → -unity-slice-scale
                            if (img.pixelsPerUnitMultiplier > 1.001f)
                            {
                                float scale = 1f / img.pixelsPerUnitMultiplier;
                                info.Styles.Add(new CssProp("-unity-slice-scale",
                                    $"{scale:F2}px",
                                    $"pixelsPerUnitMultiplier={img.pixelsPerUnitMultiplier}"));
                            }
                            break;

                        case Image.Type.Tiled:
                            info.Styles.Add(new CssProp("-unity-background-scale-mode",
                                "stretch-to-fill",
                                "NOTE: USS has no tiled mode; original was Image.Type.Tiled"));
                            break;

                        case Image.Type.Filled:
                            info.Styles.Add(new CssProp("-unity-background-scale-mode",
                                "stretch-to-fill",
                                $"NOTE: USS has no filled mode; fillMethod={img.fillMethod}, " +
                                $"fillAmount={img.fillAmount:F2}"));
                            // Approximate filled images by clipping via width/height
                            if (img.fillMethod == Image.FillMethod.Horizontal)
                            {
                                info.FilledAxis = "width";
                                info.FilledAmount = img.fillAmount;
                            }
                            else if (img.fillMethod == Image.FillMethod.Vertical)
                            {
                                info.FilledAxis = "height";
                                info.FilledAmount = img.fillAmount;
                            }
                            break;
                    }
                }

                // Tint color (skip plain white)
                if (img.color != Color.white)
                    info.Styles.Add(new CssProp("-unity-background-image-tint-color",
                        ToUssColor(img.color)));
            }
            else
            {
                // No sprite — solid color background
                if (img.color.a > 0.001f)
                    info.Styles.Add(new CssProp("background-color", ToUssColor(img.color)));
            }

            // raycastTarget → picking-mode
            if (!img.raycastTarget)
                info.IgnorePicking = true;
        }

        static void CollectRawImageStyles(Transform node, NodeInfo info)
        {
            var raw = node.GetComponent<RawImage>();
            if (raw == null) return;
            if (info.MaskHidesGraphic) return;

            if (raw.texture != null)
            {
                string path = AssetDatabase.GetAssetPath(raw.texture);
                if (!string.IsNullOrEmpty(path))
                {
                    info.Styles.Add(new CssProp("background-image", $"url(\"/{path}\")"));
                    info.Styles.Add(new CssProp("-unity-background-scale-mode",
                        "stretch-to-fill"));
                }
                if (raw.color != Color.white)
                    info.Styles.Add(new CssProp("-unity-background-image-tint-color",
                        ToUssColor(raw.color)));
            }
            else if (raw.color.a > 0.001f)
            {
                info.Styles.Add(new CssProp("background-color", ToUssColor(raw.color)));
            }

            if (!raw.raycastTarget)
                info.IgnorePicking = true;
        }

        static void CollectTextStyles(Transform node, NodeInfo info)
        {
            var tmp = node.GetComponent<TextMeshProUGUI>();
            if (tmp == null) return;

            info.Element = "Label";
            string text = tmp.text ?? "";

            // Handle uppercase font style (no USS equivalent — apply directly)
            if ((tmp.fontStyle & FontStyles.UpperCase) != 0)
                text = text.ToUpperInvariant();

            info.LabelText = text;

            // Font size (use max if auto-sizing)
            if (tmp.enableAutoSizing)
            {
                info.Styles.Add(new CssProp("font-size", FormatPx(tmp.fontSizeMax),
                    $"auto-size: min={tmp.fontSizeMin}, max={tmp.fontSizeMax}"));
            }
            else
            {
                info.Styles.Add(new CssProp("font-size", FormatPx(tmp.fontSize)));
            }

            // Color
            info.Styles.Add(new CssProp("color", ToUssColor(tmp.color)));

            // Font asset → source font file (TTF/OTF)
            if (tmp.font != null)
            {
                if (tmp.font.sourceFontFile != null)
                {
                    string fontPath = AssetDatabase.GetAssetPath(tmp.font.sourceFontFile);
                    if (!string.IsNullOrEmpty(fontPath))
                        info.Styles.Add(new CssProp("-unity-font-definition",
                            $"url(\"/{fontPath}\")"));
                }
                else
                {
                    string fontPath = ResolveTmpFontToTtf(tmp.font);
                    if (fontPath != null)
                    {
                        info.Styles.Add(new CssProp("-unity-font-definition",
                            $"url(\"/{fontPath}\")",
                            $"resolved from TMP font '{tmp.font.name}'"));
                    }
                    else
                    {
                        // Last resort: use the TMP font asset path (will produce USS warning)
                        string assetPath = AssetDatabase.GetAssetPath(tmp.font);
                        if (!string.IsNullOrEmpty(assetPath))
                            info.Styles.Add(new CssProp("-unity-font-definition",
                                $"url(\"/{assetPath}\")",
                                "TMP font asset — no TTF/OTF found"));
                    }
                }
            }

            // Font style (bold, italic)
            var style = tmp.fontStyle;
            bool bold = (style & FontStyles.Bold) != 0;
            bool italic = (style & FontStyles.Italic) != 0;
            if (bold && italic)
                info.Styles.Add(new CssProp("-unity-font-style", "bold-and-italic"));
            else if (bold)
                info.Styles.Add(new CssProp("-unity-font-style", "bold"));
            else if (italic)
                info.Styles.Add(new CssProp("-unity-font-style", "italic"));

            // Text alignment
            string align = MapTextAlignment(tmp.alignment);
            if (align != null)
                info.Styles.Add(new CssProp("-unity-text-align", align));

            // Character spacing (TMP em-units → approximate px)
            if (Mathf.Abs(tmp.characterSpacing) > 0.1f)
            {
                float pxApprox = tmp.characterSpacing * tmp.fontSize / 100f;
                info.Styles.Add(new CssProp("letter-spacing", FormatPx(pxApprox),
                    $"TMP characterSpacing={tmp.characterSpacing:F1}"));
            }

            // Word spacing
            if (Mathf.Abs(tmp.wordSpacing) > 0.1f)
            {
                float pxApprox = tmp.wordSpacing * tmp.fontSize / 100f;
                info.Styles.Add(new CssProp("word-spacing", FormatPx(pxApprox),
                    $"TMP wordSpacing={tmp.wordSpacing:F1}"));
            }

            // Line / paragraph spacing
            if (Mathf.Abs(tmp.lineSpacing) > 0.1f)
                info.Styles.Add(new CssProp("/* line-spacing */",
                    $"{tmp.lineSpacing:F1}",
                    "TMP lineSpacing (no direct USS equivalent)"));

            if (Mathf.Abs(tmp.paragraphSpacing) > 0.1f)
                info.Styles.Add(new CssProp("-unity-paragraph-spacing",
                    FormatPx(tmp.paragraphSpacing)));

            // Text overflow
            if (tmp.overflowMode == TextOverflowModes.Ellipsis)
            {
                info.Styles.Add(new CssProp("text-overflow", "ellipsis"));
                info.Styles.Add(new CssProp("overflow", "hidden"));
            }

            // Word wrapping
            if (tmp.textWrappingMode == TextWrappingModes.NoWrap)
                info.Styles.Add(new CssProp("white-space", "nowrap"));

            // TMPro margins (x=left, y=top, z=right, w=bottom)
            var m = tmp.margin;
            if (Mathf.Abs(m.x) > 0.5f)
                info.Styles.Add(new CssProp("margin-left", FormatPx(m.x)));
            if (Mathf.Abs(m.y) > 0.5f)
                info.Styles.Add(new CssProp("margin-top", FormatPx(m.y)));
            if (Mathf.Abs(m.z) > 0.5f)
                info.Styles.Add(new CssProp("margin-right", FormatPx(m.z)));
            if (Mathf.Abs(m.w) > 0.5f)
                info.Styles.Add(new CssProp("margin-bottom", FormatPx(m.w)));

            if (!tmp.raycastTarget)
                info.IgnorePicking = true;
        }

        static void CollectCanvasGroupStyles(Transform node, NodeInfo info)
        {
            var cg = node.GetComponent<CanvasGroup>();
            if (cg == null) return;

            info.CanvasGroupAlpha = cg.alpha;
            if (cg.alpha < 0.999f)
                info.Styles.Add(new CssProp("opacity",
                    cg.alpha < 0.001f ? "0" : cg.alpha.ToString("F2")));

            if (!cg.interactable)
                info.IgnorePicking = true;
        }

        static void CollectMaskStyles(Transform node, NodeInfo info)
        {
            if (node.GetComponent<Mask>() != null || node.GetComponent<RectMask2D>() != null)
                info.Styles.Add(new CssProp("overflow", "hidden"));
        }

        static void CollectLayoutGroupStyles(Transform node, NodeInfo info)
        {
            var hlg = node.GetComponent<HorizontalLayoutGroup>();
            var vlg = node.GetComponent<VerticalLayoutGroup>();
            var glg = node.GetComponent<GridLayoutGroup>();

            if (hlg != null || vlg != null)
            {
                LayoutGroup lg = (LayoutGroup)hlg ?? vlg;

                // Direction with reverseArrangement support
                bool reverse = hlg != null ? hlg.reverseArrangement
                                           : vlg.reverseArrangement;
                string dir = hlg != null
                    ? (reverse ? "row-reverse" : "row")
                    : (reverse ? "column-reverse" : "column");
                info.Styles.Add(new CssProp("flex-direction", dir));

                var pad = lg.padding;
                if (pad.left > 0)
                    info.Styles.Add(new CssProp("padding-left", FormatPx(pad.left)));
                if (pad.right > 0)
                    info.Styles.Add(new CssProp("padding-right", FormatPx(pad.right)));
                if (pad.top > 0)
                    info.Styles.Add(new CssProp("padding-top", FormatPx(pad.top)));
                if (pad.bottom > 0)
                    info.Styles.Add(new CssProp("padding-bottom", FormatPx(pad.bottom)));

                // Spacing → child margin rules (USS has no gap property)
                float spacing = hlg != null ? hlg.spacing : vlg.spacing;
                if (Mathf.Abs(spacing) > 0.1f)
                {
                    string marginProp = hlg != null ? "margin-right" : "margin-bottom";
                    info.ChildStyles.Add(new CssProp(marginProp, FormatPx(spacing),
                        $"LayoutGroup spacing={spacing}"));
                }

                // childControl flags determine whether layout overrides children's size
                bool controlWidth = hlg != null ? hlg.childControlWidth : vlg.childControlWidth;
                bool controlHeight = hlg != null ? hlg.childControlHeight : vlg.childControlHeight;
                bool forceExpandWidth = hlg != null && hlg.childForceExpandWidth;
                bool forceExpandHeight = vlg != null && vlg.childForceExpandHeight;

                var (justify, alignItems) = MapChildAlignment(lg.childAlignment);
                if (justify != null)
                    info.Styles.Add(new CssProp("justify-content", justify));

                // childControlHeight/Width with stretch → align-items: stretch overrides childAlignment
                bool isHorizontal = hlg != null;
                if (isHorizontal && controlHeight)
                    info.Styles.Add(new CssProp("align-items", "stretch"));
                else if (!isHorizontal && controlWidth)
                    info.Styles.Add(new CssProp("align-items", "stretch"));
                else if (alignItems != null)
                    info.Styles.Add(new CssProp("align-items", alignItems));

                // childForceExpand → flex-grow: 1 on children
                if ((isHorizontal && forceExpandWidth) || (!isHorizontal && forceExpandHeight))
                    info.ChildStyles.Add(new CssProp("flex-grow", "1",
                        "childForceExpand"));
            }
            else if (glg != null)
            {
                // Grid → approximate with flex-wrap
                info.Styles.Add(new CssProp("flex-direction", "row"));
                info.Styles.Add(new CssProp("flex-wrap", "wrap"));

                var pad = glg.padding;
                if (pad.left > 0)
                    info.Styles.Add(new CssProp("padding-left", FormatPx(pad.left)));
                if (pad.right > 0)
                    info.Styles.Add(new CssProp("padding-right", FormatPx(pad.right)));
                if (pad.top > 0)
                    info.Styles.Add(new CssProp("padding-top", FormatPx(pad.top)));
                if (pad.bottom > 0)
                    info.Styles.Add(new CssProp("padding-bottom", FormatPx(pad.bottom)));

                // Grid cell size + spacing → child selector rules
                if (glg.cellSize.x > 0)
                    info.ChildStyles.Add(new CssProp("width", FormatPx(glg.cellSize.x),
                        "GridLayoutGroup cellSize.x"));
                if (glg.cellSize.y > 0)
                    info.ChildStyles.Add(new CssProp("height", FormatPx(glg.cellSize.y),
                        "GridLayoutGroup cellSize.y"));
                if (glg.spacing.x > 0)
                    info.ChildStyles.Add(new CssProp("margin-right",
                        FormatPx(glg.spacing.x), "grid spacing X"));
                if (glg.spacing.y > 0)
                    info.ChildStyles.Add(new CssProp("margin-bottom",
                        FormatPx(glg.spacing.y), "grid spacing Y"));
            }
        }

        static void CollectLayoutElementStyles(Transform node, NodeInfo info)
        {
            var le = node.GetComponent<LayoutElement>();
            if (le == null) return;

            if (le.ignoreLayout)
                return; // RectTransform extractor handles positioning (isFlexChild=false)

            if (le.minWidth > 0)
                info.Styles.Add(new CssProp("min-width", FormatPx(le.minWidth)));
            if (le.minHeight > 0)
                info.Styles.Add(new CssProp("min-height", FormatPx(le.minHeight)));
            if (le.preferredWidth > 0)
                info.Styles.Add(new CssProp("width", FormatPx(le.preferredWidth),
                    "LayoutElement.preferredWidth"));
            if (le.preferredHeight > 0)
                info.Styles.Add(new CssProp("height", FormatPx(le.preferredHeight),
                    "LayoutElement.preferredHeight"));
            if (le.flexibleWidth > 0)
                info.Styles.Add(new CssProp("flex-grow", $"{le.flexibleWidth:G}"));
            if (le.flexibleHeight > 0)
                info.Styles.Add(new CssProp("flex-grow", $"{le.flexibleHeight:G}",
                    "LayoutElement.flexibleHeight"));
        }

        static void CollectContentSizeFitterStyles(Transform node, NodeInfo info)
        {
            var csf = node.GetComponent<ContentSizeFitter>();
            if (csf == null) return;

            bool fitH = csf.horizontalFit == ContentSizeFitter.FitMode.PreferredSize
                || csf.horizontalFit == ContentSizeFitter.FitMode.MinSize;
            bool fitV = csf.verticalFit == ContentSizeFitter.FitMode.PreferredSize
                || csf.verticalFit == ContentSizeFitter.FitMode.MinSize;

            if (fitH)
                info.Styles.Add(new CssProp("flex-shrink", "0",
                    $"ContentSizeFitter.{csf.horizontalFit} horizontal"));
            if (fitV)
                info.Styles.Add(new CssProp("flex-shrink", "0",
                    $"ContentSizeFitter.{csf.verticalFit} vertical"));

            // ContentSizeFitter on a flex child prevents cross-axis stretching
            // In a VLG (column): horizontal CSF → align-self prevents width stretch
            // In a HLG (row): vertical CSF → align-self prevents height stretch
            if (node.parent != null)
            {
                bool parentIsColumn = node.parent.GetComponent<VerticalLayoutGroup>() != null;
                bool parentIsRow = node.parent.GetComponent<HorizontalLayoutGroup>() != null;

                if ((parentIsColumn && fitH) || (parentIsRow && fitV))
                {
                    // Determine alignment from parent layout group
                    string align = "flex-start";
                    var vlg = node.parent.GetComponent<VerticalLayoutGroup>();
                    var hlg = node.parent.GetComponent<HorizontalLayoutGroup>();
                    var childAlign = vlg != null ? vlg.childAlignment
                        : hlg != null ? hlg.childAlignment : TextAnchor.UpperLeft;

                    if (parentIsColumn && fitH)
                    {
                        // Cross-axis is horizontal in column layout
                        if (childAlign == TextAnchor.UpperCenter || childAlign == TextAnchor.MiddleCenter
                            || childAlign == TextAnchor.LowerCenter)
                            align = "center";
                        else if (childAlign == TextAnchor.UpperRight || childAlign == TextAnchor.MiddleRight
                            || childAlign == TextAnchor.LowerRight)
                            align = "flex-end";
                    }
                    else if (parentIsRow && fitV)
                    {
                        // Cross-axis is vertical in row layout
                        if (childAlign == TextAnchor.MiddleLeft || childAlign == TextAnchor.MiddleCenter
                            || childAlign == TextAnchor.MiddleRight)
                            align = "center";
                        else if (childAlign == TextAnchor.LowerLeft || childAlign == TextAnchor.LowerCenter
                            || childAlign == TextAnchor.LowerRight)
                            align = "flex-end";
                    }

                    info.Styles.Add(new CssProp("align-self", align,
                        $"ContentSizeFitter prevents cross-axis stretch"));
                }
            }
        }

        static void CollectAspectRatioStyles(Transform node, NodeInfo info)
        {
            var arf = node.GetComponent<AspectRatioFitter>();
            if (arf == null) return;

            switch (arf.aspectMode)
            {
                case AspectRatioFitter.AspectMode.EnvelopeParent:
                case AspectRatioFitter.AspectMode.FitInParent:
                    // Fitter dynamically sizes to fill/fit parent — override to stretch-fill
                    RemoveStyle(info, "-unity-background-scale-mode");
                    RemoveStyle(info, "position");
                    RemoveStyle(info, "left"); RemoveStyle(info, "top");
                    RemoveStyle(info, "right"); RemoveStyle(info, "bottom");
                    RemoveStyle(info, "width"); RemoveStyle(info, "height");
                    info.Styles.Insert(0, new CssProp("bottom", "0"));
                    info.Styles.Insert(0, new CssProp("right", "0"));
                    info.Styles.Insert(0, new CssProp("top", "0"));
                    info.Styles.Insert(0, new CssProp("left", "0"));
                    info.Styles.Insert(0, new CssProp("position", "absolute"));
                    string scaleMode = arf.aspectMode == AspectRatioFitter.AspectMode.EnvelopeParent
                        ? "scale-and-crop" : "scale-to-fit";
                    info.Styles.Add(new CssProp("-unity-background-scale-mode",
                        scaleMode,
                        $"AspectRatioFitter {arf.aspectMode}, ratio={arf.aspectRatio:F4}"));
                    break;
                case AspectRatioFitter.AspectMode.WidthControlsHeight:
                    info.Styles.Add(new CssProp("/* aspect-ratio */",
                        $"{arf.aspectRatio:F4}",
                        "width controls height — apply manually"));
                    break;
                case AspectRatioFitter.AspectMode.HeightControlsWidth:
                    info.Styles.Add(new CssProp("/* aspect-ratio */",
                        $"{arf.aspectRatio:F4}",
                        "height controls width — apply manually"));
                    break;
            }
        }

        static void CollectSelectableStyles(Transform node, NodeInfo info)
        {
            var sel = node.GetComponent<Selectable>();
            if (sel == null) return;

            info.HasSelectable = true;
            info.IsFocusable = true;

            string typeName = sel.GetType().Name;
            if (sel.transition == Selectable.Transition.ColorTint)
            {
                var cb = sel.colors;
                info.SelectableInfo = new SelectableData
                {
                    Type = typeName,
                    NormalColor = cb.normalColor,
                    HighlightedColor = cb.highlightedColor,
                    PressedColor = cb.pressedColor,
                    DisabledColor = cb.disabledColor,
                    FadeDuration = cb.fadeDuration,
                    HasColorTint = true
                };
            }
            else
            {
                info.SelectableInfo = new SelectableData { Type = typeName };
            }

            info.Styles.Add(new CssProp("cursor", "link"));
        }

        static void CollectTransformStyles(Transform node, NodeInfo info)
        {
            var ls = node.localScale;
            var lr = node.localEulerAngles;

            if (Mathf.Abs(ls.x - 1f) > 0.001f || Mathf.Abs(ls.y - 1f) > 0.001f)
                info.Styles.Add(new CssProp("scale", $"{ls.x:F2} {ls.y:F2}"));

            if (Mathf.Abs(lr.z) > 0.1f)
                info.Styles.Add(new CssProp("rotate", $"{lr.z:F1}deg"));
        }

        static void CollectShadowOutlineStyles(Transform node, NodeInfo info)
        {
            // UnityEngine.UI.Shadow → approximate with text-shadow
            var shadow = node.GetComponent<UnityEngine.UI.Shadow>();
            if (shadow != null && !(shadow is UnityEngine.UI.Outline))
            {
                var dist = shadow.effectDistance;
                info.Styles.Add(new CssProp("text-shadow",
                    $"{FormatPx(dist.x)} {FormatPx(-dist.y)} {ToUssColor(shadow.effectColor)}",
                    "approximation of UI.Shadow"));
            }

            var outline = node.GetComponent<UnityEngine.UI.Outline>();
            if (outline != null)
            {
                var dist = outline.effectDistance;
                float spread = Mathf.Max(Mathf.Abs(dist.x), Mathf.Abs(dist.y));
                info.Styles.Add(new CssProp("text-shadow",
                    $"0 0 {FormatPx(spread)} {ToUssColor(outline.effectColor)}",
                    "approximation of UI.Outline"));
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // State detection (hover, press, interaction states)
        // ═══════════════════════════════════════════════════════════════

        static void DetectHoverStates(ConversionResult result)
        {
            // Find the first element with an Animator (the interactive root)
            NodeInfo animatorNode = null;
            foreach (var node in result.Nodes)
            {
                if (node.HasAnimator)
                {
                    animatorNode = node;
                    break;
                }
            }

            if (animatorNode == null) return;
            string rootSelector = $".{animatorNode.CssClass}";

            // ─── Strategy 1: CanvasGroup alpha sibling pairs ───
            // Find child groups where alpha=1 (normal) / alpha=0 (highlighted)
            var visibleStates = new List<NodeInfo>();
            var hiddenStates = new List<NodeInfo>();

            foreach (var child in animatorNode.Children)
            {
                if (child.CanvasGroupAlpha >= 0.9f)
                    visibleStates.Add(child);
                else if (child.CanvasGroupAlpha >= 0f && child.CanvasGroupAlpha < 0.1f)
                    hiddenStates.Add(child);
            }

            float duration = 0.25f; // Sensible default

            if (visibleStates.Count > 0 && hiddenStates.Count > 0)
            {
                foreach (var visible in visibleStates)
                {
                    visible.Styles.Add(new CssProp("transition-property", "opacity"));
                    visible.Styles.Add(new CssProp("transition-duration",
                        $"{duration:F2}s"));

                    result.HoverRules.Add(new HoverRule
                    {
                        ParentSelector = $"{rootSelector}:hover",
                        TargetSelector = $".{visible.CssClass}",
                        Comment = $"{visible.Name} → hidden on hover",
                        Styles = new List<CssProp>
                            { new CssProp("opacity", "0") }
                    });
                }

                foreach (var hidden in hiddenStates)
                {
                    hidden.Styles.Add(new CssProp("transition-property", "opacity"));
                    hidden.Styles.Add(new CssProp("transition-duration",
                        $"{duration:F2}s"));

                    result.HoverRules.Add(new HoverRule
                    {
                        ParentSelector = $"{rootSelector}:hover",
                        TargetSelector = $".{hidden.CssClass}",
                        Comment = $"{hidden.Name} → visible on hover",
                        Styles = new List<CssProp>
                            { new CssProp("opacity", "1") }
                    });
                }
            }

            // ─── Strategy 2: Deeper CanvasGroup alpha=0 elements ───
            // Elements that fade in on hover but aren't part of a sibling pair
            foreach (var node in result.Nodes)
            {
                if (node == animatorNode) continue;
                if (hiddenStates.Contains(node) || visibleStates.Contains(node)) continue;
                if (node.CanvasGroupAlpha < 0f || node.CanvasGroupAlpha >= 0.1f) continue;

                // Verify it's a descendant of the animator node
                bool isDescendant = false;
                var p = node.Parent;
                while (p != null)
                {
                    if (p == animatorNode) { isDescendant = true; break; }
                    p = p.Parent;
                }
                if (!isDescendant) continue;

                node.Styles.Add(new CssProp("transition-property", "opacity"));
                node.Styles.Add(new CssProp("transition-duration", "0.25s"));

                result.HoverRules.Add(new HoverRule
                {
                    ParentSelector = $"{rootSelector}:hover",
                    TargetSelector = $".{node.CssClass}",
                    Comment = $"{node.Name} → revealed on hover",
                    Styles = new List<CssProp>
                        { new CssProp("opacity", "1") }
                });
            }

            // ─── Strategy 3: Root interaction styles ───
            if (animatorNode.HasSelectable)
            {
                animatorNode.Styles.Add(new CssProp("transition-property", "scale"));
                animatorNode.Styles.Add(new CssProp("transition-duration", "0.15s"));

                result.HoverRules.Add(new HoverRule
                {
                    ParentSelector = $"{rootSelector}:hover",
                    TargetSelector = null,
                    Comment = "hover scale effect",
                    Styles = new List<CssProp>
                        { new CssProp("scale", "1.02 1.02") }
                });
            }

            // ─── Strategy 4: Selectable ColorBlock pseudo-classes ───
            // Generate :hover/:active/:disabled rules from Button/Toggle color transitions
            foreach (var node in result.Nodes)
            {
                if (node.SelectableInfo?.HasColorTint != true) continue;
                var sel = node.SelectableInfo;
                string nodeSelector = $".{node.CssClass}";
                float dur = sel.FadeDuration > 0.001f ? sel.FadeDuration : 0.1f;

                // Add transition to base style
                node.Styles.Add(new CssProp("transition-property",
                    "background-color, -unity-background-image-tint-color, opacity"));
                node.Styles.Add(new CssProp("transition-duration", $"{dur:F2}s"));

                if (sel.HighlightedColor != sel.NormalColor)
                {
                    result.HoverRules.Add(new HoverRule
                    {
                        ParentSelector = $"{nodeSelector}:hover",
                        TargetSelector = null,
                        Comment = $"{node.Name} highlighted color",
                        Styles = new List<CssProp>
                        {
                            new CssProp("-unity-background-image-tint-color",
                                ToUssColor(sel.HighlightedColor))
                        }
                    });
                }

                if (sel.PressedColor != sel.NormalColor)
                {
                    result.HoverRules.Add(new HoverRule
                    {
                        ParentSelector = $"{nodeSelector}:active",
                        TargetSelector = null,
                        Comment = $"{node.Name} pressed color",
                        Styles = new List<CssProp>
                        {
                            new CssProp("-unity-background-image-tint-color",
                                ToUssColor(sel.PressedColor))
                        }
                    });
                }

                if (sel.DisabledColor != sel.NormalColor)
                {
                    result.HoverRules.Add(new HoverRule
                    {
                        ParentSelector = $"{nodeSelector}:disabled",
                        TargetSelector = null,
                        Comment = $"{node.Name} disabled color",
                        Styles = new List<CssProp>
                        {
                            new CssProp("-unity-background-image-tint-color",
                                ToUssColor(sel.DisabledColor)),
                            new CssProp("opacity", "0.5")
                        }
                    });
                }

                // Toggle :checked state
                if (sel.Type == "Toggle")
                {
                    result.HoverRules.Add(new HoverRule
                    {
                        ParentSelector = $"{nodeSelector}:checked",
                        TargetSelector = null,
                        Comment = $"{node.Name} checked/on state",
                        Styles = new List<CssProp>
                        {
                            new CssProp("-unity-background-image-tint-color",
                                ToUssColor(sel.PressedColor))
                        }
                    });
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // CSS Variable extraction (repeated colors)
        // ═══════════════════════════════════════════════════════════════

        static void ExtractCssVariables(ConversionResult result)
        {
            var colorCounts = new Dictionary<string, int>();
            foreach (var node in result.Nodes)
            {
                foreach (var style in node.Styles)
                {
                    if (style.Name == "color" ||
                        style.Name == "-unity-background-image-tint-color" ||
                        style.Name == "background-color")
                    {
                        if (!colorCounts.ContainsKey(style.Value))
                            colorCounts[style.Value] = 0;
                        colorCounts[style.Value]++;
                    }
                }
            }

            int varIndex = 1;
            foreach (var kvp in colorCounts)
            {
                if (kvp.Value >= 3 &&
                    kvp.Key != "rgb(255, 255, 255)" &&
                    kvp.Key != "rgb(0, 0, 0)")
                {
                    string varName = $"--color-{varIndex++}";
                    result.CssVariables[kvp.Key] = varName;
                }
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Phase 2: Generate USS
        // ═══════════════════════════════════════════════════════════════

        static string GenerateUss(ConversionResult result, string prefabPath)
        {
            var sb = new StringBuilder();
            sb.AppendLine("/* ═════════════════════════════════════════════════════════ */");
            sb.AppendLine($"/* Generated from: {prefabPath} */");
            sb.AppendLine($"/* {DateTime.Now:yyyy-MM-dd HH:mm} */");
            sb.AppendLine("/* ═════════════════════════════════════════════════════════ */");
            sb.AppendLine();

            // CSS Variables
            if (result.CssVariables.Count > 0)
            {
                sb.AppendLine(":root {");
                foreach (var kvp in result.CssVariables)
                    sb.AppendLine($"    {kvp.Value}: {kvp.Key};");
                sb.AppendLine("}");
                sb.AppendLine();
            }

            // Base styles
            foreach (var node in result.Nodes)
            {
                var styles = node.Styles.Where(s => !s.Name.StartsWith("/*")).ToList();
                var comments = node.Styles.Where(s => s.Name.StartsWith("/*")).ToList();

                if (styles.Count == 0 && comments.Count == 0) continue;

                // Section header with hierarchy path
                sb.AppendLine($"/* ── {node.HierarchyPath} ── */");

                string selector = $".{node.CssClass}";
                sb.AppendLine($"{selector} {{");

                foreach (var style in styles)
                {
                    string value = style.Value;
                    // Substitute CSS variables where applicable
                    if (result.CssVariables.TryGetValue(value, out string varName) &&
                        (style.Name == "color" ||
                         style.Name == "-unity-background-image-tint-color" ||
                         style.Name == "background-color"))
                        value = $"var({varName})";

                    string comment = style.Comment != null ? $" /* {style.Comment} */" : "";
                    sb.AppendLine($"    {style.Name}: {value};{comment}");
                }

                foreach (var c in comments)
                    sb.AppendLine($"    /* {c.Name.Trim('/', '*', ' ')}: {c.Value}" +
                        (c.Comment != null ? $" — {c.Comment}" : "") + " */");

                sb.AppendLine("}");

                // Child selector rules (spacing, grid cell sizing)
                if (node.ChildStyles.Count > 0)
                {
                    sb.AppendLine();
                    sb.AppendLine($"{selector} > * {{");
                    foreach (var cs in node.ChildStyles)
                    {
                        string comment = cs.Comment != null ? $" /* {cs.Comment} */" : "";
                        sb.AppendLine($"    {cs.Name}: {cs.Value};{comment}");
                    }
                    sb.AppendLine("}");
                }

                sb.AppendLine();
            }

            // Hover / interaction rules
            if (result.HoverRules.Count > 0)
            {
                sb.AppendLine("/* ═════════════════════════════════════════════════════════ */");
                sb.AppendLine("/* Hover / interaction states                               */");
                sb.AppendLine("/* ═════════════════════════════════════════════════════════ */");
                sb.AppendLine();

                foreach (var rule in result.HoverRules)
                {
                    string selector = rule.TargetSelector != null
                        ? $"{rule.ParentSelector} {rule.TargetSelector}"
                        : rule.ParentSelector;

                    if (rule.Comment != null)
                        sb.AppendLine($"/* {rule.Comment} */");
                    sb.AppendLine($"{selector} {{");
                    foreach (var style in rule.Styles)
                    {
                        string comment = style.Comment != null
                            ? $" /* {style.Comment} */" : "";
                        sb.AppendLine($"    {style.Name}: {style.Value};{comment}");
                    }
                    sb.AppendLine("}");
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        // ═══════════════════════════════════════════════════════════════
        // Phase 3: Generate UXML
        // ═══════════════════════════════════════════════════════════════

        static string GenerateUxml(ConversionResult result, string ussFileName)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<ui:UXML xmlns:ui=\"UnityEngine.UIElements\"" +
                " xmlns:uie=\"UnityEditor.UIElements\">");
            sb.AppendLine($"    <Style src=\"{ussFileName}.uss\" />");
            sb.AppendLine();

            // Template usage documentation
            sb.AppendLine("    <!--");
            sb.AppendLine($"      Template: {result.PrefabName}");
            sb.AppendLine("      Usage:");
            sb.AppendLine($"        <Template src=\"{ussFileName}.uxml\" name=\"{ussFileName}\" />");
            sb.AppendLine($"        <Instance template=\"{ussFileName}\">");
            // List overridable Label text attributes (with unique names)
            var labelNames = new HashSet<string>();
            foreach (var node in result.Nodes)
            {
                if (node.Element == "Label" && !string.IsNullOrEmpty(node.LabelText))
                {
                    // Use hierarchy path context if name is duplicate
                    string displayName = labelNames.Contains(node.Name)
                        ? node.HierarchyPath : node.Name;
                    labelNames.Add(node.Name);
                    string safeDisplay = displayName.Replace("--", "- -");
                    sb.AppendLine("            <AttributeOverrides " +
                        $"element-name=\"{EscapeXml(node.Name)}\" " +
                        $"text=\"...\" />   ({safeDisplay})");
                }
            }
            sb.AppendLine($"        </Instance>");
            sb.AppendLine("    -->");
            sb.AppendLine();

            if (result.Root != null)
                WriteUxmlNode(sb, result.Root, 1);

            sb.AppendLine("</ui:UXML>");
            return sb.ToString();
        }

        static void WriteUxmlNode(StringBuilder sb, NodeInfo node, int depth)
        {
            string indent = new string(' ', depth * 4);

            var attrs = new List<string>();
            attrs.Add($"name=\"{EscapeXml(node.Name)}\"");

            // CSS classes
            string classes = node.CssClass;
            if (node.ExtraClasses.Count > 0)
                classes += " " + string.Join(" ", node.ExtraClasses);
            attrs.Add($"class=\"{classes}\"");

            // Focusable
            if (node.IsFocusable)
                attrs.Add("focusable=\"true\"");

            // Picking mode
            if (node.IgnorePicking)
                attrs.Add("picking-mode=\"Ignore\"");

            string attrStr = string.Join(" ", attrs);

            if (node.Element == "Label")
            {
                string text = $"text=\"{EscapeXml(node.LabelText ?? "")}\"";
                sb.AppendLine($"{indent}<ui:Label {attrStr} {text} />");
            }
            else if (node.Children.Count == 0)
            {
                sb.AppendLine($"{indent}<ui:VisualElement {attrStr} />");
            }
            else
            {
                sb.AppendLine($"{indent}<ui:VisualElement {attrStr}>");
                foreach (var child in node.Children)
                    WriteUxmlNode(sb, child, depth + 1);
                sb.AppendLine($"{indent}</ui:VisualElement>");
            }
        }

        // ═══════════════════════════════════════════════════════════════
        // Helpers
        // ═══════════════════════════════════════════════════════════════

        static string FormatPx(float v)
        {
            if (Mathf.Abs(v) < 0.01f) return "0";
            if (Mathf.Abs(v - Mathf.Round(v)) < 0.01f)
                return $"{Mathf.RoundToInt(v)}px";
            return $"{v:F1}px";
        }

        static void RemoveStyle(NodeInfo info, string name)
        {
            for (int i = info.Styles.Count - 1; i >= 0; i--)
                if (info.Styles[i].Name == name)
                    info.Styles.RemoveAt(i);
        }

        static string SanitizeCssClass(string name)
        {
            string result = name.ToLowerInvariant();
            result = Regex.Replace(result, @"[^a-z0-9]+", "-");
            result = Regex.Replace(result, @"-+", "-");
            result = result.Trim('-');
            if (string.IsNullOrEmpty(result)) result = "element";
            return result;
        }

        static string GetUniqueCssClass(string name, HashSet<string> used)
        {
            string baseName = SanitizeCssClass(name);
            string candidate = baseName;
            int counter = 2;
            while (used.Contains(candidate))
                candidate = $"{baseName}-{counter++}";
            used.Add(candidate);
            return candidate;
        }

        /// <summary>
        /// Resolve a TMP_FontAsset to its source TTF/OTF file path.
        /// 1. m_SourceFontFileGUID — the stored GUID of the original font (most reliable)
        /// 2. m_SourceFontFile object reference
        /// 3. Name-based matching (exact, then fuzzy family)
        /// </summary>
        static string ResolveTmpFontToTtf(TMP_FontAsset tmpFont)
        {
            if (tmpFont == null) return null;

            var so = new SerializedObject(tmpFont);

            // 1. m_SourceFontFileGUID — TMP stores the GUID even when the object ref is null
            var guidProp = so.FindProperty("m_SourceFontFileGUID");
            if (guidProp != null && !string.IsNullOrEmpty(guidProp.stringValue))
            {
                string path = AssetDatabase.GUIDToAssetPath(guidProp.stringValue);
                if (!string.IsNullOrEmpty(path)) return path;
            }

            // 2. m_SourceFontFile object reference
            var srcProp = so.FindProperty("m_SourceFontFile");
            if (srcProp != null && srcProp.objectReferenceValue != null)
            {
                string path = AssetDatabase.GetAssetPath(srcProp.objectReferenceValue);
                if (!string.IsNullOrEmpty(path)) return path;
            }

            // 3. Strip SDF suffix and try exact name match
            string fontName = Regex.Replace(tmpFont.name, @"\s*SDF.*$", "", RegexOptions.IgnoreCase).Trim();
            var guids = AssetDatabase.FindAssets($"{fontName} t:Font");
            if (guids.Length > 0)
                return AssetDatabase.GUIDToAssetPath(guids[0]);

            // 4. Fuzzy: strip weight suffix and match font family
            string familyName = Regex.Replace(fontName, @"[-_\s]?(Thin|ExtraLight|Light|Regular|Medium|SemiBold|DemiBold|Bold|ExtraBold|Black|Heavy)$", "",
                RegexOptions.IgnoreCase).Trim();
            if (!string.IsNullOrEmpty(familyName) && familyName != fontName)
            {
                guids = AssetDatabase.FindAssets($"{familyName} t:Font");
                if (guids.Length > 0)
                {
                    foreach (var guid in guids)
                    {
                        string path = AssetDatabase.GUIDToAssetPath(guid);
                        if (System.IO.Path.GetFileNameWithoutExtension(path)
                            .Contains("Regular", StringComparison.OrdinalIgnoreCase))
                            return path;
                    }
                    return AssetDatabase.GUIDToAssetPath(guids[0]);
                }
            }

            return null;
        }

        static string ToUssColor(Color c)
        {
            int r = Mathf.RoundToInt(c.r * 255);
            int g = Mathf.RoundToInt(c.g * 255);
            int b = Mathf.RoundToInt(c.b * 255);
            if (c.a >= 0.999f)
                return $"rgb({r}, {g}, {b})";
            return $"rgba({r}, {g}, {b}, {c.a:F2})";
        }

        static string EscapeXml(string s)
        {
            if (s == null) return "";
            return s.Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;")
                    .Replace("\"", "&quot;");
        }

        static string MapTextAlignment(TextAlignmentOptions alignment)
        {
            switch (alignment)
            {
                case TextAlignmentOptions.TopLeft: return "upper-left";
                case TextAlignmentOptions.Top: return "upper-center";
                case TextAlignmentOptions.TopRight: return "upper-right";
                case TextAlignmentOptions.Left: return "middle-left";
                case TextAlignmentOptions.Center: return "middle-center";
                case TextAlignmentOptions.Right: return "middle-right";
                case TextAlignmentOptions.BottomLeft: return "lower-left";
                case TextAlignmentOptions.Bottom: return "lower-center";
                case TextAlignmentOptions.BottomRight: return "lower-right";
                case TextAlignmentOptions.BaselineLeft: return "lower-left";
                case TextAlignmentOptions.Baseline: return "lower-center";
                case TextAlignmentOptions.BaselineRight: return "lower-right";
                default: return null;
            }
        }

        static (string justify, string align) MapChildAlignment(TextAnchor anchor)
        {
            switch (anchor)
            {
                case TextAnchor.UpperLeft: return ("flex-start", "flex-start");
                case TextAnchor.UpperCenter: return ("flex-start", "center");
                case TextAnchor.UpperRight: return ("flex-start", "flex-end");
                case TextAnchor.MiddleLeft: return ("center", "flex-start");
                case TextAnchor.MiddleCenter: return ("center", "center");
                case TextAnchor.MiddleRight: return ("center", "flex-end");
                case TextAnchor.LowerLeft: return ("flex-end", "flex-start");
                case TextAnchor.LowerCenter: return ("flex-end", "center");
                case TextAnchor.LowerRight: return ("flex-end", "flex-end");
                default: return (null, null);
            }
        }
    }
}
#endif
