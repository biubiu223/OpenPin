using OpenPin.Pin.Model;

namespace OpenPin.Tests.Pin;

public class PinGroupBookTests
{
    private static PinGroupBook NewBook()
    {
        var b = new PinGroupBook();
        b.EnsureValid("默认组");
        return b;
    }

    [Fact]
    public void EnsureValid_CreatesDefaultGroupFirst()
    {
        var b = NewBook();
        Assert.Single(b.Groups);
        Assert.Equal(PinGroupBook.DefaultGroupId, b.Groups[0].Id);
        Assert.Equal("默认组", b.Groups[0].Name);
        Assert.Equal(PinGroupBook.DefaultGroupId, b.CurrentId);
    }

    [Fact]
    public void EnsureValid_RepairsBrokenData()
    {
        var b = new PinGroupBook
        {
            CurrentId = "missing",
            Groups = new List<PinGroup>
            {
                new() { Id = "g1", Name = "A" },
                new() { Id = "g1", Name = "dup" },
                new() { Id = "", Name = "noid" },
                new() { Id = PinGroupBook.DefaultGroupId, Name = "" },
            },
        };
        b.EnsureValid("默认组");
        Assert.Equal(new[] { PinGroupBook.DefaultGroupId, "g1" }, b.Groups.Select(g => g.Id));
        Assert.Equal("默认组", b.Groups[0].Name);
        Assert.Equal(PinGroupBook.DefaultGroupId, b.CurrentId);
    }

    [Fact]
    public void Create_RequiresUniqueNonEmptyName()
    {
        var b = NewBook();
        Assert.NotNull(b.Create("工作", "#1677FF"));
        Assert.Null(b.Create("工作", null));
        Assert.Null(b.Create("  工作 ", null));
        Assert.Null(b.Create("   ", null));
        Assert.Null(b.Create("默认组", null));
        Assert.Equal(2, b.Groups.Count);
    }

    [Fact]
    public void UniqueName_AppendsNumber()
    {
        var b = NewBook();
        b.Create("新贴图组", null);
        b.Create("新贴图组 2", null);
        Assert.Equal("新贴图组 3", b.UniqueName("新贴图组"));
        Assert.Equal("其他", b.UniqueName("其他"));
    }

    [Fact]
    public void Rename_RejectsDuplicates_ButAllowsSameGroup()
    {
        var b = NewBook();
        var g = b.Create("A", null)!;
        b.Create("B", null);
        Assert.False(b.Rename(g.Id, "B"));
        Assert.True(b.Rename(g.Id, "a")); // same group, different case
        Assert.True(b.Rename(g.Id, "C"));
        Assert.Equal("C", b.Find(g.Id)!.Name);
    }

    [Fact]
    public void Delete_DefaultIsProtected_CurrentFallsBackToDefault()
    {
        var b = NewBook();
        var g = b.Create("A", null)!;
        b.CurrentId = g.Id;
        Assert.False(b.Delete(PinGroupBook.DefaultGroupId));
        Assert.True(b.Delete(g.Id));
        Assert.Equal(PinGroupBook.DefaultGroupId, b.CurrentId);
        Assert.Single(b.Groups);
    }

    [Fact]
    public void Move_KeepsDefaultFirst()
    {
        var b = NewBook();
        var a = b.Create("A", null)!;
        var c = b.Create("C", null)!;
        Assert.False(b.Move(PinGroupBook.DefaultGroupId, 1));
        Assert.False(b.Move(a.Id, -1)); // cannot pass the default group
        Assert.True(b.Move(c.Id, -1));
        Assert.Equal(new[] { PinGroupBook.DefaultGroupId, c.Id, a.Id }, b.Groups.Select(g => g.Id));
    }

    [Fact]
    public void NextId_Cycles()
    {
        var b = NewBook();
        var a = b.Create("A", null)!;
        var c = b.Create("C", null)!;
        Assert.Equal(a.Id, b.NextId());
        b.CurrentId = a.Id;
        Assert.Equal(c.Id, b.NextId());
        b.CurrentId = c.Id;
        Assert.Equal(PinGroupBook.DefaultGroupId, b.NextId());
    }

    [Fact]
    public void Json_RoundTrip_AndCorruptInputGivesDefaultBook()
    {
        var b = NewBook();
        var a = b.Create("A", "#FF0000")!;
        b.CurrentId = a.Id;
        var back = PinGroupBook.FromJsonOrNew(b.ToJson(), "默认组");
        Assert.Equal(a.Id, back.CurrentId);
        Assert.Equal("#FF0000", back.Find(a.Id)!.Color);

        var broken = PinGroupBook.FromJsonOrNew("{{{", "默认组");
        Assert.Single(broken.Groups);
        Assert.Equal(PinGroupBook.DefaultGroupId, broken.CurrentId);
    }
}
