# Unity UI Toolkit UXML Examples Reference

Source: Unity 6 (6000.3) Documentation - UXML Examples Hub

---

## 1. Relative and Absolute Positioning

Demonstrates the distinction between relative and absolute positioning in UI Toolkit.

### Editor UI (C#)

```csharp
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

public class PositioningTestWindow : EditorWindow
{
    [MenuItem("Window/UI Toolkit/Positioning Test Window")]
    public static void ShowExample()
    {
        var wnd = GetWindow<PositioningTestWindow>();
        wnd.titleContent = new GUIContent("Positioning Test Window");
    }

    public void CreateGUI()
    {
        for (int i = 0; i < 2; i++)
        {
            var temp = new VisualElement();
            temp.style.width = 70;
            temp.style.height = 70;
            temp.style.marginBottom = 2;
            temp.style.backgroundColor = Color.gray;
            this.rootVisualElement.Add(temp);
        }

        // Relative positioning
        var relative = new Label("Relative\nPos\n25, 0");
        relative.style.width = 70;
        relative.style.height = 70;
        relative.style.left = 25;
        relative.style.marginBottom = 2;
        relative.style.backgroundColor = new Color(0.2165094f, 0, 0.254717f);
        this.rootVisualElement.Add(relative);

        for (int i = 0; i < 2; i++)
        {
            var temp = new VisualElement();
            temp.style.width = 70;
            temp.style.height = 70;
            temp.style.marginBottom = 2;
            temp.style.backgroundColor = Color.gray;
            this.rootVisualElement.Add(temp);
        }

        // Absolute positioning
        var absolutePositionElement = new Label("Absolute\nPos\n25, 25");
        absolutePositionElement.style.position = Position.Absolute;
        absolutePositionElement.style.top = 25;
        absolutePositionElement.style.left = 25;
        absolutePositionElement.style.width = 70;
        absolutePositionElement.style.height = 70;
        absolutePositionElement.style.backgroundColor = Color.black;
        this.rootVisualElement.Add(absolutePositionElement);
    }
}
```

### Runtime UI (UXML + USS)

**PositioningTest.uss**
```css
.box {
    height: 70px;
    width: 70px;
    margin-bottom: 2px;
    background-color: gray;
}
#relative {
    width: 70px;
    height: 70px;
    background-color: purple;
    left: 25px;
    margin-bottom: 2px;
    position: relative;
}
#absolutePositionElement {
    left: 25px;
    top: 25px;
    width: 70px;
    height: 70px;
    background-color: black;
    position: absolute;
}
```

**PositioningTest.uxml**
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements">
    <Style src="PositioningTest.uss"/>
    <ui:VisualElement class="box"/>
    <ui:VisualElement class="box"/>
    <ui:Label text="Relative\nPos\n25, 0" name="relative" />
    <ui:VisualElement class="box"/>
    <ui:VisualElement class="box"/>
    <ui:Label text="Absolute\nPos\n25, 25" name="absolutePositionElement" />
</ui:UXML>
```

### Key Concepts
- **Relative Positioning**: Offsets from natural position in layout flow. Other elements are NOT affected.
- **Absolute Positioning**: Places at specific coordinates independent of layout flow. Removed from flow entirely.

---

## 2. Create List and Tree Views

Demonstrates four collection view patterns using planetary data: ListView, MultiColumnListView, TreeView, MultiColumnTreeView.

### Shared Data Structure

```csharp
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEngine.UIElements;

public class PlanetsWindow : EditorWindow
{
    [SerializeField]
    protected VisualTreeAsset uxmlAsset;

    protected interface IPlanetOrGroup
    {
        public string name { get; }
        public bool populated { get; }
    }

    protected class Planet : IPlanetOrGroup
    {
        public string name { get; }
        public bool populated { get; }
        public Planet(string name, bool populated = false)
        {
            this.name = name;
            this.populated = populated;
        }
    }

    protected class PlanetGroup : IPlanetOrGroup
    {
        public string name { get; }
        public bool populated
        {
            get
            {
                var anyPlanetPopulated = false;
                foreach (Planet planet in planets)
                    anyPlanetPopulated = anyPlanetPopulated || planet.populated;
                return anyPlanetPopulated;
            }
        }
        public readonly IReadOnlyList<Planet> planets;
        public PlanetGroup(string name, IReadOnlyList<Planet> planets)
        {
            this.name = name;
            this.planets = planets;
        }
    }

