namespace VitaSharp.Core.Hle.Modules;

/// <summary>
/// libpaf's run-time type checks. Each class has a type name, a static C string that its TypeName/WidgetType/
/// DrawObjType function returns (the HLE returns the same interned guest string for the same text); IsInherit(name)
/// compares the pointer it is given with its own type's string and then asks its base class, up to the root of the
/// hierarchy (widget_base for the widgets, DrawObj for the draw objects, property_element for the property list's
/// elements; the file list's sources and visualizer have no base). The hierarchy was read from libpaf.suprx 3.74
/// (which base each IsInherit falls back to).
/// </summary>
public sealed partial class Paf
{
    /// <summary>Type name -&gt; the type name of its base class (null at a root).</summary>
    static readonly Dictionary<string, string?> TypeBase = new(StringComparer.Ordinal)
    {
        ["button_base"] = "widget_base",
        ["scroll_view"] = "scroll_view_base",
        ["app_icon_base"] = "widget_base",
        ["image_button"] = "button",
        ["plane_button"] = "button_base",
        ["progressbar"] = "widget_base",
        ["radio_button"] = "state_button_base",
        ["scrollbar2d"] = "widget_base",
        ["corner_button"] = "button",
        ["pageturn_base"] = "widget_base",
        ["app_icon_simple"] = "widget_base",
        ["busyindicator"] = "widget_base",
        ["image_slidebar"] = "widget_base",
        ["sliding_drawer"] = "scroll_view_base",
        ["scrollbar_touch"] = "scrollbar",
        ["scroll_view_base"] = "widget_base",
        ["composite_button"] = "button_base",
        ["composite_drawer"] = "widget_base",
        ["state_button_base"] = "button_base",
        ["progressbar_touch"] = "progressbar",
        ["scroll_view_parent"] = "widget_base",
        ["box"] = "plane",
        ["text"] = "widget_base",
        ["group"] = "widget_base",
        ["plane"] = "widget_base",
        ["page"] = "widget_base",
        ["button"] = "button_base",
        ["camera"] = "widget_base",
        ["dialog"] = "box",
        ["widget_base"] = null,
        ["num_spin"] = "spin_base",
        ["text_box"] = "text",
        ["check_box"] = "state_button_base",
        ["file_list"] = "list_view",
        ["item_spin"] = "spin_base",
        ["list_item"] = "widget_base",
        ["list_view"] = "scroll_view_base",
        ["pageturn"] = "pageturn_base",
        ["radio_box"] = "widget_base",
        ["rich_text"] = "scroll_view_base",
        ["slidebar"] = "widget_base",
        ["spin_base"] = "widget_base",
        ["supplier"] = "plane",
        ["cell_source"] = null,
        ["visualizer"] = null,
        ["segment_source"] = null,
        ["app_icon_2d"] = "app_icon_base",
        ["directbar"] = "widget_base",
        ["scrollbar"] = "widget_base",
        ["DrawObj"] = null,
        ["TextObj"] = "DrawObj",
        ["PlaneObj"] = "DrawObj",
        ["string_element"] = "property_element",
        ["wstring_element"] = "property_element",
        ["property_element"] = null,
    };

    /// <summary>Whether <paramref name="name"/> is the type string of <paramref name="type"/> or of one of its bases
    /// (a pointer comparison, as in the firmware: an equal text at another address is a different type).</summary>
    bool IsType(uint name, string type)
    {
        for (string? t = type; t != null; t = TypeBase[t])
            if (name == C.ConstString(t)) return true;
        return false;
    }

