---
version: 1
slug: "src-larkcloud-app-ui"
primary_target: "src/LarkCloud.App/UI"
related_targets: []
---

## Scope

All windows of the Lark Cloud Drive tray app (src/LarkCloud.App/UI). Mode: **Operate**. People open it rarely and briefly to check status, add a folder, fix a problem file, or change a setting.

## Direction contract

THESIS: one calm window that answers "is everything OK, and if not, what do I click?" It refuses the incumbent arrangement of a grid of columns over a row of nine equal buttons, and it refuses sync jargon. It follows the user-pinned canon, native Windows 11 played straight. Since the canon is pinned, no concept roll was run.

OWN-WORLD: Windows 11 Fluent on Mica: a light or dark layer that follows Windows, with an override. Lark blue #3370FF is the only accent and appears only on the primary action, selection and the syncing state. Text uses Segoe UI Variable (Segoe UI on Windows 10), and icons use Segoe Fluent Icons (Segoe MDL2 Assets on Windows 10). Status always pairs a glyph with words and a Fluent semantic colour. Corner radius is 4 on controls and 8 on surfaces, on a 4-DIP grid. No gradients, no glass beyond Mica, no emoji or Unicode stand-ins for icons.

STORY: the person sees one plain sentence of status, sees their folders as a quiet list, and finds the single fixing action next to any problem. Settings explain each option in one line, and options IT has locked say so.

FIRST VIEWPORT: a left navigation (Folders, Files that can't sync with a count badge, Settings; the account at the bottom). The content area starts with a status header: a 32-DIP state glyph, a sentence at Subtitle 20 such as "Everything is up to date", one muted detail line ("Checked 1 min ago · 3 folders"), and on the right "Sync now" plus "Pause". Under it is the folder list: one row per folder with name, PC path and a status line, with Open folder as the visible action and the rest under a ⋯ menu. "Add a folder" is the one accent button, at the top right of the list.

FORM: the native Windows 11 Settings-app grammar (NavigationView, SettingsCard rows, InfoBar for attention), ranked first as the user-pinned canon. Seed key: none (pinned direction, no roll).

FINISH: unreviewed and undocumented is unfinished; this build ends with the finish review, the verdict, DESIGN.md, and every shipping raster carrying its provenance
