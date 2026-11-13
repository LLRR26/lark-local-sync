# Product

<!-- impeccable:product-schema 1 -->

## Platform

windows (native desktop app, WPF on .NET 8; not a web surface). The schema's values are web/ios/android/adaptive and none of them fits, so this is recorded as is.

## Users

- **Staff at Revion Land** (inferred from `Company` in the csproj and the README's "What staff see"). They are not technical. They use Lark Drive folders in File Explorer the way they would use OneDrive, and they open this app's window only when something needs them: adding a folder, a file that can't sync, deletions to confirm, signing in again.
- **IT** builds `LarkCloud.exe`, hands out `config.json` (Lark app ID and secret, default folders, `lock_folders`, delete rules) and supports the staff.

## Product Purpose

Lark Cloud Drive shows Lark Drive folders in File Explorer with files on demand. Every file is listed, and a file's data downloads only when someone opens it. Changes sync both ways within seconds to minutes. Success means staff forget the app exists. When they do open it, they understand at a glance whether everything is fine and what to do if it isn't.

## Positioning

It brings OneDrive-style files on demand (the Windows Cloud Files API) to Lark Drive. That includes Lark online docs as shortcuts, optional offline Office/PDF copies, and wiki spaces. The sibling app Lark Drive Sync keeps a full copy of every file instead.

## Operating Context

- The app runs in the tray. Its icon has a coloured status dot: green means up to date, blue means syncing, amber means something needs a look, red means it can't sync.
- Left-clicking the icon opens whatever needs attention.
- Most interaction happens in File Explorer (the right-click "Always keep on this device" and "Free up space"). The app's windows are visited rarely and briefly.
- Runs on Windows 10 1809+ and Windows 11, on office PCs and laptops, often at 125–200% display scaling and in light or dark mode.

## Capabilities and Constraints

- **Folders:** add (by search, by link, or a wiki space), open, open in Lark, pause/resume, free up space, offline doc copies per folder, remove from this PC. IT folders can't be removed, and `lock_folders` hides add/remove.
- **Problems:** files that can't sync, with Show in folder / Open in Lark / Upload with Lark… / Try again now, plus "good to know" notes.
- **Deletions:** a review step for held mass deletions.
- **Account:** sign in with a device code in the browser, and sign out.
- **Settings:** IT's `config.json` sets defaults and can lock settings. Per-person settings live in the user config.
- **Terminology:** staff never see internal words such as pair, token, base, scope or engine.
- **No new dependencies** without approval. WinForms `NotifyIcon` stays for the tray icon.

## Brand Commitments

- Lark blue `#3370FF` is the accent (already used in the app).
- The app follows the native Windows 11 look (the user chose this explicitly): light or dark to match Windows, with an override in Settings.

## Evidence on Hand

- `src/LarkCloud.App/Assets/app.ico`
- README.md describes the behaviour in plain words. Its wording is the source for UI copy.
- There are no screenshots, testimonials or metrics, and none are to be invented.

## Product Principles

1. **Calm by default.** When everything is fine, the app says so in one line and asks for nothing.
2. **Plain words.** Every state and action is explained in the language of files and folders, never sync internals.
3. **One obvious next step.** Each problem shows the action that fixes it, right next to it.
4. **Safe to explore.** Anything destructive says exactly what happens and what is kept before it runs.
5. **IT decides the guard rails, people decide the comfort settings.**

## Accessibility & Inclusion

Keyboard operable throughout, screen-reader names on every control, sharp at any display scale, Windows text-size and high-contrast settings respected. Colour is never the only status signal; an icon and words always go with it.