    protected static readonly List<PlanetGroup> planetGroups = new List<PlanetGroup>
    {
        new PlanetGroup("Inner Planets", new List<Planet>
        {
            new Planet("Mercury"), new Planet("Venus"),
            new Planet("Earth", true), new Planet("Mars")
        }),
        new PlanetGroup("Outer Planets", new List<Planet>
        {
            new Planet("Jupiter"), new Planet("Saturn"),
            new Planet("Uranus"), new Planet("Neptune")
        })
    };

    protected static List<Planet> planets
    {
        get
        {
            var retVal = new List<Planet>(8);
            foreach (var group in planetGroups)
                retVal.AddRange(group.planets);
            return retVal;
        }
    }

    protected static IList<TreeViewItemData<IPlanetOrGroup>> treeRoots
    {
        get
        {
            int id = 0;
            var roots = new List<TreeViewItemData<IPlanetOrGroup>>(planetGroups.Count);
            foreach (var group in planetGroups)
            {
                var planetsInGroup = new List<TreeViewItemData<IPlanetOrGroup>>(group.planets.Count);
                foreach (var planet in group.planets)
                    planetsInGroup.Add(new TreeViewItemData<IPlanetOrGroup>(id++, planet));
                roots.Add(new TreeViewItemData<IPlanetOrGroup>(id++, group, planetsInGroup));
            }
            return roots;
        }
    }
}
```

### Standard ListView

**UXML:**
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements">
    <ui:ListView fixed-item-height="20" />
</ui:UXML>
```

**C#:**
```csharp
public class PlanetsListView : PlanetsWindow
{
    [MenuItem("Planets/Standard List")]
    static void Summon() => GetWindow<PlanetsListView>("Standard Planet List");

    void CreateGUI()
    {
        uxmlAsset.CloneTree(rootVisualElement);
        var listView = rootVisualElement.Q<ListView>();
        listView.itemsSource = planets;
        listView.makeItem = () => new Label();
        listView.bindItem = (VisualElement element, int index) =>
            (element as Label).text = planets[index].name;
    }
}
```

### MultiColumnListView

**UXML:**
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements">
    <ui:MultiColumnListView fixed-item-height="20">
        <ui:Columns>
            <ui:Column name="name" title="Name" width="80" />
            <ui:Column name="populated" title="Populated?" width="80" />
        </ui:Columns>
    </ui:MultiColumnListView>
</ui:UXML>
```

**C#:**
```csharp
public class PlanetsMultiColumnListView : PlanetsWindow
{
    [MenuItem("Planets/Multicolumn List")]
    static void Summon() => GetWindow<PlanetsMultiColumnListView>("Multicolumn Planet List");

    void CreateGUI()
    {
        uxmlAsset.CloneTree(rootVisualElement);
        var listView = rootVisualElement.Q<MultiColumnListView>();
        listView.itemsSource = planets;
        listView.columns["name"].makeCell = () => new Label();
        listView.columns["populated"].makeCell = () => new Toggle();
        listView.columns["name"].bindCell = (VisualElement element, int index) =>
            (element as Label).text = planets[index].name;
        listView.columns["populated"].bindCell = (VisualElement element, int index) =>
            (element as Toggle).value = planets[index].populated;
    }
}
```

### Standard TreeView

**UXML:**
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements">
    <ui:TreeView fixed-item-height="20" />
</ui:UXML>
```

**C#:**
```csharp
public class PlanetsTreeView : PlanetsWindow
{
    [MenuItem("Planets/Standard Tree")]
    static void Summon() => GetWindow<PlanetsTreeView>("Standard Planet Tree");

    void CreateGUI()
    {
        uxmlAsset.CloneTree(rootVisualElement);
        var treeView = rootVisualElement.Q<TreeView>();
        treeView.SetRootItems(treeRoots);
        treeView.makeItem = () => new Label();
        treeView.bindItem = (VisualElement element, int index) =>
            (element as Label).text = treeView.GetItemDataForIndex<IPlanetOrGroup>(index).name;
    }
}
```

### MultiColumnTreeView

**UXML:**
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements">
    <ui:MultiColumnTreeView fixed-item-height="20">
        <ui:Columns>
            <ui:Column name="name" title="Name" width="120" />
            <ui:Column name="populated" title="Populated?" width="80" />
        </ui:Columns>
    </ui:MultiColumnTreeView>
</ui:UXML>
```

**C#:**
```csharp
public class PlanetsMultiColumnTreeView : PlanetsWindow
{
    [MenuItem("Planets/Multicolumn Tree")]
    static void Summon() => GetWindow<PlanetsMultiColumnTreeView>("Multicolumn Planet Tree");

