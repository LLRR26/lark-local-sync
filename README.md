# Lark Cloud Drive (files on demand)

A separate build from **Lark Drive Sync**. It shows Lark Drive folders in File Explorer the way OneDrive does:
every file is listed with its real name, size and date, but **its data stays in Lark until someone opens it**.
Opening a file downloads it on the spot; saving it uploads the change. People only use disk space for the files
they actually work with.

| | Lark Drive Sync (`lark-drive-sync`) | Lark Cloud Drive (this folder) |
|---|---|---|
| Disk space | A full copy of every file | Only files that were opened (or kept on purpose) |
| Opening a file | Instant (already on the PC) | Downloads first (a progress bar shows in Explorer) |
| Works offline | Everything | Only files already on the PC |
| App must be running | Only for syncing | Also to **open** cloud-only files |
| Built with | Python | C# / .NET 8 (Windows Cloud Files API, the same one OneDrive uses) |

Both apps can be installed side by side. They must not use the same PC folder.

## What staff see

* A folder (default `C:\Users\<name>\Lark Cloud\<Lark folder name>`) that also appears in File Explorer's left-hand panel.
* Status icons in Explorer: ☁ only in Lark · ✓ on this PC · ✓ in a green circle "always keep on this device" · ⟳ syncing.
* Right-click any file or folder:
  * **Always keep on this device** – download it now and keep it (for working offline).
  * **Free up space** – remove the local copy; it stays listed and downloads again when opened.
* In the app's Folders list, the ⋯ menu has **Rename on this PC…**: it renames only the folder on the PC (the Lark folder keeps its name) and keeps syncing from the new path. Not shown for folders your IT team manages.
* The ⋯ menu also has **Check often…**: pick subfolders to look at in Lark about every 30 seconds, so new files, edits and renames made in Lark show up on the PC quickly. Deletions (and files moved out of those subfolders) still wait for the normal check, which runs for the whole folder every 1–15 minutes as before; this keeps a short Lark hiccup from ever deleting files on the PC. Works for folders your IT team manages too.
* A tray icon (blue cloud) with a coloured dot: green = up to date, blue = syncing, amber = something needs a look,
  red = can't sync (for example signed out). Left-click opens what needs attention.

Everything else works like Lark Drive Sync:

* New and edited files on the PC are uploaded within a few seconds; changes in Lark show up within a minute or so
  (larger folders are checked less often: about 1 minute per 100 folders, at most every 15 minutes).
