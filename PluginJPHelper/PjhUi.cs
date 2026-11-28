using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures.TextureWraps;

namespace PluginJPHelper;

// PJH-only visual layer. It never changes dictionaries, configuration, or translation hooks.
internal static class PjhUi
{
    internal static readonly Vector4 Accent = new(0.42f, 0.83f, 0.89f, 1f);
    private static readonly Vector4 Ink = new(0.91f, 0.94f, 0.97f, 1f);
    private static readonly Vector4 Muted = new(0.64f, 0.70f, 0.77f, 1f);
    private static readonly Vector4 Outline = new(0.24f, 0.30f, 0.37f, 1f);
    private static readonly Vector4 Surface = new(0.058f, 0.079f, 0.095f, 0.97f);
    private static readonly Vector4 Panel = new(0.088f, 0.125f, 0.15f, 1f);
    private static readonly Vector4 Field = new(0.15f, 0.19f, 0.24f, 1f);
    private static readonly Vector4 Hover = new(0.21f, 0.28f, 0.35f, 1f);
    private static readonly Vector4 Selected = new(0.15f, 0.32f, 0.36f, 1f);
    private static readonly Vector4 Transparent = new(0f, 0f, 0f, 0f);

    // Deliberately retain the host's font, frame padding, table cell padding and item
    // spacing so existing fixed-width controls and dictionary columns are not enlarged.
    private static readonly (ImGuiCol Slot, Vector4 Value)[] Colors =
    [
        (ImGuiCol.Text, Ink), (ImGuiCol.TextDisabled, Muted),
        (ImGuiCol.WindowBg, Surface), (ImGuiCol.ChildBg, Transparent),
        (ImGuiCol.PopupBg, Panel), (ImGuiCol.Border, Outline), (ImGuiCol.BorderShadow, Transparent),
        (ImGuiCol.TitleBg, Panel), (ImGuiCol.TitleBgActive, new(0.12f, 0.20f, 0.28f, 1f)),
        (ImGuiCol.TitleBgCollapsed, Panel),
        (ImGuiCol.FrameBg, Field), (ImGuiCol.FrameBgHovered, Hover), (ImGuiCol.FrameBgActive, Selected),
        (ImGuiCol.Button, Field), (ImGuiCol.ButtonHovered, Hover), (ImGuiCol.ButtonActive, Selected),
        (ImGuiCol.Header, Field), (ImGuiCol.HeaderHovered, Hover), (ImGuiCol.HeaderActive, Selected),
        (ImGuiCol.Tab, Panel), (ImGuiCol.TabHovered, Hover), (ImGuiCol.TabActive, Selected),
        (ImGuiCol.TabUnfocused, Panel), (ImGuiCol.TabUnfocusedActive, Selected),
        (ImGuiCol.CheckMark, Accent), (ImGuiCol.SliderGrab, Accent), (ImGuiCol.SliderGrabActive, Accent),
        (ImGuiCol.Separator, Outline), (ImGuiCol.SeparatorHovered, Accent), (ImGuiCol.SeparatorActive, Accent),
        (ImGuiCol.ScrollbarGrab, Outline), (ImGuiCol.ScrollbarGrabHovered, Hover), (ImGuiCol.ScrollbarGrabActive, Selected),
        (ImGuiCol.TableHeaderBg, Field), (ImGuiCol.TableBorderStrong, Outline),
        (ImGuiCol.TableBorderLight, new(0.18f, 0.22f, 0.27f, 0.9f)),
        (ImGuiCol.TableRowBg, Transparent), (ImGuiCol.TableRowBgAlt, new(0.20f, 0.26f, 0.32f, 0.15f)),
        (ImGuiCol.TextSelectedBg, Selected),
    ];

    private static readonly (ImGuiStyleVar Slot, float Value)[] Style =
    [
        (ImGuiStyleVar.WindowRounding, 12f), (ImGuiStyleVar.ChildRounding, 10f),
        (ImGuiStyleVar.FrameRounding, 8f), (ImGuiStyleVar.PopupRounding, 6f),
        (ImGuiStyleVar.TabRounding, 5f), (ImGuiStyleVar.GrabRounding, 5f),
        (ImGuiStyleVar.ScrollbarRounding, 6f),
    ];

    internal static float Scale => Math.Max(0.7f, ImGui.GetFontSize() / 16f);

    internal static void Push()
    {
        foreach (var (slot, value) in Colors) ImGui.PushStyleColor(slot, value);
        var scale = Scale;
        foreach (var (slot, value) in Style) ImGui.PushStyleVar(slot, value * scale);
    }

    internal static void Pop()
    {
        ImGui.PopStyleVar(Style.Length);
        ImGui.PopStyleColor(Colors.Length);
    }

    // Both existing role enums have the same stable order: Primary, Success, Warning, Danger, Neutral.
    internal static Vector4 ActionColor(int role) => role switch
    {
        0 => new(0.19f, 0.37f, 0.50f, 1f),
        1 => new(0.20f, 0.36f, 0.32f, 1f),
        2 => new(0.41f, 0.31f, 0.17f, 1f),
        3 => new(0.43f, 0.23f, 0.27f, 1f),
        _ => Field,
    };