    void CreateGUI()
    {
        uxmlAsset.CloneTree(rootVisualElement);
        var treeView = rootVisualElement.Q<MultiColumnTreeView>();
        treeView.SetRootItems(treeRoots);
        treeView.columns["name"].makeCell = () => new Label();
        treeView.columns["populated"].makeCell = () => new Toggle();
        treeView.columns["name"].bindCell = (VisualElement element, int index) =>
            (element as Label).text = treeView.GetItemDataForIndex<IPlanetOrGroup>(index).name;
        treeView.columns["populated"].bindCell = (VisualElement element, int index) =>
            (element as Toggle).value = treeView.GetItemDataForIndex<IPlanetOrGroup>(index).populated;
    }
}
```

---

## 3. Create a Complex ListView

A character roster with HP sliders and dynamic color indicators using a custom `CharacterInfoVisualElement`.

```csharp
using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace UIToolkitExamples {
    public class ListViewExample : EditorWindow {
        private Gradient hpGradient;
        private GradientColorKey[] hpColorKey;
        private GradientAlphaKey[] hpAlphaKey;
        private ListView listView;
        private List<CharacterInfo> items;

        [MenuItem("Window/ListView Custom Item")]
        public static void OpenWindow() => GetWindow<ListViewExample>().Show();

        private void OnEnable() {
            SetGradient();
            const int itemCount = 50;
            items = new List<CharacterInfo>(itemCount);
            for (int i = 1; i <= itemCount; i++) {
                CharacterInfo character = new CharacterInfo { name = $"Character {i}", maxHp = 100 };
                character.currentHp = character.maxHp;
                items.Add(character);
            }

            Func<VisualElement> makeItem = () => {
                var characterInfoVisualElement = new CharacterInfoVisualElement();
                var slider = characterInfoVisualElement.Q<SliderInt>(name: "hp");
                slider.RegisterValueChangedCallback(evt => {
                    var hpColor = characterInfoVisualElement.Q<VisualElement>("hpColor");
                    var i = (int)slider.userData;
                    var characterInfo = items[i];
                    characterInfo.currentHp = evt.newValue;
                    SetHp(slider, hpColor, characterInfo);
                });
                return characterInfoVisualElement;
            };

            Action<VisualElement, int> bindItem = (e, i) => BindItem(e as CharacterInfoVisualElement, i);
            int itemHeight = 55;
            listView = new ListView(items, itemHeight, makeItem, bindItem);
            listView.reorderable = false;
            listView.style.flexGrow = 1f;
            listView.showBorder = true;
            rootVisualElement.Add(listView);
        }

        private void SetGradient() {
            hpGradient = new Gradient();
            hpColorKey = new GradientColorKey[4];
            hpColorKey[0] = new GradientColorKey(Color.red, 0f);
            hpColorKey[1] = new GradientColorKey(new Color(1f, 0.55f, 0f), 0.1f);
            hpColorKey[2] = new GradientColorKey(Color.yellow, 0.4f);
            hpColorKey[3] = new GradientColorKey(Color.green, 1f);
            hpAlphaKey = new GradientAlphaKey[2];
            hpAlphaKey[0] = new GradientAlphaKey(1f, 0f);
            hpAlphaKey[1] = new GradientAlphaKey(1f, 1f);
            hpGradient.SetKeys(hpColorKey, hpAlphaKey);
        }

        private void BindItem(CharacterInfoVisualElement elem, int i) {
            var label = elem.Q<Label>(name: "nameLabel");
            var slider = elem.Q<SliderInt>(name: "hp");
            var hpColor = elem.Q<VisualElement>("hpColor");
            slider.userData = i;
            CharacterInfo characterInfo = items[i];
            label.text = characterInfo.name;
            SetHp(slider, hpColor, characterInfo);
        }

        private void SetHp(SliderInt slider, VisualElement colorIndicator, CharacterInfo characterInfo) {
            slider.highValue = characterInfo.maxHp;
            slider.SetValueWithoutNotify(characterInfo.currentHp);
            float ratio = (float)characterInfo.currentHp / characterInfo.maxHp;
            colorIndicator.style.backgroundColor = hpGradient.Evaluate(ratio);
        }

        public class CharacterInfoVisualElement : VisualElement {
            public CharacterInfoVisualElement() {
                var root = new VisualElement();
                root.style.paddingTop = 3f;
                root.style.paddingRight = 0f;
                root.style.paddingBottom = 15f;
                root.style.paddingLeft = 3f;
                root.style.borderBottomColor = Color.gray;
                root.style.borderBottomWidth = 1f;
                var nameLabel = new Label() { name = "nameLabel" };
                nameLabel.style.fontSize = 14f;
                var hpContainer = new VisualElement();
                hpContainer.style.flexDirection = FlexDirection.Row;
                hpContainer.style.paddingLeft = 15f;
                hpContainer.style.paddingRight = 15f;
                hpContainer.Add(new Label("HP:"));
                var hpSlider = new SliderInt { name = "hp", lowValue = 0, highValue = 100 };
                hpSlider.style.flexGrow = 1f;
                hpContainer.Add(hpSlider);
                var hpColor = new VisualElement();
                hpColor.name = "hpColor";
                hpColor.style.height = 15f;
                hpColor.style.width = 15f;
                hpColor.style.marginRight = 5f;
                hpColor.style.marginBottom = 5f;
                hpColor.style.marginLeft = 5f;
                hpColor.style.backgroundColor = Color.black;
                hpContainer.Add(hpColor);
                root.Add(nameLabel);
                root.Add(hpContainer);
                Add(root);
            }
        }

        [Serializable]
        public class CharacterInfo {
            public string name;
            public int maxHp;
            public int currentHp;
        }
    }
}
```

---

## 4. Create a Runtime List View UI (Character Selection)

A runtime character selection screen with a left list and right detail panel.

### MainView.uxml
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements" editor-extension-mode="False">
    <Style src="MainView.uss" />
    <ui:VisualElement name="background">
        <ui:VisualElement name="main-container">
            <ui:ListView focusable="true" name="character-list" />
            <ui:VisualElement name="right-container">
                <ui:VisualElement name="details-container">
                    <ui:VisualElement name="details">
                        <ui:VisualElement name="character-portrait" />
                    </ui:VisualElement>
                    <ui:Label text="Label" name="character-name" />
                    <ui:Label text="Label" display-tooltip-when-elided="true" name="character-class" />
                </ui:VisualElement>
            </ui:VisualElement>
        </ui:VisualElement>
    </ui:VisualElement>
</ui:UXML>
```

