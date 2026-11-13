# Design system

Lark Cloud Drive's windows look and behave like Windows 11 apps: WPF, Segoe UI Variable, Segoe Fluent Icons, Mica, light/dark/high contrast. The source of truth is the code: `src/LarkCloud.App/UI/Styles.xaml` (styles), `UI/Theme.cs` (colour tokens). This file describes what they build; if they disagree, the code wins and this file gets fixed. Machine-readable tokens: `.impeccable/design.json`.

## Principles

Who the app is for and what it must do is in [PRODUCT.md](PRODUCT.md). In design terms:

1. **Quiet.** People open the windows rarely. Say "all fine" at a glance; put colour only where something needs a look.
2. **Native.** Use Windows 11 patterns (nav on the left, cards, InfoBars, Mica) instead of inventing new ones.
3. **One obvious action.** Each surface has at most one accent button.
4. **Plain words.** No internal terms (see Copy).
5. **Works for everyone.** Keyboard, 200% scaling, high contrast, light and dark are part of done, not extras.

## Colour tokens

Defined once in `Theme.cs` (`Table`), exposed as `<Name>Brush` / `<Name>Color`. Styles use `DynamicResource`, so switching theme updates open windows live. Never write a hex value in XAML; add a token.

| Token | Light | Dark | Role |
|---|---|---|---|
| Backdrop | #F3F3F3 | #202020 | Window base when Mica is unavailable |
| Layer | #80FFFFFF (Mica) / #FBFBFB | #4C3A3A3A (Mica) / #272727 | Content layer over the backdrop |
| Card | #FFFFFF | #323232 | Cards, list rows, settings cards |
| CardStroke | #E3E3E3 | #444444 | Card and menu outline |
| Divider | #E3E3E3 | #404040 | Separators |
| Menu | #FCFCFC | #2C2C2C | Context menu surface |
| TextPrimary | #1A1A1A | #FFFFFF | Body and headings |
| TextSecondary | #5F5F5F | #CCCCCC | Captions, helper text |
| TextTertiary | #8A8A8A | #9A9A9A | Disabled text only |
| Accent | #3370FF | #3370FF | Lark brand blue: selection bar, selected card outline, syncing glyph |
| AccentFill | #2F6BF6 | #2F6BF6 | Accent button and toggle fill. Slightly darker than brand so white text reaches 4.6:1 |
| AccentHover / Pressed | #2A62E0 / #2352BD | same | Accent button states |
| AccentText | #1F55D6 | #7AA2FF | Links and accent-coloured text |
| OnAccent | #FFFFFF | #FFFFFF | Text on accent or critical fills |
| Control | #FFFFFF | #3C3C3C | Standard button, combo, text box fill |
| ControlStroke | #D1D1D1 | #505050 | Control outline |
| ControlHover / Pressed | #F5F5F5 / #EBEBEB | #464646 / #343434 | Standard control states, card hover |
| SubtleHover / Pressed | #0F000000 / #08000000 | #17FFFFFF / #0DFFFFFF | Transparent control, menu item, nav hover |
| Selected | #14000000 | #1FFFFFFF | Selected nav item |
| ScrollThumb | #8C8C8C | #9A9A9A | Scroll bar |
| Success / SuccessBg | #0F7B0F / #DFF6DD | #6CCB5F / #393D1B | "Up to date" text and banner |
| Caution / CautionBg | #9D5D00 / #FFF4CE | #FCE100 / #433519 | "Needs a look" |
| Critical / CriticalBg | #C42B1C / #FDE7E9 | #FF99A4 / #442726 | Errors |
| CriticalFill | #C42B1C | #C42B1C | Destructive button fill (Hover #A92418 / #D63A2B, Pressed #8E1D13 / #A92418) |

Text pairs on their surfaces were checked for 4.5:1. Status is never colour alone: always an icon or words too.

