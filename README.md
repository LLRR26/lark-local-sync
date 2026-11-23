# Lark Cloud Drive

**Lark Drive folders in Windows File Explorer, with files on demand.**

Lark Cloud Drive shows your Lark Drive folders in File Explorer the way OneDrive does. Every file is listed with its
real name, size and date, but **its data stays in Lark until someone opens it**. Opening a file downloads it on the
spot, and saving it uploads the change. People only use disk space for the files they actually work with.

It is a native Windows tray app built on C# / .NET 8 and the **Windows Cloud Files API**, the same API OneDrive uses.

---

## Contents

- [Features](#features)
- [Requirements](#requirements)
- [Getting started](#getting-started)
- [Configuration](#configuration)
- [Optional features: extra Lark permissions](#optional-features-extra-lark-permissions)
- [Releasing an update](#releasing-an-update)
- [Where things are stored](#where-things-are-stored)
- [Good to know](#good-to-know)
- [Project structure](#project-structure)
- [Development](#development)

---

## Features

### In File Explorer

- **Your own Lark folder**, by default `C:\Users\<name>\Lark Cloud\<Lark folder name>`. It also shows in File Explorer's left-hand panel.
- **Status icons**: ☁ only in Lark · ✓ on this PC · ✓ in a green circle means "always keep on this device" · ⟳ syncing.
- **Right-click** any file or folder:
  - **Always keep on this device**: download it now and keep it, for working offline.
  - **Free up space**: remove the local copy. The file stays listed and downloads again when opened.
- **Automatic clean-up**: files that haven't been opened for **30 days** are freed up automatically. Files marked
  "Always keep on this device" are never freed. Windows Storage Sense can also free files when the disk is getting full.

### Tray icon and app window

- **A tray icon** (blue cloud) with a coloured dot:

  | Dot | Meaning |
  |---|---|
  | 🟢 Green | Up to date |
  | 🔵 Blue | Syncing |
  | 🟠 Amber | Something needs a look |
  | 🔴 Red | Can't sync (for example, signed out) |

  Left-click opens whatever needs attention. A small purple badge means an update is ready.
- **Folders**: add a folder (search by name, paste a link, or pick a wiki space), open it, open it in Lark, pause or
  resume it, free up space, or remove it from this PC. The ⋯ menu has two more options:
  - **Rename on this PC…** renames only the folder on the PC. The Lark folder keeps its name, and syncing carries on
    from the new path. This isn't shown for folders your IT team manages.
  - **Check often…** picks subfolders to check in Lark about every 30 seconds, so new files, edits and renames made in
    Lark show up on the PC quickly. Deletions still wait for the normal full-folder check, so a short Lark hiccup can
    never delete files on the PC.
- **Files that can't sync…** lists problems with **Show in folder**, **Open in Lark**, **Upload with Lark…** and
  **Try again now**. It also lists "good to know" notes (ℹ) that aren't problems.
- **Settings** for appearance, start with Windows, notifications, freeing up space and more (see [Configuration](#configuration)).

### How syncing works

- **Upload**: new and edited files on the PC are uploaded within a few seconds.
- **Download**: changes in Lark show up within a minute or so. Larger folders are checked less often: about 1 minute
  per 100 folders, and at most every 15 minutes.
- **Moves and renames** carry over both ways without re-uploading or re-downloading.
- **Conflicts**: a file changed on both sides keeps both versions, as
  `name (conflict from PC-NAME 2026-11-06 1430).ext`.
- **Deletes** on either side delete on the other side, always, even if the other side edited the item in the meantime.
  That edited version then ends up in Lark's trash or the Recycle Bin. Deleting a folder takes everything inside it
  along, including things added to it on the other side. Set `delete_wins: false` to use the older rule instead,
  where an edit wins and the item comes back. There are two exceptions:
  - a different Lark item that is now at the same path isn't deleted;
  - deleting a `.url` shortcut doesn't trash a doc that was edited in Lark since the last sync.
- **Deletion safety.** The app works out which side deleted something by comparing both sides with the last sync, so
  a deletion is never mistaken for something new:
  - files keep their Lark ID on the PC, so something deleted in Lark is recognised even if the sync records were reset;
  - some apps save by writing a new file. A renamed file that lost its Lark link that way is matched by size and date
    and renamed in Lark, not uploaded as a duplicate;
  - a PC folder that can't be read (locked, access denied) is left alone. If the synced folder itself is missing
    (drive not ready), nothing happens until it's back;
  - if a single check would delete more than half of a big folder (over 200 files), the app waits for the next check
    (about 20 seconds later) to confirm before doing it.
- Deleted items go to Lark's trash or the Recycle Bin, in the background, with no confirmation. IT can turn
  confirmation back on with `confirm_mass_deletes`.

### Lark docs, sheets and wikis

- **Lark online docs and sheets** appear as `Name.url` shortcuts that open in the browser. The shortcut controls the
  doc: renaming or moving it renames or moves the doc in Lark, and deleting it moves the doc to Lark's trash.
- **Offline doc copies** (turned on per folder): next to each doc, sheet or slides shortcut you also get a read-only
  `Name (Lark copy).docx / .xlsx / .pptx`, or `.pdf` (see `doc_copies_format`). It opens in Office, works offline once
  downloaded and is refreshed when the doc changes. Deleting a copy never deletes the doc.
- **Wiki spaces**: add a whole wiki space, or one page with its sub-pages. Pages with sub-pages become folders, pages
  become shortcuts (plus offline copies if turned on), and files attached in the wiki become cloud files. Wikis are
  read-only for now: changes are saved to My Folders › **Lark Cloud – changes not saved**.
- **Folders shared with you**: search for them by name when adding a folder, or paste the link.

### Special cases handled for you

- **Names Windows doesn't allow** are shown with look-alike characters, the same way OneDrive does it:
  `Q1: Plan?.docx` appears as `Q1： Plan？.docx`, a trailing dot or space is dropped, and `CON.txt` becomes
  `CON (Lark).txt`. The name in Lark doesn't change.
- **Several items with the same name** in one Lark folder all show, numbered: `Report.xlsx`, `Report (2).xlsx`.
  Each one is tracked by its Lark ID, so saving `Report (2).xlsx` updates the right file in Lark.
- **Files blocked by Lark's security filter** (some code and script files) are stored in Lark as
  `name (zipped).zip` and show as the normal file on every PC that runs this app. In the browser, people download and
  unzip them. If the zip is blocked too, a **password-protected zip** is used. The password is **`lark`**. It only
  gets past the filter and doesn't keep anything secret. If even that is blocked, **Upload with Lark…** is offered.
  Set `zip_blocked_files: false` to turn zipping off.
- **View-only folders**: files are read-only on the PC, so Word and Excel say "read-only" up front and the status
  shows "view only". If changes are made anyway, they're uploaded to My Folders › **Lark Cloud – changes not saved**
  so nothing is lost, and **Open in Lark** opens the folder so you can ask for edit access. A file deleted there is
  put back.
- **Empty (0-byte) files**: if Lark refuses them, they stay on the PC (a note says so) and upload once they have
  content. Empty files that are already in Lark show normally.
- **Never synced**: Office and LibreOffice "file is open" markers (`~$…`, `.~lock.…#`), unfinished browser downloads
  (`.crdownload`), this app's own temporary files, and Windows' `$RECYCLE.BIN` / `System Volume Information`.
  Every other file type syncs.

---

## Requirements

| | |
|---|---|
| **Staff PCs** | Windows 10 version 1809 or newer, or Windows 11. An NTFS drive (normal for `C:`). Nothing else to install. |
| **Build PC** | The [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (Windows x64) |
| **Lark** | A Lark developer app (custom app) with an `app_id` and `app_secret` |

The synced folder can't be inside OneDrive, because two cloud providers can't share a folder.

### Lark developer app permissions

In the Lark developer console, under **Permissions & Scopes**, the app needs these scopes:

```
drive:drive  drive:drive.metadata:readonly  drive:file:download  drive:file:upload
space:document:retrieve  space:folder:create  space:document:delete  space:document:move
offline_access
```

Some features need extra scopes. See [Optional features](#optional-features-extra-lark-permissions).

---

## Getting started

### 1. Build (IT, once)

1. Install the **.NET 8 SDK** on the build PC only. Staff PCs don't need it.
2. Double-click `scripts\build.bat`. It:
   1. closes any running `LarkCloud.exe`,
   2. runs the tests (and stops if any fail),
   3. builds `dist\LarkCloud\LarkCloud.exe` (a single self-contained file, about 80 MB),
   4. creates `dist\LarkCloud\config.json` from `config.example.json` if there isn't one yet.
3. Fill in `app_id` and `app_secret` in `dist\LarkCloud\config.json`.
4. Hand out the whole `dist\LarkCloud` folder (`LarkCloud.exe` + `config.json`). Put it in a folder the user can write
   to, not Program Files, so the auto-updater can replace the exe.

### 2. First run

1. Run `LarkCloud.exe` and sign in: the app shows a code, and you approve it in the browser.
2. **Add a Lark folder**: pick a Lark folder, keep the suggested PC folder (or choose an empty one) and click **Add folder**.
3. Explorer opens the folder. Files show with a cloud icon and take no space.

### 3. Pilot checklist

- [ ] Open a Word, Excel or PDF file. It downloads and opens, and its icon turns to ✓.
- [ ] Edit and save it. Within seconds the change is in Lark (check in the browser).
- [ ] Right-click a folder → **Free up space**. The icons go back to clouds and the space is freed.
- [ ] Right-click → **Always keep on this device**. The files download in the background.
- [ ] Edit a file in Lark. The PC shows the new size and date shortly after, and opening it gets the new version.
- [ ] Quit the app (tray → Quit) and open a cloud-only file. Windows says the cloud provider isn't running. Start the app again.

---

## Configuration

Settings live in two places:

| Who | Where | What |
|---|---|---|
| **IT** | `config.json` next to `LarkCloud.exe` | Defaults for everyone, the Lark app, default folders and locks |
| **Staff** | **Settings** in the app, saved to `%APPDATA%\LarkCloud\config.json` under `"settings"` | Their own choices, unless IT locked them |

### Settings in the app (staff)

Open **Settings** on the left of the main window. Changes apply at once, with no restart.

- **Always visible**: appearance, start with Windows, notifications, freeing up space, and the file type of offline doc copies.
- **Advanced**: asking before deleting many items, transfer speed, files never to sync, the Lark feature check and the activity log.
- **Updates**: shows the current version and has a **Check for updates** button.

### Locking settings (IT)

IT can lock any setting with `locked_settings`. A locked setting is greyed out with "Set by your IT team", and the
person's own choice is ignored:

```json
"locked_settings": ["free_up_after_days", "max_transfers"]
```

- Use `"locked_settings": "all"` to lock everything.
- Lockable keys: `theme`, `notifications`, `free_up_after_days`, `doc_copies_format`, `confirm_mass_deletes`,
  `max_deletes`, `max_transfers`, `extra_ignore`.
- For `extra_ignore`, people add their own patterns on top of IT's and can't remove IT's. When it's locked, they can't add any.
- **Start with Windows** is always the person's own choice.

### `config.json` reference

See [`config.example.json`](config.example.json) for a starting point.

| Key | Default | Meaning |
|---|---|---|
| `app_id`, `app_secret`, `brand` | – | The Lark developer app. `brand` is `"lark"` or `"feishu"` |
| `default_pairs` | `[]` | Folders added for everyone: `[{"folder_url": "https://…/drive/folder/…", "folder_name": "Projects", "local_folder": "%USERPROFILE%\\Lark Cloud\\Projects"}]` |
| `lock_folders` | `false` | `true` = staff only get IT's `default_pairs` and can't add or remove folders |
| `locked_settings` | `[]` | Settings people can't change in the app (a list of keys, or `"all"`) |
| `theme` | `"system"` | `"system"` (follow Windows), `"light"` or `"dark"` |
| `notifications` | `true` | Show tray notifications. Sign-in warnings always show |
| `delete_wins` | `true` | A delete on either side always wins over an edit on the other side |
| `confirm_mass_deletes` | `false` | `true` = more than `max_deletes` deletions at once wait for someone to click **Review** in the main window. `false` = deletions always run in the background |
| `max_deletes` | `20` | The batch size that needs confirming when `confirm_mass_deletes` is on |
| `max_transfers` | `8` | Uploads and Lark look-ups at the same time |
| `free_up_after_days` | `30` | Free up files not opened for this many days (`0` = never) |
| `settle_seconds` | `3` | Wait this long after a file stops changing before uploading it |
| `extra_ignore` | `[]` | Extra name patterns never to sync, for example `["*.tmp"]` |
| `zip_blocked_files` | `true` | Store files Lark's filter blocks as `name (zipped).zip` |
| `doc_copies_format` | `"office"` | Offline doc copies as Office files (`docx`/`xlsx`/`pptx`) or `"pdf"` |
| `extra_scopes` | `[]` | Optional Lark permissions this app should ask for (see below) |

> **Never commit or share a `config.json` that contains a real `app_secret`.**

---

## Optional features: extra Lark permissions

Three features need permissions that the basic setup doesn't have. Everything else works without them, and the app
says what's missing when you use the feature.

| Feature | Permission (scope) to enable |
|---|---|
| Find folders shared with you (search) | `search:docs:read` |
| Wiki spaces | `wiki:wiki:readonly` |
| Offline doc copies | `drive:export:readonly` (or `docs:document:export`) |

1. Lark developer console → your app → **Permissions & Scopes** → add the ones you want → publish a new app version
   (and approve it in the admin console if asked).
2. Add the same names to `config.json` next to `LarkCloud.exe`, for example:
   ```json
   "extra_scopes": ["search:docs:read", "wiki:wiki:readonly", "drive:export:readonly"]
   ```
   Only list scopes that you enabled in step 1, or signing in fails.
3. Tray → **Sign out**, then sign in again. The new permissions are granted at sign-in.
4. Tray → **Check Lark features…** → **Run the check**. It tests every feature against your Lark and shows ✓ / ✕ with
   what to fix. Send the results to IT if anything shows ✕.

---

## Releasing an update

The app checks the public GitHub repo [`LLRR26/lark-local-sync`](https://github.com/LLRR26/lark-local-sync/releases)
for a newer release.

1. Bump `<Version>` in `src\LarkCloud.App\LarkCloud.App.csproj`. This is the only place the version lives.
2. Run `scripts\build.bat`.
3. On GitHub, go to **Releases** → **new release**, tag it `vX.Y.Z` (the same number as `<Version>`), upload **only**
   `dist\LarkCloud\LarkCloud.exe` (never `config.json`) and publish. Don't mark it as a draft or a pre-release.

**What happens on staff PCs:**

- PCs pick up the release about a minute after start, then every 6 hours, and swap the file in the background.
- There is no pop-up notification. The tray icon gets a small purple update badge (the status dot stays), and the tray
  menu shows **Restart to update (X.Y.Z)**.
- **Settings → Updates** shows the version and has a **Check for updates** button that checks on demand.
- The exe must be in a folder the user can write to (not Program Files). PCs running a build without the updater need
  one manual replacement of `LarkCloud.exe`.

---

## Where things are stored

| What | Where |
|---|---|
| IT settings | `config.json` next to `LarkCloud.exe` |
| Per-person folder list and settings | `%APPDATA%\LarkCloud\config.json` |
| Sign-in (encrypted for this Windows user), sync records, logs | `%LOCALAPPDATA%\LarkCloud\` |
| Activity log | `%LOCALAPPDATA%\LarkCloud\logs\activity.log` (or tray → **Open activity log**) |

---

## Good to know

- **Adding a big folder for the first time takes a while.** Lark's folder listing doesn't include file sizes, and
  Windows needs them, so the app asks Lark for each one at about 8 files a second. 3,000 files take about 6 minutes.
  The status shows "Getting file details from Lark: n of N…". After that, only new or changed files are looked up.
- **The app must be running to open cloud-only files.** Files already on the PC open without it.
- **Opening a file while it's being changed in Lark** may make Windows say the cloud operation failed. The app
  refreshes that file within seconds, and opening it again works.
- **Remove from this PC…** never deletes anything in Lark. Downloaded files stay as normal files, and cloud-only ones
  are removed from the PC.
- **Harmless files blocked by Lark**: ask Lark support to look at their API filter on
  `/open-apis/drive/v1/files/upload_all`, with example files and times from the activity log. Until they fix it,
  blocked files are stored zipped.

---

## Project structure

```
LarkCloud.sln
config.example.json          Template for IT's config.json
scripts/build.bat            Test + build + package into dist\LarkCloud\
src/
  LarkCloud.Core/            Platform-independent logic (net8.0, no Windows dependencies)
    CloudEngine.cs           The two-way sync engine
    LarkApi.cs, LarkAuth.cs  Lark Open API client and device-code sign-in
    LarkRequestLimiter.cs    Keeps requests within Lark's rate limits
    RemoteIndex.cs           Cached copy of the synced Lark folder tree
    WikiRemote.cs            Wiki spaces shown as read-only folders
    RemoteFeatures.cs        Offline doc copies (exporting Lark docs to Office/PDF)
    StateDb.cs               SQLite sync records
    ProtectedZip.cs          Zipping files Lark's filter blocks
    Config.cs, Models.cs     Settings and shared types
    Update.cs, Log.cs        Release check, logging
    ICloudLocal.cs           Interface to the local cloud folder (faked in tests)
  LarkCloud.App/             The Windows app (WPF, net8.0-windows)
    Program.cs               Entry point
    CloudFiles/              Windows Cloud Files API bindings (sync root, placeholders, hydration)
    Service/                 Per-folder background workers, feature check, auto-updater
    UI/                      Tray icon and menu, main window, pages and dialogs
tests/
  LarkCloud.Tests/           xUnit tests against a fake Lark server and a fake cloud folder
```

---

## Development

Build and run the tests from the repository root:

```powershell
dotnet test tests\LarkCloud.Tests
```

```powershell
dotnet build src\LarkCloud.App
```

To produce the distributable exe, run `scripts\build.bat`. It runs the tests first and builds nothing if any fail.

- **Core** (`src/LarkCloud.Core`) has no Windows dependencies, and all sync logic lives there, so it can be tested
  end to end with the fakes in `tests/LarkCloud.Tests/Fakes.cs`.
- **App** (`src/LarkCloud.App`) is the thin Windows layer: it binds the Cloud Files API and the UI to the engine.
- Design and product notes are in [`DESIGN.md`](DESIGN.md) and [`PRODUCT.md`](PRODUCT.md).