### ListEntry.uxml
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements" editor-extension-mode="False">
    <Style src="ListEntry.uss" />
    <ui:VisualElement name="list-entry">
        <ui:Label text="Label" display-tooltip-when-elided="true" name="character-name" />
    </ui:VisualElement>
</ui:UXML>
```

### MainView.uss
```css
#background {
    flex-grow: 1;
    align-items: center;
    justify-content: center;
    background-color: rgb(115, 37, 38);
}
#main-container {
    flex-direction: row;
    height: 350px;
}
#character-list {
    width: 230px;
    border-color: rgb(49, 26, 17);
    border-width: 4px;
    background-color: rgb(110, 57, 37);
    border-radius: 15px;
    margin-right: 6px;
}
#character-name {
    -unity-font-style: bold;
    font-size: 18px;
}
#character-class {
    margin-top: 2px;
    margin-bottom: 8px;
}
#right-container {
    justify-content: space-between;
    align-items: flex-end;
}
#details-container {
    align-items: center;
    background-color: rgb(170, 89, 57);
    border-width: 4px;
    border-color: rgb(49, 26, 17);
    border-radius: 15px;
    width: 252px;
    justify-content: center;
    padding: 8px;
    height: 163px;
}
#details {
    border-color: rgb(49, 26, 17);
    border-width: 2px;
    height: 120px;
    width: 120px;
    border-radius: 13px;
    padding: 4px;
    background-color: rgb(255, 133, 84);
}
#character-portrait {
    flex-grow: 1;
    -unity-background-scale-mode: scale-to-fit;
}
/* ListView item styling */
.unity-collection-view__item { justify-content: center; background-color: slategrey; }
.unity-collection-view__item:hover { background-color: gray; }
.unity-collection-view__item--selected { background-color: black; }
```

### ListEntry.uss
```css
#list-entry {
    height: 41px;
    align-items: flex-start;
    justify-content: center;
    padding-left: 10px;
    background-color: rgb(170, 89, 57);
    border-color: rgb(49, 26, 17);
    border-width: 2px;
    border-radius: 15px;
}
#character-name {
    -unity-font-style: bold;
    font-size: 18px;
    color: rgb(49, 26, 17);
}
```

### Key Pattern: Controller + ListView
```csharp
// Controller pattern separates data from UI
public class CharacterListController
{
    ListView m_CharacterList;
    List<CharacterData> m_AllCharacters;