**High contrast.** When Windows high contrast is on, every token is replaced by the user's system colours: surfaces use Window, text/strokes/dividers use WindowText, accent and critical fills use Highlight (text on them HighlightText), TextTertiary uses GrayText, hover/pressed/selected fills become transparent. Mica is off. Do not rely on a fill to show state; keep an outline, bar or glyph.

## Type

Families: `TextFont` = Segoe UI Variable Text (fallback Segoe UI); `DisplayFont` = Segoe UI Variable Display (titles); `IconFont` = Segoe Fluent Icons (fallback Segoe MDL2 Assets).

| Style | Size | Weight | Colour |
|---|---|---|---|
| TitleText (page title) | 28 | SemiBold | TextPrimary |
| SubtitleText (dialog and section title) | 20 | SemiBold | TextPrimary |
| BodyStrongText | 14 | SemiBold | TextPrimary |
| BodyText (default) | 14 | Regular | TextPrimary |
| BodySecondaryText | 14 | Regular | TextSecondary |
| CaptionText | 12 | Regular | TextSecondary |

Sizes are DIPs. Windows use `Style="{StaticResource AppWindow}"` (14 px text, layout rounding on). Wrap text; never truncate meaning.

## Spacing, radii, elevation

- **Grid:** 4 DIP. Common steps 4, 8, 12, 16, 24. Card padding 16,12; page margin 24; gap between cards 8; nav item height 40 with 4 below.
- **Radii:** `RadiusControl` 4 (buttons, inputs, menu items, nav items, InfoBars); `RadiusSurface` 8 (cards, menus, flyouts). Focus ring radius is 6 / 10 (surface), 2 outside the element.
- **Elevation:** flat. Windows sit on Mica (Windows 11 22H2+) or Backdrop. Cards are Card fill plus 1 px CardStroke, no shadow. Menus are Menu fill plus CardStroke at 8 radius with the system popup shadow turned off (`HasDropShadow=False`). Depth comes from fill contrast, not shadow.

## Icons

Segoe Fluent Icons via the `IconText` style (16 px, centred), set as a glyph code (`""`). Rules: glyph is always paired with a text label or a tooltip; one icon per action, reused everywhere the action appears (pause ``, resume ``, open in Lark ``, remove ``, sync ``, quit ``); decorative glyphs are hidden from screen readers; never use an image or emoji for UI icons. The tray's status icon is drawn separately in `TrayIcons.cs`.

## Components

**Buttons** (32 min height, 16 horizontal padding, radius 4, one template):
- *Standard* (default `Button`): Control fill, ControlStroke.
- *Accent* (`AccentButton`): AccentFill, white text. The single main action of a surface.
- *Critical* (`CriticalButton`): CriticalFill, white text. Only the confirm button of a destructive dialog.
- *Subtle* (`SubtleButton`): transparent until hover. Secondary and inline actions.
- *Icon* (`IconButton`): subtle, 32x32, glyph only. Needs a tooltip (and `AutomationProperties.Name`). Used for the ⋯ menu on folder cards.

**Navigation item** (`NavItem`, RadioButton): 40 high, radius 4; selected = Selected fill plus a 3x16 Accent bar on the left (one bar that slides between items, see Motion); hover = SubtleHover.

**List row / folder card** (`CardList` + `CardListItem`): a Card per item, 8 apart; hover = ControlHover fill; selected = Accent outline. Arrow keys move, Tab enters and leaves the list once.

**Settings card:** Card, padding 16,12, min height 64; label and caption on the left, the control on the right. Advanced options sit inside an `Expander`.

**InfoBar** (`InfoBar`): 1 px border, radius 4, min height 48, padding 16,8,8,8; fill/border/glyph from Success/Caution/Critical (+Bg). Text says what happened and the action sits on the right as a subtle or standard button.

**ComboBox, TextBox:** 32 min height, radius 4, Control fill and ControlStroke; TextBox padding 10,0. Combo items are 32 high with 12 padding.

