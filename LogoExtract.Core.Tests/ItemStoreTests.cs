namespace LogoExtract.Core.Tests;

public class ItemStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "LogoExtractTests_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Create_StartsAtImported_AndPersistsAcrossReload()
    {
        var store = new ItemStore(_root);
        BatchGroup batch = store.CreateBatch("Lô test");
        LogoItem item = store.Create(batch.Id, "Logo 1", "original.jpg");

        Assert.Equal(ItemState.Imported, item.State);
        Assert.True(Directory.Exists(store.ItemFolder(item.Id)));

        var reloaded = new ItemStore(_root);
        LogoItem? found = reloaded.Get(item.Id);
        Assert.NotNull(found);
        Assert.Equal(item.Name, found!.Name);
        Assert.Equal(item.BatchId, found.BatchId);
        Assert.Single(reloaded.ListBatches());
    }

    [Fact]
    public void MarkSegmented_SetsStateAndFileNames()
    {
        var store = new ItemStore(_root);
        BatchGroup batch = store.CreateBatch("Lô test");
        LogoItem item = store.Create(batch.Id, "Logo 1", "original.jpg");

        LogoItem updated = store.MarkSegmented(item.Id);

        Assert.Equal(ItemState.Segmented, updated.State);
        Assert.NotNull(updated.CutoutFile);
        Assert.NotNull(updated.ThumbFile);
    }

    [Fact]
    public void ResetToImported_ClearsCutoutAndThumb()
    {
        var store = new ItemStore(_root);
        BatchGroup batch = store.CreateBatch("Lô test");
        LogoItem item = store.Create(batch.Id, "Logo 1", "original.jpg");
        store.MarkSegmented(item.Id);

        LogoItem reset = store.ResetToImported(item.Id);

        Assert.Equal(ItemState.Imported, reset.State);
        Assert.Null(reset.CutoutFile);
        Assert.Null(reset.ThumbFile);
    }

    [Fact]
    public void ResetToImported_AlsoClearsFlattenedFileAndWarp()
    {
        // A FlattenedFile is a warp of the OLD cutout's exact pixels -- once CutoutFile is about to be
        // re-segmented from scratch, leaving a stale FlattenedFile/Warp behind would silently show/export warped
        // garbage against pixels that no longer exist (same staleness class ResetToImported already guards
        // against for CutoutFile/ThumbFile itself).
        var store = new ItemStore(_root);
        BatchGroup batch = store.CreateBatch("Lô test");
        LogoItem item = store.Create(batch.Id, "Logo 1", "original.jpg");
        store.MarkSegmented(item.Id);
        store.MarkFlattened(item.Id, WarpGrid.Identity());

        LogoItem reset = store.ResetToImported(item.Id);

        Assert.Null(reset.FlattenedFile);
        Assert.Null(reset.Warp);
    }

    [Fact]
    public void MarkFlattened_SetsStateFileNameAndGrid()
    {
        var store = new ItemStore(_root);
        BatchGroup batch = store.CreateBatch("Lô test");
        LogoItem item = store.Create(batch.Id, "Logo 1", "original.jpg");
        store.MarkSegmented(item.Id);
        var grid = new WarpGrid { Columns = 2, Rows = 2, Displacements = [(0.1, 0), (0, 0), (0, 0), (0, 0)] };

        LogoItem updated = store.MarkFlattened(item.Id, grid);

        Assert.Equal(ItemState.Flattened, updated.State);
        Assert.NotNull(updated.FlattenedFile);
        Assert.NotNull(updated.Warp);
        Assert.Equal(0.1, updated.Warp!.Displacements[0].Dx);
    }

    [Fact]
    public void MarkFlattened_NeverDowngradesAnAlreadyVectorizedItem()
    {
        // Revisiting the flatten tool to tweak the grid on a finished item must not move its dashboard badge
        // backwards from Vectorized to Flattened.
        var store = new ItemStore(_root);
        BatchGroup batch = store.CreateBatch("Lô test");
        LogoItem item = store.Create(batch.Id, "Logo 1", "original.jpg");
        store.MarkSegmented(item.Id);
        store.Update(item.Id, i => i with { State = ItemState.Vectorized, VectorFile = "vector.svg" });

        LogoItem updated = store.MarkFlattened(item.Id, WarpGrid.Identity());

        Assert.Equal(ItemState.Vectorized, updated.State);
        Assert.NotNull(updated.FlattenedFile); // file/grid still update even though the badge doesn't move backwards
    }

    [Fact]
    public void Delete_RemovesFolderAndDropsFromList()
    {
        var store = new ItemStore(_root);
        BatchGroup batch = store.CreateBatch("Lô test");
        LogoItem item = store.Create(batch.Id, "Logo 1", "original.jpg");
        string folder = store.ItemFolder(item.Id);

        store.Delete(item.Id);

        Assert.False(Directory.Exists(folder));
        Assert.Null(store.Get(item.Id));
        Assert.DoesNotContain(item.Id, store.List().Select(i => i.Id));
    }

    [Fact]
    public void Delete_IsIdempotent_SecondCallDoesNotThrow()
    {
        var store = new ItemStore(_root);
        BatchGroup batch = store.CreateBatch("Lô test");
        LogoItem item = store.Create(batch.Id, "Logo 1", "original.jpg");

        store.Delete(item.Id);
        store.Delete(item.Id); // must not throw on an already-deleted item
    }

    [Fact]
    public void List_OrdersNewestFirst()
    {
        var store = new ItemStore(_root);
        BatchGroup batch = store.CreateBatch("Lô test");
        LogoItem older = store.Create(batch.Id, "Cũ hơn", "a.jpg");
        store.Update(older.Id, i => i with { CreatedUtc = DateTime.UtcNow.AddMinutes(-10) });
        LogoItem newer = store.Create(batch.Id, "Mới hơn", "b.jpg");

        IReadOnlyList<LogoItem> list = store.List();

        Assert.Equal(newer.Id, list[0].Id);
        Assert.Equal(older.Id, list[1].Id);
    }

    [Fact]
    public void Update_UnknownId_Throws()
    {
        var store = new ItemStore(_root);
        Assert.Throws<InvalidOperationException>(() => store.Update("not-a-real-id", i => i));
    }
}