    [Hle("_ZNK3paf2ui10ButtonBase9IsInheritEPKc")] bool IsInheritUiButtonBase(uint me, uint name) => IsType(name, "button_base");
    [Hle("_ZNK3paf2ui10ScrollView9IsInheritEPKc")] bool IsInheritUiScrollView(uint me, uint name) => IsType(name, "scroll_view");
    [Hle("_ZNK3paf2ui11AppIconBase9IsInheritEPKc")] bool IsInheritUiAppIconBase(uint me, uint name) => IsType(name, "app_icon_base");
    [Hle("_ZNK3paf2ui11ImageButton9IsInheritEPKc")] bool IsInheritUiImageButton(uint me, uint name) => IsType(name, "image_button");
    [Hle("_ZNK3paf2ui11PlaneButton9IsInheritEPKc")] bool IsInheritUiPlaneButton(uint me, uint name) => IsType(name, "plane_button");
    [Hle("_ZNK3paf2ui11ProgressBar9IsInheritEPKc")] bool IsInheritUiProgressBar(uint me, uint name) => IsType(name, "progressbar");
    [Hle("_ZNK3paf2ui11RadioButton9IsInheritEPKc")] bool IsInheritUiRadioButton(uint me, uint name) => IsType(name, "radio_button");
    [Hle("_ZNK3paf2ui11ScrollBar2d9IsInheritEPKc")] bool IsInheritUiScrollBar2d(uint me, uint name) => IsType(name, "scrollbar2d");
    [Hle("_ZNK3paf2ui12CornerButton9IsInheritEPKc")] bool IsInheritUiCornerButton(uint me, uint name) => IsType(name, "corner_button");
    [Hle("_ZNK3paf2ui12PageTurnBase9IsInheritEPKc")] bool IsInheritUiPageTurnBase(uint me, uint name) => IsType(name, "pageturn_base");
    [Hle("_ZNK3paf2ui13AppIconSimple9IsInheritEPKc")] bool IsInheritUiAppIconSimple(uint me, uint name) => IsType(name, "app_icon_simple");
    [Hle("_ZNK3paf2ui13BusyIndicator9IsInheritEPKc")] bool IsInheritUiBusyIndicator(uint me, uint name) => IsType(name, "busyindicator");
    [Hle("_ZNK3paf2ui13ImageSlideBar9IsInheritEPKc")] bool IsInheritUiImageSlideBar(uint me, uint name) => IsType(name, "image_slidebar");
    [Hle("_ZNK3paf2ui13SlidingDrawer9IsInheritEPKc")] bool IsInheritUiSlidingDrawer(uint me, uint name) => IsType(name, "sliding_drawer");
    [Hle("_ZNK3paf2ui14ScrollBarTouch9IsInheritEPKc")] bool IsInheritUiScrollBarTouch(uint me, uint name) => IsType(name, "scrollbar_touch");
    [Hle("_ZNK3paf2ui14ScrollViewBase9IsInheritEPKc")] bool IsInheritUiScrollViewBase(uint me, uint name) => IsType(name, "scroll_view_base");
    [Hle("_ZNK3paf2ui15CompositeButton9IsInheritEPKc")] bool IsInheritUiCompositeButton(uint me, uint name) => IsType(name, "composite_button");
    [Hle("_ZNK3paf2ui15CompositeDrawer9IsInheritEPKc")] bool IsInheritUiCompositeDrawer(uint me, uint name) => IsType(name, "composite_drawer");
    [Hle("_ZNK3paf2ui15StateButtonBase9IsInheritEPKc")] bool IsInheritUiStateButtonBase(uint me, uint name) => IsType(name, "state_button_base");
    [Hle("_ZNK3paf2ui16ProgressBarTouch9IsInheritEPKc")] bool IsInheritUiProgressBarTouch(uint me, uint name) => IsType(name, "progressbar_touch");
    [Hle("_ZNK3paf2ui16ScrollViewParent9IsInheritEPKc")] bool IsInheritUiScrollViewParent(uint me, uint name) => IsType(name, "scroll_view_parent");
    [Hle("_ZNK3paf2ui3Box9IsInheritEPKc")] bool IsInheritUiBox(uint me, uint name) => IsType(name, "box");
    [Hle("_ZNK3paf2ui4Text9IsInheritEPKc")] bool IsInheritUiText(uint me, uint name) => IsType(name, "text");
    [Hle("_ZNK3paf2ui5Group9IsInheritEPKc")] bool IsInheritUiGroup(uint me, uint name) => IsType(name, "group");
    [Hle("_ZNK3paf2ui5Plane9IsInheritEPKc")] bool IsInheritUiPlane(uint me, uint name) => IsType(name, "plane");
    [Hle("_ZNK3paf2ui5Scene9IsInheritEPKc")] bool IsInheritUiScene(uint me, uint name) => IsType(name, "page");
    [Hle("_ZNK3paf2ui6Button9IsInheritEPKc")] bool IsInheritUiButton(uint me, uint name) => IsType(name, "button");
    [Hle("_ZNK3paf2ui6Camera9IsInheritEPKc")] bool IsInheritUiCamera(uint me, uint name) => IsType(name, "camera");
    [Hle("_ZNK3paf2ui6Dialog9IsInheritEPKc")] bool IsInheritUiDialog(uint me, uint name) => IsType(name, "dialog");
    [Hle("_ZNK3paf2ui6Widget9IsInheritEPKc")] bool IsInheritUiWidget(uint me, uint name) => IsType(name, "widget_base");
    [Hle("_ZNK3paf2ui7NumSpin9IsInheritEPKc")] bool IsInheritUiNumSpin(uint me, uint name) => IsType(name, "num_spin");
    [Hle("_ZNK3paf2ui7TextBox9IsInheritEPKc")] bool IsInheritUiTextBox(uint me, uint name) => IsType(name, "text_box");
    [Hle("_ZNK3paf2ui8CheckBox9IsInheritEPKc")] bool IsInheritUiCheckBox(uint me, uint name) => IsType(name, "check_box");
    [Hle("_ZNK3paf2ui8FileList9IsInheritEPKc")] bool IsInheritUiFileList(uint me, uint name) => IsType(name, "file_list");
    [Hle("_ZNK3paf2ui8ItemSpin9IsInheritEPKc")] bool IsInheritUiItemSpin(uint me, uint name) => IsType(name, "item_spin");
    [Hle("_ZNK3paf2ui8ListItem9IsInheritEPKc")] bool IsInheritUiListItem(uint me, uint name) => IsType(name, "list_item");
    [Hle("_ZNK3paf2ui8ListView9IsInheritEPKc")] bool IsInheritUiListView(uint me, uint name) => IsType(name, "list_view");
    [Hle("_ZNK3paf2ui8PageTurn9IsInheritEPKc")] bool IsInheritUiPageTurn(uint me, uint name) => IsType(name, "pageturn");
    [Hle("_ZNK3paf2ui8RadioBox9IsInheritEPKc")] bool IsInheritUiRadioBox(uint me, uint name) => IsType(name, "radio_box");
    [Hle("_ZNK3paf2ui8RichText9IsInheritEPKc")] bool IsInheritUiRichText(uint me, uint name) => IsType(name, "rich_text");
    [Hle("_ZNK3paf2ui8SlideBar9IsInheritEPKc")] bool IsInheritUiSlideBar(uint me, uint name) => IsType(name, "slidebar");
    [Hle("_ZNK3paf2ui8SpinBase9IsInheritEPKc")] bool IsInheritUiSpinBase(uint me, uint name) => IsType(name, "spin_base");
    [Hle("_ZNK3paf2ui8Supplier9IsInheritEPKc")] bool IsInheritUiSupplier(uint me, uint name) => IsType(name, "supplier");
    [Hle("_ZNK3paf2ui8filelist10CellSource9IsInheritEPKc")] bool IsInheritUiFilelistCellSource(uint me, uint name) => IsType(name, "cell_source");
    [Hle("_ZNK3paf2ui8filelist10Visualizer9IsInheritEPKc")] bool IsInheritUiFilelistVisualizer(uint me, uint name) => IsType(name, "visualizer");
    [Hle("_ZNK3paf2ui8filelist13SegmentSource9IsInheritEPKc")] bool IsInheritUiFilelistSegmentSource(uint me, uint name) => IsType(name, "segment_source");
    [Hle("_ZNK3paf2ui9AppIcon2d9IsInheritEPKc")] bool IsInheritUiAppIcon2d(uint me, uint name) => IsType(name, "app_icon_2d");
    [Hle("_ZNK3paf2ui9DirectBar9IsInheritEPKc")] bool IsInheritUiDirectBar(uint me, uint name) => IsType(name, "directbar");
    [Hle("_ZNK3paf2ui9ScrollBar9IsInheritEPKc")] bool IsInheritUiScrollBar(uint me, uint name) => IsType(name, "scrollbar");
    [Hle("_ZNK3paf5graph7DrawObj9IsInheritEPKc")] bool IsInheritGraphDrawObj(uint me, uint name) => IsType(name, "DrawObj");
    [Hle("_ZNK3paf5graph7TextObj9IsInheritEPKc")] bool IsInheritGraphTextObj(uint me, uint name) => IsType(name, "TextObj");
    [Hle("_ZNK3paf5graph8PlaneObj9IsInheritEPKc")] bool IsInheritGraphPlaneObj(uint me, uint name) => IsType(name, "PlaneObj");
    [Hle("_ZNK3paf6common13property_list13StringElement9IsInheritEPKc")] bool IsInheritCommonPropertylistStringElement(uint me, uint name) => IsType(name, "string_element");
    [Hle("_ZNK3paf6common13property_list14WStringElement9IsInheritEPKc")] bool IsInheritCommonPropertylistWStringElement(uint me, uint name) => IsType(name, "wstring_element");
    [Hle("_ZNK3paf6common13property_list15PropertyElement9IsInheritEPKc")] bool IsInheritCommonPropertylistPropertyElement(uint me, uint name) => IsType(name, "property_element");
}