**Toggle** (`ToggleSwitch` on a CheckBox): 40x20 track; off = outline only, on = AccentFill with white knob. Always next to a text label.

**Expander:** used once, for Advanced settings, collapsed by default.

**Context menu** (`ContextMenu`, `MenuItem`, `Separator`): Menu fill, 8 radius, 4 vertical padding, min width 240. Items are 32 high with a 36-wide icon column (Fluent glyph, or a check mark for checkable items), 4,1 margin, radius 4, hover = SubtleHover; disabled = 45% opacity. Items with children show a chevron and open a submenu to the right. Separators are 1 px DividerBrush inset 12. Used for the ⋯ menu and the tray menu.

**Tray menu** (`UI/TrayMenu.cs`): the same menu, built fresh on each right-click and opened at the cursor, above it when the icon is at the bottom edge. Order: status line (disabled), separator, Open Lark Cloud Drive (SemiBold, default), Open folder (submenu, one entry per folder), Sync now, Pause/Resume syncing, separator, Quit. Closes on Esc, on item click, and when another app takes focus. Left click on the icon opens the window; it never opens this menu.

**Dialogs** share `AppWindow`, the solid Backdrop (no Mica), 24 padding, and buttons bottom-right (primary first, 8 apart, Cancel last with `IsCancel`).
- *Confirm* (`ConfirmDialog`): 460 wide, height to content. Title (SubtitleText), one short explanation, two buttons. The confirm button is named by its effect ("Sign out", "Free up space"), Accent normally, Critical when it destroys data. `ConfirmDialog.Ask` / `Tell` are the only way to ask or inform.
- *Step dialog* (`AddFolderDialog`): 760x720 (min 620x600). A header shows numbered steps (24 px circles joined by 1 px dividers), the body shows the current step, the footer has Back / Next (Accent) / Cancel. Each step has one question. Sign-in and the feature check are single-step variants.

## States

- **Hover:** one step towards more contrast (ControlHover, SubtleHover, AccentHover, CriticalHover).
- **Pressed:** one step further (…Pressed tokens).
- **Disabled:** whole element at 45% opacity, not clickable, not focusable by mouse. Say why in nearby text if it is not obvious.
- **Selected:** Selected fill plus Accent bar (nav), Accent outline (card), check mark (menu).
- **Focus:** keyboard-only 2 px TextPrimary ring outside the element (`FocusRing`, `FocusRingSurface` for cards), visible in every theme and in high contrast. Do not remove it.

## Motion

Motion explains state, continuity or feedback; nothing is there for decoration. All of it goes through `UI/Motion.cs`, which is the only place that knows about reduced motion. Only opacity and render transforms are animated, never layout. No bounce or elastic curves. Entrances decelerate (`CubicEase` EaseOut; `QuinticEase` EaseOut for pages and steps); exits are shorter than entrances or instant.

| What | Motion | Time |
|---|---|---|
| **Sync glyph (the focal moment)** | Rotates 360 degrees, linear, about its own centre, forever, while the state is busy. `Motion.Spin="True"` on any icon element. | 1.2 s per turn |
| Where the glyph turns | Header state glyph (Tone.Busy), a folder row's status glyph while that folder syncs, the Sync now icon while the sync that button started runs (one acknowledging turn if nothing starts), the tray menu's status line, the `Spinner` ring | |
| Tray icon | While syncing, the arrow in the blue dot turns: 12 frames drawn once per icon size (`TrayIcons.SyncingFrames`) and cycled by a timer that exists only while syncing; the static icon returns afterwards | 110 ms per frame |
| Page switch | New page fades in and settles up 8 DIPs; the nav selection bar slides to the new item | 200 ms, 250 ms |
| Add-a-folder steps | Step slides in from the right (Next) or left (Back) with a fade; step dots fade their colours | 250 ms, 200 ms |
| Dialogs | Open with a fade and scale 0.96 to 1; closing is instant | 180 ms |
| Expander | Chevron turns 180 degrees, content fades in | 200 ms |
| Banners, header Review / Sign in button | Fade in and settle down 8 DIPs when they appear; they vanish instantly | 200 ms |
| Status header | Glyph and sentence fade in when the state changes (not on the 1 s refresh when nothing changed) | 150 ms |
| Buttons | Hover and pressed fills fade (overlay opacity) | 100 ms |
| Toggle | Knob slides 20 DIPs, accent track and white knob fade in | 150 ms |
| Folder and problem rows | A new row fades in and settles 8 DIPs; a removed row fades out, then leaves the list | 200 ms, 120 ms |

