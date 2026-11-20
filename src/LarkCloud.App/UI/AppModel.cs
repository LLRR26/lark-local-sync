using LarkCloud.App.Service;
using LarkCloud.Core;

namespace LarkCloud.App.UI;

internal enum AppPage { Folders, Problems, Settings }

/// <summary>What the main window needs from the app. TrayApp implements it for real; the debug design preview fakes it.
/// New pages (Settings, Problems, ...) get everything through this, so they never touch the sync engine directly.</summary>
internal interface IAppModel
{
    bool Configured { get; }
    bool SignedIn { get; }
    string UserName { get; }
    bool LockFolders { get; }
    IReadOnlyList<Pair> Pairs();
    PairStatus StatusOf(string pairId);

    void SetPaused(string pairId, bool paused);
    void SetDocCopies(string pairId, bool on);
    void FreeUpSpace(string pairId);
    void RemoveFolder(string pairId);          // slow: call from a background task
    void RenameFolder(string pairId, string newPath);   // slow, throws if the move fails: call from a background task
    void SyncAllNow();

    void AddFolder();
    IFolderSource OpenFolderSource();          // Lark browsing for the add-folder dialog
    void AddPair(Pair p);
    void RetryFailed(string? pairId);          // "Try again now": one folder, or all
    void AllowDeletes(string pairId, string approval);
    void RestoreDeletes(string pairId);        // review deletions: put the items back
    void ShowProblems(string? pairId);         // the Files that can't sync page (one folder, or all)
    void ShowDeletions(string pairId);
    void SignInOrOut();
    void OpenCheck();
    void OpenLog();
    bool StartsWithWindows { get; set; }

    /// <summary>The settings page reads effective values and locks from here and saves the person's choices with SetMine.</summary>
    Config Settings { get; }
    /// <summary>Call after changing a setting: applies it everywhere without a restart.</summary>
    void SettingsChanged();
}

internal enum Tone { Ok, Busy, Caution, Critical, Muted }

/// <summary>The one plain sentence about everything, worked out from the folders' states.</summary>
internal sealed record StatusSummary(Tone Tone, string Glyph, string Sentence, string Detail,
    int ProblemFiles, int Deletions, string? DeletionsPairId, bool HasFolders, bool AllPaused)
{
    // Segoe Fluent Icons / Segoe MDL2 Assets code points that exist in both.
    public const string GlyphOk = "\uE930", GlyphBusy = "\uE895", GlyphCaution = "\uE7BA", GlyphCritical = "\uEA39",
        GlyphPaused = "\uE769", GlyphFolder = "\uE8B7";

    public static StatusSummary Of(IAppModel m)
    {
        var pairs = m.Pairs();
        var sts = pairs.Select(p => (Pair: p, St: m.StatusOf(p.Id))).ToList();
        var problems = sts.Sum(x => x.St.Items.Count);
        var dels = sts.Sum(x => x.St.PendingDeletes.Count);
        var delPair = sts.FirstOrDefault(x => x.St.PendingDeletes.Count > 0).Pair?.Id;
        StatusSummary Make(Tone t, string g, string sentence, string detail, bool paused = false) =>
            new(t, g, sentence, detail, problems, dels, delPair, pairs.Count > 0, paused);

        StatusSummary r;
        if (!m.Configured) r = Make(Tone.Critical, GlyphCritical, "Not set up yet", "Ask IT to finish setting up Lark Cloud Drive (the config.json next to LarkCloud.exe).");
        else if (!m.SignedIn) r = Make(Tone.Critical, GlyphCritical, "You're signed out", "Files that are only in Lark can't be opened until you sign in.");
        else if (pairs.Count == 0) r = Make(Tone.Muted, GlyphFolder, "No folders yet",
            m.LockFolders ? "Your IT team chooses which folders appear on this PC." : "Add a Lark folder to see it in File Explorer.");
        else
        {
            var active = sts.Where(x => !x.Pair.Paused).ToList();
            var paused = pairs.Count - active.Count;
            var last = sts.Max(x => x.St.LastFull);
            var checkedText = last == null ? "Not checked yet" : $"Checked {Ui.Ago(last)}";
            var detail = $"{checkedText} · {pairs.Count} folder{(pairs.Count == 1 ? "" : "s")}" + (paused > 0 && active.Count > 0 ? $" · {paused} paused" : "");
            if (active.Count == 0) r = Make(Tone.Muted, GlyphPaused, "Syncing is paused", "Resume to bring your folders up to date.", paused: true);
            else
            {
                var order = new[] { SyncState.Error, SyncState.Attention, SyncState.Syncing, SyncState.Starting, SyncState.Idle };
                var worst = active.OrderBy(x => Array.IndexOf(order, x.St.State)).First();
                switch (worst.St.State)
                {
                    case SyncState.Error: r = Make(Tone.Critical, GlyphCritical, worst.St.Message, detail); break;
                    case SyncState.Attention:
                        r = Make(Tone.Caution, GlyphCaution,
                            problems > 0 ? $"{problems:N0} file{(problems == 1 ? "" : "s")} can't sync"
                            : dels > 0 ? $"{dels:N0} deletion{(dels == 1 ? " is" : "s are")} waiting for you"
                            : worst.St.Message, detail); break;
                    case SyncState.Syncing or SyncState.Starting:
                        r = Make(Tone.Busy, GlyphBusy, worst.St.State == SyncState.Starting ? "Starting…" : "Syncing your files…",
                            worst.St.State == SyncState.Syncing && worst.St.Message.Length > 0 ? $"{worst.St.Message} · {pairs.Count} folder{(pairs.Count == 1 ? "" : "s")}" : detail); break;
                    default: r = Make(Tone.Ok, GlyphOk, "Everything is up to date", detail); break;
                }
            }
        }
        return r;
    }
}