    public void InitializeCharacterList(VisualElement root, VisualTreeAsset listElementTemplate)
    {
        m_AllCharacters = new List<CharacterData>();
        m_AllCharacters.AddRange(Resources.LoadAll<CharacterData>("Characters"));

        m_CharacterList = root.Q<ListView>("character-list");
        m_CharacterList.makeItem = () =>
        {
            var newListEntry = listElementTemplate.Instantiate();
            var newListEntryLogic = new CharacterListEntryController();
            newListEntry.userData = newListEntryLogic;
            newListEntryLogic.SetVisualElement(newListEntry);
            return newListEntry;
        };
        m_CharacterList.bindItem = (item, index) =>
        {
            (item.userData as CharacterListEntryController)?.SetCharacterData(m_AllCharacters[index]);
        };
        m_CharacterList.fixedItemHeight = 45;
        m_CharacterList.itemsSource = m_AllCharacters;
        m_CharacterList.selectionChanged += OnCharacterSelected;
    }
}
```

---

## 5. Drag-and-Drop Between List and Tree Views

Demonstrates drag-and-drop between ListView, MultiColumnListView, and TreeView in an editor window.

### Key UXML Layout
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements" editor-extension-mode="False">
    <Style src="main.uss" />
    <ui:VisualElement class="main-view">
        <ui:Toggle name="Toggle-LobbyOwner" text="Lobby Owner" />
        <ui:VisualElement class="section-container">
            <ui:TwoPaneSplitView fixed-pane-initial-dimension="300">
                <ui:VisualElement class="split-window">
                    <ui:VisualElement name="LobbyContainer" class="section-container">
                        <ui:Label text="Lobby" name="Name-Lobby" />
                        <ui:ListView name="ListView-Lobby" reorderable="true" selection-type="Multiple" class="team-list" />
                    </ui:VisualElement>
                </ui:VisualElement>
                <ui:VisualElement class="split-window">
                    <ui:VisualElement name="BlueTeam" class="section-container">
                        <ui:Label text="Blue Team" />
                        <ui:MultiColumnListView name="ListView-BlueTeam" reorderable="true" selection-type="Multiple" class="team-list">
                            <ui:Columns>
                                <ui:Column name="icon" title="Icon" width="50" resizable="false" />
                                <ui:Column name="number" title="#" width="40" resizable="false" />
                                <ui:Column name="name" stretchable="true" title="Name" />
                            </ui:Columns>
                        </ui:MultiColumnListView>
                    </ui:VisualElement>
                    <ui:VisualElement name="RedTeam" class="section-container">
                        <ui:Label text="Red Team" />
                        <ui:TreeView name="TreeView-RedTeam" reorderable="true" selection-type="Multiple" class="team-list" />
                    </ui:VisualElement>
                </ui:VisualElement>
            </ui:TwoPaneSplitView>
        </ui:VisualElement>
    </ui:VisualElement>
</ui:UXML>
```

### Key Drag-and-Drop Callbacks
```csharp
// Required callbacks for drag-and-drop:
listView.canStartDrag += OnCanStartDrag;
listView.setupDragAndDrop += args => OnSetupDragAndDrop(args, listView);
listView.dragAndDropUpdate += args => OnDragAndDropUpdate(args, listView, true);
listView.handleDrop += args => OnHandleDrop(args, listView, true);

// Use StartDragArgs with generic data to pass source and dragged items
var startDragArgs = new StartDragArgs(args.startDragArgs.title, DragVisualMode.Move);
startDragArgs.SetGenericData("SourceKey", source);
startDragArgs.SetGenericData("DraggedIndices", args.selectedIds);
```

---

## 6. Wrap Content Inside a ScrollView

### UXML
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements" editor-extension-mode="True">
    <Style src="ScrollViewExample.uss" />
    <ui:ScrollView>
        <ui:VisualElement>
            <ui:Label text="ScrollView Wrapping Example" />
        </ui:VisualElement>
    </ui:ScrollView>
    <ui:ScrollView name="scroll-view-wrap-example" />
</ui:UXML>
```

### USS (Key Wrapping Styles)
```css
Label {
    font-size: 20px;
    -unity-font-style: bold;
    color: rgb(68, 138, 255);
    white-space: normal; /* Text wrapping */
}

/* Element wrapping inside scroll view */
#scroll-view-wrap-example .unity-scroll-view__content-container {
    flex-direction: row;
    flex-wrap: wrap;
}

