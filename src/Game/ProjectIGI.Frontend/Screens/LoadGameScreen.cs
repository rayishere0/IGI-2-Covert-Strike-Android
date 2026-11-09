using System.Globalization;
using ProjectIGI.Core.Graphics;
using ProjectIGI.Game.Player;
using ProjectIGI.Input;
using ProjectIGI.UI;
using ProjectIGI.UI.Animation;
using ProjectIGI.UI.Focus;
using ProjectIGI.UI.Kit.Widgets;
using ProjectIGI.UI.Layout;
using ProjectIGI.UI.Widgets;

namespace ProjectIGI.Frontend.Screens;

/// <summary>
/// The main menu's load view: the active profile's saves, across every mission, each with its mission's name, its progress and its
/// mission's briefing picture. Opened from the Android LOAD GAME button; loading a slot arms it for the next level and starts its
/// mission, through the same handoff the retail load screen uses. Back returns to the main menu.
/// </summary>
/// <remarks>
/// A sibling of <see cref="MissionSavesScreen"/>, not a mode of it: that screen is the running mission's own slots, in one row, with
/// Back to the pause menu and a load that resumes the mission. This one lists saves across missions, in a scrolling list, with Back to
/// the main menu and a load that starts the mission instead. Loading destroys nothing, so there is no two-tap confirm here.
/// </remarks>
public sealed class LoadGameScreen : ShellScreen
{
    private readonly Dictionary<int, UiImage> _thumbnails = [];
    private ScrollView? _scroll;
    private UiVec2? _touchDragStart;
    private float _touchDragStartOffset;
    private bool _isDragging;
    private Control? _pendingTapTarget;
    private Label? _message;

    /// <summary>Initializes a new instance of the <see cref="LoadGameScreen"/> class.</summary>
    /// <param name="context">The shared shell context.</param>
    public LoadGameScreen(ShellContext context)
        : base(context)
    {
    }

    /// <inheritdoc/>
    protected override ShellTab Tab => ShellTab.Home;

    /// <inheritdoc/>
    protected override IReadOnlyList<KeyHint> Hints => [new("ENTER", "Load"), new("ESC", "Back")];

    /// <inheritdoc/>
    protected override UiElement? BuildContent(LayoutContext context)
    {
        UiTheme theme = Context.Theme;
        var column = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 16f,
            Width = 880f,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(64f, 0f, 0f, 0f),
        };

        column.AddChild(new SectionTitle(theme, "Operation", "Load game")
        {
            TitleSize = 88f,
            EyebrowColor = theme.Accent,
            Margin = new Thickness(0f, 0f, 0f, 20f),
        });

        _scroll = new ScrollView(theme)
        {
            Width = 880f,
            Height = 560f,
            ScrollbarWidth = 14f,
            ScrollbarHitWidth = 56f,
        };
        var rows = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 12f,
            Width = 860f,
        };
        _scroll.SetContent(rows);
        column.AddChild(_scroll);

        _message = new Label(theme.Body, string.Empty, 17f, theme.Accent);
        column.AddChild(_message);

        IReadOnlyList<LoadGameSave> saves = Context.Host.LoadGameSaves;
        if (saves.Count == 0)
        {
            _message.Text = "No save games yet.";
            return column;
        }

        foreach (LoadGameSave save in saves)
        {
            LoadGameSave chosen = save;
            MissionInfo? mission = Context.Missions.FirstOrDefault(m => m.Id == save.MissionId);
            string title = (mission?.Name ?? string.Create(CultureInfo.InvariantCulture, $"Mission {save.MissionId}")).ToUpperInvariant();
            string progress = string.Create(
                CultureInfo.InvariantCulture,
                $"Slot {save.Slot + 1} — Mission Time {save.MissionTime}");
            string when = save.IsQuicksave ? $"{save.SavedAt} [Quicksave]" : save.SavedAt;
            var tile = new LoadGameTile(theme, ThumbnailFor(save), title, progress, when) { Width = 860f, Height = 132f };
            tile.Activated += _ => Choose(chosen);
            rows.AddChild(tile);
        }

        return column;
    }

    /// <inheritdoc/>
    protected override bool HandleInput(UiNavigation nav, InputState input)
    {
        // Back returns to the main menu this page was opened from.
        if (nav.Back)
        {
            Context.Stack.Reset(new MainMenuScreen(Context));
            return false;
        }

        // Drag to scroll the list, mirroring the settings screen: a drag scrolls, a tap still activates the row under the pointer.
        if (_scroll is not null)
        {
            var pointer = new UiVec2(input.Mouse.X, input.Mouse.Y);
            bool insideScroll = _scroll.Bounds.Contains(pointer);

            if (input.Mouse.WasPressed(MouseButton.Left))
            {
                if (insideScroll && !_scroll.IsOverScrollbar(pointer))
                {
                    _touchDragStart = pointer;
                    _touchDragStartOffset = _scroll.ScrollOffset;
                    _isDragging = false;
                    _pendingTapTarget = Focus?.FocusAt(pointer) as Control;
                    return false;
                }
            }
            else if (_touchDragStart is { } start && input.Mouse.IsDown(MouseButton.Left))
            {
                float dy = pointer.Y - start.Y;
                if (!_isDragging && MathF.Abs(dy) > 10f)
                {
                    _isDragging = true;
                }

                if (_isDragging)
                {
                    _scroll.ScrollOffset = _touchDragStartOffset - dy;
                    return false;
                }
            }
            else if (input.Mouse.WasReleased(MouseButton.Left) || !input.Mouse.IsDown(MouseButton.Left))
            {
                if (_touchDragStart is not null)
                {
                    _touchDragStart = null;
                    if (!_isDragging)
                    {
                        _pendingTapTarget?.ActivateAt(pointer);
                    }

                    _isDragging = false;
                    _pendingTapTarget = null;
                    return false;
                }
            }
        }

        return base.HandleInput(nav, input);
    }

    private void Choose(LoadGameSave save)
    {
        // On success the mission starts and the menu goes away with it, so there is nothing to refresh here.
        if (!Context.Host.LoadSave(save.MissionId, save.Slot))
        {
            SetMessage("Loading failed.");
        }
    }

    private UiImage ThumbnailFor(LoadGameSave save)
    {
        if (_thumbnails.TryGetValue(save.MissionId, out UiImage cached))
        {
            return cached;
        }

        UiImage image = UiImage.None;
        MissionInfo? mission = Context.Missions.FirstOrDefault(m => m.Id == save.MissionId);
        if (mission is { BriefingSprite.Length: > 0 } && Context.Theme.ImageFactory is { } factory)
        {
            Bgra32[] pixels = Context.Host.MissionThumbnail(mission.BriefingSprite, out int width, out int height);
            if (pixels.Length > 0 && width > 0 && height > 0)
            {
                image = factory.CreateImage(width, height, pixels);
            }
        }

        _thumbnails[save.MissionId] = image;
        return image;
    }

    private void SetMessage(string text)
    {
        if (_message is not null)
        {
            _message.Text = text;
        }
    }
}