**Spin rule.** A glyph spins only while its state is busy, it is visible (page shown, window not minimized or hidden) and animation effects are on. Setting the same state again (the 1 s refresh) does nothing, so the turn never restarts or jumps. When any of those stops being true the animation clock is removed and the glyph rests at 0 degrees. The glyph is rotated as a whole text element about its centre; check a new spinning glyph for wobble (its ink must stay put while it turns) before using it.

**Reduced motion.** When Windows "Animation effects" is off (checked live, no restart), `Motion.Enabled` is false: no rotation, no tray frame cycling, no slides, scales, knob travel or chevron turn. State is still said by glyph, words and colour. Short opacity and colour fades stay (page, dialog, step, banner, button and row fades, step-dot colours). The debug preview takes `--reduced-motion` to test this path.

**Idle.** With nothing syncing and no window open there is no storyboard and no animation timer. The tray timer runs only while syncing; spinners stop when hidden, minimized or unloaded; every other animation is one-shot and removes itself. (The app's own 1.5 s status poll is not an animation and keeps running.)

## Copy

- Plain words a non-technical colleague uses. Say "folder", "file", "sign in". Never pair, token, scope, engine, base, sync root or similar.
- Name actions by their effect: "Free up space", "Remove from this PC", "Try again now", not "OK", "Yes", "Submit".
- Sentence case. No exclamation marks. Ellipsis (…) only when the action opens a dialog first.
- Say what is wrong, then what to do. Errors name the file and the reason in one line.
- Dialog titles are the question or the outcome ("Sign out of Lark?"); body explains the consequence once.

## Accessibility

- Every interactive element is reachable by keyboard in a logical order; Esc cancels dialogs and menus; Enter triggers the default action.
- Icon-only buttons have a tooltip and an automation name.
- Contrast 4.5:1 for text, 3:1 for outlines and focus. Tertiary text is for disabled items only.
- Never colour alone: status uses glyph or words.
- Layout survives 200% scaling and the Windows text-size setting: use min heights, wrapping, and `SizeToContent` for height rather than fixed pixel sizes.
- Light, dark, high contrast and live theme switching are all tested through the debug preview.

## Adding a page or dialog

1. Create `UI/<Name>.xaml` (+ `.cs`). Pages are `UserControl`s shown in `MainWindow`; dialogs are `Window`s with `Style="{StaticResource AppWindow}"`.
2. Call `Theme.Attach(this, mica: false)` in a dialog's constructor (dark title bar, icon, live theme, solid Backdrop). Only `MainWindow` uses Mica; pages inherit it.
3. Use only styles and tokens from `Styles.xaml` and `Theme.cs`. If something is missing, add a token or style there, not a local colour or size.
4. Pages: add a `NavItem` and an `AppPage` value in `MainWindow`; dialogs are opened from `TrayApp` (`ShowModal` for must-answer, `ShowOnce` for one-at-a-time).
5. Give the page a sample in `UI/Preview.cs` and look at it in light and dark before finishing: `LarkCloud.exe --ui-preview --theme light|dark --page <name> --shot file.png` (Debug builds). `--page traymenu` shows the tray menu.