Button {
    width: 50px;
    height: 50px;
}
```

### C#
```csharp
public class ScrollViewExample : EditorWindow
{
    public void CreateGUI()
    {
        var visualTree = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Editor/ScrollViewExample.uxml");
        root.Add(visualTree.Instantiate());
        VisualElement scrollview = root.Query<ScrollView>("scroll-view-wrap-example");
        for (int i = 0; i < 15; i++)
        {
            Button button = new Button();
            button.text = "Button";
            scrollview.Add(button);
        }
    }
}
```

### Key Patterns
- **Text wrapping**: `white-space: normal` on Labels
- **Element wrapping**: `flex-direction: row` + `flex-wrap: wrap` on `.unity-scroll-view__content-container`

---

## 7. Create a Tabbed Menu

Uses Tab and TabView controls for tab-based navigation.

### UXML
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements" editor-extension-mode="False">
    <Style src="TabbedMenu.uss" />
    <ui:TabView reorderable="true" view-data-key="TabbedMenu">
        <ui:Tab label="London" view-data-key="LondonTab">
            <ui:Label text="London is the capital city of England" class="tab-content"/>
        </ui:Tab>
        <ui:Tab label="Paris" view-data-key="ParisTab">
            <ui:Label text="Paris is the capital of France" class="tab-content"/>
        </ui:Tab>
        <ui:Tab label="Ottawa" view-data-key="OttawaTab">
            <ui:Label text="Ottawa is the capital of Canada" class="tab-content"/>
        </ui:Tab>
    </ui:TabView>
</ui:UXML>
```

### USS
```css
.unity-tab__header {
    background-color: rgb(229, 223, 223);
    -unity-font-style: bold;
    font-size: 14px;
}
.unity-tab__header:checked {
    background-color: rgb(173, 166, 166);
}
.tab-content {
    background-color: rgb(255, 255, 255);
    font-size: 20px;
}
.unity-tab__header-underline {
    opacity: 0;
}
```

### Key Features
- `view-data-key` on TabView and Tabs enables persistence of tab order across sessions
- `reorderable="true"` allows drag-to-reorder tabs

---

## 8. Create a Pop-up Window

Uses `UnityEditor.PopupWindow` with UI Toolkit content.

### Trigger Button UXML
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements" editor-extension-mode="True">
    <Style src="PopupExample.uss" />
    <ui:Button text="Popup Options" class="popup-button" />
</ui:UXML>
```

### Popup Content UXML
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements" editor-extension-mode="True">
    <ui:Toggle label="Toggle 1" name="Toggle1"/>
    <ui:Toggle label="Toggle 2" />
    <ui:Toggle label="Toggle 3" />
</ui:UXML>
```

### C# (Show popup)
```csharp
var button = rootVisualElement.Q<Button>();
button.clicked += () => PopupWindow.Show(button.worldBound, new PopupContentExample());
```

### C# (Popup content)
```csharp
public class PopupContentExample : PopupWindowContent
{
    public override VisualElement CreateGUI()
    {
        var visualTreeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>("Assets/Editor/PopupWindowContent.uxml");
        return visualTreeAsset.CloneTree();
    }
}
```

---

## 9. Use Toggle to Create a Conditional UI

```csharp
public class ToggleExample : EditorWindow
{
    [MenuItem("Window/ToggleExample")]
    public static void OpenWindow() => GetWindow<ToggleExample>("Controls: Toggle Sample");

    public void CreateGUI()
    {
        var showToggle = new Toggle("Show label") { value = true };
        var activateToggle = new Toggle("Active button") { value = true };
        var labelToShow = new Label("This label is shown when the above toggle is set to On");
        var buttonToActivate = new Button(() => Debug.Log("Button pressed!"))
        {
            text = "Active if above toggle is On"
        };

        rootVisualElement.Add(showToggle);
        rootVisualElement.Add(labelToShow);
        rootVisualElement.Add(activateToggle);
        rootVisualElement.Add(buttonToActivate);

        showToggle.RegisterValueChangedCallback(evt => labelToShow.visible = evt.newValue);
        activateToggle.RegisterValueChangedCallback(evt => buttonToActivate.SetEnabled(evt.newValue));
    }
}
```

### Key Patterns
- `RegisterValueChangedCallback` for reactive UI updates
- `.visible` controls visibility (element still takes space)
- `.SetEnabled()` controls interactability

---

## 10. Create a Custom Control with Two Attributes

```csharp
using UnityEngine;
using UnityEngine.UIElements;

[UxmlElement]
partial class MyElement : VisualElement
{
    string _myString = "default_value";
    int _myInt = 2;

    [UxmlAttribute]
    public string myString
    {
        get => _myString;
        set { _myString = value; }
    }

    [UxmlAttribute]
    public int myInt
    {
        get => _myInt;
        set { _myInt = value; }
    }
}
```

### Key Patterns
- `[UxmlElement]` + `partial` class exposes to UXML and UI Builder
- `[UxmlAttribute]` on properties exposes them as configurable attributes
- Appears in UI Builder under Library > Project > Custom Controls

---

## 11. Create a Slide Toggle Custom Control

A switch-like toggle inheriting from `BaseField<bool>`.