/// <summary>
/// One save in the load list: its mission's briefing picture on the left, its mission's name and what the save holds on the right,
/// outlined in the accent colour when it is the one focused.
/// </summary>
internal sealed class LoadGameTile : Control
{
    private const float ThumbnailWidth = 176f;

    private readonly UiImage _image;
    private readonly string _title;
    private readonly string _progress;
    private readonly string _when;

    /// <summary>Initializes a new instance of the <see cref="LoadGameTile"/> class.</summary>
    /// <param name="theme">The theme.</param>
    /// <param name="image">The mission's briefing picture, or <see cref="UiImage.None"/> when it has none.</param>
    /// <param name="title">The mission's name, in capitals.</param>
    /// <param name="progress">The slot and mission time line.</param>
    /// <param name="when">When the save was written, and its quicksave tag.</param>
    public LoadGameTile(UiTheme theme, UiImage image, string title, string progress, string when)
        : base(theme)
    {
        _image = image;
        _title = title ?? string.Empty;
        _progress = progress ?? string.Empty;
        _when = when ?? string.Empty;
    }

    /// <inheritdoc/>
    protected override UiVec2 MeasureContent(LayoutContext context, UiVec2 available) =>
        new(available.X, MathF.Max(context.Px(96f), available.Y));

    /// <inheritdoc/>
    protected override void OnDraw(UiPainter painter)
    {
        LayoutContext context = painter.Context;
        float appear = Visual.Appear;
        float hot = MathF.Max(Visual.Focus, IsPointerOver ? 1f : 0f);

        painter.FillRect(Bounds, UiPalette.Fade(UiPalette.White(0.02f), appear));
        Bgra32 border = Interpolate.Lerp(Theme.Border, UiPalette.AccentAt(0.45f), hot);
        painter.StrokeRect(Bounds, context.Px(1f), UiPalette.Fade(border, appear));

        // The mission's picture, or the same dark fill as the tile behind it when there is none.
        var picture = new UiRect(Bounds.X, Bounds.Y, ThumbnailWidth, Bounds.Height);
        if (_image.IsValid)
        {
            painter.DrawSprite(_image, picture, UiRect.Unit, Bgra32.White, UiBlend.Opaque);
        }
        else
        {
            painter.FillRect(picture, UiPalette.Fade(Bgra32.FromRgb(8, 10, 18), appear));
        }

        float textX = Bounds.X + ThumbnailWidth + context.Px(20f);
        painter.PushClip(Bounds);

        float titleSize = context.Px(24f);
        float progressSize = context.Px(15f);
        float whenSize = context.Px(12f);
        float y = Bounds.Y + context.Px(16f);
        painter.DrawLabel(Theme.Heading, _title, new UiVec2(textX, y), titleSize, UiPalette.Fade(Theme.Text, appear), 0.14f);
        y += Theme.Heading.LineHeight(titleSize) + context.Px(6f);
        painter.DrawLabel(Theme.Mono, _progress, new UiVec2(textX, y), progressSize, UiPalette.Fade(Theme.Text, appear), 0.12f);
        y += Theme.Mono.LineHeight(progressSize) + context.Px(4f);
        painter.DrawLabel(Theme.Mono, _when, new UiVec2(textX, y), whenSize, UiPalette.Fade(Theme.TextMuted, appear), 0.12f);

        painter.PopClip();
    }
}