    internal static float HeaderHeight => 30f * Scale;
    internal static float IconButtonSize => 24f * Scale;

    private static void IconAt(Vector2 pos, FontAwesomeIcon icon, float size, uint color)
    {
        ImGui.GetWindowDrawList().AddText(UiBuilder.IconFont, size, pos, color, ((char)icon).ToString());
    }

    internal static bool IconButton(string id, FontAwesomeIcon icon, string tooltip)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, IconButtonSize * 0.5f);
        ImGui.PushStyleColor(ImGuiCol.Button, Panel);
        try
        {
            var clicked = ImGui.Button(id, new Vector2(IconButtonSize));
            var min = ImGui.GetItemRectMin();
            IconAt(min + new Vector2(IconButtonSize * 0.25f), icon, IconButtonSize * 0.5f, ImGui.GetColorU32(Ink));
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(tooltip);
            return clicked;
        }
        finally { ImGui.PopStyleColor(); ImGui.PopStyleVar(); }
    }

    internal static bool Brand(float width, string notice, IDalamudTextureWrap? logo, bool compact)
    {
        var h = HeaderHeight;
        var start = ImGui.GetCursorScreenPos();
        // A dedicated title grip, distinct from the navigation and content areas.
        ImGui.InvisibleButton("##pjhTitleGrip", new Vector2(Math.Max(1f, width), h));
        var active = ImGui.IsItemActive();
        var hovered = ImGui.IsItemHovered();
        var draw = ImGui.GetWindowDrawList();
        var badge = h - 4f * Scale;
        var min = start + new Vector2(2f * Scale);
        if (logo != null)
            draw.AddImage(logo.Handle, min, min + new Vector2(badge), Vector2.Zero, Vector2.One, ImGui.GetColorU32(Vector4.One));
        else
        {
            // A missing/temporarily loading texture must not stop the UI.
            draw.AddRectFilled(min, min + new Vector2(badge), ImGui.GetColorU32(Accent), 4f * Scale);
            var size = ImGui.CalcTextSize("JP");
            draw.AddText(min + (new Vector2(badge) - size) * 0.5f, ImGui.GetColorU32(Surface), "JP");
        }
        var textX = min.X + badge + 6f * Scale;
        if (start.X + width > textX)
        {
            draw.PushClipRect(new Vector2(textX, start.Y), start + new Vector2(width, h), true);
            var font = ImGui.GetFont();
            var fontSize = ImGui.GetFontSize();
            if (compact)
                draw.AddText(font, fontSize * 0.92f, new Vector2(textX, start.Y + (h - fontSize * 0.92f) / 2f), ImGui.GetColorU32(Ink), "Plugin JP Helper");
            else
            {
                draw.AddText(font, fontSize * 0.96f, new Vector2(textX, start.Y + 1f * Scale), ImGui.GetColorU32(Ink), "Plugin JP Helper");
                draw.AddText(font, fontSize * 0.72f, new Vector2(textX, start.Y + 17f * Scale), ImGui.GetColorU32(Muted),
                    string.IsNullOrEmpty(notice) ? "v0.5.5  /  UI3" : "v0.5.5  /  UI3  ·  " + notice);
            }
            draw.PopClipRect();
        }
        if (hovered && (compact || !string.IsNullOrEmpty(notice))) ImGui.SetTooltip("Plugin JP Helper v0.5.5 / UI3" + (string.IsNullOrEmpty(notice) ? "" : "\n" + notice));
        return active;
    }

    internal static bool NavigationButton(string label, bool selected, Vector2 size, FontAwesomeIcon? icon = null)
    {
        ImGui.PushStyleVar(ImGuiStyleVar.FrameRounding, 10f * Scale);
        ImGui.PushStyleColor(ImGuiCol.Button, selected ? Selected : Transparent);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Hover);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Selected);
        try
        {
            if (!icon.HasValue) return ImGui.Button(label, size);
            // Use a real button for keyboard navigation, and render icon and label left aligned.
            var clicked = ImGui.Button("##pjhPage_" + label, size);
            var min = ImGui.GetItemRectMin();
            var max = ImGui.GetItemRectMax();
            var draw = ImGui.GetWindowDrawList();
            var textColor = ImGui.GetColorU32(selected ? Accent : Ink);
            float h = max.Y - min.Y;
            float glyph = ImGui.GetFontSize();
            draw.PushClipRect(min, max, true);
            if (selected)
                draw.AddRectFilled(min + new Vector2(2f * Scale, h * 0.23f), min + new Vector2(5f * Scale, h * 0.77f), textColor, 2f * Scale);
            IconAt(min + new Vector2(12f * Scale, (h - glyph) * 0.5f), icon.Value, glyph, textColor);
            draw.AddText(min + new Vector2(30f * Scale, (h - ImGui.GetTextLineHeight()) * 0.5f), textColor, label);
            draw.PopClipRect();
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(label);
            return clicked;
        }
        finally { ImGui.PopStyleColor(3); ImGui.PopStyleVar(); }
    }

    internal static void PageHeading(string title, string description, FontAwesomeIcon icon)
    {
        var start = ImGui.GetCursorScreenPos();
        var size = ImGui.GetFontSize();
        IconAt(start + new Vector2(1, 2), icon, size, ImGui.GetColorU32(Accent));
        ImGui.Indent(25f * Scale);
        var headingPos = ImGui.GetCursorScreenPos();
        ImGui.GetWindowDrawList().AddText(ImGui.GetFont(), size * 1.2f, headingPos, ImGui.GetColorU32(Ink), title);
        ImGui.Dummy(new Vector2(1, size * 1.25f));
        MutedText(description);
        ImGui.Unindent(25f * Scale);
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    // A normal ImGui button supplies keyboard focus, click and disabled-state semantics;
    // only its visual appearance is custom, while the callers retain their original bools.
    internal static bool Toggle(string label, ref bool value, bool rightAligned = false)
    {
        var idStart = label.IndexOf("##", StringComparison.Ordinal);
        var visible = idStart < 0 ? label : label[..idStart];
        var height = rightAligned ? Math.Max(ImGui.GetFrameHeight(), 36f * Scale) : ImGui.GetFrameHeight();
        var trackHeight = height * 0.66f;
        var trackWidth = trackHeight * 1.8f;
        var gap = 7f * Scale;
        var width = trackWidth + gap + ImGui.CalcTextSize(visible).X;
        if (rightAligned) width = Math.Max(width, ImGui.GetContentRegionAvail().X - 12f * Scale);
        ImGui.PushStyleColor(ImGuiCol.Button, Transparent);
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Transparent);
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, Transparent);
        bool changed;
        try { changed = ImGui.Button("##pjhSwitch_" + label, new Vector2(width, height)); }
        finally { ImGui.PopStyleColor(3); }
        if (changed) value = !value;

        var min = ImGui.GetItemRectMin();
        var top = min + new Vector2(rightAligned ? width - trackWidth : 0, (height - trackHeight) * 0.5f);
        var bottom = top + new Vector2(trackWidth, trackHeight);
        var draw = ImGui.GetWindowDrawList();
        var focused = ImGui.IsItemHovered() || ImGui.IsItemFocused();
        draw.AddRectFilled(top, bottom, ImGui.GetColorU32(value ? new Vector4(0.17f, 0.54f, 0.58f, 1f) : Field), trackHeight * 0.5f);
        draw.AddRect(top, bottom, ImGui.GetColorU32(focused ? Accent : Outline), trackHeight * 0.5f);
        var radius = trackHeight * 0.34f;
        var center = top + new Vector2(value ? trackWidth - trackHeight * 0.5f : trackHeight * 0.5f, trackHeight * 0.5f);
        draw.AddCircleFilled(center, radius, ImGui.GetColorU32(value ? Ink : Muted));
        if (visible.Length > 0)
            draw.AddText(min + new Vector2(rightAligned ? 0 : trackWidth + gap, (height - ImGui.GetTextLineHeight()) * 0.5f), ImGui.GetColorU32(ImGuiCol.Text), visible);
        return changed;
    }

    internal static void SameLineIfFits(string nextLabel)
    {
        var right = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X - 8f * Scale;
        var needed = ImGui.CalcTextSize(nextLabel).X + ImGui.GetStyle().FramePadding.X * 2f + ImGui.GetStyle().ItemSpacing.X;
        if (ImGui.GetItemRectMax().X + needed <= right) ImGui.SameLine();
    }

    internal static void MutedText(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, Muted);
        try { ImGui.TextWrapped(text); }
        finally { ImGui.PopStyleColor(); }
    }

    // Only small settings/action groups use cards; wide data tables remain untouched.
    internal sealed class Card : IDisposable
    {
        private readonly Vector2 start;
        private readonly float width;
        private readonly float inset;

        internal Card(string title)
        {
            start = ImGui.GetCursorScreenPos();
            width = ImGui.GetContentRegionAvail().X;
            inset = 14f * Scale;
            var draw = ImGui.GetWindowDrawList();
            draw.ChannelsSplit(2);
            draw.ChannelsSetCurrent(1);
            ImGui.BeginGroup();
            ImGui.Dummy(new Vector2(1, inset * 0.5f));
            ImGui.Indent(inset);
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + Math.Max(1f, width - 2f * inset));
            ImGui.TextColored(Accent, title);
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
        }

        public void Dispose()
        {
            ImGui.PopTextWrapPos();
            ImGui.Unindent(inset);
            ImGui.Dummy(new Vector2(1, inset * 0.5f));
            ImGui.EndGroup();
            var end = new Vector2(start.X + Math.Max(1f, width), ImGui.GetItemRectMax().Y);
            var draw = ImGui.GetWindowDrawList();
            draw.ChannelsSetCurrent(0);
            draw.AddRectFilled(start, end, ImGui.GetColorU32(Panel), 10f * Scale);
            draw.AddRect(start, end, ImGui.GetColorU32(Outline), 10f * Scale);
            draw.ChannelsMerge();
            ImGui.Dummy(new Vector2(1, 5f * Scale));
        }
    }
}