### C#
```csharp
using UnityEngine;
using UnityEngine.UIElements;

namespace MyUILibrary
{
    [UxmlElement]
    public partial class SlideToggle : BaseField<bool>
    {
        public static readonly new string ussClassName = "slide-toggle";
        public static readonly new string inputUssClassName = "slide-toggle__input";
        public static readonly string inputKnobUssClassName = "slide-toggle__input-knob";
        public static readonly string inputCheckedUssClassName = "slide-toggle__input--checked";

        VisualElement m_Input;
        VisualElement m_Knob;

        public SlideToggle() : this(null) { }

        public SlideToggle(string label) : base(label, null)
        {
            AddToClassList(ussClassName);
            m_Input = this.Q(className: BaseField<bool>.inputUssClassName);
            m_Input.AddToClassList(inputUssClassName);
            m_Knob = new();
            m_Knob.AddToClassList(inputKnobUssClassName);
            m_Input.Add(m_Knob);

            RegisterCallback<ClickEvent>(evt => { (evt.currentTarget as SlideToggle).ToggleValue(); evt.StopPropagation(); });
            RegisterCallback<KeyDownEvent>(evt => OnKeydownEvent(evt));
            RegisterCallback<NavigationSubmitEvent>(evt => { (evt.currentTarget as SlideToggle).ToggleValue(); evt.StopPropagation(); });
        }

        void ToggleValue() => value = !value;

        public override void SetValueWithoutNotify(bool newValue)
        {
            base.SetValueWithoutNotify(newValue);
            m_Input.EnableInClassList(inputCheckedUssClassName, newValue);
        }
    }
}
```

### USS
```css
.slide-toggle__input {
    background-color: var(--unity-colors-slider_groove-background);
    max-width: 25px;
    border-radius: 8px;
    overflow: visible;
    border-width: 1px;
    border-color: var(--unity-colors-slider_thumb-border);
    max-height: 16px;
    margin-top: 10px;
    transition-property: background-color;
    transition-duration: 0.5s;
}
.slide-toggle__input-knob {
    height: 16px;
    width: 16px;
    background-color: var(--unity-colors-slider_thumb-background);
    position: absolute;
    border-radius: 25px;
    top: -1px;
    transition-property: translate, background-color;
    transition-duration: 0.5s, 0.5s;
    translate: -1px 0;
    border-width: 1px;
    border-color: var(--unity-colors-slider_thumb-border);
}
.slide-toggle__input--checked {
    background-color: rgb(0, 156, 10);
}
.slide-toggle__input--checked > .slide-toggle__input-knob {
    translate: 8px 0;
}
```

---

## 12. Create a Radial Progress Indicator (Mesh API)

Custom control using `generateVisualContent` to render a ring-shaped progress indicator.

### Key Pattern
```csharp
[UxmlElement]
public partial class RadialProgress : VisualElement
{
    static CustomStyleProperty<Color> s_TrackColor = new("--track-color");
    static CustomStyleProperty<Color> s_ProgressColor = new("--progress-color");

    [UxmlAttribute]
    public float progress
    {
        get => m_Progress;
        set { m_Progress = value; m_Label.text = Mathf.Clamp(Mathf.Round(value), 0, 100) + "%"; MarkDirtyRepaint(); }
    }

    public RadialProgress()
    {
        RegisterCallback<CustomStyleResolvedEvent>(evt => CustomStylesResolved(evt));
        generateVisualContent += context => GenerateVisualContent(context);
    }
}
```

### USS Custom Properties
```css
.radial-progress {
    --track-color: rgb(130, 130, 130);
    --progress-color: rgb(46, 132, 24);
    --percentage-color: white;
    width: 100px;
    height: 100px;
}
```

---

## 13. Create a Bindable Custom Control

A `BaseField<double>` that binds to serialized properties.

### Custom Field
```csharp
[UxmlElement]
public partial class ExampleField : BaseField<double>
{
    Label m_Input;
    public ExampleField() : this(null) { }
    public ExampleField(string label) : base(label, new Label() { })
    {
        m_Input = this.Q<Label>(className: inputUssClassName);
    }
    public override void SetValueWithoutNotify(double newValue)
    {
        base.SetValueWithoutNotify(newValue);
        m_Input.text = value.ToString("N");
    }
}
```

### UXML Binding
```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements" xmlns:example="UIToolkitExamples">
    <example:ExampleField label="Binding Target" binding-path="m_Value" />
</ui:UXML>
```

---

## 14. Create a Custom Style for a Custom Control

Uses `CustomStyleProperty<T>` to read USS custom variables.

