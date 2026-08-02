// Design doc 2026-08-01, test U18 (hard requirement: merged AND split layouts
// must both work). Parameterized over the three physical layouts that coexist
// in the live library:
//   (a) "Season NN" physical folders per aired season (Dr. STONE shape),
//       per-season numbering -> renumbering via resolved absolute numbers;
//   (b) flat (Bleach shape), already absolute -> merge is a no-op;
//   (c) everything in "Season 01" + virtual seasons 2+ (Naruto shape),
//       absolute numbering -> merge moves without renumbering.
// For each layout: the merge plan produces a converged state (all episodes
// ParentIndexNumber=1, SeasonId=Season 1, distinct numbers); the split plan
// produces the inverse (aired seasons restored, SeasonId pointing at the
// aired season's item); merging again converges; a further merge run is a
// pure no-op (idempotency).
using Jellyfin.Plugin.Ronin.Helpers;
using Xunit;

namespace Jellyfin.Plugin.Ronin.Tests;

public class LayoutMatrixTests
{
    private sealed record Ep(int Pin, int Idx, Guid SeasonId);

    private sealed record Layout(
        string Name,
        Dictionary<int, (Guid Id, string SeasonName)> Seasons,
        List<Ep> Episodes,
        Dictionary<(int Pin, int Idx), int> AbsoluteNumbers);

    private static readonly Guid S1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid S2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid S3 = Guid.Parse("00000000-0000-0000-0000-000000000003");

    private static Layout SeasonFoldersLayout() => new(
        "physical Season NN folders, per-season numbering",
        new() { [1] = (S1, "Season 1"), [2] = (S2, "Season 2") },
        [new(1, 1, S1), new(1, 2, S1), new(2, 1, S2), new(2, 2, S2)],
        new() { [(2, 1)] = 3, [(2, 2)] = 4 });

    private static Layout FlatLayout() => new(
        "flat, absolute numbering",
        new() { [1] = (S1, "Season 1") },
        [new(1, 1, S1), new(1, 2, S1), new(1, 3, S1), new(1, 4, S1)],
        new());

    private static Layout Season01PlusVirtualLayout() => new(
        "Season 01 physical + virtual seasons 2+, absolute numbering with gaps",
        new() { [1] = (S1, "Season 1"), [2] = (S2, "Season 2"), [3] = (S3, "Season 3") },
        [new(1, 1, S1), new(1, 2, S1), new(2, 3, S2), new(2, 5, S2), new(3, 6, S3), new(3, 8, S3)],
        new());

    public static TheoryData<string> Layouts => new() { "a", "b", "c" };

    private static Layout Get(string key) => key switch
    {
        "a" => SeasonFoldersLayout(),
        "b" => FlatLayout(),
        _ => Season01PlusVirtualLayout(),
    };

    private static Ep Apply(Ep e, EpisodePlan plan) => plan.Outcome == PlanOutcome.Update
        ? new Ep(
            plan.ParentIndexNumber ?? e.Pin,
            plan.IndexNumber ?? e.Idx,
            plan.SeasonId ?? e.SeasonId)
        : e;

    private static List<Ep> RunMerge(Layout layout, List<Ep> eps, out int noOpCount)
    {
        var (season1Id, season1Name) = layout.Seasons[1];
        var pairs = eps.Where(e => e.Pin > 0 && e.Idx > 0).Select(e => (e.Pin, e.Idx)).ToList();
        var renumberNeeded = !Numbering.IsAbsoluteNumbering(pairs);

        noOpCount = 0;
        var result = new List<Ep>();
        foreach (var e in eps)
        {
            int? resolved = null;
            if (renumberNeeded && e.Pin != 1)
            {
                resolved = layout.AbsoluteNumbers[(e.Pin, e.Idx)];
            }

            var plan = MergePlan.Compute(e.Pin, e.Idx, e.SeasonId, season1Id, season1Name, renumberNeeded, resolved);
            Assert.NotEqual(PlanOutcome.SkipEpisode, plan.Outcome);
            Assert.NotEqual(PlanOutcome.SkipSeries, plan.Outcome);
            if (plan.Outcome == PlanOutcome.NoOp)
            {
                noOpCount++;
            }

            result.Add(Apply(e, plan));
        }

        return result;
    }

    private static void AssertMerged(Layout layout, List<Ep> eps)
    {
        var (season1Id, _) = layout.Seasons[1];
        Assert.All(eps, e => Assert.Equal(1, e.Pin));
        Assert.All(eps, e => Assert.Equal(season1Id, e.SeasonId));
        Assert.Equal(eps.Count, eps.Select(e => e.Idx).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void Merge_ProducesConvergedState(string key)
    {
        var layout = Get(key);
        var merged = RunMerge(layout, layout.Episodes, out _);
        AssertMerged(layout, merged);
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void MergeTwice_SecondRunIsNoOp(string key)
    {
        var layout = Get(key);
        var merged = RunMerge(layout, layout.Episodes, out _);
        var again = RunMerge(layout, merged, out var noOps);
        Assert.Equal(merged, again);
        Assert.Equal(merged.Count, noOps); // every plan was NoOp
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public void SplitAfterMerge_RestoresAiredSeasons_ThenMergeConvergesAgain(string key)
    {
        var layout = Get(key);
        var airedByOriginal = layout.Episodes.ToList(); // original pins = aired seasons
        var merged = RunMerge(layout, layout.Episodes, out _);

        // Split: aired season comes from the original layout (stands in for
        // the TVDB aired-order lookup); episode numbers are preserved.
        var split = new List<Ep>();
        for (var i = 0; i < merged.Count; i++)
        {
            var aired = airedByOriginal[i].Pin;
            var target = layout.Seasons.TryGetValue(aired, out var t) ? t : default((Guid Id, string SeasonName)?);
            var plan = SplitPlan.Compute(merged[i].Pin, merged[i].SeasonId, aired, target?.Id, target?.SeasonName);
            split.Add(Apply(merged[i], plan));
        }

        // Inverse restored: aired seasons and season items re-assigned.
        for (var i = 0; i < split.Count; i++)
        {
            Assert.Equal(airedByOriginal[i].Pin, split[i].Pin);
            Assert.Equal(layout.Seasons[airedByOriginal[i].Pin].Id, split[i].SeasonId);
        }

        // And merging the split state converges again with no scraping
        // (post-merge numbers are absolute, so renumbering is not needed).
        var remerged = RunMerge(layout, split, out _);
        AssertMerged(layout, remerged);
    }
}