* Moves and renames carry over both ways without re-uploading or re-downloading.
* Deleting on either side deletes on the other, always – even if the other side edited the item in the
  meantime (that edited version is then in Lark's trash or the Recycle Bin). Deleting a folder takes everything inside
  it along, including things added to it on the other side. (`delete_wins: false` brings back the older rule where an
  edit wins and the item returns.) Two exceptions: a different Lark item now at the same path isn't deleted, and
  deleting a `.url` shortcut doesn't trash a doc that was edited in Lark since the last sync.
* **Which side deleted it** is always worked out from what both sides looked like at the last sync, so a deletion
  is never mistaken for something new on the other side:
  * files keep their Lark ID on the PC, so something deleted in Lark is recognised even if the sync records were reset;
  * a renamed file that lost its Lark link (some apps save by writing a new file) is matched by size and date and
    renamed in Lark, not uploaded as a duplicate;
  * a folder on the PC that can't be read (locked, access denied) is left alone, and if the synced folder itself is
    missing (drive not ready) nothing happens until it's back;
  * if a single check would delete more than half of a big folder (over 200 files), it waits for the next check
    (about 20 seconds later) to see the same thing before doing it.
* Deleted items go to the Lark trash or the Recycle Bin, in the background with no confirmation (IT can turn
  confirmation back on with `confirm_mass_deletes`).
* A file changed on both sides keeps both versions (`name (conflict from PC-NAME 2026-11-06 1430).ext`).
* Lark online docs/sheets appear as shortcuts that open in the browser.
* Every file type syncs, except Office/LibreOffice "file is open" markers (`~$…`, `.~lock.…#`), unfinished browser
  downloads (`.crdownload`), this app's own temporary files and Windows' `$RECYCLE.BIN` / `System Volume Information`.
* **Files that can't sync…** lists problems with **Show in folder**, **Open in Lark**, **Upload with Lark…** and
  **Try again now**, plus "good to know" notes (ℹ) that aren't problems.
* **Names Windows doesn't allow** are shown with look-alike characters, like OneDrive does: `Q1: Plan?.docx` appears
  as `Q1： Plan？.docx`, a trailing dot/space is dropped, `CON.txt` becomes `CON (Lark).txt`. The name in Lark doesn't change.
* **Several items with the same name** in one Lark folder all show, numbered: `Report.xlsx`, `Report (2).xlsx`.
  The app tracks each by its Lark ID, so saving `Report (2).xlsx` updates the right file in Lark.
* **Files Lark's security filter blocks** (some code/script files) are stored in Lark as `name (zipped).zip` and show
  as the normal file on every PC with this app; in the browser people download and unzip them. Lark's filter can
  look inside normal zips, so if the zip is blocked too, a **password-protected zip** is used (password **`lark`** –
  it's only there to get past the filter, not to keep anything secret). If even that is blocked, **Upload with
  Lark…** is offered (`zip_blocked_files: false` turns zipping off).
* **Lark doc shortcuts** (`Name.url`) control the doc: renaming or moving the shortcut renames/moves the doc in
  Lark, deleting it moves the doc to Lark's trash.
* **Offline doc copies** (per folder, button in "Lark folders on this PC"): next to each doc/sheet/slides shortcut you
  also get a read-only `Name (Lark copy).docx / .xlsx / .pptx` (or `.pdf`, see `doc_copies_format`) that opens in
  Office, offline too once downloaded, refreshed when the doc changes. Deleting a copy never deletes the doc.
* **View-only folders**: files are read-only on the PC, so Word/Excel say "read-only" up front and the status shows
  "view only". If changes are made anyway, they're uploaded to My Folders › **Lark Cloud – changes not saved** so
  nothing is lost, and **Open in Lark** opens the folder to ask for edit access. Deleting there puts the file back.
* **Empty (0-byte) files**: if Lark refuses them, they stay on the PC (a note says so) and upload once they have
  content. Empty files already in Lark show normally.
* **Folders shared with you**: search for them by name when adding a folder (or paste the link).
* **Wiki spaces**: add a whole wiki space or a page with sub-pages. Pages with sub-pages become folders, pages become
  shortcuts (plus offline copies if turned on), files attached in the wiki become cloud files. Read-only for now:
  changes are saved to My Folders › Lark Cloud – changes not saved.
* Downloaded files that haven't been opened for **30 days** are freed up automatically; files marked
  "Always keep on this device" are never freed (`free_up_after_days` in `config.json`; 0 turns this off).
  Windows Storage Sense can also free them when the disk is getting full.

## Build (IT, once)

1. Install the **.NET 8 SDK** (Windows x64) from <https://dotnet.microsoft.com/download/dotnet/8.0>
   (only on the PC that builds; staff PCs don't need it).
2. Double-click `scripts\build.bat`. It runs the tests, builds `dist\LarkCloud\LarkCloud.exe` (self-contained, ~80 MB)
   and copies the Lark app settings (`app_id`, `app_secret`) from the Lark Drive Sync build if it finds them.
   Otherwise fill in `dist\LarkCloud\config.json` yourself.
3. The Lark developer app is the same one Lark Drive Sync uses: same scopes, nothing new to enable.

## Try it

1. Run `dist\LarkCloud\LarkCloud.exe`. Sign in (same as Lark Drive Sync: approve the code in the browser).
2. **Add a Lark folder**: pick a Lark folder, keep the suggested PC folder (or choose an empty one), click **Add folder**.
3. Explorer opens the folder. Files show with a cloud icon and take no space.
4. Things to check on a pilot PC:
   * Open a Word/Excel/PDF file → it downloads and opens; its icon turns to ✓.
   * Edit and save it → within seconds the change is in Lark (check in the browser).
   * Right-click a folder → **Free up space** → icons go back to clouds and the space is freed.
   * Right-click → **Always keep on this device** → files download in the background.
   * Edit a file in Lark → the PC shows the new size/date shortly after; opening it gets the new version.
   * Quit the app (tray → Quit) and open a cloud-only file → Windows says the cloud provider isn't running. Start it again.

## Settings

**Staff** change the everyday options in the app: **Settings** (left of the main window). Appearance, start with Windows,
notifications, freeing up space, and the file type of offline doc copies are always visible. **Advanced** holds asking before
deleting many items, transfer speed, files never to sync, the Lark feature check and the activity log. Changes apply at once
(no restart) and are saved per person in `%APPDATA%\LarkCloud\config.json` under `"settings"`.

**IT** sets the defaults in `config.json` next to `LarkCloud.exe` (below) and can lock any of them with `locked_settings`.
A locked setting shows greyed out with "Set by your IT team" and the person's own choice is ignored:

```json
"locked_settings": ["free_up_after_days", "max_transfers"]
```

Use `"locked_settings": "all"` to lock everything. Lockable keys: `theme`, `notifications`, `free_up_after_days`,
`doc_copies_format`, `confirm_mass_deletes`, `max_deletes`, `max_transfers`, `extra_ignore`. For `extra_ignore`, people add
their own patterns on top of IT's (they can't remove IT's); when it is locked they can't add any. Start with Windows is always the person's own choice.

### `config.json` next to `LarkCloud.exe`

| Key | Default | Meaning |
|---|---|---|
| `app_id`, `app_secret`, `brand` | – | The Lark developer app (same as Lark Drive Sync) |
| `default_pairs` | `[]` | Folders added for everyone: `[{"folder_url": "https://…/drive/folder/…", "folder_name": "Projects", "local_folder": "%USERPROFILE%\\Lark Cloud\\Projects"}]` |
| `lock_folders` | `false` | `true` = staff only get IT's `default_pairs` and can't add/remove folders |
| `locked_settings` | `[]` | Settings people can't change in the app (list of keys, or `"all"`), see above |
| `theme` | `"system"` | `"system"` (follow Windows), `"light"` or `"dark"` |
| `notifications` | `true` | Show tray notifications (sign-in warnings always show) |
| `delete_wins` | `true` | A delete on either side always wins over an edit on the other side |
| `confirm_mass_deletes` | `false` | `true` = more than `max_deletes` deletions at once wait for someone to click **Review** in the main window; `false` = deletions always run in the background |
| `max_deletes` | `20` | The batch size that needs confirming when `confirm_mass_deletes` is on |
| `max_transfers` | `8` | Uploads / Lark look-ups at the same time |
| `free_up_after_days` | `30` | Free up files not opened for this many days (0 = never) |
| `settle_seconds` | `3` | Wait this long after a file stops changing before uploading it |
| `extra_ignore` | `[]` | Extra name patterns never to sync, e.g. `["*.tmp"]` |
| `zip_blocked_files` | `true` | Store files Lark's filter blocks as `name (zipped).zip` |
| `doc_copies_format` | `"office"` | Offline doc copies as Office files (`docx`/`xlsx`/`pptx`) or `"pdf"` |
| `extra_scopes` | `[]` | Optional Lark permissions this app should ask for (see below) |

## Optional features: extra Lark permissions

Three features need permissions the basic setup doesn't have. Without them everything else works, and the app says
what's missing when you use the feature.

| Feature | Permission (scope) to enable |
|---|---|
| Find folders shared with you (search) | `search:docs:read` |
| Wiki spaces | `wiki:wiki:readonly` |
| Offline doc copies | `drive:export:readonly` (or `docs:document:export`) |

1. Lark developer console → your app → **Permissions & Scopes** → add the ones you want → publish a new app version
   (and approve it in the admin console if asked).
2. Add the same names to `config.json` next to `LarkCloud.exe`, e.g.
   `"extra_scopes": ["search:docs:read", "wiki:wiki:readonly", "drive:export:readonly"]`.
   Only list scopes that are enabled in step 1, or signing in fails.
3. Tray → **Sign out**, then sign in again (the new permissions are granted at sign-in).
4. Tray → **Check Lark features…** → **Run the check**: it tests every feature against your Lark and shows ✓ / ✕
   with what to fix. Send the results to IT if anything shows ✕.

## Where things are

* Per-person folder list: `%APPDATA%\LarkCloud\config.json`
* Sign-in (encrypted for this Windows user), sync records, logs: `%LOCALAPPDATA%\LarkCloud\`
* Activity log: tray → **Open activity log** (`%LOCALAPPDATA%\LarkCloud\logs\activity.log`)

## Good to know

* The first time a big Lark folder is added, the app asks Lark for each file's size (Lark's folder listing doesn't
  include sizes, and Windows needs them). That's about 8 files a second, so 3,000 files take about 6 minutes; the
  status shows "Getting file details from Lark: n of N…". After that only new/changed files are looked up.
* If someone opens a file at the exact moment it's being changed in Lark, Windows may say the cloud operation
  failed; the app refreshes that file within seconds and opening it again works.
* **Remove from this PC…** never deletes anything in Lark. Downloaded files stay as normal files; cloud-only ones
  are removed from the PC.
* Needs Windows 10 version 1809 or newer (or Windows 11) and an NTFS drive (normal for C:). A folder inside OneDrive
  can't be used (two cloud providers can't share a folder).
* Ask Lark support to look at their API filter blocking harmless files (endpoint
  `/open-apis/drive/v1/files/upload_all`, with example files and times from the log). Until they do, blocked files are
  stored zipped.

## Code

* `src/LarkCloud.Core` – Lark sign-in and API client, the sync engine (`CloudEngine`), state database. No Windows
  dependencies; fully covered by `tests/LarkCloud.Tests` (fake Lark server + fake cloud folder).
* `src/LarkCloud.App` – the Windows app: Cloud Files API bindings (`CloudFiles/`), the per-folder background
  workers (`Service/`), and the tray + windows (`UI/`).