```csharp
[UxmlElement]
public partial class ExampleElementCustomStyle : VisualElement
{
    static readonly CustomStyleProperty<Color> S_GradientFrom = new("--gradient-from");
    static readonly CustomStyleProperty<Color> S_GradientTo = new("--gradient-to");

    public ExampleElementCustomStyle()
    {
        m_Texture2D = new Texture2D(100, 100);
        m_Image = new Image();
        m_Image.image = m_Texture2D;
        Add(m_Image);
        RegisterCallback<CustomStyleResolvedEvent>(OnStylesResolved);
    }

    void OnStylesResolved(CustomStyleResolvedEvent evt)
    {
        if (evt.customStyle.TryGetValue(S_GradientFrom, out var from)
            && evt.customStyle.TryGetValue(S_GradientTo, out var to))
            GenerateGradient(from, to);
    }
}
```

### USS
```css
ExampleElementCustomStyle {
    --gradient-from: red;
    --gradient-to: yellow;
}
```

---

## 15. Create an Aspect Ratio Custom Control

Dynamically adjusts padding to maintain a specified aspect ratio.

```csharp
[UxmlElement]
public partial class AspectRatioElement : VisualElement
{
    [UxmlAttribute("width")]
    public int RatioWidth { get => _ratioWidth; set { _ratioWidth = value; UpdateAspect(); } }

    [UxmlAttribute("height")]
    public int RatioHeight { get => _ratioHeight; set { _ratioHeight = value; UpdateAspect(); } }

    private int _ratioWidth = 16;
    private int _ratioHeight = 9;

    public AspectRatioElement()
    {
        RegisterCallback<GeometryChangedEvent>(UpdateAspectAfterEvent);
        RegisterCallback<AttachToPanelEvent>(UpdateAspectAfterEvent);
    }

    private void UpdateAspect()
    {
        var designRatio = (float)RatioWidth / RatioHeight;
        var currRatio = resolvedStyle.width / resolvedStyle.height;
        var diff = currRatio - designRatio;
        if (diff > 0.01f) {
            var w = (resolvedStyle.width - (resolvedStyle.height * designRatio)) * 0.5f;
            style.paddingLeft = w; style.paddingRight = w;
            style.paddingTop = 0; style.paddingBottom = 0;
        } else if (diff < -0.01f) {
            var h = (resolvedStyle.height - (resolvedStyle.width * (1/designRatio))) * 0.5f;
            style.paddingLeft = 0; style.paddingRight = 0;
            style.paddingTop = h; style.paddingBottom = h;
        } else { ClearPadding(); }
    }
}
```

---

## 16. Create a Custom Inventory Property Drawer

Demonstrates `[UxmlObject]`, `[UxmlObjectReference]`, `UxmlSerializedDataCreator`, and custom property drawers.

### Key Patterns

**UxmlObject for polymorphic items:**
```csharp
[UxmlObject]
public abstract partial class Item
{
    [UxmlAttribute, HideInInspector] public int id;
    [UxmlAttribute] public string name;
    [UxmlAttribute] public float weight;
}

[UxmlObject]
public partial class HealthPack : Item { [UxmlAttribute] public float healAmount = 100; }

[UxmlObject]
public partial class Sword : Item { [UxmlAttribute, Range(1, 100)] public float slashDamage; }
```

**UxmlObjectReference for collections:**
```csharp
[UxmlObject]
public partial class Inventory
{
    [UxmlAttribute] int nextItemId = 1;

    [UxmlObjectReference("Items")]
    public List<Item> items { get; set; }
}
```

**UxmlAttributeConverter for custom serialization:**
```csharp
public class AmmoConverter : UxmlAttributeConverter<Ammo>
{
    public override Ammo FromString(string value) { /* parse "count/maxCount" */ }
    public override string ToString(Ammo value) => $"{value.count}/{value.maxCount}";
}
```

**CustomPropertyDrawer with UxmlSerializedDataCreator:**
```csharp
[CustomPropertyDrawer(typeof(Inventory.UxmlSerializedData))]
public class InventoryPropertyDrawer : PropertyDrawer
{
    public override VisualElement CreatePropertyGUI(SerializedProperty property)
    {
        var items = new ListView
        {
            showAddRemoveFooter = true,
            reorderable = true,
            virtualizationMethod = CollectionVirtualizationMethod.DynamicHeight,
            reorderMode = ListViewReorderMode.Animated,
            bindingPath = m_ItemsProperty.propertyPath,
            overridingAddButtonBehavior = OnAddItem
        };
        // Use UxmlSerializedDataCreator to create typed items
        newItem.managedReferenceValue = UxmlSerializedDataCreator.CreateUxmlSerializedData(typeof(Gun));
    }
}
```
